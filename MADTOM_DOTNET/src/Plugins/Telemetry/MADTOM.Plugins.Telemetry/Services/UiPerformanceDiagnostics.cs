using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MadTOM.Services;

/// <summary>Bounded samples; counts/totals cover the interval, percentiles use its latest 512 observations.</summary>
public sealed class UiTimingWindow
{
    public sealed record Stats(long Count, double MeanMs, double P95Ms, double MaxMs, long Over16Ms, long Over50Ms, long AllocatedBytes, int PercentileSamples);
    private sealed class Bucket
    {
        public readonly double[] Samples = new double[512];
        public long Count, Over16, Over50, Bytes;
        public double Total, Max;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new();
    public void Add(string name, double ms, long allocatedBytes = 0)
    {
        if (!double.IsFinite(ms) || ms < 0) return;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(name, out var b))
            {
                if (_buckets.Count >= 64) return;
                _buckets[name] = b = new();
            }
            b.Samples[b.Count % 512] = ms;
            b.Count++; b.Total += ms; b.Max = Math.Max(b.Max, ms);
            if (ms > 16.7) b.Over16++;
            if (ms > 50) b.Over50++;
            b.Bytes += Math.Max(0, allocatedBytes);
        }
    }
    public Dictionary<string, Stats> Drain()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, Stats>();
            foreach (var (name, b) in _buckets)
            {
                var values = b.Samples.Take((int)Math.Min(b.Count, 512)).OrderBy(x => x).ToArray();
                result[name] = new(b.Count, b.Total / b.Count, values[(int)Math.Ceiling(values.Length * .95) - 1],
                    b.Max, b.Over16, b.Over50, b.Bytes, values.Length);
            }
            _buckets.Clear();
            return result;
        }
    }
}

/// <summary>One compact record per history refresh; stage totals overlap and are not elapsed wall time.</summary>
public sealed class UiRefreshSummaryCollector
{
    public sealed record Summary(string Kind, DateTime Utc, string Refresh, string Node, string? Range,
        string Outcome, double WallMs, Dictionary<string, int> Cache, Dictionary<string, double> StageWorkMs,
        int RpcCount, long EvictedRefreshes, bool IsZeroRpc = false, double LockWaitMs = 0, double LockHoldMs = 0,
        long DecodedPoints = 0, int DecodedBlocks = 0);
    private sealed class Pending
    {
        public string? Range;
        public string Outcome = "interrupted";
        public readonly Dictionary<string, int> Cache = new();
        public readonly Dictionary<string, double> Work = new();
        public int Rpcs;
        public double LockWaitMs;
        public double LockHoldMs;
        public long DecodedPoints;
        public int DecodedBlocks;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private long _evicted;

