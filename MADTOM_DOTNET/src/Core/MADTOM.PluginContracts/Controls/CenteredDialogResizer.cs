using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MADTOM.PluginContracts.Controls;

/// <summary>
/// Universal resizer for centered or popup modal dialogs.
/// Supports both mouse pointers and touch gestures (touchscreen dragging, corner touch grips, edge dragging).
/// </summary>
public static class CenteredDialogResizer
{
    public static void Attach(
        Control dialog,
        Control left, Control right, Control top, Control bottom,
        Control topLeft, Control topRight, Control bottomLeft, Control bottomRight,
        Control? touchGrip = null,
        double minWidth = 400, double minHeight = 300,
        double maxWidth = double.PositiveInfinity, double maxHeight = double.PositiveInfinity,
        bool symmetric = true)
    {
        void Setup(Control handle, int dirX, int dirY, bool forceCorner = false)
        {
            Point startPoint = default;
            double startW = 0;
            double startH = 0;
            bool isDragging = false;

            handle.PointerPressed += (s, e) =>
            {
                var point = e.GetCurrentPoint(handle);
                // Accept left mouse button, touch pointers, or stylus/pen pointers
                if (point.Properties.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen)
                {
                    isDragging = true;
                    startPoint = e.GetPosition(null);
                    startW = double.IsNaN(dialog.Width) || dialog.Width <= 0 ? dialog.Bounds.Width : dialog.Width;
                    startH = double.IsNaN(dialog.Height) || dialog.Height <= 0 ? dialog.Bounds.Height : dialog.Height;

                    if (startW <= 0) startW = minWidth;
                    if (startH <= 0) startH = minHeight;

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

                double multiplier = symmetric ? 2.0 : 1.0;

                if (dirX != 0)
                {
                    double newW = startW + (dirX * deltaX * multiplier);
                    newW = Math.Max(minWidth, newW);
                    if (!double.IsPositiveInfinity(maxWidth) && maxWidth > minWidth)
                    {
                        newW = Math.Min(maxWidth, newW);
                    }
                    dialog.Width = newW;
                }

                if (dirY != 0)
                {
                    double newH = startH + (dirY * deltaY * multiplier);
                    newH = Math.Max(minHeight, newH);
                    if (!double.IsPositiveInfinity(maxHeight) && maxHeight > minHeight)
                    {
                        newH = Math.Min(maxHeight, newH);
                    }
                    dialog.Height = newH;
                }

                e.Handled = true;
            };

            void EndDrag(PointerEventArgs e)
            {
                if (isDragging)
                {
                    isDragging = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                }
            }

            handle.PointerReleased += (s, e) => EndDrag(e);
            handle.PointerCaptureLost += (s, e) => { isDragging = false; };
        }

        if (left != null) Setup(left, -1, 0);
        if (right != null) Setup(right, 1, 0);
        if (top != null) Setup(top, 0, -1);
        if (bottom != null) Setup(bottom, 0, 1);
        if (topLeft != null) Setup(topLeft, -1, -1);
        if (topRight != null) Setup(topRight, 1, -1);
        if (bottomLeft != null) Setup(bottomLeft, -1, 1);
        if (bottomRight != null) Setup(bottomRight, 1, 1);
        if (touchGrip != null) Setup(touchGrip, 1, 1, forceCorner: true);
    }
}
