using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.IPCam.ViewModels;
using MADTOM.Plugins.IPCam.Views;

namespace MADTOM.Plugins.IPCam;

public sealed class IPCamPluginModule : IPluginModule
{
    private Control? _view;
    private IPluginHostContext? _hostContext;
    private IPCamViewModel? _viewModel;

    public string Id => "ipcam";
    public string DisplayName => "IP Camera";
    public string Description => "Direct video capture source and camera control client for MADTOM Hub";
    public string IconGlyph => "📷";
    public string Category => "Media";
    public Version Version => new(0, 1, 0);
    public int OrderWeight => 40;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _viewModel = new IPCamViewModel(hostContext);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_viewModel != null)
        {
            await _viewModel.DisposeAsync();
        }
    }

    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    public Control CreateView()
    {
        if (_view != null) return _view;
        _view = new IPCamView { DataContext = _viewModel };
        if (_viewModel != null)
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await _viewModel.InitializeAsync());
        return _view;
    }

    public async ValueTask DisposeAsync()
    {
        if (_viewModel != null)
        {
            await _viewModel.DisposeAsync();
            _viewModel = null;
        }
    }
}
