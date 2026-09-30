using MadTOM.Services;

namespace MadTOM.Tests;

public class TelemetryHistoryCacheTests
{
    private DateTime _now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private static long Nano(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds() * 1_000_000;
    private void Record(TelemetryHistoryCache c, DateTime t, double value, string collector = "a") =>
        c.Record(collector, "node", Nano(t), new Dictionary<string, double> { ["cpu.total"] = value });
    private Task<IReadOnlyList<LODPoint>> Read(TelemetryHistoryCache c, DateTime start, string collector = "a") =>
        c.QueryAsync(collector, "node", "cpu.total", start, _now, true, (_, _, _) => throw new Exception("monitor-only must stay local"));

    [Fact]
    public async Task SizePressureEvictsOldestLiveSamplesAndReclaimsAllocatedBuffers()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 4096, 4096, 30);
        for (int i = 0; i < 1000; i++) Record(cache, _now.AddSeconds(-1000 + i), i);
        var points = await Read(cache, _now.AddHours(-1));
        Assert.InRange(points.Count, 1, 120);
        Assert.Equal(999, points[^1].Value);
        Assert.True(points[0].Value > 0);
        Assert.InRange(cache.GetUsage(60).LiveBytes, 1, 4096);
        cache.Configure(60, 1024, 4096, 30);
        Assert.InRange(cache.GetUsage(60).LiveBytes, 1, 1024);
        Assert.Equal(999, (await Read(cache, _now.AddHours(-1)))[^1].Value);
    }

    [Fact]
    public async Task StoredSizeLimitUsesLruAndReportsUsageWithoutLiveData()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 4096, 800, 60);
        int fetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            fetches++;
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[] { new LODPoint(Nano(_now), 1, 1, 1) });
        }
        Task<IReadOnlyList<LODPoint>> Query(string metric) => cache.QueryAsync("a", "n", metric, _now.AddHours(-1), _now, false, Fetch);
        await Query("x"); await Query("y"); await Query("x"); await Query("z");
        Assert.Equal(3, fetches);
        await Query("x");
        Assert.Equal(3, fetches);
        await Query("y");
        Assert.Equal(4, fetches);
        var usage = cache.GetUsage(60);
        Assert.Equal(0, usage.LiveBytes);
        Assert.Equal(2, usage.StoredRanges);
        Assert.InRange(usage.StoredBytes, 1, 800);
        Assert.Equal(usage.StoredBytes, usage.TotalBytes);
        _now = _now.AddSeconds(61);
        Assert.Equal(0, cache.GetUsage(60).StoredRanges);
    }

    [Fact]
    public async Task SeparateClearsPreserveOtherCacheAndRejectInflightResults()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        Record(cache, _now, 4);
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LODPoint>>(new[] { new LODPoint(Nano(_now), 9, 9, 9) });
        await cache.QueryAsync("a", "n", "x", _now.AddHours(-1), _now, false, Fetch);
        cache.ClearLive();
        Assert.Equal(1, cache.GetUsage(60).StoredRanges);
        Record(cache, _now, 5);
        var pending = new TaskCompletionSource<IReadOnlyList<LODPoint>>();
        var query = cache.QueryAsync("a", "n", "y", _now.AddHours(-1), _now, false, (_, _, _) => pending.Task);
        cache.ClearStored();
        pending.SetResult(new[] { new LODPoint(Nano(_now), 9, 9, 9) });
        Assert.Empty(await query);
        Assert.Equal(0, cache.GetUsage(60).StoredRanges);
        Assert.Single(await Read(cache, _now.AddHours(-1)));
    }

    [Fact]
    public async Task HistorySurvivesReadersAndSeparatesCollectors()
    {
        var cache = new TelemetryHistoryCache(120, () => _now);
        Record(cache, _now.AddMinutes(-100), 1);
        Record(cache, _now, 2);
        Record(cache, _now, 9, "b");
        Assert.Equal(new[] { 1d, 2d }, (await Read(cache, _now.AddHours(-2))).Select(p => p.Value));
        Assert.Equal(2, (await Read(cache, _now.AddHours(-2))).Count);
        Assert.Equal(9, Assert.Single(await Read(cache, _now.AddHours(-2), "b")).Value);
        Record(cache, _now, 99);
        Assert.Equal(2, (await Read(cache, _now.AddHours(-2))).Last().Value);
    }

    [Fact]
    public async Task RetentionPrunesIdleNodesAndShrinkIsImmediate()
    {
        var cache = new TelemetryHistoryCache(120, () => _now);
        Record(cache, _now.AddMinutes(-90), 1);
        Record(cache, _now, 2);
        cache.RetentionMinutes = 60;
        Assert.Single(await Read(cache, _now.AddHours(-2)));
        _now = _now.AddHours(2);
        cache.Prune();
        Assert.Empty(await Read(cache, _now.AddHours(-3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.RetentionMinutes = 0);
    }

    [Fact]
    public async Task LocalHistoryWinsOverlapAndRemoteRangeIsReused()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        Record(cache, _now, 2);
        int calls = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            calls++;
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[] { new LODPoint(Nano(_now.AddMinutes(-10)), 1, 1, 1), new LODPoint(Nano(_now), 99, 99, 99) });
        }
        var first = await cache.QueryAsync("a", "node", "cpu.total", _now.AddMinutes(-30), _now, false, Fetch);
        Assert.Equal(new[] { 1d, 2d }, first.Select(p => p.Value));
        await cache.QueryAsync("a", "node", "cpu.total", _now.AddMinutes(-20), _now, false, Fetch);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CoveredLiveWindowAvoidsCollectorAndClearWinsInflightFetch()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        for (int i = -10; i <= 0; i++) Record(cache, _now.AddSeconds(i), i);
        var result = await cache.QueryAsync("a", "node", "cpu.total", _now.AddSeconds(-5.5), _now, false, (_, _, _) => throw new Exception("covered"));
        Assert.Equal(6, result.Count);
        var pending = new TaskCompletionSource<IReadOnlyList<LODPoint>>();
        var query = cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-1), _now, false, (_, _, _) => pending.Task);
        cache.Clear();
        pending.SetResult(new[] { new LODPoint(Nano(_now), 7, 7, 7) });
        Assert.Empty(await query);
        Assert.Empty(await Read(cache, _now.AddHours(-1)));
    }

    [Fact]
    public async Task FailureReturnsLocalHistoryButCancellationPropagates()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        Record(cache, _now, 2);
        Assert.Single(await cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-1), _now, false, (_, _, _) => throw new IOException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-1), _now, false, (_, _, _) => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task LiveHistoryMergedIntoStoredQueryCacheAndCoveredPointsEvictedWhilePreservingMonitoredOnlyMetrics()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        // Record live samples for cpu.total (to be queried) and gpu.temp (monitored only, not queried)
        long t1 = Nano(_now.AddSeconds(-30));
        long t2 = Nano(_now.AddSeconds(-10));
        cache.Record("a", "node", t1, new Dictionary<string, double> { ["cpu.total"] = 15.0, ["gpu.temp"] = 45.0 });
        cache.Record("a", "node", t2, new Dictionary<string, double> { ["cpu.total"] = 25.0, ["gpu.temp"] = 47.0 });

        int collectorFetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            collectorFetches++;
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[]
            {
                new LODPoint(Nano(_now.AddHours(-2)), 10.0, 10.0, 10.0),
                new LODPoint(Nano(_now.AddHours(-1)), 12.0, 12.0, 12.0)
            });
        }

        // Query cpu.total for last 3 hours
        var result = await cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-3), _now, false, Fetch, targetPoints: 800);
        Assert.Equal(1, collectorFetches);
        // Result should include collector data + live data (10, 12, 15, 25)
        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { 10.0, 12.0, 15.0, 25.0 }, result.Select(p => p.Value));

        // Stored query cache now holds the merged result
        var usage = cache.GetUsage(60);
        Assert.Equal(1, usage.StoredRanges);
        Assert.Equal(4, usage.StoredPoints);

        // The returned live points for cpu.total must be evicted from _live
        var liveCpu = await cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-3), _now, true, (_, _, _) => Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>()));
        Assert.Empty(liveCpu);

        // But gpu.temp (monitored-only, never queried/cached in stored cache) must NOT be deleted!
        var liveGpu = await cache.QueryAsync("a", "node", "gpu.temp", _now.AddHours(-3), _now, true, (_, _, _) => Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>()));
        Assert.Equal(2, liveGpu.Count);
        Assert.Equal(new[] { 45.0, 47.0 }, liveGpu.Select(p => p.Value));
    }

    [Fact]
    public async Task RapidQueriesWithinBucketIntervalHitStoredQueryCacheWithoutCollectorFetch()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        int collectorFetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            collectorFetches++;
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[]
            {
                new LODPoint(Nano(a.AddHours(1)), 50.0, 50.0, 50.0),
                new LODPoint(Nano(b.AddMinutes(-10)), 60.0, 60.0, 60.0)
            });
        }

        // 24-hour query with 800 target points -> bucket interval is 86,400 / 800 = 108 seconds
        var first = await cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-24), _now, false, Fetch, targetPoints: 800);
        Assert.Equal(1, collectorFetches);
        Assert.Equal(2, first.Count);

        // User clicks 24h again 10 seconds later (well within the 108-second bucket width of the graph's stored data)
        _now = _now.AddSeconds(10);
        var second = await cache.QueryAsync("a", "node", "cpu.total", _now.AddHours(-24), _now, false, Fetch, targetPoints: 800);
        // Must hit stored query cache, NO extra collector fetch!
        Assert.Equal(1, collectorFetches);
        Assert.Equal(2, second.Count);
    }
}
