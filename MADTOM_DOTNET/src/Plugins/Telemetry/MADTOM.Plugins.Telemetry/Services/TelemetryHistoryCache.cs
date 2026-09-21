using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly List<(string Collector, string Node, string Metric)> _deadKeysBuffer = new();
    private DateTime _lastPruneUtc = DateTime.MinValue;
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

    private long _liveStorageBytes;
    private long _livePoints;
    private int _liveBlocks;
    private long _liveCompressedBytes;
    private long _liveCompressedRawBytes;

    private long _storedStorageBytes;
    private long _storedPoints;
    private int _storedBlocks;
    private long _storedCompressedBytes;
    private long _storedCompressedRawBytes;

    public long LiveLimitBytes { get { lock (_gate) return _liveLimitBytes; } }
    public long StoredLimitBytes { get { lock (_gate) return _storedLimitBytes; } }
    public int StoredRetentionSeconds { get { lock (_gate) return _storedRetentionSeconds; } }

    public (long LiveBytes, long StoredBytes, long LivePoints, long StoredPoints, int SeriesCount, int LiveBlocks, int StoredBlocks) CompactStats
    {
        get
        {
            lock (_gate)
            {
                return (_liveStorageBytes, _storedStorageBytes, _livePoints, _storedPoints, _live.Count, _liveBlocks, _storedBlocks);
            }
        }
    }

    private static long KeyBytes((string Collector, string Node, string Metric) key) =>
        256L + 2L * (key.Collector.Length + key.Node.Length + key.Metric.Length);
    private static long RangeBytes(RemoteRange range) => KeyBytes(range.Key) + range.Points.StorageBytes;

    private void RemoveSeriesLocked((string Collector, string Node, string Metric) key, Series series)
    {
        _liveStorageBytes -= KeyBytes(key) + series.Points.StorageBytes;
        _livePoints -= series.Points.Count;
        _liveBlocks -= series.Points.BlocksCount;
        _liveCompressedBytes -= series.Points.CompressedBytes;
        _liveCompressedRawBytes -= series.Points.CompressedRawBytes;
        _live.Remove(key);
    }

    private void UpdateSeriesDeltaLocked(Series series, Action action)
    {
        long oldStorage = series.Points.StorageBytes;
        long oldCount = series.Points.Count;
        int oldBlocks = series.Points.BlocksCount;
        long oldComp = series.Points.CompressedBytes;
        long oldCompRaw = series.Points.CompressedRawBytes;

        action();

        _liveStorageBytes += (series.Points.StorageBytes - oldStorage);
        _livePoints += (series.Points.Count - oldCount);
        _liveBlocks += (series.Points.BlocksCount - oldBlocks);
        _liveCompressedBytes += (series.Points.CompressedBytes - oldComp);
        _liveCompressedRawBytes += (series.Points.CompressedRawBytes - oldCompRaw);
    }

    private void AddRemoteRangeLocked(RemoteRange range)
    {
        _remote.Add(range);
        _storedStorageBytes += RangeBytes(range);
        _storedPoints += range.Points.Count;
        _storedBlocks++;
        _storedCompressedBytes += range.Points.CompressedBytes;
        if (range.Points.IsCompressed) _storedCompressedRawBytes += range.Points.RawBytes;
    }

    private void RemoveRemoteRangeLocked(RemoteRange range)
    {
        _storedStorageBytes -= RangeBytes(range);
        _storedPoints -= range.Points.Count;
        _storedBlocks--;
        _storedCompressedBytes -= range.Points.CompressedBytes;
        if (range.Points.IsCompressed) _storedCompressedRawBytes -= range.Points.RawBytes;
    }

    private void ClearLiveLocked()
    {
        _live.Clear();
        _liveStorageBytes = 0;
        _livePoints = 0;
        _liveBlocks = 0;
        _liveCompressedBytes = 0;
        _liveCompressedRawBytes = 0;
    }

    private void ClearRemoteRangesLocked()
    {
        _remote.Clear();
        _storedStorageBytes = 0;
        _storedPoints = 0;
        _storedBlocks = 0;
        _storedCompressedBytes = 0;
        _storedCompressedRawBytes = 0;
    }

    private void PruneExpiredRemoteLocked(DateTime now)
    {
        for (int i = _remote.Count - 1; i >= 0; i--)
        {
            var r = _remote[i];
            if (r.Expires <= now)
            {
                _remote.RemoveAt(i);
                RemoveRemoteRangeLocked(r);
            }
        }
    }

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
            if (_storedRetentionSeconds != storedSeconds) ClearRemoteRangesLocked();
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

    public Usage GetUsage(int minutes)
    {
        lock (_gate)
        {
            if ((_utcNow() - _lastPruneUtc).TotalSeconds >= 5 || _utcNow() < _lastPruneUtc)
                PruneLocked();
            double projected = 0;
            foreach (var (key, series) in _live)
            {
                double seconds = series.Points.Count > 1 ? (series.Latest - series.Points.FirstTimestamp) / (double)Second : 0;
                double rate = seconds > 0 ? (series.Points.Count - 1) / seconds : 1;
                projected += Math.Ceiling(minutes * 60 * rate) * series.Points.StorageBytes / Math.Max(1.0, series.Points.Count) + KeyBytes(key);
            }
            return new(_liveStorageBytes, _storedStorageBytes, _livePoints,
                _live.Count, _remote.Count, _storedPoints, projected,
                _liveCompressedBytes, _storedCompressedBytes,
                _liveCompressedRawBytes, _storedCompressedRawBytes);
        }
    }

    public (long LiveStorage, long StoredStorage, long LivePoints, long StoredPoints, long LiveComp, long StoredComp, long LiveCompRaw, long StoredCompRaw, int LiveBlocks, int StoredBlocks) RecalculateSlow()
    {
        lock (_gate)
        {
            long liveStorage = _live.Sum(pair => KeyBytes(pair.Key) + pair.Value.Points.StorageBytes);
            long storedStorage = _remote.Sum(RangeBytes);
            long livePoints = _live.Sum(p => (long)p.Value.Points.Count);
            long storedPoints = _remote.Sum(r => (long)r.Points.Count);
            long liveComp = _live.Sum(p => p.Value.Points.CompressedBytes);
            long storedComp = _remote.Sum(r => r.Points.CompressedBytes);
            long liveCompRaw = _live.Sum(p => p.Value.Points.CompressedRawBytes);
            long storedCompRaw = _remote.Where(r => r.Points.IsCompressed).Sum(r => r.Points.RawBytes);
            int liveBlocks = _live.Sum(p => p.Value.Points.BlocksCount);
            int storedBlocks = _remote.Count;
            return (liveStorage, storedStorage, livePoints, storedPoints, liveComp, storedComp, liveCompRaw, storedCompRaw, liveBlocks, storedBlocks);
        }
    }

    public void ClearLive() { lock (_gate) { _generation++; ClearLiveLocked(); } }
    public void ClearStored() { lock (_gate) { _generation++; ClearRemoteRangesLocked(); } }

    private void EnforceLimitsLocked()
    {
        if (_liveStorageBytes > _liveLimitBytes)
        {
            var oldest = new PriorityQueue<(string Collector, string Node, string Metric), long>();
            foreach (var (key, series) in _live)
                if (series.Points.Count > 0) oldest.Enqueue(key, series.Points.FirstTimestamp);
            while (_liveStorageBytes > _liveLimitBytes * 3 / 4 && oldest.TryDequeue(out var key, out _))
            {
                if (!_live.TryGetValue(key, out var series)) continue;
                UpdateSeriesDeltaLocked(series, () => series.Points.DropOldestBlock());
                if (series.Points.Count == 0) RemoveSeriesLocked(key, series);
                else oldest.Enqueue(key, series.Points.FirstTimestamp);
            }
        }
        while (_remote.Count > 0 && _storedStorageBytes > _storedLimitBytes)
        {
            var r = _remote[0];
            _remote.RemoveAt(0);
            RemoveRemoteRangeLocked(r);
        }
    }

    public int RetentionMinutes
    {
        get { lock (_gate) return _retentionMinutes; }
        set
        {
            if (value is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { _retentionMinutes = value; _generation++; ClearRemoteRangesLocked(); PruneLocked(); }
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
                if (!_live.TryGetValue(key, out var series))
                {
                    _live[key] = series = new();
                    _liveStorageBytes += KeyBytes(key) + series.Points.StorageBytes;
                }
                var points = series.Points;
                if (points.Count > 0 && series.Latest >= timestamp) continue;

                long oldStorage = points.StorageBytes;
                long oldCount = points.Count;
                int oldBlocks = points.BlocksCount;
                long oldComp = points.CompressedBytes;
                long oldCompRaw = points.CompressedRawBytes;

                points.Enqueue(new(timestamp, value, value, value));
                series.Latest = timestamp;

                if (points.FirstTimestamp < cutoff)
                {
                    points.RemoveBefore(cutoff);
                }

                _liveStorageBytes += (points.StorageBytes - oldStorage);
                _livePoints += (points.Count - oldCount);
                _liveBlocks += (points.BlocksCount - oldBlocks);
                _liveCompressedBytes += (points.CompressedBytes - oldComp);
                _liveCompressedRawBytes += (points.CompressedRawBytes - oldCompRaw);
            }
            EnforceLimitsLocked();
        }
    }

    public void Prune() { lock (_gate) PruneLocked(); }
    public void Clear() { lock (_gate) { _generation++; ClearLiveLocked(); ClearRemoteRangesLocked(); } }

    public string Estimate(int minutes)
    {
        lock (_gate)
        {
            if ((_utcNow() - _lastPruneUtc).TotalSeconds >= 5 || _utcNow() < _lastPruneUtc)
                PruneLocked();
            double projected = 0;
            long current = _storedStorageBytes;
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
        using var timing = HistoryTiming.Begin("cache", node, metric);
        ct.ThrowIfCancellationRequested();
        long from = Nano(start), to = Nano(end);
        if (from > to) return Array.Empty<LODPoint>();
        var key = Key(collector, node, metric);

        CompressedPointHistory.HistorySnapshot liveSnapshot;
        CompressedPointBlock? cachedBlock = null;
        CompressedPointBlock? prefixBlock = null;
        DateTime prefixExpires = default;
        long cachedEndNano = 0;
        long prefixEnd = 0;
        int prefixBudget = 0;
        long generation;
        bool isLocalOnlyOrCovered = false;

        long lockWaitStart = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            double lockWaitMs = Stopwatch.GetElapsedTime(lockWaitStart).TotalMilliseconds;
            long lockHoldStart = Stopwatch.GetTimestamp();
            timing?.Mark("lock-acquired", $"waitMs={lockWaitMs:F2}");

            long cutoff = Cutoff();
            if (_live.TryGetValue(key, out var series))
            {
                if (series.Points.Count > 0 && series.Points.FirstTimestamp < cutoff)
                {
                    UpdateSeriesDeltaLocked(series, () => series.Points.RemoveBefore(cutoff));
                    if (series.Points.Count == 0)
                    {
                        RemoveSeriesLocked(key, series);
                        series = null;
                    }
                }
            }

            generation = _generation;
            liveSnapshot = series != null ? series.Points.Snapshot(from, to) : default;

            if (timing != null)
            {
                timing.Mark("live-timestamps", $"requestedStartNano={from}; requestedEndNano={to}; firstLiveNano={(series?.Points.Count > 0 ? series.Points.FirstTimestamp : 0)}; latestLiveNano={series?.Latest ?? 0}");
                LogCoverage(timing, series?.Points, from, to, "full");
            }

            if (localOnly || (series != null && series.Points.Covers(from, to)))
            {
                isLocalOnlyOrCovered = true;
                double lockHoldMs = Stopwatch.GetElapsedTime(lockHoldStart).TotalMilliseconds;
                timing?.Mark("lock-released", $"holdMs={lockHoldMs:F2}");
            }
            else
            {
                var nowUtc = _utcNow();
                for (int i = _remote.Count - 1; i >= 0; i--)
                {
                    var r = _remote[i];
                    if (r.Expires <= nowUtc) continue;
                    if (r.Key == key && HasResolution(r, from, to, targetPoints) && r.Start <= from &&
                        (r.End >= to || (series != null && series.Points.Covers(r.End, to))))
                    {
                        _remote.RemoveAt(i);
                        _remote.Add(r);
                        cachedBlock = r.Points;
                        cachedEndNano = r.End;
                        break;
                    }
                }

                if (cachedBlock == null)
                {
                    RemoteRange? prefixRange = null;
                    for (int i = 0; i < _remote.Count; i++)
                    {
                        var r = _remote[i];
                        if (r.Expires <= nowUtc) continue;
                        if (r.Key == key && HasResolution(r, from, to, targetPoints) &&
                            r.Start <= from && r.End >= from && r.End < to)
                        {
                            if (prefixRange == null || r.End > prefixRange.End) prefixRange = r;
                        }
                    }
                    if (prefixRange != null)
                    {
                        _remote.Remove(prefixRange);
                        _remote.Add(prefixRange);
                        prefixBlock = prefixRange.Points;
                        prefixExpires = prefixRange.Expires;
                        prefixEnd = prefixRange.End;
                        prefixBudget = prefixRange.PointBudget;
                        if (timing != null)
                        {
                            LogCoverage(timing, series?.Points, prefixRange.End, to, "tail");
                        }
                    }
                }

                double lockHoldMs = Stopwatch.GetElapsedTime(lockHoldStart).TotalMilliseconds;
                timing?.Mark("lock-released", $"holdMs={lockHoldMs:F2}");
            }
        }

        var local = liveSnapshot.Decode(from, to);
        timing?.Mark("live-decoded", $"points={local.Length}");

        if (isLocalOnlyOrCovered)
        {
            timing?.Mark(localOnly ? "local-only" : "live-hit");
            return local;
        }

        if (cachedBlock != null)
        {
            var decoded = cachedBlock.Decode();
            timing?.Mark("stored-hit-decoded", $"points={decoded.Length}");
            timing?.Mark("stored-timestamps", $"cachedEndNano={cachedEndNano}; newestStoredNano={(decoded.Length > 0 ? decoded[^1].TimestampUnixNano : 0)}");
            var merged = Merge(decoded, local, from, to);
            timing?.Mark("merged", $"points={merged.Length}");
            return merged;
        }

        LODPoint[] prefixPoints = Array.Empty<LODPoint>();
        if (prefixBlock != null)
        {
            prefixPoints = prefixBlock.Decode();
            timing?.Mark("stored-timestamps", $"cachedEndNano={prefixEnd}; newestStoredNano={(prefixPoints.Length > 0 ? prefixPoints[^1].TimestampUnixNano : 0)}");
            timing?.Mark("prefix-decoded", $"points={prefixPoints.Length}");
        }

        DateTime fetchStart = targetPoints > 0 ? start : new DateTime(start.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (prefixBlock != null) fetchStart = DateTime.UnixEpoch.AddTicks(prefixEnd / 100);
        timing?.Mark(prefixBlock == null ? "miss" : "partial-hit", $"fetchStart={fetchStart:O}; fetchEnd={end:O}");

        IReadOnlyList<LODPoint> remote;
        bool fetchSucceeded = true;
        try { remote = await fetch(fetchStart, end, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { timing?.Mark("cancelled"); throw; }
        catch when (local.Length > 0 || prefixPoints.Length > 0) { timing?.Mark("fetch-failed-fallback"); fetchSucceeded = false; remote = Array.Empty<LODPoint>(); }
        timing?.Mark("fetch-finished", $"points={remote.Count}");

        if (prefixBlock != null) remote = Merge(prefixPoints, remote, from, to);
        timing?.Mark("prefix-merged", $"points={remote.Count}");
        ct.ThrowIfCancellationRequested();

        CompressedPointBlock? candidate = fetchSucceeded && remote.Count <= MaxStoredQueryPoints
            ? new CompressedPointBlock(remote.ToArray()) : null;
        timing?.Mark("encoded", candidate == null ? "not-admitted" : $"bytes={candidate.StorageBytes}");
        ct.ThrowIfCancellationRequested();

        CompressedPointHistory.HistorySnapshot latestLiveSnapshot;
        bool genMatch;
        long postLockWaitStart = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            double postLockWaitMs = Stopwatch.GetElapsedTime(postLockWaitStart).TotalMilliseconds;
            long postLockHoldStart = Stopwatch.GetTimestamp();
            timing?.Mark("lock-acquired-publication", $"waitMs={postLockWaitMs:F2}");
            genMatch = (generation == _generation);
            if (genMatch)
            {
                if (candidate != null)
                {
                    var expires = prefixBlock != null ? prefixExpires : _utcNow().AddSeconds(_storedRetentionSeconds);
                    var range = new RemoteRange(key, prefixBlock != null ? from : Nano(fetchStart), to,
                        expires, candidate, targetPoints);
                    if (range.Expires > _utcNow() && RangeBytes(range) <= _storedLimitBytes)
                    {
                        PruneExpiredRemoteLocked(_utcNow());
                        bool alreadyCovered = _remote.Any(r => r.Key == key && r.Start == range.Start &&
                            r.End == range.End && r.PointBudget >= range.PointBudget);
                        if (!alreadyCovered)
                        {
                            for (int i = _remote.Count - 1; i >= 0; i--)
                            {
                                if (_remote[i].Key == key && _remote[i].Start == range.Start && _remote[i].End == range.End)
                                {
                                    var r = _remote[i];
                                    _remote.RemoveAt(i);
                                    RemoveRemoteRangeLocked(r);
                                }
                            }
                            if (prefixBlock != null && prefixBudget == targetPoints)
                            {
                                for (int i = _remote.Count - 1; i >= 0; i--)
                                {
                                    if (ReferenceEquals(_remote[i].Points, prefixBlock))
                                    {
                                        var r = _remote[i];
                                        _remote.RemoveAt(i);
                                        RemoveRemoteRangeLocked(r);
                                        break;
                                    }
                                }
                            }
                            AddRemoteRangeLocked(range);
                            EnforceLimitsLocked();
                        }
                    }
                }
            }
            _live.TryGetValue(key, out var finalLiveSeries);
            latestLiveSnapshot = finalLiveSeries != null ? finalLiveSeries.Points.Snapshot(from, to) : default;
            double postLockHoldMs = Stopwatch.GetElapsedTime(postLockHoldStart).TotalMilliseconds;
            timing?.Mark("lock-released-publication", $"holdMs={postLockHoldMs:F2}");
        }

        var finalLocal = latestLiveSnapshot.Decode(from, to);
        if (!genMatch) return finalLocal;

        var result = Merge(remote, finalLocal, from, to);
        timing?.Mark("published", $"points={result.Length}");
        return result;
    }

    private static void LogCoverage(HistoryTiming timing, CompressedPointHistory? points, long start, long end, string interval)
    {
        var result = points?.InspectCoverage(start, end) ?? new CompressedPointHistory.Coverage(false, "empty", 0, 0, 0);
        timing.Mark("coverage", $"interval={interval}; startNano={start}; endNano={end}; covered={result.Covered}; reason={result.Reason}; gapNano={result.GapNano}; gapStartNano={result.GapStartNano}; gapEndNano={result.GapEndNano}; latestLiveNano={points?.LastTimestamp ?? 0}");
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
        _lastPruneUtc = _utcNow();
        long cutoff = Cutoff();
        _deadKeysBuffer.Clear();

        foreach (var (key, series) in _live)
        {
            long oldStorage = series.Points.StorageBytes;
            long oldCount = series.Points.Count;
            int oldBlocks = series.Points.BlocksCount;
            long oldComp = series.Points.CompressedBytes;
            long oldCompRaw = series.Points.CompressedRawBytes;

            series.Points.RemoveBefore(cutoff);

            _liveStorageBytes += (series.Points.StorageBytes - oldStorage);
            _livePoints += (series.Points.Count - oldCount);
            _liveBlocks += (series.Points.BlocksCount - oldBlocks);
            _liveCompressedBytes += (series.Points.CompressedBytes - oldComp);
            _liveCompressedRawBytes += (series.Points.CompressedRawBytes - oldCompRaw);

            if (series.Points.Count == 0) _deadKeysBuffer.Add(key);
        }

        for (int i = 0; i < _deadKeysBuffer.Count; i++)
        {
            var key = _deadKeysBuffer[i];
            if (_live.TryGetValue(key, out var series))
                RemoveSeriesLocked(key, series);
        }
        _deadKeysBuffer.Clear();

        PruneExpiredRemoteLocked(_utcNow());
        // Expiring part of a sealed block can materialize a raw head. Recheck budgets.
        EnforceLimitsLocked();
    }
    private long Cutoff() => Nano(_utcNow().AddMinutes(-_retentionMinutes));
    private static long Nano(DateTime value) => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;
    private static (string, string, string) Key(string collector, string node, string metric) => (collector.Trim().ToLowerInvariant(), node, metric.ToLowerInvariant());
}
