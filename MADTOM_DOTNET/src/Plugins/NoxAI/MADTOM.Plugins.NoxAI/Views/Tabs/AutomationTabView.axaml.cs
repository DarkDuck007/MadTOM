using Avalonia;
using Avalonia.Controls;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class AutomationTabView : UserControl
{
    public AutomationTabView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AutomationTabViewModel vm)
        {
            vm.IsTabActive = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is AutomationTabViewModel vm)
        {
            vm.IsTabActive = false;
        }
    }
}
