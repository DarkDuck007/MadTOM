using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MadTOM.Services;

/// <summary>Session-owned numeric history. Keys include collector identity; no telemetry is written to disk.</summary>
public sealed class TelemetryHistoryCache
{
    public const int MaxStoredQueryPoints = 64 * 1024;
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<(string Collector, string Node, string Metric), Series> _live = new();
    private readonly List<RemoteRange> _remote = new();
    private sealed class Series
    {
        public CompressedPointHistory Points { get; set; } = new();
        public long Latest;
    }
    private int _retentionMinutes;
    private long _generation;
    private const long Second = 1_000_000_000;
    private sealed record RemoteRange((string Collector, string Node, string Metric) Key,
        long Start, long End, DateTime Expires, CompressedPointBlock Points, int PointBudget);

    public TelemetryHistoryCache(int retentionMinutes = 60, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _retentionMinutes = Math.Clamp(retentionMinutes, 1, 1440);
    }

    private long _liveLimitBytes = 64L * 1048576;
    private long _storedLimitBytes = 32L * 1048576;
    private int _storedRetentionSeconds = 30;
    public long LiveLimitBytes { get { lock (_gate) return _liveLimitBytes; } }
    public long StoredLimitBytes { get { lock (_gate) return _storedLimitBytes; } }
    public int StoredRetentionSeconds { get { lock (_gate) return _storedRetentionSeconds; } }

    public void Configure(int minutes, long liveBytes, long storedBytes, int storedSeconds)
    {
        if (minutes is < 1 or > 1440 || liveBytes < 1 || storedBytes < 1 || storedSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(minutes));
        lock (_gate)
        {
            _generation++;
            _retentionMinutes = minutes;
            _liveLimitBytes = liveBytes;
            _storedLimitBytes = storedBytes;
            if (_storedRetentionSeconds != storedSeconds) _remote.Clear();
            _storedRetentionSeconds = storedSeconds;
            PruneLocked();
            EnforceLimitsLocked();
        }
    }

    public sealed record Usage(long LiveBytes, long StoredBytes, long LivePoints, int SeriesCount, int StoredRanges,
        long StoredPoints, double ProjectedLiveBytes, long LiveZstdBytes = 0, long StoredZstdBytes = 0,
        long LiveZstdRawBytes = 0, long StoredZstdRawBytes = 0)
    {
        public long TotalBytes => LiveBytes + StoredBytes;
    }
    private static long KeyBytes((string Collector, string Node, string Metric) key) =>
        256L + 2L * (key.Collector.Length + key.Node.Length + key.Metric.Length);
    private long LiveBytesLocked() => _live.Sum(pair => KeyBytes(pair.Key) + pair.Value.Points.StorageBytes);
    private static long RangeBytes(RemoteRange range) => KeyBytes(range.Key) + range.Points.StorageBytes;
    public Usage GetUsage(int minutes)
    {
        lock (_gate)
        {
            PruneLocked();
            double projected = 0;
            foreach (var (key, series) in _live)
            {
                double seconds = series.Points.Count > 1 ? (series.Latest - series.Points.FirstTimestamp) / (double)Second : 0;
                double rate = seconds > 0 ? (series.Points.Count - 1) / seconds : 1;
                projected += Math.Ceiling(minutes * 60 * rate) * series.Points.StorageBytes / Math.Max(1.0, series.Points.Count) + KeyBytes(key);
            }
            return new(LiveBytesLocked(), _remote.Sum(RangeBytes), _live.Sum(p => (long)p.Value.Points.Count),
                _live.Count, _remote.Count, _remote.Sum(r => (long)r.Points.Count), projected,
                _live.Sum(p => p.Value.Points.CompressedBytes), _remote.Sum(r => r.Points.CompressedBytes),
                _live.Sum(p => p.Value.Points.CompressedRawBytes), _remote.Where(r => r.Points.IsCompressed).Sum(r => r.Points.RawBytes));
        }
    }
    public void ClearLive() { lock (_gate) { _generation++; _live.Clear(); } }
    public void ClearStored() { lock (_gate) { _generation++; _remote.Clear(); } }

