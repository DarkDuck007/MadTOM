using System.Collections.Concurrent;
using MadTOM.Services;

namespace MadTOM.Tests;

public class HistoryTimingTests
{
    [Fact]
    public void CoverageReasonsDistinguishStartGapEndAndConservativeBlocks()
    {
        const long second = 1_000_000_000;
        var history = new CompressedPointHistory();
        Assert.Equal("empty", history.InspectCoverage(0, second).Reason);
        history.Enqueue(new(second, 1, 1, 1));
        Assert.Equal("missing-start", history.InspectCoverage(0, second).Reason);
        Assert.Equal("stale-end", history.InspectCoverage(second, 7 * second).Reason);
        history.Enqueue(new(8 * second, 1, 1, 1));
        var gap = history.InspectCoverage(second, 8 * second);
        Assert.Equal("sample-gap", gap.Reason);
        Assert.Equal(7 * second, gap.GapNano);
        Assert.False(history.Covers(second, 8 * second));
        Assert.True(history.Covers(9 * second, 9 * second));
        for (int i = 2; i < 256; i++) history.Enqueue(new((i + 7) * second, 1, 1, 1));
        // The original metadata rule rejects even a later slice of a block with an early gap.
        Assert.Equal("block-max-gap", history.InspectCoverage(200 * second, 250 * second).Reason);
        Assert.False(history.Covers(200 * second, 250 * second));
    }

    [Fact]
    public async Task PartialHitReportsLatestLiveAndTailRejection()
    {
        var entries = new List<HistoryTiming.Entry>();
        using var timing = HistoryTiming.Begin("test", sink: entries.Add);
        var now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var cache = new TelemetryHistoryCache(60, () => now);
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>());
        await cache.QueryAsync("c", "n", "cpu", now.AddMinutes(-1), now, false, Fetch, targetPoints: 2000);
        long sample = new DateTimeOffset(now.AddSeconds(-8)).ToUnixTimeMilliseconds() * 1000000;
        cache.Record("c", "n", sample, new Dictionary<string, double> { ["cpu"] = 1 });
        now = now.AddSeconds(1);
        await cache.QueryAsync("c", "n", "cpu", now.AddMinutes(-1), now, false, Fetch, targetPoints: 2000);
        Assert.Contains(entries, e => e.Event == "coverage" && e.Detail!.Contains("interval=tail") &&
            e.Detail.Contains("reason=stale-end") && e.Detail.Contains("gapNano=9000000000"));
        Assert.Contains(entries, e => e.Event == "live-timestamps" && e.Detail!.Contains($"latestLiveNano={sample}"));
    }

    [Fact]
    public async Task FirstLiveUpdateReportsWindowShiftOnlyOncePerHistoryLoad()
    {
        var entries = new List<HistoryTiming.Entry>();
        using var timing = HistoryTiming.Begin("test", sink: entries.Add);
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new MadTOM.ViewModels.HostMetricsTabViewModel(provider);
        var node = new MadTOM.Models.FleetNodeModel { Id = "timing-node" };
        vm.UpdateForNode(node.Id, node, new[] { node });
        vm.SetScope("1m");
        await vm.RefreshHistoryAsync();
        var before = vm.Graphs.First().WindowEnd;
        node.TimestampUnixNano = before - 3_000_000_000;
        node.TelemetryReceivedUtc = DateTime.UtcNow;
        entries.Clear();
        vm.UpdateForNode(node.Id, node, new[] { node });
        Assert.Contains(entries, e => e.Event == "arrival" && e.Detail!.Contains("historyRefresh="));
        Assert.Contains(entries, e => e.Event == "window-before" && e.Detail!.Contains($"endNano={before}"));
        Assert.Contains(entries, e => e.Event == "window-after" && e.Detail!.Contains($"endNano={node.TimestampUnixNano}"));
        entries.Clear();
        node.TimestampUnixNano += 1_000_000_000;
        vm.UpdateForNode(node.Id, node, new[] { node });
        Assert.DoesNotContain(entries, e => e.Stage == "first-live-after-history");
    }

    [Fact]
    public async Task ParallelSpansKeepCorrelationAndRestoreParent()
    {
        var entries = new ConcurrentQueue<HistoryTiming.Entry>();
        using (HistoryTiming.Begin("refresh", sink: entries.Enqueue, newRefresh: true))
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
            {
                using var child = HistoryTiming.Begin("metric", "node", i.ToString());
                await Task.Yield();
                child!.Mark("ready");
            }));
            using var final = HistoryTiming.Begin("final");
        }
        var root = entries.Single(e => e.Stage == "refresh" && e.Event == "begin");
        Assert.All(entries, e => Assert.Equal(root.Refresh, e.Refresh));
        Assert.All(entries.Where(e => e.Stage != "refresh"), e => Assert.Equal(root.Span, e.Parent));
        Assert.Equal(6, entries.Count(e => e.Event == "end"));
        Assert.All(entries, e => Assert.True(e.ElapsedMs >= 0 && e.StepMs >= 0));
    }

    [Fact]
    public async Task CacheReportsMissStoredHitAndPartialHitWithPointCounts()
    {
        var entries = new List<HistoryTiming.Entry>();
        using var timing = HistoryTiming.Begin("test", sink: entries.Add);
        var now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var cache = new TelemetryHistoryCache(60, () => now);
        int calls = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime a, DateTime b, CancellationToken ct)
        {
            calls++;
            long stamp = new DateTimeOffset(b).ToUnixTimeMilliseconds() * 1000000;
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[] { new LODPoint(stamp, 1, 1, 1) });
        }
        Task<IReadOnlyList<LODPoint>> Query() => cache.QueryAsync("collector", "node", "cpu", now.AddMinutes(-30), now, false, Fetch, targetPoints: 2000);
        await Query(); await Query();
        now = now.AddSeconds(1); await Query();
        Assert.Equal(2, calls);
        Assert.Contains(entries, e => e.Event == "miss");
        Assert.Contains(entries, e => e.Event == "stored-hit-decoded" && e.Detail == "points=1");
        Assert.Contains(entries, e => e.Event == "partial-hit");
        Assert.All(entries.Where(e => e.Stage == "cache"), e => Assert.Equal("cpu", e.Metric));
    }

    [Fact]
    public async Task CancelledFetchIsReportedAndSinkFailureCannotBreakCache()
    {
        var entries = new List<HistoryTiming.Entry>();
        using (HistoryTiming.Begin("test", sink: entries.Add))
        {
            var cache = new TelemetryHistoryCache();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.QueryAsync("c", "n", "cpu",
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, false,
                (_, _, _) => throw new OperationCanceledException()));
        }
        Assert.Contains(entries, e => e.Stage == "cache" && e.Event == "cancelled");
        using var broken = HistoryTiming.Begin("broken", sink: _ => throw new IOException());
        broken!.Mark("still-running");
        var local = await new TelemetryHistoryCache().QueryAsync("c", "n", "cpu",
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, true, (_, _, _) => throw new Exception());
        Assert.Empty(local);
    }
}
