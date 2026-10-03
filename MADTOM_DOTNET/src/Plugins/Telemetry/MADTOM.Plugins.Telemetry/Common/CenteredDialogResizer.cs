using System;
using Avalonia.Controls;
using MADTOM.PluginContracts.Controls;

namespace MadTOM.Common;

/// <summary>
/// Backward-compatible bridge to universal CenteredDialogResizer.
/// Supports both mouse drag resizing and touchscreen drag/touch handles.
/// </summary>
public static class CenteredDialogResizer
{
    public static void Attach(
        Control dialog,
        Control left, Control right, Control top, Control bottom,
        Control topLeft, Control topRight, Control bottomLeft, Control bottomRight,
        Control? touchGrip = null,
        double minWidth = 480, double minHeight = 360)
    {
        MADTOM.PluginContracts.Controls.CenteredDialogResizer.Attach(
            dialog,
            left, right, top, bottom,
            topLeft, topRight, bottomLeft, bottomRight,
            touchGrip: touchGrip,
            minWidth: minWidth,
            minHeight: minHeight);
    }
}
