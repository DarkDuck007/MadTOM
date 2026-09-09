using MadTOM.Common.Utils;
using Xunit;

namespace MadTOM.Tests;

public class CoreLayoutSolverTests
{
    [Theory]
    [InlineData(16, 200, 200)]
    [InlineData(32, 300, 200)]
    [InlineData(64, 400, 250)]
    [InlineData(256, 600, 400)]
    [InlineData(512, 800, 500)]
    [InlineData(1024, 1000, 600)]
    [InlineData(1904, 1200, 800)]
    public void ComputeLayout_ShouldAccommodateAllCores_AndFitInBounds(int coreCount, double w, double h)
    {
        var result = CoreLayoutSolver.ComputeVerticalFillSquareLayout(w, h, coreCount);

        Assert.True(result.Cols > 0, "Columns must be positive");
        Assert.True(result.Rows > 0, "Rows must be positive");
        Assert.True(result.Cols * result.Rows >= coreCount, "Total slots (Cols * Rows) must accommodate all cores");
        Assert.True(result.CellSize >= 1.0, "Cell size should be at least 1px");
        Assert.True(result.GridWidth <= w + 0.01, "GridWidth must not exceed available width");
        Assert.True(result.GridHeight <= h + 0.01, "GridHeight must not exceed available height");
        Assert.True(result.StartX >= 0.0, "StartX must be non-negative");
        Assert.True(result.StartY >= 0.0, "StartY must be non-negative");
    }

    [Fact]
    public void ComputeLayout_HandlesEdgeCases_ZeroAndNegativeCores()
    {
        var resZero = CoreLayoutSolver.ComputeVerticalFillSquareLayout(100, 100, 0);
        Assert.True(resZero.Cols * resZero.Rows >= 1);

        var resNeg = CoreLayoutSolver.ComputeVerticalFillSquareLayout(100, 100, -10);
        Assert.True(resNeg.Cols * resNeg.Rows >= 1);
    }

    [Fact]
    public void ComputeLayout_HandlesSmallDimensions()
    {
        var res = CoreLayoutSolver.ComputeVerticalFillSquareLayout(5, 5, 16);
        Assert.True(res.Cols > 0);
        Assert.True(res.Rows > 0);
        Assert.True(res.Cols * res.Rows >= 16);
    }
}

