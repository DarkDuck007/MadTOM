using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Plugins.SlideForge.Views.Tabs;

public partial class SfSandboxTabView : UserControl
{
    public SfSandboxTabView()
    {
        InitializeComponent();
    }

    private void OnVmSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SfSandboxViewModel vm && vm.SelectedVm is not null)
        {
            vm.FilesPath = "/data";
            _ = vm.RefreshFilesAsync();
        }
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SfSandboxViewModel vm && vm.SelectedFile is not null)
        {
            _ = vm.OpenFileAsync(vm.SelectedFile);
        }
    }
}
