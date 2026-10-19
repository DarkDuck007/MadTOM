using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MADTOM.PluginContracts;
using MADTOM.Plugins.VideoFeedCenter.ViewModels;
using MADTOM.Plugins.VideoFeedCenter.Views;

namespace MADTOM.Plugins.VideoFeedCenter;

public sealed class VideoFeedCenterPluginModule : IPluginModule
{
    private Control? _view;
    private IPluginHostContext? _hostContext;
    private VideoFeedCenterViewModel? _viewModel;

    public string Id => "videofeedcenter";
    public string DisplayName => "Video Feed Center";
    public string Description => "Multi-source video ingest, SRT monitoring, and proxy recording hub";
    public string IconGlyph => "🎥";
    public string Category => "Media";
    public Version Version => new(0, 1, 0);
    public int OrderWeight => 30;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        _hostContext = hostContext;
        _viewModel = new VideoFeedCenterViewModel(hostContext);
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
        _view = new VideoFeedCenterView { DataContext = _viewModel };
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
