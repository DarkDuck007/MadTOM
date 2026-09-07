using Avalonia.Media;
using MadTOM.Common.Utils;
using Xunit;

namespace MadTOM.Tests;

public class ColorInterpolatorTests
{
    [Fact]
    public void Interpolate_AtZeroLoad_ReturnsSlateColor()
    {
        var color = ColorInterpolator.InterpolateLoadColor(0.0);
        Assert.Equal(30, color.R);
        Assert.Equal(41, color.G);
        Assert.Equal(59, color.B);
    }

    [Fact]
    public void Interpolate_At40Percent_ReturnsEmeraldColor()
    {
        var color = ColorInterpolator.InterpolateLoadColor(0.40);
        Assert.Equal(16, color.R);
        Assert.Equal(185, color.G);
        Assert.Equal(129, color.B);
    }

    [Fact]
    public void Interpolate_At75Percent_ReturnsAmberColor()
    {
        var color = ColorInterpolator.InterpolateLoadColor(0.75);
        Assert.Equal(245, color.R);
        Assert.Equal(158, color.G);
        Assert.Equal(11, color.B);
    }

    [Fact]
    public void Interpolate_At100Percent_ReturnsCrimsonColor()
    {
        var color = ColorInterpolator.InterpolateLoadColor(1.0);
        Assert.Equal(244, color.R);
        Assert.Equal(63, color.G);
        Assert.Equal(94, color.B);
    }

    [Fact]
    public void Interpolate_ClampsNegativeAndExcessiveValues()
    {
        var colorNeg = ColorInterpolator.InterpolateLoadColor(-0.5);
        var colorZero = ColorInterpolator.InterpolateLoadColor(0.0);
        Assert.Equal(colorZero, colorNeg);

        var colorOver = ColorInterpolator.InterpolateLoadColor(2.5);
        var colorMax = ColorInterpolator.InterpolateLoadColor(1.0);
        Assert.Equal(colorMax, colorOver);
    }

    [Fact]
    public void InterpolateLoadBrush_ReturnsCachedBrush()
    {
        var brush1 = ColorInterpolator.InterpolateLoadBrush(0.5);
        var brush2 = ColorInterpolator.InterpolateLoadBrush(0.5);
        Assert.Same(brush1, brush2);
    }
}

