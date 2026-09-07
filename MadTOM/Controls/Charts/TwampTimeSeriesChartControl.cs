using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MadTOM.Controls.Charts;

public sealed class TwampTimeSeriesChartControl : Control
{
    public static readonly StyledProperty<double[]> ForwardSeriesProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, double[]>(nameof(ForwardSeries), Array.Empty<double>());

    public static readonly StyledProperty<double[]> ReverseSeriesProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, double[]>(nameof(ReverseSeries), Array.Empty<double>());

    public static readonly StyledProperty<double[]> AsymmetrySeriesProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, double[]>(nameof(AsymmetrySeries), Array.Empty<double>());

    public static readonly StyledProperty<string[]> TimeLabelsProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, string[]>(nameof(TimeLabels), Array.Empty<string>());

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, double>(nameof(ZoomLevel), 1.0);

    public static readonly StyledProperty<double> PanOffsetProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, double>(nameof(PanOffset), 0.0);

    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(50, 30, 41, 59)), 1);
    private static readonly IPen CrosshairPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, 100, 116, 139)), 1,
        new ImmutableDashStyle([3, 3], 0));

    private static readonly IPen ForwardPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)), 2.0);
    private static readonly IBrush ForwardFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(25, 6, 182, 212));

    private static readonly IPen ReversePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(99, 102, 241)), 2.0);
    private static readonly IBrush ReverseFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(25, 99, 102, 241));

    private static readonly IPen AsymPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11)), 1.5,
        new ImmutableDashStyle([4, 4], 0));

    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly IBrush TooltipBg = new ImmutableSolidColorBrush(Color.FromArgb(240, 12, 18, 32));
    private static readonly IPen TooltipBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(51, 65, 85)), 1);
    private static readonly Typeface MonoTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private bool _isHovering;
    private Point _hoverPoint;
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartPan;

    public double[] ForwardSeries
    {
        get => GetValue(ForwardSeriesProperty);
        set => SetValue(ForwardSeriesProperty, value);
    }

    public double[] ReverseSeries
    {
        get => GetValue(ReverseSeriesProperty);
        set => SetValue(ReverseSeriesProperty, value);
    }

    public double[] AsymmetrySeries
    {
        get => GetValue(AsymmetrySeriesProperty);
        set => SetValue(AsymmetrySeriesProperty, value);
    }

    public string[] TimeLabels
    {
        get => GetValue(TimeLabelsProperty);
        set => SetValue(TimeLabelsProperty, value);
    }

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    public double PanOffset
    {
        get => GetValue(PanOffsetProperty);
        set => SetValue(PanOffsetProperty, value);
    }

    static TwampTimeSeriesChartControl()
    {
        AffectsRender<TwampTimeSeriesChartControl>(
            ForwardSeriesProperty,
            ReverseSeriesProperty,
            AsymmetrySeriesProperty,
            TimeLabelsProperty,
            ZoomLevelProperty,
            PanOffsetProperty);
    }

    public TwampTimeSeriesChartControl()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        const double leftPad = 46.0;
        const double rightPad = 16.0;
        const double topPad = 24.0;
        const double bottomPad = 32.0;

        double plotW = w - leftPad - rightPad;
        double plotH = h - topPad - bottomPad;

        var fwd = ForwardSeries ?? Array.Empty<double>();
        var rev = ReverseSeries ?? Array.Empty<double>();
        var asym = AsymmetrySeries ?? Array.Empty<double>();

        int count = Math.Max(fwd.Length, Math.Max(rev.Length, asym.Length));
        if (count < 2) return;

        // Calculate Y scale range
        double minY = 0.0;
        double maxY = 3.5;
        foreach (var v in fwd) if (v > maxY) maxY = v;
        foreach (var v in rev) if (v > maxY) maxY = v;
        maxY = Math.Ceiling(maxY * 1.2 * 2.0) / 2.0; // round up to nearest 0.5
        if (maxY <= minY) maxY = 4.0;

        // Draw horizontal grid lines & labels
        int gridSteps = 4;
        for (int i = 0; i <= gridSteps; i++)
        {
            double frac = (double)i / gridSteps;
            double y = topPad + plotH * (1.0 - frac);
            double val = minY + frac * (maxY - minY);

            context.DrawLine(GridPen, new Point(leftPad, y), new Point(w - rightPad, y));

            var text = new FormattedText(
                $"{val:F1}ms",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                10.0,
                LabelBrush);
            context.DrawText(text, new Point(4, y - 6));
        }

        // Apply zoom and pan transformation
        double zoom = Math.Max(1.0, Math.Min(5.0, ZoomLevel));
        double maxPan = (zoom - 1.0) * plotW;
        double pan = Math.Clamp(PanOffset, 0.0, maxPan);

        double effectiveW = plotW * zoom;
        double stepX = effectiveW / (count - 1);

        // Clip plot area
        using (context.PushClip(new Rect(leftPad, topPad - 4, plotW, plotH + 8)))
        {
            // Draw Curves
            DrawAreaCurve(context, rev, leftPad, topPad, plotH, stepX, pan, minY, maxY, ReversePen, ReverseFillBrush);
            DrawAreaCurve(context, fwd, leftPad, topPad, plotH, stepX, pan, minY, maxY, ForwardPen, ForwardFillBrush);
            DrawLineCurve(context, asym, leftPad, topPad, plotH, stepX, pan, minY, maxY, AsymPen);

            // Draw Data points
            DrawPoints(context, fwd, leftPad, topPad, plotH, stepX, pan, minY, maxY, Brushes.Cyan);
            DrawPoints(context, rev, leftPad, topPad, plotH, stepX, pan, minY, maxY, Brushes.Indigo);

            // Crosshair
            if (_isHovering && _hoverPoint.X >= leftPad && _hoverPoint.X <= w - rightPad)
            {
                double hx = _hoverPoint.X;
                context.DrawLine(CrosshairPen, new Point(hx, topPad), new Point(hx, topPad + plotH));
            }
        }

        // Draw X-axis time labels
        var labels = TimeLabels;
        int labelInterval = Math.Max(1, count / 6);
        for (int i = 0; i < count; i += labelInterval)
        {
            double x = leftPad + (i * stepX) - pan;
            if (x >= leftPad - 20 && x <= w - rightPad + 20)
            {
                string labelStr = (labels != null && i < labels.Length) ? labels[i] : $"{i * 5}s ago";
                var text = new FormattedText(
                    labelStr,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    9.5,
                    LabelBrush);
                context.DrawText(text, new Point(x - 18, h - bottomPad + 8));
            }
        }

        // Draw Hover Tooltip Box
        if (_isHovering && _hoverPoint.X >= leftPad && _hoverPoint.X <= w - rightPad && _hoverPoint.Y >= topPad && _hoverPoint.Y <= h - bottomPad)
        {
            double mouseRelX = _hoverPoint.X - leftPad + pan;
            int nearestIdx = Math.Clamp((int)Math.Round(mouseRelX / stepX), 0, count - 1);

            double curFwd = nearestIdx < fwd.Length ? fwd[nearestIdx] : 0;
            double curRev = nearestIdx < rev.Length ? rev[nearestIdx] : 0;
            double curAsym = nearestIdx < asym.Length ? asym[nearestIdx] : Math.Abs(curFwd - curRev);
            string timeStr = (labels != null && nearestIdx < labels.Length) ? labels[nearestIdx] : $"{nearestIdx * 5}s ago";

            string tooltipText = $"{timeStr}\n↑ {curFwd:F2}ms  ↓ {curRev:F2}ms  Δ {curAsym:F2}ms";
            var ft = new FormattedText(
                tooltipText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                10.0,
                Brushes.White);

            double tipW = ft.Width + 16;
            double tipH = ft.Height + 12;
            double tipX = Math.Min(w - tipW - 10, _hoverPoint.X + 12);
            double tipY = Math.Max(topPad + 5, _hoverPoint.Y - tipH - 5);

            context.FillRectangle(TooltipBg, new Rect(tipX, tipY, tipW, tipH), 6);
            context.DrawRectangle(null, TooltipBorder, new Rect(tipX, tipY, tipW, tipH), 6);
            context.DrawText(ft, new Point(tipX + 8, tipY + 6));
        }
    }

    private static void DrawAreaCurve(DrawingContext context, double[] data, double leftPad, double topPad,
        double plotH, double stepX, double pan, double minY, double maxY, IPen strokePen, IBrush fillBrush)
    {
        if (data.Length < 2) return;
        double range = Math.Max(0.1, maxY - minY);

        var fillGeom = new StreamGeometry();
        var lineGeom = new StreamGeometry();

        using (var fctx = fillGeom.Open())
        using (var lctx = lineGeom.Open())
        {
            double firstX = leftPad - pan;
            double firstY = topPad + plotH * (1.0 - (data[0] - minY) / range);

            fctx.BeginFigure(new Point(firstX, topPad + plotH), true);
            fctx.LineTo(new Point(firstX, firstY));
            lctx.BeginFigure(new Point(firstX, firstY), false);

            for (int i = 1; i < data.Length; i++)
            {
                double x = leftPad + (i * stepX) - pan;
                double y = topPad + plotH * (1.0 - (data[i] - minY) / range);

                fctx.LineTo(new Point(x, y));
                lctx.LineTo(new Point(x, y));
            }

            double lastX = leftPad + ((data.Length - 1) * stepX) - pan;
            fctx.LineTo(new Point(lastX, topPad + plotH));
        }

        context.DrawGeometry(fillBrush, null, fillGeom);
        context.DrawGeometry(null, strokePen, lineGeom);
    }

    private static void DrawLineCurve(DrawingContext context, double[] data, double leftPad, double topPad,
        double plotH, double stepX, double pan, double minY, double maxY, IPen strokePen)
    {
        if (data.Length < 2) return;
        double range = Math.Max(0.1, maxY - minY);

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            double firstX = leftPad - pan;
            double firstY = topPad + plotH * (1.0 - (data[0] - minY) / range);
            ctx.BeginFigure(new Point(firstX, firstY), false);

            for (int i = 1; i < data.Length; i++)
            {
                double x = leftPad + (i * stepX) - pan;
                double y = topPad + plotH * (1.0 - (data[i] - minY) / range);
                ctx.LineTo(new Point(x, y));
            }
        }

        context.DrawGeometry(null, strokePen, geom);
    }

    private static void DrawPoints(DrawingContext context, double[] data, double leftPad, double topPad,
        double plotH, double stepX, double pan, double minY, double maxY, IBrush brush)
    {
        double range = Math.Max(0.1, maxY - minY);
        for (int i = 0; i < data.Length; i++)
        {
            double x = leftPad + (i * stepX) - pan;
            double y = topPad + plotH * (1.0 - (data[i] - minY) / range);
            context.FillRectangle(brush, new Rect(x - 2, y - 2, 4, 4), 2);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPoint = e.GetPosition(this);
        _isHovering = true;

        if (_isDragging)
        {
            double deltaX = _hoverPoint.X - _dragStartPoint.X;
            PanOffset = _dragStartPan - deltaX;
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartPan = PanOffset;
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y;
        double newZoom = Math.Clamp(ZoomLevel + (delta * 0.25), 1.0, 5.0);
        ZoomLevel = newZoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovering = false;
        _isDragging = false;
        InvalidateVisual();
    }
}

