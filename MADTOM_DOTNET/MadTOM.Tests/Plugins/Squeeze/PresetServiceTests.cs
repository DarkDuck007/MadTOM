using System.Linq;
using SQUEEZE.Models;
using SQUEEZE.Services;
using Xunit;

namespace SQUEEZE.Tests;

public class PresetServiceTests
{
    [Fact]
    public void Catalog_ContainsAllFourStandardCategories_WithTotalNineteenPresets()
    {
        var service = new PresetService();
        var all = service.GetAllPresets();

        Assert.Equal(19, all.Count);
        Assert.Equal(6, service.GetPresetsByCategory("General").Count);
        Assert.Equal(5, service.GetPresetsByCategory("Web").Count);
        Assert.Equal(4, service.GetPresetsByCategory("Hardware").Count);
        Assert.Equal(4, service.GetPresetsByCategory("Production").Count);

        var ultra = service.GetPresetByKey("ultra_compression");
        Assert.NotNull(ultra);
        Assert.Equal("Ultra Compression", ultra.Title);
        Assert.Equal("General", ultra.Category);
        Assert.Equal("libx264", ultra.Codec);
        Assert.Equal(30.0, ultra.Crf);
    }

    [Fact]
    public void DefaultPreset_InitiallyFast1080()
    {
        var service = new PresetService();
        Assert.Equal("fast1080", service.DefaultPresetKey);

        var preset = service.GetPresetByKey("fast1080");
        Assert.NotNull(preset);
        Assert.Equal("DEFAULT", preset.Tag);
    }

    [Fact]
    public void SetDefaultPresetKey_UpdatesDefault_AndFiresEvent()
    {
        var service = new PresetService();
        string? changedKey = null;
        service.DefaultPresetChanged += (s, key) => changedKey = key;

        service.SetDefaultPresetKey("hevc");

        Assert.Equal("hevc", service.DefaultPresetKey);
        Assert.Equal("hevc", changedKey);
        Assert.Equal("DEFAULT", service.GetPresetByKey("hevc")?.Tag);
        Assert.Null(service.GetPresetByKey("fast1080")?.Tag);
    }

    [Fact]
    public void AddCustomPreset_CreatesAndRegistersCustomPreset()
    {
        var service = new PresetService();
        TranscodePreset? addedPreset = null;
        service.CustomPresetAdded += (s, p) => addedPreset = p;

        var snapshot = new TranscodePreset
        {
            Codec = "libx265",
            Crf = 19.5,
            SpeedIndex = 3
        };

        var custom = service.AddCustomPreset("My Discord Custom", "Specially tuned for Discord 25MB", snapshot);

        Assert.NotNull(custom);
        Assert.Equal("My Discord Custom", custom.Title);
        Assert.Equal("Custom", custom.Category);
        Assert.Equal("CUSTOM", custom.Tag);
        Assert.Equal("libx265", custom.Codec);
        Assert.Equal(19.5, custom.Crf);
        Assert.Same(custom, addedPreset);
        Assert.Contains(custom, service.GetAllPresets());
    }
}

