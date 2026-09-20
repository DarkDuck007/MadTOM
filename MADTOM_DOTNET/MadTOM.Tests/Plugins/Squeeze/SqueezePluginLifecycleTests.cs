using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.Squeeze;
using MadTOM.Theming;
using SQUEEZE.Views;
using Xunit;

namespace SQUEEZE.Tests;

public class SqueezePluginLifecycleTests
{
    private class TestHostContext : IPluginHostContext, IThemeHost, ILexiconHost, IPluginNotificationService
    {
        public IPluginNotificationService Notifications => this;
        public ILexiconHost Lexicons => this;
        public IThemeHost Themes => this;
        public IMessenger Messenger => WeakReferenceMessenger.Default;
        public ITrayMenuService? Tray { get; set; } = new TrayMenuService();

        public string CurrentLexicon { get; private set; } = "goose";
        public event EventHandler<string>? CurrentLexiconChanged
        {
            add { }
            remove { }
        }

        public void RegisterLexicon(string pluginId, string lexiconKey, IReadOnlyDictionary<string, string> entries) { }
        public string GetString(string pluginId, string key, string? fallback = null) => fallback ?? key;

        public string CurrentTheme { get; private set; } = "default-dark";
        public event EventHandler<string>? CurrentThemeChanged
        {
            add { }
            remove { }
        }
        public IReadOnlyList<ThemePaletteModel> AvailablePalettes => Array.Empty<ThemePaletteModel>();

        public ThemePaletteModel? GetPalette(string themeName)
        {
            var p = new ThemePaletteModel { ThemeName = themeName };
            p.Colors["Background"] = "#121417";
            p.Colors["SurfaceElevated"] = "#1c2128";
            p.Colors["Accent"] = "#58a6ff";
            return p;
        }

        public void ShowToast(string title, string message, int durationMs = 3000) { }
    }

    [Fact]
    public void SqueezePluginModule_Metadata_MatchesSpecification()
    {
        var module = new SqueezePluginModule();
        Assert.Equal("squeeze", module.Id);
        Assert.Equal("SQUEEZE", module.DisplayName);
        Assert.Equal(20, module.OrderWeight);
        Assert.Equal("Media", module.Category);
        Assert.Equal("🎬", module.IconGlyph);
    }

    [Fact]
    public async Task SqueezePluginModule_InitializeAndLifecycle_WorksCleanly()
    {
        var module = new SqueezePluginModule();
        var host = new TestHostContext();
        await module.InitializeAsync(host);

        Assert.Equal("squeeze", module.Id);
        Assert.Equal("SQUEEZE", module.DisplayName);

        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }
}
