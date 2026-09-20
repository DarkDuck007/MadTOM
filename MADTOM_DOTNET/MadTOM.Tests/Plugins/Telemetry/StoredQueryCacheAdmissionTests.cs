using MadTOM.Services;

namespace MadTOM.Tests;

public class StoredQueryCacheAdmissionTests
{
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static long Nano(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds() * 1_000_000;
    private LODPoint[] Points(int count) => Enumerable.Range(0, count)
        .Select(i => new LODPoint(Nano(_now.AddDays(-2)) + i * 1_000_000_000L, i % 7, i % 7, i % 7)).ToArray();
    private Task<IReadOnlyList<LODPoint>> Query(TelemetryHistoryCache cache, string metric,
        Func<DateTime, DateTime, CancellationToken, Task<IReadOnlyList<LODPoint>>> fetch, int target = 100000) =>
        cache.QueryAsync("c", "n", metric, _now.AddDays(-3), _now, false, fetch, targetPoints: target);

    [Theory]
    [InlineData(10001)]
    [InlineData(60000)]
    [InlineData(65536)]
    public async Task LargeSupportedQueriesAreReusedWithinCompressedByteBudget(int count)
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 1 << 20, 1 << 20, 300);
        var data = Points(count);
        int fetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        { fetches++; return Task.FromResult<IReadOnlyList<LODPoint>>(data); }
        Assert.Equal(data, await Query(cache, "cpu", Fetch));
        Assert.Equal(data, await Query(cache, "cpu", Fetch));
        Assert.Equal(1, fetches);
        var usage = cache.GetUsage(60);
        Assert.Equal(count, usage.StoredPoints);
        Assert.True(usage.StoredZstdBytes > 0);
        Assert.InRange(usage.StoredBytes, 1, 1 << 20);
    }

    [Fact]
    public async Task ResultAbove64KIsDisplayedWithoutAdmissionOrEviction()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        await Query(cache, "small", (_, _, _) => Task.FromResult<IReadOnlyList<LODPoint>>(Points(100)));
        var before = cache.GetUsage(60);
        var over = Points(65537);
        int fetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        { fetches++; return Task.FromResult<IReadOnlyList<LODPoint>>(over); }
        Assert.Equal(over, await Query(cache, "over", Fetch));
        Assert.Equal(over, await Query(cache, "over", Fetch));
        Assert.Equal(2, fetches);
        Assert.Equal(before.StoredPoints, cache.GetUsage(60).StoredPoints);
        Assert.Equal(before.StoredBytes, cache.GetUsage(60).StoredBytes);
    }

    [Fact]
    public async Task MoreThan128RangesAreRetainedWhenTheyFit()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 1 << 20, 1 << 20, 300);
        var data = Points(500);
        int fetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        { fetches++; return Task.FromResult<IReadOnlyList<LODPoint>>(data); }
        for (int i = 0; i < 160; i++) await Query(cache, "metric" + i, Fetch);
        Assert.Equal(160, cache.GetUsage(60).StoredRanges);
        Assert.Equal(80000, cache.GetUsage(60).StoredPoints);
        await Query(cache, "metric0", Fetch);
        Assert.Equal(160, fetches);
    }

    [Fact]
    public async Task IndividuallyOversizedResultDoesNotEvictUsefulEntries()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 4096, 800, 300);
        int smallFetches = 0;
        Task<IReadOnlyList<LODPoint>> Small(DateTime a, DateTime b, CancellationToken ct)
        { smallFetches++; return Task.FromResult<IReadOnlyList<LODPoint>>(Points(1)); }
        await Query(cache, "a", Small);
        await Query(cache, "b", Small);
        var before = cache.GetUsage(60);
        var large = Points(60000);
        Assert.Equal(large, await Query(cache, "huge", (_, _, _) => Task.FromResult<IReadOnlyList<LODPoint>>(large)));
        Assert.Equal(before.StoredBytes, cache.GetUsage(60).StoredBytes);
        Assert.Equal(2, cache.GetUsage(60).StoredRanges);
        await Query(cache, "a", Small);
        await Query(cache, "b", Small);
        Assert.Equal(2, smallFetches);
    }

    [Fact]
    public async Task ConcurrentFillsForSameRangeRetainOneResultAndKeepFinerResolution()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        var duplicate = new TaskCompletionSource<IReadOnlyList<LODPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coarse = new TaskCompletionSource<IReadOnlyList<LODPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Query(cache, "cpu", (_, _, _) => duplicate.Task, 60000);
        var b = Query(cache, "cpu", (_, _, _) => duplicate.Task, 60000);
        var c = Query(cache, "cpu", (_, _, _) => coarse.Task, 1000);
        var fine = Points(60000);
        duplicate.SetResult(fine);
        await Task.WhenAll(a, b);
        coarse.SetResult(Points(1000));
        await c;
        Assert.Equal(1, cache.GetUsage(60).StoredRanges);
        Assert.Equal(60000, cache.GetUsage(60).StoredPoints);
        var cached = await Query(cache, "cpu", (_, _, _) => throw new Exception("fine entry lost"), 60000);
        Assert.Equal(fine, cached);
    }

    [Fact]
    public async Task ConfiguredAgeIsAbsoluteAndCacheHitsDoNotRefreshStaleDataForever()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 1 << 20, 1 << 20, 30);
        var start = _now.AddDays(-3); var end = _now;
        int fetches = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        { fetches++; return Task.FromResult<IReadOnlyList<LODPoint>>(Points(15000)); }
        Task<IReadOnlyList<LODPoint>> Get() => cache.QueryAsync("c", "n", "cpu", start, end, false, Fetch, targetPoints: 20000);
        await Get();
        _now = _now.AddSeconds(29); await Get(); Assert.Equal(1, fetches);
        _now = _now.AddSeconds(2); await Get(); Assert.Equal(2, fetches);
    }
}
