using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Plugins.SlideForge.Views.Tabs;

public partial class SfJobsTabView : UserControl
{
    public SfJobsTabView()
    {
        InitializeComponent();
    }

    private async void OnJobSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SfJobsViewModel vm && vm.SelectedJob is not null)
        {
            await vm.SelectAsync(vm.SelectedJob);
        }
    }

    private async void OnDownloadFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SfJobsViewModel vm) return;
        if (sender is not Button { Tag: SfJobFileItem file }) return;

        var result = await vm.FetchJobFileBytesAsync(file);
        if (result is null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var saved = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Job File",
            SuggestedFileName = result.Value.FileName,
        });

        if (saved is null) return;
        await using var stream = await saved.OpenWriteAsync();
        await stream.WriteAsync(result.Value.Bytes);
        await stream.FlushAsync();
    }
}