    private static double ParseDouble(string? detail, string key)
    {
        if (string.IsNullOrEmpty(detail)) return 0;
        int idx = detail.IndexOf(key + "=", StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + key.Length + 1;
        int end = detail.IndexOf(';', start);
        string span = end < 0 ? detail.Substring(start) : detail.Substring(start, end - start);
        return double.TryParse(span, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static long ParseLong(string? detail, string key)
    {
        if (string.IsNullOrEmpty(detail)) return 0;
        int idx = detail.IndexOf(key + "=", StringComparison.Ordinal);
        if (idx < 0) return 0;
        int start = idx + key.Length + 1;
        int end = detail.IndexOf(';', start);
        string span = end < 0 ? detail.Substring(start) : detail.Substring(start, end - start);
        return long.TryParse(span, out long v) ? v : 0;
    }

    public Summary? Observe(HistoryTiming.Entry e)
    {
        lock (_gate)
        {
            if (e.Stage == "scope-refresh" && e.Event == "begin")
            {
                if (_pending.Count >= 128) { _pending.Remove(_pending.Keys.First()); _evicted++; }
                _pending[e.Refresh] = new();
            }
            if (!_pending.TryGetValue(e.Refresh, out var p)) return null;
            if (e.Stage == "cache")
            {
                if (e.Event is "miss" or "partial-hit" or "stored-hit-decoded" or "live-hit" or "local-only")
                    p.Cache[e.Event] = p.Cache.GetValueOrDefault(e.Event) + 1;
                if (e.Event is "lock-acquired" or "lock-acquired-publication")
                    p.LockWaitMs += ParseDouble(e.Detail, "waitMs");
                if (e.Event is "lock-released" or "lock-released-publication")
                    p.LockHoldMs += ParseDouble(e.Detail, "holdMs");
                if (e.Event is "live-decoded" or "stored-hit-decoded" or "prefix-decoded")
                {
                    p.DecodedPoints += ParseLong(e.Detail, "points");
                    if (e.Event is "stored-hit-decoded" or "prefix-decoded") p.DecodedBlocks++;
                }
            }
            if (e.Stage == "range-rpc" && e.Event == "begin") p.Rpcs++;
            if (e.Stage is "configuration" or "cache" or "range-rpc" or "series" && e.Event == "end")
                p.Work[e.Stage] = p.Work.GetValueOrDefault(e.Stage) + e.ElapsedMs;
            if (e.Stage == "series" && e.Event is "transformed" or "sampled")
                p.Work[e.Event] = p.Work.GetValueOrDefault(e.Event) + e.StepMs;
            if (e.Stage != "scope-refresh") return null;
            if (e.Event == "range") p.Range = e.Detail;
            if (e.Event is "complete" or "cancelled" or "error") p.Outcome = e.Event;
            if (e.Event != "end") return null;
            _pending.Remove(e.Refresh);
            bool isZeroRpc = p.Rpcs == 0;
            return new("refresh", e.Utc, e.Refresh, e.Node, p.Range, p.Outcome, e.ElapsedMs, p.Cache, p.Work, p.Rpcs, _evicted,
                isZeroRpc, p.LockWaitMs, p.LockHoldMs, p.DecodedPoints, p.DecodedBlocks);
        }
    }
}

/// <summary>Process-lifetime, opt-in baseline logger. Never calls blocking output on the UI thread.</summary>
public static class UiPerformanceDiagnostics
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MADTOM_UI_TIMING") == "1";
    private static readonly Lazy<State> Instance = new(() => new State());
    public static Func<(long LiveBytes, long StoredBytes, long LivePoints, long StoredPoints, int SeriesCount, int LiveBlocks, int StoredBlocks)>? CacheStatsProvider { get; set; }
    public static void Observe(HistoryTiming.Entry entry)
    {
        if (!Enabled) return;
        var state = Instance.Value;
        var summary = state.Refreshes.Observe(entry);
        if (summary != null) state.Write(summary);
    }
    public static void Record(string name, double ms)
    { if (Enabled) Instance.Value.Window.Add(name, ms); }
    public static IDisposable? Measure(string name) => Enabled ? new Measurement(Instance.Value, name) : null;
    private sealed class Measurement : IDisposable
    {
        private readonly State _state;
        private readonly string _name;
        private readonly long _start = Stopwatch.GetTimestamp(), _bytes = GC.GetAllocatedBytesForCurrentThread();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _disposed;
        public Measurement(State state, string name) { _state = state; _name = name; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _state.Window.Add(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
                _thread == Environment.CurrentManagedThreadId ? GC.GetAllocatedBytesForCurrentThread() - _bytes : 0);
        }
    }
    private sealed class State
    {
        public readonly UiTimingWindow Window = new();
        public readonly UiRefreshSummaryCollector Refreshes = new();
        private readonly Channel<object> _output = Channel.CreateBounded<object>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly Timer _timer;
        private long _dropped, _lastReport = Stopwatch.GetTimestamp(), _allocated = GC.GetTotalAllocatedBytes();
        private readonly int[] _collections = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        private int _pending, _ticking;
        public State()
        {
            // Do not retain the initiating refresh's AsyncLocal context in process-lifetime workers.
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var record in _output.Reader.ReadAllAsync())
                        try { Console.Error.WriteLine("[ui-performance] " + JsonSerializer.Serialize(record)); }
                        catch { Interlocked.Increment(ref _dropped); }
                });
                _timer = new Timer(Tick, null, 250, 250);
            }
        }
        public void Write(object record)
        { if (!_output.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped); }
        private void Tick(object? unused)
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try
            {
                // At most one probe waits in the dispatcher, even during a long stall.
                if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
                {
                    long posted = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() =>
                    {
                        Window.Add("dispatcher.normal-wait", Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                        Volatile.Write(ref _pending, 0);
                    }, DispatcherPriority.Normal);
                }
                double seconds = Stopwatch.GetElapsedTime(_lastReport).TotalSeconds;
                if (seconds < 5) return;
                _lastReport = Stopwatch.GetTimestamp();
                long allocated = GC.GetTotalAllocatedBytes();
                var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - _collections[i]).ToArray();
                for (int i = 0; i < 3; i++) _collections[i] += collections[i];
                using var process = Process.GetCurrentProcess();
                var cacheStats = CacheStatsProvider?.Invoke();
                Write(new { Kind = "interval", Utc = DateTime.UtcNow, Seconds = seconds,
                    Metrics = Window.Drain(), AllocatedBytes = allocated - _allocated,
                    GcCollections = collections, ManagedHeapBytes = GC.GetTotalMemory(false), WorkingSetBytes = process.WorkingSet64,
                    DispatcherProbePending = Volatile.Read(ref _pending) != 0, DroppedRecords = Interlocked.Read(ref _dropped),
                    LiveBytes = cacheStats?.LiveBytes ?? 0, StoredBytes = cacheStats?.StoredBytes ?? 0,
                    LivePoints = cacheStats?.LivePoints ?? 0, StoredPoints = cacheStats?.StoredPoints ?? 0,
                    SeriesCount = cacheStats?.SeriesCount ?? 0, LiveBlocks = cacheStats?.LiveBlocks ?? 0, StoredBlocks = cacheStats?.StoredBlocks ?? 0 });
                _allocated = allocated;
            }
            catch { Interlocked.Increment(ref _dropped); }
            finally { Volatile.Write(ref _ticking, 0); }
        }
    }
}
