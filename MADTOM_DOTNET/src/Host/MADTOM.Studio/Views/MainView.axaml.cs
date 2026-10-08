using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MADTOM.Console.ViewModels;

namespace MADTOM.Console.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachKeyboardListener();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachKeyboardListener();
        if (DataContext is ConsoleMainViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName is nameof(ConsoleMainViewModel.SidebarState)
                    or nameof(ConsoleMainViewModel.IsSidebarCollapsed)
                    or nameof(ConsoleMainViewModel.IsSidebarExpanded)
                    or nameof(ConsoleMainViewModel.IsSidebarVisible)
                    or nameof(ConsoleMainViewModel.SidebarWidth))
                {
                    ResetSidebarColumnAuto();
                }
            };
        }
    }

    private void ResetSidebarColumnAuto()
    {
        if (this.FindControl<Grid>("MainShellGrid") is { } grid)
        {
            if (grid.ColumnDefinitions.Count > 0)
                grid.ColumnDefinitions[0].Width = GridLength.Auto;
            if (grid.ColumnDefinitions.Count > 1)
                grid.ColumnDefinitions[1].Width = GridLength.Auto;
        }
    }

    private void AttachKeyboardListener()
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel && DataContext is ConsoleMainViewModel vm)
        {
            vm.Keyboard.AttachToTopLevel(topLevel);
        }
    }

    private void OnSidebarSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is ConsoleMainViewModel vm && vm.IsSidebarExpanded)
        {
            vm.SidebarWidth += e.Vector.X;
        }
        ResetSidebarColumnAuto();
    }
}
