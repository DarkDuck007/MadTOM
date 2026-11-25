using Avalonia;
using Avalonia.Controls;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class AgySessionsTabView : UserControl
{
    public AgySessionsTabView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AgySessionsTabViewModel vm)
        {
            vm.IsTabActive = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is AgySessionsTabViewModel vm)
        {
            vm.IsTabActive = false;
        }
    }
}
