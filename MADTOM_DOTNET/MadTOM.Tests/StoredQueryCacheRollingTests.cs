using MadTOM.Services;

namespace MadTOM.Tests;

public class StoredQueryCacheRollingTests
{
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static long Nano(DateTime value) => new DateTimeOffset(value).ToUnixTimeMilliseconds() * 1000000;
    private static IReadOnlyList<LODPoint> Samples(DateTime start, DateTime end) =>
        Enumerable.Range(0, (int)(end - start).TotalSeconds + 1)
            .Select(i => new LODPoint(Nano(start.AddSeconds(i)), i, i, i)).ToArray();

    [Fact]
    public async Task NodeWithoutLiveTailReusesPrefixAndOnlyFetchesMissingSeconds()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        cache.Configure(60, 1 << 20, 1 << 20, 300);
        var requests = new List<(DateTime Start, DateTime End)>();
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        { requests.Add((a, b)); return Task.FromResult(Samples(a, b)); }
        Task<IReadOnlyList<LODPoint>> Query() => cache.QueryAsync("c", "slow", "cpu", _now.AddMinutes(-30), _now, false, Fetch, targetPoints: 2000);
        await Query();
        for (int i = 0; i < 5; i++)
        {
            var previousEnd = _now;
            _now = _now.AddSeconds(2);
            var result = await Query();
            Assert.Equal((previousEnd, _now), requests[^1]);
            Assert.Equal(1801, result.Count);
            Assert.Equal(Nano(_now.AddMinutes(-30)), result[0].TimestampUnixNano);
            Assert.Equal(Nano(_now), result[^1].TimestampUnixNano);
            Assert.Equal(1, cache.GetUsage(60).StoredRanges);
        }
        await Query(); // Exact same time bounds use the merged cache without any RPC.
        Assert.Equal(6, requests.Count);
    }

    [Fact]
    public async Task EmptyTailIsCachedButPrefixExpiryStillForcesFullRefresh()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        var fetched = new List<TimeSpan>();
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            fetched.Add(b - a);
            return Task.FromResult<IReadOnlyList<LODPoint>>(fetched.Count == 1 ? Samples(a, b) : Array.Empty<LODPoint>());
        }
        Task<IReadOnlyList<LODPoint>> Query() => cache.QueryAsync("c", "lagging", "cpu", _now.AddHours(-1), _now, false, Fetch, targetPoints: 4000);
        await Query();
        _now = _now.AddSeconds(20); Assert.NotEmpty(await Query());
        await Query(); Assert.Equal(2, fetched.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), fetched[1]);
        _now = _now.AddSeconds(11); await Query();
        Assert.Equal(TimeSpan.FromHours(1), fetched[2]);
    }

    [Fact]
    public async Task FailedTailDoesNotClaimCoverageAndClearWinsInflightMerge()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        await cache.QueryAsync("c", "n", "cpu", _now.AddMinutes(-30), _now, false,
            (a, b, _) => Task.FromResult(Samples(a, b)), targetPoints: 2000);
        _now = _now.AddSeconds(2);
        int failures = 0;
        Task<IReadOnlyList<LODPoint>> Fail(DateTime a, DateTime b, CancellationToken ct)
        { failures++; throw new IOException(); }
        for (int i = 0; i < 2; i++)
            Assert.NotEmpty(await cache.QueryAsync("c", "n", "cpu", _now.AddMinutes(-30), _now, false, Fail, targetPoints: 2000));
        Assert.Equal(2, failures);
        var pending = new TaskCompletionSource<IReadOnlyList<LODPoint>>();
        var query = cache.QueryAsync("c", "n", "cpu", _now.AddMinutes(-30), _now, false, (_, _, _) => pending.Task, targetPoints: 2000);
        cache.ClearStored();
        pending.SetResult(Samples(_now.AddSeconds(-2), _now));
        Assert.Empty(await query);
        Assert.Equal(0, cache.GetUsage(60).StoredRanges);
    }

    [Fact]
    public async Task ZoomInDoesNotReuseInsufficientlyDensePrefix()
    {
        var cache = new TelemetryHistoryCache(60, () => _now);
        await cache.QueryAsync("c", "n", "cpu", _now.AddHours(-2), _now, false,
            (a, b, _) => Task.FromResult(Samples(a, b)), targetPoints: 2000);
        _now = _now.AddSeconds(2);
        DateTime? fetchedFrom = null;
        await cache.QueryAsync("c", "n", "cpu", _now.AddMinutes(-30), _now, false,
            (a, b, _) => { fetchedFrom = a; return Task.FromResult(Samples(a, b)); }, targetPoints: 2000);
        Assert.Equal(_now.AddMinutes(-30), fetchedFrom);
    }
}
