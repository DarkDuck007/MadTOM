using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MadTOM.Common.Utils;

namespace MadTOM.Controls.CoreMatrix;

public sealed class MicroCoreStripControl : Control
{
    public static readonly StyledProperty<int> CoreCountProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, int>(nameof(CoreCount), 64);

    public static readonly StyledProperty<float[]> CoreLoadsProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, float[]>(nameof(CoreLoads), Array.Empty<float>());

    public static readonly StyledProperty<string> HostIdProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, string>(nameof(HostId), string.Empty);

    public static readonly StyledProperty<int> HoveredIndexProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, int>(nameof(HoveredIndex), -1);

    public static readonly StyledProperty<float> HoveredLoadProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, float>(nameof(HoveredLoad), 0.0f);

    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, bool>(nameof(IsHovered), false);

    private static readonly IPen HoverPen = new ImmutablePen(Brushes.White, 1.5);

    private CoreLayoutResult _currentLayout;

    public int CoreCount
    {
        get => GetValue(CoreCountProperty);
        set => SetValue(CoreCountProperty, value);
    }

    public float[] CoreLoads
    {
        get => GetValue(CoreLoadsProperty);
        set => SetValue(CoreLoadsProperty, value);
    }

    public string HostId
    {
        get => GetValue(HostIdProperty);
        set => SetValue(HostIdProperty, value);
    }

    public int HoveredIndex
    {
        get => GetValue(HoveredIndexProperty);
        set => SetValue(HoveredIndexProperty, value);
    }

    public float HoveredLoad
    {
        get => GetValue(HoveredLoadProperty);
        set => SetValue(HoveredLoadProperty, value);
    }

    public bool IsHovered
    {
        get => GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    static MicroCoreStripControl()
    {
        AffectsRender<MicroCoreStripControl>(CoreCountProperty, CoreLoadsProperty, HoveredIndexProperty);
    }

    public MicroCoreStripControl()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 10 || h < 10) return;

        const double padX = 4.0;
        const double padY = 3.0;
        double availW = Math.Max(20.0, w - (padX * 2.0));
        double availH = Math.Max(20.0, h - (padY * 2.0));

        int count = CoreCount;
        if (count <= 0) count = 1;

        _currentLayout = CoreLayoutSolver.ComputeVerticalFillSquareLayout(availW, availH, count);
        var L = _currentLayout;

        double startX = padX + L.StartX;
        double startY = padY + L.StartY;
        double s = L.CellSize;
        double gap = L.Gap;
        int cols = L.Cols;

        var loads = CoreLoads;
        bool hasLoads = loads != null && loads.Length >= count;
        int hovered = HoveredIndex;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            double x = startX + col * (s + gap);
            double y = startY + row * (s + gap);

            double load = hasLoads ? loads![i] : 0.4;
            var brush = ColorInterpolator.InterpolateLoadBrush(load);
            var rect = new Rect(x, y, s, s);

            context.FillRectangle(brush, rect);

            if (i == hovered)
            {
                context.DrawRectangle(null, HoverPen, rect);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        var L = _currentLayout;
        const double padX = 4.0;
        const double padY = 3.0;
        double startX = padX + L.StartX;
        double startY = padY + L.StartY;

        double relX = pt.X - startX;
        double relY = pt.Y - startY;

        if (relX >= 0 && relY >= 0 && L.Cols > 0)
        {
            double step = L.CellSize + L.Gap;
            int col = (int)(relX / step);
            int row = (int)(relY / step);

            bool inCellX = (relX - col * step) <= L.CellSize;
            bool inCellY = (relY - row * step) <= L.CellSize;

            if (inCellX && inCellY && col >= 0 && col < L.Cols && row >= 0 && row < L.Rows)
            {
                int idx = (row * L.Cols) + col;
                if (idx >= 0 && idx < CoreCount)
                {
                    HoveredIndex = idx;
                    IsHovered = true;
                    HoveredLoad = (CoreLoads != null && idx < CoreLoads.Length) ? CoreLoads[idx] : 0.0f;
                    return;
                }
            }
        }

        if (HoveredIndex != -1)
        {
            HoveredIndex = -1;
            IsHovered = false;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredIndex = -1;
        IsHovered = false;
    }
}

