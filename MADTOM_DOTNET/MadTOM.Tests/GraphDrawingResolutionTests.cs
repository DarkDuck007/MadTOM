using Avalonia;
using MadTOM.Services;

namespace MadTOM.Tests;

public class GraphDrawingResolutionTests
{
    [Theory]
    [InlineData(0.1, 75)]
    [InlineData(1, 750)]
    [InlineData(2, 1500)]
    public void DrawingHonoursBudgetPreservesSpikesAndLeavesHistoryUnchanged(double density, int budget)
    {
        var source = Enumerable.Range(0, 2250).Select(i => new Point(i / 3.0, i == 101 ? 999 : i == 102 ? -999 : i % 10)).ToArray();
        var result = GraphDrawingResolution.Reduce(source, 0, 750, density);
        Assert.Equal(budget, GraphDrawingResolution.PointBudget(750, density));
        Assert.InRange(result.Count, 2, budget);
        Assert.Contains(result, p => p.Y == 999);
        Assert.Contains(result, p => p.Y == -999);
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.X <= p.Second.X));
        Assert.Equal(2250, source.Length);
        Assert.Equal(2250, GraphHistoryResolution.PointBudget(750));
    }

    [Fact]
    public void TinyTimedViewportStillHonoursTwoPointDrawingLimit()
    {
        var points = new[] { new Point(0, 1), new Point(5, 10), new Point(10, 1) };
        var result = GraphDrawingResolution.Reduce(points, 0, 10, 0.1, new long[] { 0, 5, 10 }, 0, 10);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ZoomedViewportExcludesDistantOffscreenHistoryAndKeepsEdgeNeighbours()
    {
        var source = Enumerable.Range(-5000, 10000).Select(i => new Point(i, i % 10)).ToArray();
        var result = GraphDrawingResolution.Reduce(source, 52, 750, 0.1);
        Assert.InRange(result.Count, 2, 75);
        Assert.Equal(51, result[0].X);
        Assert.Equal(803, result[^1].X);
        Assert.All(result, p => Assert.InRange(p.X, 51, 803));
    }

    [Fact]
    public void SparseDrawingPreservesEveryPoint()
    {
        var points = new[] { new Point(0, 1), new Point(300, 4), new Point(749, 2) };
        Assert.Equal(points, GraphDrawingResolution.Reduce(points, 0, 750, 0.1));
    }

    [Fact]
    public void SettingsPersistRejectInvalidValuesAndRecoverFromMalformedFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string path = Path.Combine(directory, "performance.json");
        try
        {
            var settings = new GraphPerformanceSettings(path);
            Assert.Equal(1, settings.PointsPerPixel);
            int changes = 0;
            settings.Changed += () => changes++;
            settings.Save(0.1);
            Assert.Equal(0.1, new GraphPerformanceSettings(path).PointsPerPixel);
            settings.Save(2, 6);
            Assert.Equal(6, new GraphPerformanceSettings(path).HistoryPointsPerPixel);
            settings.Save(1);
            Assert.Equal(6, new GraphPerformanceSettings(path).HistoryPointsPerPixel);
            settings.Save(2);
            Assert.Equal(4, changes);
            foreach (double invalid in new[] { 0, 0.09, 2.01, double.NaN, double.PositiveInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.Save(invalid));
            Assert.Equal(2, new GraphPerformanceSettings(path).PointsPerPixel);
            foreach (double invalid in new[] { 0.9, 10.1, double.NaN, double.PositiveInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.Save(1, invalid));
            File.WriteAllText(path, "{\"GraphPointsPerPixel\": 0.5}");
            Assert.Equal(3, new GraphPerformanceSettings(path).HistoryPointsPerPixel);
            File.WriteAllText(path, "{\"GraphPointsPerPixel\": 0.5, \"HistoryPointsPerPixel\": 99}");
            Assert.Equal(3, new GraphPerformanceSettings(path).HistoryPointsPerPixel);
            Assert.Equal(0.5, new GraphPerformanceSettings(path).PointsPerPixel);
            File.WriteAllText(path, "{\"GraphPointsPerPixel\": 3}");
            Assert.Equal(1, new GraphPerformanceSettings(path).PointsPerPixel);
            File.WriteAllText(path, "broken");
            Assert.Equal(1, new GraphPerformanceSettings(path).PointsPerPixel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
