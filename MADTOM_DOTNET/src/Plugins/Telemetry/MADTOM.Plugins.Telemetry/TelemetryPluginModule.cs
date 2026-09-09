using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using MADTOM.PluginContracts;
using MadTOM.Localization;
using MadTOM.Services;
using MadTOM.ViewModels;
using MadTOM.Views;

namespace MadTOM;

/// <summary>
/// Plugin module implementation for MADTOM Telemetry &amp; Operations Matrix.
/// Enables in-process visual mounting inside MADTOM Console as well as isolated teardown.
/// </summary>
public class TelemetryPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MockTelemetryDataProvider? _telemetryProvider;
    private MainViewModel? _mainViewModel;
    private TelemetryRootView? _rootView;

    public string Id => "telemetry";
    public string DisplayName => "Telemetry & Operations";
    public string Description => "High-density fleet telemetry, node deep-dive, and global network radar";
    public string IconGlyph => "📊";
    public string Category => "Operations";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 10;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;

        // Register bundled lexicons with host
        RegisterBundledLexicons(hostContext.Lexicons);

        // Listen for global culture/lexicon changes from MADTOM Console
        hostContext.Lexicons.CurrentLexiconChanged += OnGlobalLexiconChanged;

        return Task.CompletedTask;
    }

    private void RegisterBundledLexicons(ILexiconHost lexiconHost)
    {
        string[] packs = ["standard", "feline", "goose"];
        foreach (var pack in packs)
        {
            try
            {
                var uri = new Uri($"avares://MADTOM.Plugins.Telemetry/Assets/Lexicons/{pack}.json");
                if (!AssetLoader.Exists(uri))
                {
                    uri = new Uri($"avares://MadTOM/Assets/Lexicons/{pack}.json");
                }

                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed != null)
                    {
                        lexiconHost.RegisterLexicon(Id, pack, parsed);
                    }
                }
            }
            catch
            {
                // Fallback handled gracefully
            }
        }
    }

    private void OnGlobalLexiconChanged(object? sender, string newLexicon)
    {
        LexiconService.Instance.LoadLexicon(newLexicon);
    }

    public Control CreateView()
    {
        if (_rootView != null)
            return _rootView;

        _telemetryProvider ??= new MockTelemetryDataProvider(startBackgroundTimer: true);

        // Wire notification dispatch to host context
        var notifService = new HostNotificationBridge(_hostContext);

        _mainViewModel = new MainViewModel(
            _telemetryProvider,
            LexiconService.Instance,
            notifService);

        _rootView = new TelemetryRootView
        {
            DataContext = _mainViewModel
        };

        return _rootView;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Telemetry stream is initialized and active
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _telemetryProvider?.Dispose();
        _telemetryProvider = null;
        return Task.CompletedTask;
    }

    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_hostContext != null)
        {
            _hostContext.Lexicons.CurrentLexiconChanged -= OnGlobalLexiconChanged;
        }

        _rootView = null;
        _mainViewModel = null;
    }

    private sealed class HostNotificationBridge : INotificationService
    {
        private readonly IPluginHostContext? _hostContext;

        public event EventHandler<(string Message, string Icon)>? ToastRequested;

        public HostNotificationBridge(IPluginHostContext? hostContext)
        {
            _hostContext = hostContext;
        }

        public void ShowToast(string message, string icon = "ℹ️")
        {
            ToastRequested?.Invoke(this, (message, icon));
            if (_hostContext != null)
            {
                _hostContext.Notifications.ShowToast(message, icon);
            }
            else
            {
                NotificationService.Instance.ShowToast(message, icon);
            }
        }
    }
}
