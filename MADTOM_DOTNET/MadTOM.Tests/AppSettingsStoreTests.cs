using System;
using System.IO;
using MADTOM.PluginContracts;
using Xunit;

namespace MadTOM.Tests;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _tempFile;

    public AppSettingsStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"madtom_settings_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
        string tmp = _tempFile + ".tmp";
        if (File.Exists(tmp))
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultSettings()
    {
        var settings = AppSettingsStore.Load(_tempFile);

        Assert.NotNull(settings);
        Assert.Equal("default-dark", settings.Theme);
        Assert.Equal("goose", settings.Language);
        Assert.False(settings.IsConsoleSidebarCollapsed);
        Assert.False(settings.IsTelemetrySidebarCollapsed);
    }

    [Fact]
    public void SaveAndLoad_PersistsAllPropertiesAccurately()
    {
        var settings = new AppSettings
        {
            Theme = "paper-white",
            Language = "feline",
            IsConsoleSidebarCollapsed = true,
            IsTelemetrySidebarCollapsed = true
        };

        AppSettingsStore.Save(settings, _tempFile);

        Assert.True(File.Exists(_tempFile));

        var reloaded = AppSettingsStore.Load(_tempFile);

        Assert.NotNull(reloaded);
        Assert.Equal("paper-white", reloaded.Theme);
        Assert.Equal("feline", reloaded.Language);
        Assert.True(reloaded.IsConsoleSidebarCollapsed);
        Assert.True(reloaded.IsTelemetrySidebarCollapsed);
    }

    [Fact]
    public void Load_WhenFileCorrupted_ReturnsDefaultsSafely()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json content [!");

        var settings = AppSettingsStore.Load(_tempFile);

        Assert.NotNull(settings);
        Assert.Equal("default-dark", settings.Theme);
        Assert.Equal("goose", settings.Language);
    }

    [Fact]
    public void Save_OverwritesExistingFileAtomically()
    {
        var first = new AppSettings { Theme = "pure-light", Language = "standard" };
        AppSettingsStore.Save(first, _tempFile);

        var second = new AppSettings { Theme = "anti-bleed-grey", Language = "feline", IsConsoleSidebarCollapsed = true };
        AppSettingsStore.Save(second, _tempFile);

        var loaded = AppSettingsStore.Load(_tempFile);
        Assert.Equal("anti-bleed-grey", loaded.Theme);
        Assert.Equal("feline", loaded.Language);
        Assert.True(loaded.IsConsoleSidebarCollapsed);
    }
}
