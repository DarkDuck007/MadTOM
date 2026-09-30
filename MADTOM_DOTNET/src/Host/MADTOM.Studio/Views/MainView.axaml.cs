using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Console.ViewModels;

namespace MADTOM.Console.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void OnSidebarSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is ConsoleMainViewModel vm && !vm.IsSidebarCollapsed)
        {
            vm.SidebarWidth += e.Vector.X;
        }
    }
}
