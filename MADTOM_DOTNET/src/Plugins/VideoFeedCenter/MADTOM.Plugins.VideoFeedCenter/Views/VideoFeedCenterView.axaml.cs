using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MADTOM.PluginContracts.Controls;
using MADTOM.Plugins.VideoFeedCenter.ViewModels;

namespace MADTOM.Plugins.VideoFeedCenter.Views;

public partial class VideoFeedCenterView : UserControl
{
    private VideoFeedViewerControl? _viewerControl;

    public VideoFeedCenterView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _viewerControl = this.FindControl<VideoFeedViewerControl>("ViewerControl");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is VideoFeedCenterViewModel vm)
        {
            vm.OnFrameReceived = (width, height, bgraData) =>
            {
                _viewerControl?.UpdateFrame(width, height, bgraData);
            };
        }
    }
}
