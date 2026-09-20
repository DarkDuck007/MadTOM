using MadTOM.Services;

namespace MadTOM.Tests;

public class UiPerformanceDiagnosticsTests
{
    [Fact]
    public void WindowBoundsPercentileSamplesButKeepsAllCountsAndDrains()
    {
        var window = new UiTimingWindow();
        for (int i = 1; i <= 1000; i++) window.Add("render", i, 32);
        window.Add("render", double.NaN);
        var stats = window.Drain()["render"];
        Assert.Equal(1000, stats.Count);
        Assert.Equal(500.5, stats.MeanMs);
        Assert.Equal(975, stats.P95Ms);
        Assert.Equal(1000, stats.MaxMs);
        Assert.Equal(512, stats.PercentileSamples);
        Assert.Equal(984, stats.Over16Ms);
        Assert.Equal(950, stats.Over50Ms);
        Assert.Equal(32000, stats.AllocatedBytes);
        Assert.Empty(window.Drain());
    }

    [Fact]
    public async Task ConcurrentRecordingPreservesCountsAndCapsMetricCardinality()
    {
        var window = new UiTimingWindow();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        { for (int i = 0; i < 1000; i++) window.Add("work", 2); })));
        Assert.Equal(8000, window.Drain()["work"].Count);
        for (int i = 0; i < 100; i++) window.Add(i.ToString(), 1);
        Assert.Equal(64, window.Drain().Count);
    }

    private static HistoryTiming.Entry Entry(string refresh, string stage, string ev, double ms = 0) =>
        new(DateTime.UtcNow, refresh, "span", null, stage, "node", "cpu", ev, ms, ms, null);

    [Fact]
    public void SummarySeparatesOverlappingStageWorkFromRefreshWallTime()
    {
        var summaries = new UiRefreshSummaryCollector();
        summaries.Observe(Entry("r", "scope-refresh", "begin"));
        summaries.Observe(Entry("r", "cache", "stored-hit-decoded"));
        summaries.Observe(Entry("r", "range-rpc", "begin"));
        summaries.Observe(Entry("r", "series", "end", 80));
        summaries.Observe(Entry("r", "series", "end", 90));
        summaries.Observe(Entry("r", "scope-refresh", "complete", 100));
        var summary = summaries.Observe(Entry("r", "scope-refresh", "end", 101))!;
        Assert.Equal(101, summary.WallMs);
        Assert.Equal(170, summary.StageWorkMs["series"]);
        Assert.Equal(1, summary.RpcCount);
        Assert.Equal(1, summary.Cache["stored-hit-decoded"]);
        Assert.Equal("complete", summary.Outcome);
        Assert.Null(summaries.Observe(Entry("r", "series", "end", 1000)));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("error")]
    public void FailedRefreshesAreNotReportedAsSuccessful(string outcome)
    {
        var summaries = new UiRefreshSummaryCollector();
        summaries.Observe(Entry("r", "scope-refresh", "begin"));
        summaries.Observe(Entry("r", "scope-refresh", outcome));
        Assert.Equal(outcome, summaries.Observe(Entry("r", "scope-refresh", "end"))!.Outcome);
    }

    [Fact]
    public void IncompleteRefreshTrackingIsBoundedAndEvictionsAreVisible()
    {
        var summaries = new UiRefreshSummaryCollector();
        for (int i = 0; i < 129; i++) summaries.Observe(Entry(i.ToString(), "scope-refresh", "begin"));
        Assert.Null(summaries.Observe(Entry("0", "scope-refresh", "end")));
        var summary = summaries.Observe(Entry("128", "scope-refresh", "end"))!;
        Assert.Equal(1, summary.EvictedRefreshes);
        Assert.Equal("interrupted", summary.Outcome);
    }
}