    private void EnforceLimitsLocked()
    {
        long bytes = LiveBytesLocked();
        if (bytes > _liveLimitBytes)
        {
            var oldest = new PriorityQueue<(string Collector, string Node, string Metric), long>();
            foreach (var (key, series) in _live)
                if (series.Points.Count > 0) oldest.Enqueue(key, series.Points.FirstTimestamp);
            while (bytes > _liveLimitBytes * 3 / 4 && oldest.TryDequeue(out var key, out _))
            {
                var series = _live[key];
                bytes -= series.Points.StorageBytes;
                series.Points.DropOldestBlock();
                bytes += series.Points.StorageBytes;
                if (series.Points.Count == 0) { bytes -= KeyBytes(key); _live.Remove(key); }
                else oldest.Enqueue(key, series.Points.FirstTimestamp);
            }
        }
        long remoteBytes = _remote.Sum(RangeBytes);
        while (_remote.Count > 0 && remoteBytes > _storedLimitBytes)
        {
            remoteBytes -= RangeBytes(_remote[0]);
            _remote.RemoveAt(0);
        }
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
                points.RemoveBefore(cutoff);
            }
            EnforceLimitsLocked();
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
            long current = _remote.Sum(RangeBytes);
            foreach (var series in _live.Values)
            {
                var points = series.Points;
                current += points.StorageBytes;
                double seconds = points.Count > 1 ? (series.Latest - points.FirstTimestamp) / (double)Second : 0;
                double rate = seconds > 0 ? (points.Count - 1) / seconds : 1;
                projected += Math.Ceiling(minutes * 60 * rate) * points.StorageBytes / Math.Max(1.0, points.Count) + 256;
            }
            return _live.Count == 0
                ? "Waiting for telemetry. Estimate uses observed metric count and sample rate."
                : $"Estimated at {minutes} min: ~{projected / 1048576:F1} MiB; current buffers: ~{current / 1048576.0:F1} MiB ({_live.Count} series). Approximate, excludes charts/runtime.";
        }
    }

    public async Task<IReadOnlyList<LODPoint>> QueryAsync(string collector, string node, string metric,
        DateTime start, DateTime end, bool localOnly,
        Func<DateTime, DateTime, CancellationToken, Task<IReadOnlyList<LODPoint>>> fetch, CancellationToken ct = default, int targetPoints = 0)
    {
        ct.ThrowIfCancellationRequested();
        long from = Nano(start), to = Nano(end);
        if (from > to) return Array.Empty<LODPoint>();
        var key = Key(collector, node, metric);
        LODPoint[] local;
        RemoteRange? prefix = null;
        LODPoint[] prefixPoints = Array.Empty<LODPoint>();
        long generation;
        lock (_gate)
        {
            PruneLocked();
            generation = _generation;
            local = ReadLocked(key, from, to);
            if (localOnly || (_live.TryGetValue(key, out var series) && series.Points.Covers(from, to))) return local;
            var cached = _remote.LastOrDefault(r => r.Key == key && HasResolution(r, from, to, targetPoints) &&
                r.Start <= from && (r.End >= to || (_live.TryGetValue(key, out var tail) && tail.Points.Covers(r.End, to))));
            if (cached != null) { _remote.Remove(cached); _remote.Add(cached); return Merge(cached.Points.Decode(), local, from, to); }
            // A rolling scope has a later end on every visit. A slow/missing live
            // stream must not force a refetch of the already cached historical prefix.
            prefix = _remote.Where(r => r.Key == key && HasResolution(r, from, to, targetPoints) &&
                r.Start <= from && r.End >= from && r.End < to).MaxBy(r => r.End);
            if (prefix != null)
            {
                prefixPoints = prefix.Points.Decode();
                _remote.Remove(prefix); _remote.Add(prefix);
            }
        }

        // Broader minute-aligned starts allow nearby navigation requests to reuse historical results.
        DateTime fetchStart = targetPoints > 0 ? start : new DateTime(start.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (prefix != null) fetchStart = DateTime.UnixEpoch.AddTicks(prefix.End / 100);
        IReadOnlyList<LODPoint> remote;
        bool fetchSucceeded = true;
        try { remote = await fetch(fetchStart, end, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch when (local.Length > 0 || prefixPoints.Length > 0) { fetchSucceeded = false; remote = Array.Empty<LODPoint>(); }
        if (prefix != null) remote = Merge(prefixPoints, remote, from, to);
        ct.ThrowIfCancellationRequested();
        // The per-result admission ceiling is 64K points; total retention follows
        // the configured compressed-byte budget. Compression must not hold _gate.
        CompressedPointBlock? candidate = fetchSucceeded && remote.Count <= MaxStoredQueryPoints
            ? new CompressedPointBlock(remote.ToArray()) : null;
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Clear/shrink wins over an older in-flight query, including what it returns to a view.
            if (generation != _generation) return ReadLocked(key, from, to);
            if (candidate != null)
            {
                // Refreshing a tail does not renew the freshness of the older prefix.
                var expires = prefix?.Expires ?? _utcNow().AddSeconds(_storedRetentionSeconds);
                var range = new RemoteRange(key, prefix != null ? from : Nano(fetchStart), to,
                    expires, candidate, targetPoints);
                // A result that cannot fit by itself must not evict the entire cache.
                if (range.Expires > _utcNow() && RangeBytes(range) <= _storedLimitBytes)
                {
                    _remote.RemoveAll(r => r.Expires <= _utcNow());
                    // Coalesced fetches have multiple callers: retain one result for
                    // an identical interval and never replace a finer result with a
                    // later-finishing coarser request.
                    bool alreadyCovered = _remote.Any(r => r.Key == key && r.Start == range.Start &&
                        r.End == range.End && r.PointBudget >= range.PointBudget);
                    if (!alreadyCovered)
                    {
                        _remote.RemoveAll(r => r.Key == key && r.Start == range.Start && r.End == range.End);
                        // Extend the same rolling entry instead of accumulating a
                        // near-duplicate on every visit. Preserve a finer source entry.
                        if (prefix != null && prefix.PointBudget == targetPoints) _remote.Remove(prefix);
                        _remote.Add(range);
                        EnforceLimitsLocked();
                    }
                }
            }
            return Merge(remote, ReadLocked(key, from, to), from, to);
        }
    }

    private static bool HasResolution(RemoteRange range, long start, long end, int targetPoints) =>
        targetPoints <= 0 || (range.PointBudget >= targetPoints &&
            (decimal)range.PointBudget * Math.Max(1m, (decimal)end - start) >=
            (decimal)targetPoints * Math.Max(1m, (decimal)range.End - range.Start));

    private LODPoint[] ReadLocked((string Collector, string Node, string Metric) key, long start, long end) =>
        _live.TryGetValue(key, out var series) ? series.Points.Read(start, end).ToArray() : Array.Empty<LODPoint>();

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
            points.RemoveBefore(cutoff);
            if (points.Count == 0) _live.Remove(key);

        }
        _remote.RemoveAll(r => r.Expires <= _utcNow());
        // Expiring part of a sealed block can materialize a raw head. Recheck budgets.
        EnforceLimitsLocked();
    }
    private long Cutoff() => Nano(_utcNow().AddMinutes(-_retentionMinutes));
    private static long Nano(DateTime value) => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;
    private static (string, string, string) Key(string collector, string node, string metric) => (collector.Trim().ToLowerInvariant(), node, metric.ToLowerInvariant());
}
