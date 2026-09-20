using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.MediaCenter.ViewModels;
using MADTOM.Plugins.MediaCenter.Views;

namespace MADTOM.Plugins.MediaCenter;

public sealed class MediaCenterPluginModule : IPluginModule
{
    private IPluginHostContext? _hostContext;
    private MainViewModel? _mainViewModel;
    private MainView? _mainView;
    private IDisposable? _trayRegistration;

    public string Id => "media-center";
    public string DisplayName => "MediaCenter";
    public string Description => "Local & network media streaming, library management, and DLNA/UPnP playback";
    public string IconGlyph => "📺";
    public string Category => "Media";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 40;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _mainViewModel = new MainViewModel();

        if (hostContext.Tray != null)
        {
            var section = new MediaCenterTrayMenuSection(_mainViewModel);
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

public sealed class MediaCenterTrayMenuSection : ITrayMenuSection
{
    private readonly MainViewModel _mainViewModel;

    public string SectionId => "media-center";
    public string PluginName => "MediaCenter";
    public int OrderWeight => 40;

    public event EventHandler? ItemsChanged
    {
        add { }
        remove { }
    }

    public MediaCenterTrayMenuSection(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        return new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem("Library: " + _mainViewModel.LibraryStatus),
            TrayMenuItemDescriptor.TextItem("Player: " + _mainViewModel.ActivePlayerState)
        };
    }
}
