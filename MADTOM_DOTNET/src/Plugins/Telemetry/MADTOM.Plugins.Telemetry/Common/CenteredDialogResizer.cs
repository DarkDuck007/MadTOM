using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MadTOM.Common;

/// <summary>
/// Attaches mouse drag resizing handles to a centered modal dialog container.
/// Resizing symmetrically expands and contracts around the center.
/// </summary>
public static class CenteredDialogResizer
{
    public static void Attach(
        Control dialog,
        Control left, Control right, Control top, Control bottom,
        Control topLeft, Control topRight, Control bottomLeft, Control bottomRight,
        double minWidth = 480, double minHeight = 360)
    {
        void Setup(Control handle, int dirX, int dirY)
        {
            Point startPoint = default;
            double startW = 0;
            double startH = 0;
            bool isDragging = false;

            handle.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                {
                    isDragging = true;
                    startPoint = e.GetPosition(null);
                    startW = double.IsNaN(dialog.Width) ? dialog.Bounds.Width : dialog.Width;
                    startH = double.IsNaN(dialog.Height) ? dialog.Bounds.Height : dialog.Height;
                    e.Pointer.Capture(handle);
                    e.Handled = true;
                }
            };

            handle.PointerMoved += (s, e) =>
            {
                if (!isDragging) return;
                var currentPoint = e.GetPosition(null);
                double deltaX = currentPoint.X - startPoint.X;
                double deltaY = currentPoint.Y - startPoint.Y;

                if (dirX != 0)
                {
                    double newW = startW + (dirX * deltaX * 2);
                    dialog.Width = Math.Max(minWidth, newW);
                }
                if (dirY != 0)
                {
                    double newH = startH + (dirY * deltaY * 2);
                    dialog.Height = Math.Max(minHeight, newH);
                }
                e.Handled = true;
            };

            handle.PointerReleased += (s, e) =>
            {
                if (isDragging)
                {
                    isDragging = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                }
            };

            handle.PointerCaptureLost += (s, e) =>
            {
                isDragging = false;
            };
        }

        Setup(left, -1, 0);
        Setup(right, 1, 0);
        Setup(top, 0, -1);
        Setup(bottom, 0, 1);
        Setup(topLeft, -1, -1);
        Setup(topRight, 1, -1);
        Setup(bottomLeft, -1, 1);
        Setup(bottomRight, 1, 1);
    }
}
