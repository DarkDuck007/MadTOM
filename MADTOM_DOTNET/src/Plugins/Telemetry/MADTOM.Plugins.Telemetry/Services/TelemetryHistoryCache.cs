using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MadTOM.Services;

/// <summary>Session-owned numeric history. Keys include collector identity; no telemetry is written to disk.</summary>
public sealed class TelemetryHistoryCache
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<(string Collector, string Node, string Metric), Series> _live = new();
    private readonly List<RemoteRange> _remote = new();
    private sealed class Series
    {
        public Queue<LODPoint> Points { get; } = new();
        public long Latest;
    }
    private int _retentionMinutes;
    private long _generation;
    private const long Second = 1_000_000_000;
    private sealed record RemoteRange((string Collector, string Node, string Metric) Key,
        long Start, long End, DateTime Expires, LODPoint[] Points);

    public TelemetryHistoryCache(int retentionMinutes = 60, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _retentionMinutes = Math.Clamp(retentionMinutes, 1, 1440);
    }

    public int RetentionMinutes
    {
        get { lock (_gate) return _retentionMinutes; }
        set
        {
            if (value is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { _retentionMinutes = value; _generation++; _remote.Clear(); PruneLocked(); }
        }
    }

    public void Record(string collector, string node, long timestamp, IEnumerable<KeyValuePair<string, double>> values)
    {
        lock (_gate)
        {
            long cutoff = Cutoff();
            if (timestamp < cutoff || timestamp > Nano(_utcNow().AddMinutes(1))) return;
            foreach (var (metric, value) in values)
            {
                if (!double.IsFinite(value)) continue;
                var key = Key(collector, node, metric);
                if (!_live.TryGetValue(key, out var series)) _live[key] = series = new();
                var points = series.Points;
                // Live streams are monotonic. Ignore replay/duplicates without retaining extra objects.
                if (points.Count > 0 && series.Latest >= timestamp) continue;
                points.Enqueue(new(timestamp, value, value, value));
                series.Latest = timestamp;
                while (points.Count > 0 && points.Peek().TimestampUnixNano < cutoff) points.Dequeue();
            }
        }
    }

    public void Prune() { lock (_gate) PruneLocked(); }
    public void Clear() { lock (_gate) { _generation++; _live.Clear(); _remote.Clear(); } }

    public string Estimate(int minutes)
    {
        lock (_gate)
        {
            PruneLocked();
            double projected = 0;
            long current = _remote.Sum(r => (long)r.Points.Length * 32);
            foreach (var series in _live.Values)
            {
                var points = series.Points;
                current += (long)points.EnsureCapacity(0) * 32;
                double seconds = points.Count > 1 ? (series.Latest - points.Peek().TimestampUnixNano) / (double)Second : 0;
                double rate = seconds > 0 ? (points.Count - 1) / seconds : 1;
                projected += Math.Ceiling(minutes * 60 * rate) * 32 * 1.5 + 256;
            }
            return _live.Count == 0
                ? "Waiting for telemetry. Estimate uses observed metric count and sample rate."
                : $"Estimated at {minutes} min: ~{projected / 1048576:F1} MiB; current buffers: ~{current / 1048576.0:F1} MiB ({_live.Count} series). Approximate, excludes charts/runtime.";
        }
    }

    public async Task<IReadOnlyList<LODPoint>> QueryAsync(string collector, string node, string metric,
        DateTime start, DateTime end, bool localOnly,
        Func<DateTime, DateTime, CancellationToken, Task<IReadOnlyList<LODPoint>>> fetch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long from = Nano(start), to = Nano(end);
        if (from > to) return Array.Empty<LODPoint>();
        var key = Key(collector, node, metric);
        LODPoint[] local;
        long generation;
        lock (_gate)
        {
            PruneLocked();
            generation = _generation;
            local = ReadLocked(key, from, to);
            if (localOnly || (_live.TryGetValue(key, out var series) && Covers(series.Points.ToArray(), from, to))) return local;
            var cached = _remote.LastOrDefault(r => r.Key == key && r.Start <= from && (r.End >= to || (_live.TryGetValue(key, out var tail) && Covers(tail.Points.ToArray(), r.End, to))));
            if (cached != null) return Merge(cached.Points, local, from, to);
        }

        // Broader minute-aligned starts allow nearby navigation requests to reuse historical results.
        DateTime fetchStart = new DateTime(start.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        IReadOnlyList<LODPoint> remote;
        bool fetchSucceeded = true;
        try { remote = await fetch(fetchStart, end, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch when (local.Length > 0) { fetchSucceeded = false; remote = Array.Empty<LODPoint>(); }
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Clear/shrink wins over an older in-flight query, including what it returns to a view.
            if (generation != _generation) return ReadLocked(key, from, to);
            if (fetchSucceeded && remote.Count <= 10000)
            {
                _remote.Add(new(key, Nano(fetchStart), to, _utcNow().AddSeconds(30), remote.ToArray()));
                if (_remote.Count > 128) _remote.RemoveAt(0);
            }
            return Merge(remote, ReadLocked(key, from, to), from, to);
        }
    }

    private static bool Covers(LODPoint[] points, long start, long end)
    {
        if (points.Length == 0 || points[0].TimestampUnixNano > start || end - points[^1].TimestampUnixNano > 5 * Second) return false;
        for (int i = 1; i < points.Length; i++)
            if (points[i].TimestampUnixNano >= start && points[i - 1].TimestampUnixNano <= end && points[i].TimestampUnixNano - points[i - 1].TimestampUnixNano > 5 * Second) return false;
        return true;
    }

    private LODPoint[] ReadLocked((string Collector, string Node, string Metric) key, long start, long end) =>
        _live.TryGetValue(key, out var series) ? series.Points.Where(p => p.TimestampUnixNano >= start && p.TimestampUnixNano <= end).ToArray() : Array.Empty<LODPoint>();

    private static LODPoint[] Merge(IEnumerable<LODPoint> remote, IEnumerable<LODPoint> local, long start, long end)
    {
        var points = new SortedDictionary<long, LODPoint>();
        foreach (var p in remote.Concat(local))
            if (p.TimestampUnixNano >= start && p.TimestampUnixNano <= end) points[p.TimestampUnixNano] = p;
        return points.Values.ToArray();
    }

    private void PruneLocked()
    {
        long cutoff = Cutoff();
        foreach (var (key, series) in _live.ToArray())
        {
            var points = series.Points;
            while (points.Count > 0 && points.Peek().TimestampUnixNano < cutoff) points.Dequeue();
            if (points.Count == 0) _live.Remove(key);
            else if (points.Count < points.EnsureCapacity(0) / 2) points.TrimExcess();
        }
        _remote.RemoveAll(r => r.Expires <= _utcNow());
    }
    private long Cutoff() => Nano(_utcNow().AddMinutes(-_retentionMinutes));
    private static long Nano(DateTime value) => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;
    private static (string, string, string) Key(string collector, string node, string metric) => (collector.Trim().ToLowerInvariant(), node, metric.ToLowerInvariant());
}
