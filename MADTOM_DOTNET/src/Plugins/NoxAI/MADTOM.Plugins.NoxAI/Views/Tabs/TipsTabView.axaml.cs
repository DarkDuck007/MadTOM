using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class TipsTabView : UserControl
{
    public TipsTabView()
    {
        InitializeComponent();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TipsTabViewModel vm)
        {
            _ = vm.RefreshAsync();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TipsTabViewModel vm)
        {
            _ = vm.SearchAsync();
        }
    }
}
