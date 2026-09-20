using System;
using System.ComponentModel;
using System.Collections.Generic;
using Avalonia.Media;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SQUEEZE.ViewModels;

namespace SQUEEZE.Views;

public partial class MainView
{
    private sealed class DialogSize
    {
        public Grid Dialog { get; }
        public Grid Handles { get; } = new();
        public double Width { get; set; }
        public double Height { get; set; }
        public DialogSize(Grid dialog, double width, double height) { Dialog = dialog; Width = width; Height = height; }
    }
    private readonly List<DialogSize> _otherDialogs = new();
    private bool _compact;
    private bool _showQueue;
    private bool _desktopQueueCollapsed;
    private double _presetWidth = 800;
    private double _presetHeight = 520;
    private MainViewModel? _layoutViewModel;
    private IInputElement? _focusBeforeDialog;

    private void InitializeResponsiveLayout()
    {
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
        {
            if (e.Source is TabControl) Dispatcher.UIThread.Post(UpdateResponsiveLayout);
        });
        foreach (var (name, x, y) in new[]
        {
            ("Left", -1, 0), ("Right", 1, 0), ("Top", 0, -1), ("Bottom", 0, 1),
            ("TopLeft", -1, -1), ("TopRight", 1, -1), ("BottomLeft", -1, 1), ("BottomRight", 1, 1)
        })
        {
            CenteredDialogResizer.Attach(PresetDialog, this.FindControl<Control>("Resize" + name)!, x, y,
                (width, height) =>
                {
                    _presetWidth = Math.Clamp(width, Math.Min(480, Math.Max(1, Bounds.Width - 24)), Math.Max(1, Bounds.Width - 24));
                    _presetHeight = Math.Clamp(height, Math.Min(320, Math.Max(1, Bounds.Height - 24)), Math.Max(1, Bounds.Height - 24));
                    UpdateDialogBounds();
                });
        }
        AttachDialogResizing(HardwareDialog, 620, 520);
        AttachDialogResizing(SavePresetDialog, 460, 340);
        AttachDialogResizing(SettingsWindow, 600, 500);
        HardwareDialog.SizeChanged += (_, _) =>
        {
            HardwareMetrics.Columns = HardwareDialog.Bounds.Width < 480 ? 1 : 3;
        };
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => AttachLayoutViewModel();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_layoutViewModel != null)
            {
                _layoutViewModel.PropertyChanged -= OnLayoutPropertyChanged;
                _layoutViewModel.PropertyChanging -= OnLayoutPropertyChanging;
            }
            _layoutViewModel = null;
        };
    }

    private void AttachDialogResizing(Grid dialog, double width, double height)
    {
        var state = new DialogSize(dialog, width, height);
        _otherDialogs.Add(state);
        foreach (var (name, x, y) in new[]
        {
            ("Left", -1, 0), ("Right", 1, 0), ("Top", 0, -1), ("Bottom", 0, 1),
            ("TopLeft", -1, -1), ("TopRight", 1, -1), ("BottomLeft", -1, 1), ("BottomRight", 1, 1)
        })
        {
            var handle = new Border
            {
                Name = dialog.Name + "Resize" + name,
                Background = Brushes.Transparent,
                HorizontalAlignment = x < 0 ? Avalonia.Layout.HorizontalAlignment.Left : x > 0 ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = y < 0 ? Avalonia.Layout.VerticalAlignment.Top : y > 0 ? Avalonia.Layout.VerticalAlignment.Bottom : Avalonia.Layout.VerticalAlignment.Stretch,
                Width = x == 0 ? double.NaN : 6,
                Height = y == 0 ? double.NaN : 6,
                Margin = new Thickness(x == 0 ? 6 : 0, y == 0 ? 6 : 0),
                Cursor = new Cursor(x == 0 ? StandardCursorType.SizeNorthSouth : y == 0 ? StandardCursorType.SizeWestEast : x == y ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner)
            };
            state.Handles.Children.Add(handle);
            CenteredDialogResizer.Attach(dialog, handle, x, y, (w, h) =>
            {
                state.Width = Math.Clamp(w, Math.Min(340, Math.Max(1, Bounds.Width - 16)), Math.Max(1, Bounds.Width - 16));
                state.Height = Math.Clamp(h, Math.Min(260, Math.Max(1, Bounds.Height - 16)), Math.Max(1, Bounds.Height - 16));
                UpdateDialogBounds();
            });
        }
        var grip = new PathIcon
        {
            Data = Geometry.Parse("M1,10 L10,1 10,3 3,10 Z M6,10 L10,6 10,8 8,10 Z"),
            Width = 10, Height = 10, Margin = new Thickness(3), IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
        };
        grip.Classes.Add("resize-grip");
        state.Handles.Children.Add(grip);
        dialog.Children.Add(state.Handles);
    }

    private void AttachLayoutViewModel()
    {
        if (_layoutViewModel != null)
        {
            _layoutViewModel.PropertyChanged -= OnLayoutPropertyChanged;
            _layoutViewModel.PropertyChanging -= OnLayoutPropertyChanging;
        }
        _layoutViewModel = DataContext as MainViewModel;
        if (_layoutViewModel != null)
        {
            _layoutViewModel.PropertyChanged += OnLayoutPropertyChanged;
            _layoutViewModel.PropertyChanging += OnLayoutPropertyChanging;
        }
        UpdateResponsiveLayout();
    }

    private void OnLayoutPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        // Capture before the modal's bindings disable the previously focused control.
        if (_layoutViewModel?.IsAnyDialogOpen == false && e.PropertyName is
            nameof(MainViewModel.IsMegaMenuOpen) or nameof(MainViewModel.IsSettingsOpen) or
            nameof(MainViewModel.IsAddPresetModalOpen) or nameof(MainViewModel.IsRigInfoOpen))
            _focusBeforeDialog = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsJobQueueCollapsed)) UpdateWorkspace();
        if (e.PropertyName != nameof(MainViewModel.IsAnyDialogOpen) || _layoutViewModel == null) return;
        if (_layoutViewModel.IsAnyDialogOpen)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_layoutViewModel?.IsMegaMenuOpen == true) PresetSearch.Focus();
                else if (_layoutViewModel?.IsAddPresetModalOpen == true) PresetNameInput.Focus();
                else if (_layoutViewModel?.IsSettingsOpen == true)
                    ConnectionDialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
                else if (_layoutViewModel?.IsRigInfoOpen == true)
                    this.FindControl<Button>("RescanButton")?.Focus();
            });
        }
        else
        {
            var previous = _focusBeforeDialog;
            Dispatcher.UIThread.Post(() => previous?.Focus());
        }
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm) return;
        if (vm.IsSettingsOpen) vm.CloseSettings();
        else if (vm.IsRigInfoOpen) vm.CloseRigInfo();
        else if (vm.IsAddPresetModalOpen) vm.CloseAddPresetModal();
        else if (vm.IsMegaMenuOpen) vm.CloseMegaMenu();
        else return;
        e.Handled = true;
    }

    private void UpdateResponsiveLayout()
    {
        if (Workspace == null || Bounds.Width <= 0) return;
        bool compact = Bounds.Width < 760;
        if (compact != _compact && DataContext is MainViewModel vm)
        {
            if (compact)
            {
                _desktopQueueCollapsed = vm.IsJobQueueCollapsed;
                if (vm.IsJobQueueCollapsed) vm.ToggleJobQueueCollapse();
            }
            else if (vm.IsJobQueueCollapsed != _desktopQueueCollapsed) vm.ToggleJobQueueCollapse();
        }
        _compact = compact;
        Classes.Set("compact", compact);
        MobileNavigation.IsVisible = compact;
        HeaderTelemetry.IsVisible = !compact;
        HardwareMetrics.Columns = compact ? 1 : 3;
        EditorScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        CollapseQueueButton.IsVisible = !compact;
        foreach (var grid in this.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("form-row")))
        {
            grid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "120,*");
            grid.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto");
            foreach (var child in grid.Children.Skip(1))
            {
                Grid.SetColumn(child, compact ? 0 : 1);
                Grid.SetRow(child, compact ? 1 : 0);
            }
        }
        UpdateWorkspace();
        UpdateDialogBounds();
    }

    private void UpdateWorkspace()
    {
        if (DataContext is not MainViewModel vm || Workspace == null) return;
        // Keep the bound sidebar width untouched so desktop splitter preferences survive rotation.
        Grid.SetColumn(QueuePanel, _compact ? 2 : 0);
        QueuePanel.IsVisible = !_compact || _showQueue;
        EditorPanel.IsVisible = !_compact || !_showQueue;
        Workspace.ColumnDefinitions[0].MinWidth = _compact ? 0 : 38;
        Workspace.ColumnDefinitions[0].MaxWidth = _compact ? 0 : Math.Min(600, Math.Max(200, Bounds.Width - 400));
        QueueSplitter.IsVisible = !_compact && !vm.IsJobQueueCollapsed;
    }

    private void UpdateDialogBounds()
    {
        PresetDialog.Width = Math.Min(_compact ? Bounds.Width - 16 : _presetWidth, Math.Max(1, Bounds.Width - 16));
        PresetDialog.Height = Math.Min(_compact ? Bounds.Height - 16 : _presetHeight, Math.Max(1, Bounds.Height - 16));
        ResizeHandles.IsVisible = !_compact;
        ResetSizeButton.IsVisible = !_compact;
        foreach (var state in _otherDialogs)
        {
            state.Dialog.Width = Math.Max(1, Math.Min(_compact ? Bounds.Width - 16 : state.Width, Bounds.Width - 16));
            state.Dialog.Height = Math.Max(1, Math.Min(state.Height, Bounds.Height - 16));
            state.Handles.IsVisible = !_compact;
        }
    }

    private async void CopyEncoderSummary(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { vm.SourceWarningMessage = "Clipboard is not available on this device."; return; }
            await clipboard.SetTextAsync(vm.TranscodeParams.RawCliInput);
        }
        catch (Exception ex) { vm.SourceWarningMessage = $"Could not copy preview: {ex.Message}"; }
    }

    private void ShowEditor(object? sender, RoutedEventArgs e) { _showQueue = false; UpdateWorkspace(); }
    private void ShowQueue(object? sender, RoutedEventArgs e) { _showQueue = true; UpdateWorkspace(); }
    private void ResetPresetSize(object? sender, RoutedEventArgs e)
    {
        _presetWidth = 800;
        _presetHeight = 520;
        UpdateDialogBounds();
    }
}
