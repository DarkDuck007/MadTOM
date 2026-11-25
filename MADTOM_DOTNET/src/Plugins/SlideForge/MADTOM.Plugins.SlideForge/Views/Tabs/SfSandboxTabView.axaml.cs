using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Plugins.SlideForge.Views.Tabs;

public partial class SfSandboxTabView : UserControl
{
    private long? _lastSelectedChatId;

    public SfSandboxTabView()
    {
        InitializeComponent();
    }

    private void OnVmSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SfSandboxViewModel vm) return;

        long? currentId = vm.SelectedVm?.ChatId;
        if (currentId == _lastSelectedChatId) return;

        _lastSelectedChatId = currentId;
        if (vm.SelectedVm is not null)
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
