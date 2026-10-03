using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    public bool IsPlaceholder => Module.Id is not ("telemetry" or "squeeze");

    [ObservableProperty]
    private bool _isSelected;

    public PluginItemViewModel(IPluginModule module)
    {
        Module = module;
    }
}

public sealed partial class ThemeOption : ObservableObject
{
    public string Key { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string PreviewBg { get; }
    public string PreviewAccent { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isActive;

    public ThemeOption(string key, string displayName, string category, string previewBg, string previewAccent, Action<string> onSelect, bool isActive = false)
    {
        Key = key;
        DisplayName = displayName;
        Category = category;
        PreviewBg = previewBg;
        PreviewAccent = previewAccent;
        IsActive = isActive;
        SelectCommand = new RelayCommand(() => onSelect(key));
    }
}

public sealed partial class ConsoleMainViewModel : ObservableObject
{
    private readonly ConsoleHostContext _hostContext;
    private readonly PluginManager _pluginManager;
    private DispatcherTimer? _toastTimer;
    private readonly AppSettings _appSettings;

    public IBrightnessService BrightnessService { get; }
    public OnScreenKeyboardViewModel Keyboard { get; } = new();

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = new();
    public ObservableCollection<ThemeOption> AvailableThemes { get; } = new();
    public ObservableCollection<DisplayDeviceItemViewModel> BrightnessDevices { get; } = new();

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private int _selectedSettingsTab = 0;

    [ObservableProperty]
    private bool _isBrightnessWarningOpen;

    [ObservableProperty]
    private string _brightnessWarningMessage = string.Empty;

    public DisplayDeviceItemViewModel? PendingBrightnessDevice { get; private set; }
    public int PendingBrightnessValue { get; private set; }

    public bool IsBrightnessAvailable => BrightnessService.IsSupported && BrightnessDevices.Count > 0;

    [ObservableProperty]
    private PluginItemViewModel? _selectedPlugin;

    [ObservableProperty]
    private Control? _currentPluginView;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    private double _uncollapsedSidebarWidth = 220;

    public double SidebarWidth
    {
        get => IsSidebarCollapsed ? 64 : _uncollapsedSidebarWidth;
        set
        {
            if (!IsSidebarCollapsed)
            {
                _uncollapsedSidebarWidth = Math.Clamp(value, 140, 600);
                OnPropertyChanged(nameof(SidebarWidth));
            }
        }
    }

    [ObservableProperty]
    private int _uiScalePercent = 100;

    public double UiScale => Math.Clamp(UiScalePercent, 10, 1000) / 100.0;

    partial void OnUiScalePercentChanged(int value)
    {
        OnPropertyChanged(nameof(UiScale));
        if (_appSettings != null)
        {
            _appSettings.UiScalePercent = value;
            AppSettingsStore.Save(_appSettings);
        }
    }

    [RelayCommand]
    public void SetUiScale(object? param)
    {
        if (param != null && int.TryParse(param.ToString(), out int p))
        {
            UiScalePercent = Math.Clamp(p, 10, 1000);
        }
    }

    [RelayCommand]
    public void ResetUiScale()
    {
        UiScalePercent = 100;
    }

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

    public IReadOnlyList<TrayBehaviorOption> TrayBehaviorOptions { get; } = new[]
    {
        new TrayBehaviorOption("close", "Close Button (X)", "Minimizes to tray when X is clicked"),
        new TrayBehaviorOption("minimize", "Minimize Button (_)", "Minimizes to tray when _ is clicked"),
        new TrayBehaviorOption("disabled", "Disabled", "Standard window controls (X exits MADTOM)")
    };

    public TrayBehaviorOption? SelectedTrayBehavior
    {
        get
        {
            if (CloseToTray && !MinimizeToTray) return TrayBehaviorOptions[0];
            if (MinimizeToTray && !CloseToTray) return TrayBehaviorOptions[1];
            if (!CloseToTray && !MinimizeToTray) return TrayBehaviorOptions[2];
            return TrayBehaviorOptions[0];
        }
        set
        {
            if (value == null) return;
            switch (value.Key)
            {
                case "close":
                    CloseToTray = true;
                    MinimizeToTray = false;
                    break;
                case "minimize":
                    CloseToTray = false;
                    MinimizeToTray = true;
                    break;
                case "disabled":
                    CloseToTray = false;
                    MinimizeToTray = false;
                    break;
            }
            OnPropertyChanged(nameof(SelectedTrayBehavior));
        }
    }

    [ObservableProperty]
    private bool _closeToTray = true;

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_appSettings != null)
        {
            _appSettings.CloseToTray = value;
            AppSettingsStore.Save(_appSettings);
        }
        OnPropertyChanged(nameof(SelectedTrayBehavior));
    }

