using System;
using Avalonia.Controls;
using MADTOM.Plugins.IPCam.ViewModels;

namespace MADTOM.Plugins.IPCam.Views;

public partial class IPCamView : UserControl
{
    private readonly MADTOM.PluginContracts.Controls.VideoFeedViewerControl? _viewer;
    private IPCamViewModel? _currentVm;

    public IPCamView()
    {
        InitializeComponent();
        _viewer = this.FindControl<MADTOM.PluginContracts.Controls.VideoFeedViewerControl>("ViewfinderControl");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            _currentVm.ViewfinderFrameReceived -= OnViewfinderFrameReceived;
        }

        if (DataContext is IPCamViewModel vm)
        {
            _currentVm = vm;
            _currentVm.ViewfinderFrameReceived += OnViewfinderFrameReceived;
        }
    }

    private void OnViewfinderFrameReceived(uint width, uint height, byte[] frameData)
    {
        _viewer?.UpdateFrame(width, height, frameData);
    }
}
