using System;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.IPCam.ViewModels;

namespace MADTOM.Plugins.IPCam.App;

public partial class MainWindow : Window
{
    private IPCamViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new IPCamViewModel(new StandaloneHostContext());
        _ = _viewModel.InitializeAsync();
        CameraView.DataContext = _viewModel;
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_viewModel != null)
        {
            await _viewModel.DisposeAsync();
        }
    }
}

internal sealed class StandaloneHostContext : IPluginHostContext
{
    public IPluginNotificationService Notifications { get; } = new DummyNotificationService();
    public ILexiconHost Lexicons { get; } = new DummyLexiconHost();
    public IThemeHost Themes { get; } = new DummyThemeHost();
    public CommunityToolkit.Mvvm.Messaging.IMessenger Messenger { get; } = CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default;
    public ITrayMenuService? Tray => null;

    private sealed class DummyNotificationService : IPluginNotificationService
    {
        public void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000) { }
    }

    private sealed class DummyLexiconHost : ILexiconHost
    {
        public string CurrentLexicon => "standard";
        public event EventHandler<string>? CurrentLexiconChanged { add { } remove { } }
        public void RegisterLexicon(string pluginId, string lexiconKey, System.Collections.Generic.IReadOnlyDictionary<string, string> entries) { }
        public string GetString(string pluginId, string key, string? fallback = null) => fallback ?? key;
    }

    private sealed class DummyThemeHost : IThemeHost
    {
        public string CurrentTheme => "default-dark";
        public event EventHandler<string>? CurrentThemeChanged { add { } remove { } }
        public System.Collections.Generic.IReadOnlyList<MadTOM.Theming.ThemePaletteModel> AvailablePalettes => System.Array.Empty<MadTOM.Theming.ThemePaletteModel>();
        public MadTOM.Theming.ThemePaletteModel? GetPalette(string themeName) => null;
    }
}
