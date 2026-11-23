using Avalonia.Controls;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Plugins.SlideForge.Views.Tabs;

public partial class SfGuidesTabView : UserControl
{
    public SfGuidesTabView()
    {
        InitializeComponent();
    }

    private async void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SfGuidesViewModel vm && vm.SelectedFile is not null)
        {
            await vm.OpenAsync(vm.SelectedFile.Path);
        }
    }
}
