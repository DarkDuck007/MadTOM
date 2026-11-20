using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class SandboxTabView : UserControl
{
    public SandboxTabView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SandboxTabViewModel vm)
        {
            vm.IsTabActive = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (DataContext is SandboxTabViewModel vm)
        {
            vm.IsTabActive = false;
        }
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SandboxTabViewModel vm && vm.SelectedFile != null)
        {
            _ = vm.OpenFileAsync(vm.SelectedFile);
        }
    }
}
