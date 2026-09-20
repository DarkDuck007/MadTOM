using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using SQUEEZE.Views;

namespace MADTOM.Plugins.Squeeze;

public sealed class SqueezePluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private SqueezeTrayMenuSection? _traySection;
    private IDisposable? _trayRegistration;

    public string Id => "squeeze";
    public string DisplayName => "SQUEEZE";
    public string Description => "Distributed media transcoding, hardware-accelerated video/audio encoding toolkit";
    public string IconGlyph => "🎬";
    public string Category => "Media";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 20;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;

        // Apply theme from host and subscribe to theme changes
        if (hostContext.Themes != null)
        {
            SqueezeThemeService.ApplyThemeFromHost(hostContext.Themes);
            hostContext.Themes.CurrentThemeChanged += OnHostThemeChanged;
        }

        // Initialize view model
        _mainViewModel = new MainViewModel();

        // Register Tray Menu Section if tray service is available
        if (hostContext.Tray != null)
        {
            _traySection = new SqueezeTrayMenuSection(_mainViewModel);
            _trayRegistration = hostContext.Tray.RegisterSection(_traySection);
        }

        return Task.CompletedTask;
    }

    private void OnHostThemeChanged(object? sender, string themeName)
    {
        if (_hostContext?.Themes != null)
        {
            SqueezeThemeService.ApplyThemeFromHost(_hostContext.Themes);
        }
    }

    public Control CreateView()
    {
        _mainViewModel ??= new MainViewModel();
        _mainView = new MainView
        {
            DataContext = _mainViewModel
        };
        return _mainView;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> CanCloseAsync()
    {
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        if (_hostContext?.Themes != null)
        {
            _hostContext.Themes.CurrentThemeChanged -= OnHostThemeChanged;
        }

        _trayRegistration?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SqueezeTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "squeeze";
    public string PluginName => "MADTOM SQUEEZE";
    public int OrderWeight => 20;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public SqueezeTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        var queue = _mainViewModel.JobQueue;
        int activeJobs = queue?.Jobs?.Count ?? 0;
        string status = activeJobs > 0 ? $"{activeJobs} jobs queued" : "Idle (Queue empty)";

        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem($"Status: {status}"),
            TrayMenuItemDescriptor.TextItem("Preset: " + _mainViewModel.ActivePresetTitle)
        };
    }
}
