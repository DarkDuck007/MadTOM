using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views.Tabs;

public partial class MemoriesRagTabView : UserControl
{
    public MemoriesRagTabView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MemoriesRagTabViewModel vm)
            {
                _ = vm.SearchMemoriesHybrid();
            }
        }
    }
}
