using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SQUEEZE.Views;

// Same centered edge/corner interaction as Telemetry, bounded to the plugin viewport.
internal static class CenteredDialogResizer
{
    public static void Attach(Control dialog, Control handle, int x, int y, Action<double, double> resize)
    {
        Point origin = default;
        Size size = default;
        bool dragging = false;
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            origin = e.GetPosition(null);
            size = dialog.Bounds.Size;
            dragging = true;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var delta = e.GetPosition(null) - origin;
            resize(size.Width + x * delta.X * 2, size.Height + y * delta.Y * 2);
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => dragging = false;
    }
}
