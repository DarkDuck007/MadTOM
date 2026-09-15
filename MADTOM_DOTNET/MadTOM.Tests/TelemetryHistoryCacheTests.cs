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
}
