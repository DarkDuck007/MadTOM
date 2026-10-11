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

    [Fact]
    public void ComputeLayout_TwelveCores_ShouldFitInSingleLine_OnCardBounds()
    {
        // On host card with ~240px width and ~46px height, 12 cores should fit in 1 line
        var res = CoreLayoutSolver.ComputeVerticalFillSquareLayout(240, 46, 12);
        Assert.Equal(1, res.Rows);
        Assert.Equal(12, res.Cols);
        Assert.True(res.CellSize >= 10.0, "Cell size should be sufficiently large in single line");
        Assert.True(res.GridWidth <= 240.01);
        Assert.True(res.GridHeight <= 46.01);
    }

    [Fact]
    public void ComputeEvenPerCoreGrid_TwelveCores_ShouldUseEvenColumnsAndRows()
    {
        var (cols, rows) = MadTOM.Controls.CoreMatrix.MicroCoreStripControl.ComputeEvenPerCoreGrid(240, 46, 12);
        Assert.True(cols % 2 == 0, $"Columns ({cols}) should be even");
        Assert.True(cols * rows >= 12, "Slots must fit 12 cores");
        Assert.Equal(0, (cols * rows) - 12); // Zero empty slots (e.g. 6x2)
    }

    [Fact]
    public void GetNonOverlappingCoreColor_CalculatesValidDistinctColors_WithoutByteOverflow()
    {
        for (int i = 0; i < 256; i++)
        {
            var color = MadTOM.Controls.CoreMatrix.MicroCoreStripControl.GetNonOverlappingCoreColor(i);
            Assert.Equal(255, color.A);
            // Verify RGB values are valid bytes in 0..255 range (implicitly guaranteed by byte type)
        }

        // Consecutive colors should have distinct hues
        var c0 = MadTOM.Controls.CoreMatrix.MicroCoreStripControl.GetNonOverlappingCoreColor(0);
        var c1 = MadTOM.Controls.CoreMatrix.MicroCoreStripControl.GetNonOverlappingCoreColor(1);
        Assert.False(c0.R == c1.R && c0.G == c1.G && c0.B == c1.B, "Consecutive colors must be distinct");
    }
}

