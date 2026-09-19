using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MadTOM;
using Xunit;

namespace MadTOM.Tests;

[Collection("GlobalSingletons")]
public class PluginLifecycleTests
{
    private sealed class TestHostContext : IPluginHostContext, ILexiconHost, IThemeHost, IPluginNotificationService
    {
        public IPluginNotificationService Notifications => this;
        public ILexiconHost Lexicons => this;
        public IThemeHost Themes => this;
        public IMessenger Messenger => WeakReferenceMessenger.Default;
        public ITrayMenuService Tray { get; } = new TrayMenuService();

        public string CurrentLexicon { get; private set; } = "goose";
        public event EventHandler<string>? CurrentLexiconChanged;

        public readonly Dictionary<string, Dictionary<string, string>> RegisteredStrings = new();
        public readonly List<string> DispatchedToasts = new();

        public void SetLexicon(string lexicon)
        {
            CurrentLexicon = lexicon;
            CurrentLexiconChanged?.Invoke(this, lexicon);
        }

        public void RegisterLexicon(string pluginId, string lexiconKey, IReadOnlyDictionary<string, string> entries)
        {
            var key = $"{pluginId}:{lexiconKey}";
            RegisteredStrings[key] = new Dictionary<string, string>(entries);
        }

        public string GetString(string pluginId, string key, string? fallback = null)
        {
            var lookup = $"{pluginId}:{CurrentLexicon}";
            if (RegisteredStrings.TryGetValue(lookup, out var dict) && dict.TryGetValue(key, out var val))
            {
                return val;
            }
            return fallback ?? key;
        }

        public string CurrentTheme { get; private set; } = "default-dark";
        public event EventHandler<string>? CurrentThemeChanged;
        public readonly Dictionary<string, MadTOM.Theming.ThemePaletteModel> CustomPalettes = new();
        public IReadOnlyList<MadTOM.Theming.ThemePaletteModel> AvailablePalettes =>
            CustomPalettes.Values.ToList();
        public MadTOM.Theming.ThemePaletteModel? GetPalette(string themeName) =>
            CustomPalettes.TryGetValue(themeName, out var p) ? p : null;

        public void SetTheme(string theme)
        {
            CurrentTheme = theme;
            CurrentThemeChanged?.Invoke(this, theme);
        }

        public void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000)
        {
            DispatchedToasts.Add(message);
        }
    }

    [Fact]
    public async Task TelemetryPluginModule_MetadataAndContract_AreValid()
    {
        var module = new TelemetryPluginModule();

        Assert.Equal("telemetry", module.Id);
        Assert.Equal("Telemetry", module.DisplayName);
        Assert.Equal("📊", module.IconGlyph);
        Assert.Equal("Operations", module.Category);
        Assert.True(module.OrderWeight > 0);
        Assert.True(await module.CanCloseAsync());
    }

    [Fact]
    public async Task TelemetryPluginModule_Lifecycle_InitializesStartsStopsCleanly()
    {
        var host = new TestHostContext();
        var module = new TelemetryPluginModule();

        // 1. Initialize
        await module.InitializeAsync(host);

        // 2. Start
        await module.StartAsync();

        // 3. Stop and Dispose
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void TestHostContext_LexiconRegistrationAndResolution_Works()
    {
        var host = new TestHostContext();

        host.RegisterLexicon("test-plugin", "goose", new Dictionary<string, string>
        {
            ["greeting"] = "HONK!"
        });

        host.RegisterLexicon("test-plugin", "feline", new Dictionary<string, string>
        {
            ["greeting"] = "MEOW!"
        });

        Assert.Equal("HONK!", host.GetString("test-plugin", "greeting"));

        host.SetLexicon("feline");
        Assert.Equal("MEOW!", host.GetString("test-plugin", "greeting"));

        Assert.Equal("fallback_val", host.GetString("test-plugin", "missing_key", "fallback_val"));
    }

    [Fact]
    public async Task TelemetryPluginModule_SubscribesToHostThemeChanges()
    {
        var host = new TestHostContext();
        var module = new TelemetryPluginModule();
        await module.InitializeAsync(host);

        host.SetTheme("pure-light");
        Assert.Equal("pure-light", MadTOM.Theming.ThemeService.Instance.CurrentTheme);

        host.SetTheme("anti-bleed-grey");
        Assert.Equal("anti-bleed-grey", MadTOM.Theming.ThemeService.Instance.CurrentTheme);

        await module.DisposeAsync();
    }
}

