using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MadTOM;

namespace MADTOM.Plugins.Telemetry.App;

public partial class MainWindow : Window
{
    private TelemetryPluginModule? _module;

    public MainWindow()
    {
        InitializeComponent();

        _module = new TelemetryPluginModule();
        var standaloneHostContext = new StandaloneHostContext();
        _module.InitializeAsync(standaloneHostContext).GetAwaiter().GetResult();
        _module.StartAsync().GetAwaiter().GetResult();

        ModuleContainer.Content = _module.CreateView();

        Closing += async (_, _) =>
        {
            if (_module != null)
            {
                await _module.DisposeAsync();
                _module = null;
            }
        };
    }

    private sealed class StandaloneHostContext : IPluginHostContext, ILexiconHost, IThemeHost, IPluginNotificationService
    {
        public IPluginNotificationService Notifications => this;
        public ILexiconHost Lexicons => this;
        public IThemeHost Themes => this;
        public IMessenger Messenger => WeakReferenceMessenger.Default;
        public ITrayMenuService? Tray => null;

        // ILexiconHost
        public string CurrentLexicon => "goose";
        public event EventHandler<string>? CurrentLexiconChanged;
        public void RegisterLexicon(string pluginId, string lexiconKey, System.Collections.Generic.IReadOnlyDictionary<string, string> entries) { }
        public string GetString(string pluginId, string key, string? fallback = null) => fallback ?? key;

        // IThemeHost
        public string CurrentTheme => "default-dark";
        public event EventHandler<string>? CurrentThemeChanged;
        public System.Collections.Generic.IReadOnlyList<MadTOM.Theming.ThemePaletteModel> AvailablePalettes =>
            MadTOM.Theming.ThemeService.Instance.AvailablePalettes;
        public MadTOM.Theming.ThemePaletteModel? GetPalette(string themeName) =>
            MadTOM.Theming.ThemeService.Instance.GetPalette(themeName);

        // IPluginNotificationService
        public void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000)
        {
            // Handled internally by Telemetry's internal toast overlay
        }
    }
}

