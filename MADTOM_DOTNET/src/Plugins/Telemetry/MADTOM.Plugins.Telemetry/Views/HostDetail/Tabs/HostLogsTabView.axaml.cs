using Avalonia.Controls;
using MadTOM.Controls.Logs;
using MadTOM.ViewModels;

namespace MadTOM.Views.HostDetail.Tabs;

public partial class HostLogsTabView : UserControl
{
    public HostLogsTabView()
    {
        InitializeComponent();
        var viewer = this.FindControl<VirtualLogViewControl>("LogViewer");
        if (viewer != null)
        {
            viewer.NeedChunk += (_, chunkId) =>
            {
                if (DataContext is HostLogsTabViewModel vm)
                {
                    vm.HandleNeedChunk(chunkId);
                }
            };
        }
    }
}
