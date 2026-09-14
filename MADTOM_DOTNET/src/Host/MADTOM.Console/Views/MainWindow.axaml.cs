using Avalonia.Controls;
using Avalonia.Input;

namespace MADTOM.Console.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSidebarSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is ViewModels.ConsoleMainViewModel vm && !vm.IsSidebarCollapsed)
        {
            vm.SidebarWidth += e.Vector.X;
        }
    }
}

