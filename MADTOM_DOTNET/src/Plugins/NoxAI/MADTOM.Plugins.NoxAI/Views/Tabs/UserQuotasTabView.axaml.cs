using Avalonia;
using Avalonia.Controls;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class UserQuotasTabView : UserControl
{
    public UserQuotasTabView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is UserQuotasTabViewModel vm)
        {
            vm.IsTabActive = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is UserQuotasTabViewModel vm)
        {
            vm.IsTabActive = false;
        }
    }
}