    [ObservableProperty]
    private bool _minimizeToTray = false;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_appSettings != null)
        {
            _appSettings.MinimizeToTray = value;
            AppSettingsStore.Save(_appSettings);
        }
        OnPropertyChanged(nameof(SelectedTrayBehavior));
    }

    [ObservableProperty]
    private bool _isSleeping;

    public ThemeOption? SelectedThemeOption
    {
        get => AvailableThemes.FirstOrDefault(t => t.Key.Equals(ActiveTheme, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null && !value.Key.Equals(ActiveTheme, StringComparison.OrdinalIgnoreCase))
            {
                SwitchTheme(value.Key);
            }
        }
    }

    public ConsoleMainViewModel(ConsoleHostContext hostContext, PluginManager pluginManager, IBrightnessService? brightnessService = null)
    {
        _hostContext = hostContext;
        _pluginManager = pluginManager;
        BrightnessService = brightnessService ?? new LinuxBrightnessService();

        _appSettings = AppSettingsStore.Load();
        _activeTheme = _appSettings.Theme;
        _activeLexicon = _appSettings.Language;
        _isSidebarCollapsed = _appSettings.IsConsoleSidebarCollapsed;
        _uncollapsedSidebarWidth = 220;
        _uiScalePercent = _appSettings.UiScalePercent > 0 ? Math.Clamp(_appSettings.UiScalePercent, 10, 1000) : 100;
        _closeToTray = _appSettings.CloseToTray;
        _minimizeToTray = _appSettings.MinimizeToTray;

        _hostContext.SetTheme(_activeTheme);
        _hostContext.SetLexicon(_activeLexicon);

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

        InitializeThemes();
        Theming.ConsoleThemeManager.Instance.ThemesCollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(RefreshAvailableThemes);
        };

        RefreshBrightnessDevices();
    }

    public void RefreshBrightnessDevices()
    {
        BrightnessDevices.Clear();
        if (BrightnessService.IsSupported)
        {
            foreach (var dev in BrightnessService.GetDevices())
            {
                BrightnessDevices.Add(new DisplayDeviceItemViewModel(dev, BrightnessService, OnRequestLowBrightnessConfirmation));
            }
        }
        OnPropertyChanged(nameof(IsBrightnessAvailable));
    }

    private void OnRequestLowBrightnessConfirmation(DisplayDeviceItemViewModel device, int targetRaw, double percent)
    {
        PendingBrightnessDevice = device;
        PendingBrightnessValue = targetRaw;
        BrightnessWarningMessage = $"Setting display brightness to {percent:0.#}% ({targetRaw} / {device.MaxBrightness}) is below 5% and may turn off the display completely or make the screen unreadable.\n\nDo you wish to continue and apply this brightness level?";
        IsBrightnessWarningOpen = true;
    }

    [RelayCommand]
    public void ConfirmLowBrightness()
    {
        if (PendingBrightnessDevice != null)
        {
            PendingBrightnessDevice.ApplyRawBrightness(PendingBrightnessValue);
            ShowToast($"Applied low brightness ({PendingBrightnessValue})", "⚠️");
        }
        IsBrightnessWarningOpen = false;
    }

    [RelayCommand]
    public void CancelLowBrightness()
    {
        PendingBrightnessDevice?.RevertSliderToSafe();
        IsBrightnessWarningOpen = false;
    }

    [RelayCommand]
    public void OpenSettings()
    {
        RefreshBrightnessDevices();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    [RelayCommand]
    public void ToggleKeyboard()
    {
        Keyboard.ToggleVisibility();
    }

    private void InitializeThemes() => RefreshAvailableThemes();

    private void RefreshAvailableThemes()
    {
        AvailableThemes.Clear();
        foreach (var p in _hostContext.Themes.AvailablePalettes)
        {
            string bg = p.Colors.TryGetValue("Background", out var b) ? b : "#070a12";
            string acc = p.Colors.TryGetValue("Accent", out var a) ? a : "#06b6d4";
            string cat = p.GetEffectiveCategory();
            AvailableThemes.Add(new ThemeOption(
                p.ThemeName,
                p.DisplayName,
                cat,
                bg,
                acc,
                SwitchTheme,
                p.ThemeName.Equals(ActiveTheme, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [RelayCommand]
    public void SwitchTheme(string themeKey)
    {
        ActiveTheme = themeKey;
        foreach (var t in AvailableThemes)
        {
            t.IsActive = t.Key.Equals(themeKey, StringComparison.OrdinalIgnoreCase);
        }

        _hostContext.SetTheme(themeKey);
        _appSettings.Theme = themeKey;
        AppSettingsStore.Save(_appSettings);
        ShowToast($"Theme changed to {themeKey}", "🎨");
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
        OnPropertyChanged(nameof(SidebarWidth));
        _appSettings.IsConsoleSidebarCollapsed = IsSidebarCollapsed;
        AppSettingsStore.Save(_appSettings);
    }

    [RelayCommand]
    public void SwitchLexicon(string lexiconKey)
    {
        ActiveLexicon = lexiconKey;
        _hostContext.SetLexicon(lexiconKey);
        _appSettings.Language = lexiconKey;
        AppSettingsStore.Save(_appSettings);
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

    public void EnterSleepMode()
    {
        IsSleeping = true;
        _toastTimer?.Stop();
        IsToastVisible = false;
    }

    public void WakeFromSleepMode()
    {
        IsSleeping = false;
    }

    public bool SupportsSystemTray => MADTOM.PluginContracts.PlatformCapabilities.SupportsFloatingWindows;

    [RelayCommand]
    public async Task ExitApplicationAsync()
    {
        await Hosting.AppShutdownCoordinator.ShutdownAsync();
    }
}

public sealed class TrayBehaviorOption
{
    public string Key { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public TrayBehaviorOption(string key, string displayName, string description)
    {
        Key = key;
        DisplayName = displayName;
        Description = description;
    }

    public override string ToString() => DisplayName;
}
