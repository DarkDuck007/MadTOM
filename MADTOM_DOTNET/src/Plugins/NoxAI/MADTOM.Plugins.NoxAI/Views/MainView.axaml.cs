using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.Views;

public partial class MainView : UserControl
{
    private NoxCompressionDiagnosticsWindow? _compressionWindow;

    public MainView()
    {
        InitializeComponent();
    }

    private void ShowCompression(Control anchor, bool tapped)
    {
        if (DataContext is not MainViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner)
        {
            if (tapped)
            {
                vm.OpenZstdDiagnostics();
            }
            return;
        }

        if (_compressionWindow == null)
        {
            vm.RefreshCompressionDiagnostics();
            var window = new NoxCompressionDiagnosticsWindow { DataContext = vm };
            foreach (var key in new[] { "MonoFontFamily", "CardBgBrush", "BorderBrush", "TextMutedBrush", "TextSecondaryBrush" })
            {
                if (this.TryFindResource(key, ActualThemeVariant, out var resource)) window.Resources[key] = resource;
            }
            window.RequestedThemeVariant = ActualThemeVariant;
            _compressionWindow = window;
            window.Closed += (_, _) => { if (_compressionWindow == window) _compressionWindow = null; };

            var point = anchor.PointToScreen(new Point(0, anchor.Bounds.Height + 4));
            var screen = owner.Screens.ScreenFromPoint(point);
            if (screen != null)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                window.Width = Math.Min(window.Width, area.Width / scale);
                window.Height = Math.Min(window.Height, area.Height / scale);
                point = new PixelPoint(
                    Math.Clamp(point.X, area.X, Math.Max(area.X, area.Right - (int)(window.Width * scale))),
                    Math.Clamp(point.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(window.Height * scale))));
            }
            window.Position = point;
            window.Show(owner);
        }

        _compressionWindow?.AnchorEntered();
        if (tapped) _compressionWindow?.OpenByTap();
    }

    private void OnCompressionPointerEntered(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && sender is Control anchor)
        {
            ShowCompression(anchor, false);
        }
    }

    private void OnCompressionPointerExited(object? sender, PointerEventArgs e)
    {
        _compressionWindow?.AnchorExited();
    }

    private void OnCompressionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control anchor)
        {
            ShowCompression(anchor, true);
        }
        e.Handled = true;
    }
}
