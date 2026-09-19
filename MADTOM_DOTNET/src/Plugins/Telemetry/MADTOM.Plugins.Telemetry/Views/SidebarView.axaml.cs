using System;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using MadTOM.ViewModels;

namespace MadTOM.Views;

public partial class SidebarView : UserControl
{
    private CompressionDiagnosticsWindow? _compressionWindow;
    private readonly DispatcherTimer _summaryTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SidebarView()
    {
        InitializeComponent();
        _summaryTimer.Tick += (_, _) =>
        {
            if (_compressionWindow == null && DataContext is SidebarViewModel vm) vm.RefreshCompressionSummary();
        };
        AttachedToVisualTree += (_, _) => _summaryTimer.Start();
        DetachedFromVisualTree += (_, _) => { _summaryTimer.Stop(); _compressionWindow?.Close(); };
    }

    private void ShowCompression(Control anchor, bool tapped)
    {
        if (DataContext is not SidebarViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_compressionWindow == null)
        {
            vm.RefreshCompressionDiagnostics();
            var window = new CompressionDiagnosticsWindow { DataContext = vm };
            // Use an independent top-level surface: tooltip constraints and the host's
            // layout scaling must not clip the diagnostics content.
            foreach (var key in new[] { "MonoFontFamily", "CardBgBrush", "BorderBrush", "TextMutedBrush", "TextSecondaryBrush" })
                if (this.TryFindResource(key, ActualThemeVariant, out var resource)) window.Resources[key] = resource;
            window.RequestedThemeVariant = ActualThemeVariant;
            _compressionWindow = window;
            window.Closed += (_, _) => { if (_compressionWindow == window) _compressionWindow = null; };
            var point = anchor.PointToScreen(new Point(anchor.Bounds.Width, 0));
            var screen = owner.Screens.ScreenFromPoint(point);
            if (screen != null)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                window.Width = System.Math.Min(window.Width, area.Width / scale);
                window.Height = System.Math.Min(window.Height, area.Height / scale);
                point = new PixelPoint(
                    System.Math.Clamp(point.X, area.X, System.Math.Max(area.X, area.Right - (int)(window.Width * scale))),
                    System.Math.Clamp(point.Y, area.Y, System.Math.Max(area.Y, area.Bottom - (int)(window.Height * scale))));
            }
            window.Position = point;
            window.Show(owner);
        }
        _compressionWindow?.AnchorEntered();
        if (tapped) _compressionWindow?.OpenByTap();
    }

    private void CompressionPointerEntered(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && sender is Control anchor) ShowCompression(anchor, false);
    }
    private void CompressionPointerExited(object? sender, PointerEventArgs e) => _compressionWindow?.AnchorExited();
    private void CompressionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control anchor) ShowCompression(anchor, true);
        e.Handled = true;
    }
}
