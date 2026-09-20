using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.AudioSync.ViewModels;
using MADTOM.Plugins.AudioSync.Views;

namespace MADTOM.Plugins.AudioSync;

public sealed class AudioSyncPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "audio-sync";
    public string DisplayName => "AUDIOSYNC";
    public string Description => "Multi-room audio distribution, PTP clock synchronization, and latency buffer calibration";
    public string IconGlyph => "🔊";
    public string Category => "Audio";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 60;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new AudioSyncTrayMenuSection(_mainViewModel);
            _trayRegistration = hostContext.Tray.RegisterSection(section);
        }

        return Task.CompletedTask;
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

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync()
    {
        _trayRegistration?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AudioSyncTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "audio-sync";
    public string PluginName => "AUDIOSYNC";
    public int OrderWeight => 60;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public AudioSyncTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Clock: " + _mainViewModel.MasterClockStatus),
            TrayMenuItemDescriptor.TextItem("Stream: " + _mainViewModel.ActiveStreamState)
        };
    }
}

