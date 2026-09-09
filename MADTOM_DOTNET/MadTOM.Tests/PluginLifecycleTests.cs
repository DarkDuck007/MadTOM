using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MadTOM;
using Xunit;

namespace MadTOM.Tests;

[Collection("Lexicon")]
public class PluginLifecycleTests
{
    private sealed class TestHostContext : IPluginHostContext, ILexiconHost, IThemeHost, IPluginNotificationService
    {
        public IPluginNotificationService Notifications => this;
        public ILexiconHost Lexicons => this;
        public IThemeHost Themes => this;
        public IMessenger Messenger => WeakReferenceMessenger.Default;

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

        public string CurrentTheme => "default-dark";
        public event EventHandler<string>? CurrentThemeChanged;

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
        Assert.Equal("Telemetry & Operations", module.DisplayName);
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
}

