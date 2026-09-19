using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MADTOM.Console.ViewModels;

namespace MADTOM.Console.Views;

public partial class MainWindow : Window
{
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();

        Closing += OnWindowClosing;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() == WindowState.Minimized)
        {
            if (DataContext is ConsoleMainViewModel vm && vm.MinimizeToTray)
            {
                SendToTray();
            }
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExplicitExit)
            return;

        if (DataContext is ConsoleMainViewModel vm && vm.CloseToTray)
        {
            e.Cancel = true;
            SendToTray();
        }
    }

    public void SendToTray()
    {
        Hide();
        if (DataContext is ConsoleMainViewModel vm)
        {
            vm.EnterSleepMode();
        }
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (DataContext is ConsoleMainViewModel vm)
        {
            vm.WakeFromSleepMode();
        }
    }

    public void CloseForReal()
    {
        _isExplicitExit = true;
        Close();
    }

    private void OnSidebarSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is ConsoleMainViewModel vm && !vm.IsSidebarCollapsed)
        {
            vm.SidebarWidth += e.Vector.X;
        }
    }
}
