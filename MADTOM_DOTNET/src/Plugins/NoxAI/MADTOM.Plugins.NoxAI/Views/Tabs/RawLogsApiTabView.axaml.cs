using Avalonia.Controls;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class RawLogsApiTabView : UserControl
{
    public RawLogsApiTabView()
    {
        InitializeComponent();
        LogViewer.NeedChunk += (_, chunkId) =>
        {
            if (DataContext is RawLogsApiTabViewModel vm)
            {
                vm.RequestChunk(chunkId);
            }
        };
    }
}
