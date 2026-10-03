using System;
using Avalonia;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachKeyboardListener();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachKeyboardListener();
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
        if (DataContext is ConsoleMainViewModel vm && !vm.IsSidebarCollapsed)
        {
            vm.SidebarWidth += e.Vector.X;
        }
    }
}
