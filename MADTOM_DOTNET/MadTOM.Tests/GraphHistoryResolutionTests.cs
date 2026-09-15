using Avalonia;
using MadTOM.Controls.Charts;
using MadTOM.Services;

namespace MadTOM.Tests;

public class GraphHistoryResolutionTests
{
    [Theory]
    [InlineData(300, 900)]
    [InlineData(750, 2250)]
    [InlineData(1200, 3600)]
    public void BudgetProvidesThreeSamplesPerPlotPixel(double width, int expected) =>
        Assert.Equal(expected, GraphHistoryResolution.PointBudget(width));

    [Theory]
    [InlineData(1, 750)]
    [InlineData(3, 2250)]
    [InlineData(10, 7500)]
    public void HistoryMultiplierChangesRetentionBudgetIndependentlyOfDrawing(double multiplier, int expected)
    {
        Assert.Equal(expected, GraphHistoryResolution.PointBudget(750, multiplier));
        Assert.Equal(750, GraphDrawingResolution.PointBudget(750, 1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.2)]
    public void ScrollingKeepsCompletedHistoryAndDrawingBucketsStable(double drawingDensity)
    {
        const long second = 1_000_000_000;
        const long epoch = 1_700_000_000 * second;
        var all = Enumerable.Range(0, 18200).Select(i =>
            new LODPoint(epoch + i * (second / 10), Math.Sin(i * 17.13) * 100, 0, 0)).ToArray();
        long[]? baseline = null;
        for (int tick = 0; tick < 10; tick++)
        {
            long start = epoch + tick * second, end = start + 1800 * second;
            var raw = all.Where(p => p.TimestampUnixNano >= start && p.TimestampUnixNano <= end).ToArray();
            var history = GraphHistoryResolution.Downsample(raw, 750, start, end);
            var mapped = history.Select(p => new Point((p.TimestampUnixNano - start) / (double)(1800 * second) * 750, p.Value)).ToArray();
            var drawn = GraphDrawingResolution.Reduce(mapped, 0, 750, drawingDensity,
                history.Select(p => p.TimestampUnixNano).ToArray(), start, end);
            Assert.InRange(drawn.Count, 2, GraphDrawingResolution.PointBudget(750, drawingDensity));
            Assert.All(drawn, p => Assert.Contains(p, mapped)); // exact original coordinates and values
            var interior = drawn.Select(p => start + (long)Math.Round(p.X / 750 * (1800 * second)))
                .Where(t => t > epoch + 100 * second && t < epoch + 1700 * second).ToArray();
            if (baseline == null) baseline = interior;
            else Assert.Equal(baseline, interior);
        }
    }

    [Fact]
    public void ControlPublishesPlotWidthBudgetOnResize()
    {
        var chart = new MetricHistoryChartControl();
        chart.Measure(new Size(818, 200));
        chart.Arrange(new Rect(0, 0, 818, 200));
        Assert.Equal(GraphHistoryResolution.PointBudget(750, GraphPerformanceSettings.Current.HistoryPointsPerPixel), chart.HistoryPointBudget); // subtract 52 + 16 axis padding
        chart.Arrange(new Rect(0, 0, 468, 200));
        Assert.Equal(GraphHistoryResolution.PointBudget(400, GraphPerformanceSettings.Current.HistoryPointsPerPixel), chart.HistoryPointBudget);
    }

    [Fact]
    public void ThirtyMinutesAtOneHzFitsWideGraphWithoutReduction()
    {
        var input = Enumerable.Range(0, 1800).Select(i => new LODPoint(i * 1_000_000_000L, i % 20, i % 20, i % 20)).ToArray();
        Assert.Same(input, GraphHistoryResolution.Downsample(input, GraphHistoryResolution.PointBudget(750), 0, 1800_000_000_000));
    }

    [Fact]
    public void DenseHistoryRetainsSpikesEndpointsAndTemporalCoverage()
    {
        var input = Enumerable.Range(0, 18000).Select(i => new LODPoint(i * 100_000_000L, i == 1001 ? 999 : i == 1002 ? -999 : i % 10, 0, 0)).ToArray();
        var result = GraphHistoryResolution.Downsample(input, 2250, 0, 1800_000_000_000);
        Assert.InRange(result.Count, 1500, 2250);
        Assert.Equal(input[0], result[0]);
        Assert.Equal(input[^1], result[^1]);
        Assert.Contains(result, p => p.Value == 999);
        Assert.Contains(result, p => p.Value == -999);
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.TimestampUnixNano < p.Second.TimestampUnixNano));
        Assert.Equal(18000, input.Length);
    }

    [Fact]
    public async Task CoarseRemoteCacheCannotSatisfyWiderGraph()
    {
        var now = DateTime.UtcNow;
        var cache = new TelemetryHistoryCache(60, () => now);
        int calls = 0;
        Task<IReadOnlyList<LODPoint>> Fetch(DateTime start, DateTime end, CancellationToken ct)
        {
            calls++;
            return Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>());
        }
        await cache.QueryAsync("collector", "node", "cpu.total", now.AddMinutes(-30), now, false, Fetch, targetPoints: 900);
        await cache.QueryAsync("collector", "node", "cpu.total", now.AddMinutes(-30), now, false, Fetch, targetPoints: 2250);
        Assert.Equal(2, calls);
        await cache.QueryAsync("collector", "node", "cpu.total", now.AddMinutes(-30), now, false, Fetch, targetPoints: 900);
        Assert.Equal(2, calls);
    }
}
