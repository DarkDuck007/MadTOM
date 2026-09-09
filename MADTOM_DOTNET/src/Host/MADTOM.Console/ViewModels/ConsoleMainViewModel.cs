using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Console.Hosting;
using MADTOM.PluginContracts;

namespace MADTOM.Console.ViewModels;

public sealed partial class PluginItemViewModel : ObservableObject
{
    public IPluginModule Module { get; }

    public string Id => Module.Id;
    public string DisplayName => Module.DisplayName;
    public string Description => Module.Description;
    public string IconGlyph => Module.IconGlyph;
    public string Category => Module.Category;
    public int OrderWeight => Module.OrderWeight;

    [ObservableProperty]
    private bool _isSelected;

    public PluginItemViewModel(IPluginModule module)
    {
        Module = module;
    }
}

public sealed partial class ConsoleMainViewModel : ObservableObject
{
    private readonly ConsoleHostContext _hostContext;
    private readonly PluginManager _pluginManager;
    private DispatcherTimer? _toastTimer;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = new();

    [ObservableProperty]
    private PluginItemViewModel? _selectedPlugin;

    [ObservableProperty]
    private Control? _currentPluginView;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private double _sidebarWidth = 220;

    [ObservableProperty]
    private string _activeLexicon = "goose";

    [ObservableProperty]
    private string _activeTheme = "default-dark";

    // Toast state
    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private string _toastIcon = "ℹ️";

    public ConsoleMainViewModel(ConsoleHostContext hostContext, PluginManager pluginManager)
    {
        _hostContext = hostContext;
        _pluginManager = pluginManager;

        _hostContext.ToastTriggered += OnHostToastTriggered;

        // Populate available plugins
        foreach (var desc in _pluginManager.LoadedPlugins.OrderBy(p => p.Module.OrderWeight))
        {
            Plugins.Add(new PluginItemViewModel(desc.Module));
        }

        // Select first plugin by default
        var first = Plugins.FirstOrDefault();
        if (first != null)
        {
            SelectPlugin(first);
        }
    }

    [RelayCommand]
    public void SelectPlugin(PluginItemViewModel plugin)
    {
        if (SelectedPlugin == plugin && CurrentPluginView != null)
            return;

        foreach (var p in Plugins)
        {
            p.IsSelected = (p == plugin);
        }

        SelectedPlugin = plugin;

        // Visual unmount and view creation
        CurrentPluginView = plugin.Module.CreateView();
    }

    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        SidebarWidth = IsSidebarCollapsed ? 64 : 220;
    }

    [RelayCommand]
    public void SwitchLexicon(string lexiconKey)
    {
        ActiveLexicon = lexiconKey;
        _hostContext.SetLexicon(lexiconKey);
        ShowToast($"Language switched to {lexiconKey.ToUpperInvariant()}", "🌐");
    }

    private void OnHostToastTriggered(object? sender, (string Message, string Icon, int DurationMs) e)
    {
        ShowToast(e.Message, e.Icon, e.DurationMs);
    }

    public void ShowToast(string message, string icon = "ℹ️", int durationMs = 3000)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _toastTimer?.Stop();
            ToastMessage = message;
            ToastIcon = icon;
            IsToastVisible = true;

            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(durationMs)
            };
            _toastTimer.Tick += (_, _) =>
            {
                IsToastVisible = false;
                _toastTimer?.Stop();
            };
            _toastTimer.Start();
        });
    }
}

