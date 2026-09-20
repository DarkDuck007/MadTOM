using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MadTOM.Services;

/// <summary>Opt-in history timings. No sample values are recorded.</summary>
public sealed class HistoryTiming : IDisposable
{
    public sealed record Entry(DateTime Utc, string Refresh, string Span, string? Parent,
        string Stage, string Node, string Metric, string Event, double ElapsedMs, double StepMs, string? Detail);
    private static readonly AsyncLocal<HistoryTiming?> Current = new();
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("MADTOM_HISTORY_TIMING") == "1";
    private static readonly Lazy<Channel<Entry>> Output = new(() =>
    {
        var channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _ = Task.Run(async () =>
        {
            await foreach (var entry in channel.Reader.ReadAllAsync())
            {
                try { Console.Error.WriteLine("[history-timing] " + JsonSerializer.Serialize(entry)); }
                catch { /* Diagnostics must never fail a graph request. */ }
            }
        });
        return channel;
    });
    public string RefreshId => _refresh;
    private static long _dropped;
    public static long DroppedEntries => Interlocked.Read(ref _dropped);
    private readonly HistoryTiming? _parent;
    private readonly Action<Entry> _sink;
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _last;
    private readonly string _refresh, _span = Guid.NewGuid().ToString("N"), _stage, _node, _metric;
    private bool _disposed;

    private HistoryTiming(string stage, string node, string metric, Action<Entry> sink, bool newRefresh)
    {
        _parent = Current.Value; _sink = sink; _stage = stage; _node = node; _metric = metric;
        _refresh = !newRefresh && _parent != null ? _parent._refresh : _span;
        _last = _start; Current.Value = this;
        Mark("begin");
    }
    public static HistoryTiming? Begin(string stage, string node = "", string metric = "",
        Action<Entry>? sink = null, bool newRefresh = false)
    {
        sink ??= Current.Value?._sink;
        if (sink == null && !Enabled && !UiPerformanceDiagnostics.Enabled) return null;
        return new(stage, node, metric, sink ?? Write, newRefresh);
    }
    private static void Write(Entry entry)
    {
        UiPerformanceDiagnostics.Observe(entry);
        if (!Enabled) return;
        if (!Output.Value.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
    }
    public void Mark(string name, string? detail = null)
    {
        long now = Stopwatch.GetTimestamp();
        var entry = new Entry(DateTime.UtcNow, _refresh, _span, _parent?._span, _stage, _node, _metric,
            name, Stopwatch.GetElapsedTime(_start, now).TotalMilliseconds,
            Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds, detail);
        _last = now;
        try { _sink(entry); } catch { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Mark("end", $"droppedEntries={DroppedEntries}");
        Current.Value = _parent;
    }
}
