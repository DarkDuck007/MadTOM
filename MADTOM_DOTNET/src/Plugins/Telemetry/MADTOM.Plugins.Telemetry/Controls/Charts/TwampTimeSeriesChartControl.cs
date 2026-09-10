using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    public static readonly StyledProperty<bool> PausePanningOnZoomProperty =
        AvaloniaProperty.Register<TwampTimeSeriesChartControl, bool>(nameof(PausePanningOnZoom), true);

    public bool PausePanningOnZoom
    {
        get => GetValue(PausePanningOnZoomProperty);
        set => SetValue(PausePanningOnZoomProperty, value);
    }

    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1,
        new ImmutableDashStyle([2, 4], 0));

    private static readonly IPen CrosshairPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, 56, 189, 248)), 1,
        new ImmutableDashStyle([3, 3], 0));

    private static readonly IPen ForwardPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)), 2.0);
    private static readonly IBrush ForwardFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(35, 6, 182, 212));

    private static readonly IPen ReversePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(99, 102, 241)), 2.0);
    private static readonly IBrush ReverseFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(25, 99, 102, 241));

    private static readonly IPen AsymPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11)), 1.5,
        new ImmutableDashStyle([4, 4], 0));

    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly IBrush TooltipBg = new ImmutableSolidColorBrush(Color.FromArgb(240, 12, 18, 32));
    private static readonly IPen TooltipBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(51, 65, 85)), 1);
    private static readonly Typeface MonoTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private Point? _hoverPoint;
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartPan;

    private readonly Dictionary<long, Point> _activeTouchPoints = new();
    private double _multiTouchStartDistance;
    private double _multiTouchStartZoom = 1.0;
    private double _multiTouchStartPan;
    private Point _multiTouchStartCenter;

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
            PanOffsetProperty,
            PausePanningOnZoomProperty);
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

        // Transparent background across entire bounds for continuous hit-testing
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

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
        double zoom = Math.Max(1.0, Math.Min(8.0, ZoomLevel));
        double maxPan = Math.Max(0.0, (zoom - 1.0) * plotW);
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
            if (_hoverPoint.HasValue && _hoverPoint.Value.X >= leftPad && _hoverPoint.Value.X <= w - rightPad)
            {
                double hx = _hoverPoint.Value.X;
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
        if (_hoverPoint.HasValue && _hoverPoint.Value.X >= leftPad && _hoverPoint.Value.X <= w - rightPad && _hoverPoint.Value.Y >= topPad && _hoverPoint.Value.Y <= h - bottomPad)
        {
            double mouseRelX = _hoverPoint.Value.X - leftPad + pan;
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
            double tipX = Math.Min(w - tipW - 10, _hoverPoint.Value.X + 12);
            double tipY = Math.Max(topPad + 5, _hoverPoint.Value.Y - tipH - 5);

            var tipRect = new RoundedRect(new Rect(tipX, tipY, tipW, tipH), 6);
            context.DrawRectangle(TooltipBg, TooltipBorder, tipRect);
            context.DrawText(ft, new Point(tipX + 8, tipY + 6));
        }

        // Draw Zoom Indicator & Magnifying Glass Reset Button
        if (ZoomLevel > 1.05)
        {
            double btnW = 84;
            double btnH = 22;
            double btnX = w - rightPad - btnW;
            double btnY = topPad + 4;
            var resetRect = new RoundedRect(new Rect(btnX, btnY, btnW, btnH), 4);
            context.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(220, 15, 23, 42)), TooltipBorder, resetRect);

            var resetText = new FormattedText(
                "🔍 Reset",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                9.5,
                new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)));
            context.DrawText(resetText, new Point(btnX + 16, btnY + 4));
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
        if (data.Length == 0) return;
        double range = Math.Max(0.1, maxY - minY);
        int stride = Math.Max(1, (int)Math.Round(10.0 / Math.Max(0.001, stepX)));
        for (int i = 0; i < data.Length; i += stride)
        {
            double x = leftPad + (i * stepX) - pan;
            double y = topPad + plotH * (1.0 - (data[i] - minY) / range);
            context.FillRectangle(brush, new Rect(x - 2, y - 2, 4, 4), 2);
        }
    }

    public static (double newZoom, double newPan) ComputeCursorAnchoredZoom(
        double oldZoom,
        double oldPan,
        double deltaY,
        double cursorX,
        double leftPad,
        double plotW,
        double minZoom = 1.0,
        double maxZoom = 8.0)
    {
        if (plotW <= 0) return (oldZoom, oldPan);

        double relX = Math.Clamp(cursorX - leftPad, 0.0, plotW);
        double dataFrac = (relX + oldPan) / (plotW * oldZoom);

        double newZoom = Math.Clamp(oldZoom + (deltaY * 0.25), minZoom, maxZoom);
        double newMaxPan = Math.Max(0.0, (newZoom - 1.0) * plotW);
        double newPan = (dataFrac * plotW * newZoom) - relX;

        return (newZoom, Math.Clamp(newPan, 0.0, newMaxPan));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);

        // Magnifying Glass Reset Button click check
        if (ZoomLevel > 1.05)
        {
            const double rightPad = 16.0;
            const double topPad = 24.0;
            double btnW = 84;
            double btnH = 22;
            double btnX = Bounds.Width - rightPad - btnW;
            double btnY = topPad + 4;
            var resetRect = new Rect(btnX, btnY, btnW, btnH);

            if (resetRect.Contains(p.Position))
            {
                ZoomLevel = 1.0;
                PanOffset = 0.0;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (e.Pointer.Type == PointerType.Touch)
        {
            _activeTouchPoints[e.Pointer.Id] = p.Position;
            if (_activeTouchPoints.Count == 2)
            {
                _isDragging = false;
                var pts = _activeTouchPoints.Values.ToArray();
                _multiTouchStartDistance = Math.Max(10.0, Math.Abs(pts[0].X - pts[1].X));
                _multiTouchStartZoom = ZoomLevel;
                _multiTouchStartPan = PanOffset;
                _multiTouchStartCenter = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
            }
            else if (_activeTouchPoints.Count == 1)
            {
                _isDragging = true;
                _dragStartPoint = p.Position;
                _dragStartPan = PanOffset;
            }
            e.Handled = true;
        }
        else if (p.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = p.Position;
            _dragStartPan = PanOffset;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPoint = e.GetPosition(this);

        const double leftPad = 46.0;
        const double rightPad = 16.0;
        double plotW = Math.Max(10.0, Bounds.Width - leftPad - rightPad);
        double maxPan = Math.Max(0.0, (ZoomLevel - 1.0) * plotW);

        if (e.Pointer.Type == PointerType.Touch && _activeTouchPoints.ContainsKey(e.Pointer.Id))
        {
            _activeTouchPoints[e.Pointer.Id] = e.GetPosition(this);
            if (_activeTouchPoints.Count >= 2)
            {
                var pts = _activeTouchPoints.Values.Take(2).ToArray();
                double currentDistance = Math.Max(10.0, Math.Abs(pts[0].X - pts[1].X));
                double scale = currentDistance / _multiTouchStartDistance;

                double newZoom = Math.Clamp(_multiTouchStartZoom * scale, 1.0, 8.0);
                double relX = Math.Clamp(_multiTouchStartCenter.X - leftPad, 0.0, plotW);
                double dataFrac = (relX + _multiTouchStartPan) / (plotW * _multiTouchStartZoom);
                double newMaxPan = Math.Max(0.0, (newZoom - 1.0) * plotW);
                double newPan = (dataFrac * plotW * newZoom) - relX;

                PanOffset = Math.Clamp(newPan, 0.0, newMaxPan);
                ZoomLevel = newZoom;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (_isDragging && _hoverPoint.HasValue)
        {
            double deltaX = _hoverPoint.Value.X - _dragStartPoint.X;
            PanOffset = Math.Clamp(_dragStartPan - deltaX, 0.0, maxPan);
            Cursor = new Cursor(StandardCursorType.Hand);
        }
        else
        {
            if (ZoomLevel > 1.05 && _hoverPoint.HasValue && _hoverPoint.Value.X >= leftPad && _hoverPoint.Value.X <= Bounds.Width - rightPad)
            {
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
            }
            else
            {
                Cursor = Cursor.Default;
            }
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type == PointerType.Touch)
        {
            _activeTouchPoints.Remove(e.Pointer.Id);
            if (_activeTouchPoints.Count == 0)
            {
                _isDragging = false;
            }
        }
        else if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            Cursor = ZoomLevel > 1.05 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        const double leftPad = 46.0;
        const double rightPad = 16.0;
        double plotW = Math.Max(10.0, Bounds.Width - leftPad - rightPad);
        double maxPan = Math.Max(0.0, (ZoomLevel - 1.0) * plotW);

        // Trackpad horizontal scroll
        if (Math.Abs(e.Delta.X) > 0.001 && maxPan > 0)
        {
            PanOffset = Math.Clamp(PanOffset - (e.Delta.X * 24.0), 0.0, maxPan);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Vertical wheel delta -> Cursor-Anchored Zoom
        double deltaY = e.Delta.Y;
        if (Math.Abs(deltaY) > 0.001)
        {
            Point cursorPt = _hoverPoint ?? e.GetPosition(this);
            var (newZoom, newPan) = ComputeCursorAnchoredZoom(ZoomLevel, PanOffset, deltaY, cursorPt.X, leftPad, plotW, 1.0, 8.0);
            PanOffset = newPan;
            ZoomLevel = newZoom;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverPoint = null;
        if (!_isDragging)
        {
            Cursor = Cursor.Default;
        }
        InvalidateVisual();
    }
}

