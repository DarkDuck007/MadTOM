using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MadTOM.ViewModels;

namespace MadTOM.Views;

public partial class CompressionDiagnosticsWindow : Window
{
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DiagnosticsPanelState _state = new();

    public CompressionDiagnosticsWindow()
    {
        InitializeComponent();
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); if (_state.ShouldDismiss) Close(); };
        _refresh.Tick += (_, _) => Refresh();
        PointerEntered += (_, _) => { _state.PointerInside = true; _dismiss.Stop(); };
        PointerExited += (_, _) => { _state.PointerInside = false; ScheduleDismiss(); };
        PointerPressed += (_, _) => _state.OpenByTap();
        Deactivated += (_, _) => { if (!_state.Pinned) Close(); };
        Opened += (_, _) => { Refresh(); _refresh.Start(); };
        Closed += (_, _) => { _dismiss.Stop(); _refresh.Stop(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public void OpenByTap() { _state.OpenByTap(); _dismiss.Stop(); Activate(); }
    public void AnchorEntered() { _state.AnchorInside = true; _dismiss.Stop(); }
    public void AnchorExited() { _state.AnchorInside = false; ScheduleDismiss(); }
    private void ScheduleDismiss() { if (_state.ShouldDismiss) _dismiss.Start(); }
    private void Refresh() { if (DataContext is SidebarViewModel vm) vm.RefreshCompressionDiagnostics(); }
    private void TogglePin(object? sender, RoutedEventArgs e) => _state.Pinned = PinButton.IsChecked == true;
    private void ClosePanel(object? sender, RoutedEventArgs e) => Close();
    private void DragHeader(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch)
        {
            OpenByTap();
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }
}
