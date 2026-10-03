using MADTOM.Console.Hosting;
using Xunit;

namespace MadTOM.Tests;

public class StudioCommandLineOptionsTests
{
    [Fact]
    public void Parse_DefaultArguments_ReturnsDesktopDefaults()
    {
        var (options, error) = StudioCommandLineOptions.Parse(Array.Empty<string>());

        Assert.Null(error);
        Assert.False(options.ShowHelp);
        Assert.Equal("desktop", options.OutputMode);
        Assert.Null(options.DrmCard);
        Assert.Equal(1.0, options.DrmScaling);
    }

    [Theory]
    [InlineData("--output", "drm")]
    [InlineData("-o", "drm")]
    [InlineData("--output=drm")]
    [InlineData("--OUTPUT", "DRM")]
    public void Parse_DrmOutput_SelectsDrmMode(params string[] args)
    {
        var (options, error) = StudioCommandLineOptions.Parse(args);

        Assert.Null(error);
        Assert.False(options.ShowHelp);
        Assert.Equal("drm", options.OutputMode);
    }

    [Theory]
    [InlineData("--card", "/dev/dri/card1")]
    [InlineData("-c", "/dev/dri/card1")]
    [InlineData("--card=/dev/dri/card1")]
    public void Parse_CardArgument_SetsCardDevice(params string[] args)
    {
        var (options, error) = StudioCommandLineOptions.Parse(args);

        Assert.Null(error);
        Assert.Equal("/dev/dri/card1", options.DrmCard);
    }

    [Theory]
    [InlineData(new[] { "--scaling", "1.5" }, 1.5)]
    [InlineData(new[] { "-s", "2.0" }, 2.0)]
    [InlineData(new[] { "--scaling=1.25" }, 1.25)]
    public void Parse_ScalingArgument_SetsScalingFactor(string[] args, double expectedScaling)
    {
        var (options, error) = StudioCommandLineOptions.Parse(args);

        Assert.Null(error);
        Assert.Equal(expectedScaling, options.DrmScaling);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpFlag_SetsShowHelp(string arg)
    {
        var (options, error) = StudioCommandLineOptions.Parse(new[] { arg });

        Assert.Null(error);
        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void Parse_UnknownOutputMode_ReturnsDescriptiveError()
    {
        var (options, error) = StudioCommandLineOptions.Parse(new[] { "--output", "wayland_custom" });

        Assert.NotNull(error);
        Assert.Contains("Unknown output mode 'wayland_custom'", error);
    }
}
