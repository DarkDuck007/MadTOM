using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MadTOM.Models;

namespace MadTOM.Controls.Charts;

/// <summary>
/// High-performance time-series chart control supporting single-metric and
/// multi-metric aggregated graphs with smooth gradient area shading and full-area hover.
/// </summary>
public sealed class MetricHistoryChartControl : Control
{
    public static readonly StyledProperty<long[]> TimestampsProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, long[]>(nameof(Timestamps), Array.Empty<long>());

    public static readonly StyledProperty<long> WindowStartProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, long>(nameof(WindowStart));

    public static readonly StyledProperty<long> WindowEndProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, long>(nameof(WindowEnd));

    public static readonly StyledProperty<double[]> ValuesProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, double[]>(nameof(Values), Array.Empty<double>());

    public static readonly StyledProperty<string[]> LabelsProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, string[]>(nameof(Labels), Array.Empty<string>());

    public static readonly StyledProperty<IReadOnlyList<ChartSeriesModel>?> SeriesListProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, IReadOnlyList<ChartSeriesModel>?>(nameof(SeriesList));

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, double>(nameof(ZoomLevel), 1.0);

    public static readonly StyledProperty<double> PanOffsetProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, double>(nameof(PanOffset), 0.0);

    public long[] Timestamps { get => GetValue(TimestampsProperty); set => SetValue(TimestampsProperty, value); }
    public long WindowStart { get => GetValue(WindowStartProperty); set => SetValue(WindowStartProperty, value); }
    public long WindowEnd { get => GetValue(WindowEndProperty); set => SetValue(WindowEndProperty, value); }
    public double[] Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public string[] Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public IReadOnlyList<ChartSeriesModel>? SeriesList { get => GetValue(SeriesListProperty); set => SetValue(SeriesListProperty, value); }
    public double ZoomLevel { get => GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }
    public double PanOffset { get => GetValue(PanOffsetProperty); set => SetValue(PanOffsetProperty, value); }

    private Point? _hoverPoint;
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartPan;

    private static readonly IPen GridPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(35, 148, 163, 184)), 1,
        new ImmutableDashStyle([2, 4], 0));

    private static readonly IPen CrosshairPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(160, 56, 189, 248)), 1,
        new ImmutableDashStyle([3, 3], 0));

    private static readonly IPen DefaultLinePen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)), 2.0);

    private static readonly IBrush DefaultFillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0.0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1.0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(50, 6, 182, 212), 0.0),
            new GradientStop(Color.FromArgb(2, 6, 182, 212), 1.0)
        }
    };

    private static readonly IBrush TooltipBg = new ImmutableSolidColorBrush(Color.FromArgb(240, 10, 15, 26));
    private static readonly IPen TooltipBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 30, 41, 59)), 1);
    private static readonly Typeface MonoTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    static MetricHistoryChartControl()
    {
        AffectsRender<MetricHistoryChartControl>(
            ValuesProperty, LabelsProperty, TimestampsProperty,
            WindowStartProperty, WindowEndProperty, SeriesListProperty,
            ZoomLevelProperty, PanOffsetProperty);
    }

    public MetricHistoryChartControl()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesListProperty)
        {
            if (change.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldColl)
            {
                oldColl.CollectionChanged -= OnSeriesCollectionChanged;
                if (change.OldValue is IEnumerable<ChartSeriesModel> oldItems)
                {
                    foreach (var item in oldItems) item.PropertyChanged -= OnSeriesItemPropertyChanged;
                }
            }
            if (change.NewValue is System.Collections.Specialized.INotifyCollectionChanged newColl)
            {
                newColl.CollectionChanged += OnSeriesCollectionChanged;
                if (change.NewValue is IEnumerable<ChartSeriesModel> newItems)
                {
                    foreach (var item in newItems) item.PropertyChanged += OnSeriesItemPropertyChanged;
                }
            }
            InvalidateVisual();
        }
    }

    private void OnSeriesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<ChartSeriesModel>())
                item.PropertyChanged -= OnSeriesItemPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<ChartSeriesModel>())
                item.PropertyChanged += OnSeriesItemPropertyChanged;
        }
        InvalidateVisual();
    }

    private void OnSeriesItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        // 1. Transparent background across the entire control bounds ensures mouse events hit-test anywhere
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        // 2. Build list of active series
        var activeSeries = new List<ChartSeriesModel>();
        if (SeriesList != null && SeriesList.Count > 0)
        {
            foreach (var s in SeriesList)
            {
                if (s.IsVisible && s.Values.Length > 0)
                    activeSeries.Add(s);
            }
        }
        else if (Values != null && Values.Length > 0)
        {
            activeSeries.Add(new ChartSeriesModel("metric", "Value", "#06B6D4")
            {
                Values = Values,
                Timestamps = Timestamps ?? Array.Empty<long>()
            });
        }

        const double leftPad = 52.0;
        const double rightPad = 16.0;
        const double topPad = 26.0;
        const double bottomPad = 24.0;
        double plotW = Math.Max(10, w - leftPad - rightPad);
        double plotH = Math.Max(10, h - topPad - bottomPad);

        if (activeSeries.Count == 0)
        {
            var emptyText = new FormattedText("No measurements in this time window",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 11, Brushes.Gray);
            context.DrawText(emptyText, new Point(leftPad + (plotW - emptyText.Width) / 2, topPad + (plotH - emptyText.Height) / 2));
            return;
        }

        // 3. Compute global Y scale range across all visible series
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        foreach (var s in activeSeries)
        {
            foreach (var val in s.Values)
            {
                if (val < minY) minY = val;
                if (val > maxY) maxY = val;
            }
        }

        if (minY == double.MaxValue) { minY = 0.0; maxY = 1.0; }
        if (minY > 0) minY = 0.0; // Baseline at zero for resource utilization & latency
        if (maxY <= minY) maxY = minY + 1.0;
        double rangeY = Math.Max(0.001, maxY - minY);

        // 4. Draw horizontal grid lines & Y-axis scale labels
        int gridSteps = 4;
        for (int i = 0; i <= gridSteps; i++)
        {
            double frac = (double)i / gridSteps;
            double y = topPad + plotH * frac;
            double val = maxY - frac * rangeY;

            context.DrawLine(GridPen, new Point(leftPad, y), new Point(w - rightPad, y));

            string text = FormatScaleValue(val);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9, Brushes.SlateGray);
            context.DrawText(ft, new Point(Math.Max(2, leftPad - ft.Width - 6), y - 6));
        }

        // 5. Time axis configuration
        long wStart = WindowStart;
        long wEnd = WindowEnd;
        long timeSpanNano = wEnd - wStart;
        bool useTimeMapping = timeSpanNano > 1_000_000_000L; // Valid time range > 1 sec

        double zoom = Math.Clamp(ZoomLevel, 1.0, 8.0);
        double maxPan = (zoom - 1.0) * plotW;
        double pan = Math.Clamp(PanOffset, 0.0, maxPan);

        Point MapPoint(long tsNano, double val, int idx, int totalCount)
        {
            double xFrac;
            if (useTimeMapping && tsNano >= wStart)
            {
                xFrac = Math.Clamp((double)(tsNano - wStart) / timeSpanNano, 0.0, 1.0);
            }
            else
            {
                xFrac = totalCount <= 1 ? 1.0 : (double)idx / (totalCount - 1);
            }
            double x = leftPad + (xFrac * plotW * zoom) - pan;
            double y = topPad + plotH * (1.0 - (val - minY) / rangeY);
            return new Point(x, y);
        }

        // 6. Draw each series: Shaded Area under curve + Line Stroke
        using (context.PushClip(new Rect(leftPad, topPad, plotW, plotH)))
        {
            foreach (var series in activeSeries)
            {
                var vals = series.Values;
                var ts = series.Timestamps;
                int count = vals.Length;
                if (count < 2)
                {
                    if (count == 1)
                    {
                        var pt = MapPoint(ts.Length > 0 ? ts[0] : 0, vals[0], 0, 1);
                        context.FillRectangle(series.SolidBrush, new Rect(pt.X - 3, pt.Y - 3, 6, 6), 3);
                    }
                    continue;
                }

                var fillGeom = new StreamGeometry();
                var lineGeom = new StreamGeometry();

                using (var fctx = fillGeom.Open())
                using (var lctx = lineGeom.Open())
                {
                    Point first = MapPoint(ts.Length > 0 ? ts[0] : 0, vals[0], 0, count);

                    // Fill geometry polygon starts at bottom baseline, traces line, closes at bottom
                    fctx.BeginFigure(new Point(first.X, topPad + plotH), true);
                    fctx.LineTo(first);
                    lctx.BeginFigure(first, false);

                    for (int i = 1; i < count; i++)
                    {
                        Point pt = MapPoint(ts.Length > i ? ts[i] : 0, vals[i], i, count);
                        fctx.LineTo(pt);
                        lctx.LineTo(pt);
                    }

                    Point last = MapPoint(ts.Length >= count ? ts[count - 1] : 0, vals[count - 1], count - 1, count);
                    fctx.LineTo(new Point(last.X, topPad + plotH));
                }

                // Fill area gradient below curve
                context.DrawGeometry(series.FillBrush, null, fillGeom);
                // Stroke primary line curve
                context.DrawGeometry(null, series.LinePen, lineGeom);
            }

            // 7. Hover crosshair & point indicators
            if (_hoverPoint is { } hp && hp.X >= leftPad && hp.X <= w - rightPad && hp.Y >= topPad && hp.Y <= topPad + plotH)
            {
                context.DrawLine(CrosshairPen, new Point(hp.X, topPad), new Point(hp.X, topPad + plotH));

                // Draw highlighted circle on each series line at hover position
                foreach (var series in activeSeries)
                {
                    int closestIdx = FindClosestIndex(series, hp.X, leftPad, plotW, zoom, pan, wStart, timeSpanNano, useTimeMapping);
                    if (closestIdx >= 0 && closestIdx < series.Values.Length)
                    {
                        long t = series.Timestamps.Length > closestIdx ? series.Timestamps[closestIdx] : 0;
                        Point p = MapPoint(t, series.Values[closestIdx], closestIdx, series.Values.Length);
                        context.FillRectangle(new ImmutableSolidColorBrush(Color.FromRgb(15, 23, 42)), new Rect(p.X - 5, p.Y - 5, 10, 10), 5f);
                        context.FillRectangle(series.SolidBrush, new Rect(p.X - 3.5, p.Y - 3.5, 7, 7), 3.5f);
                    }
                }
            }
        }

        // 8. Legend bar (Top-Left)
        double legendX = leftPad + 4;
        double legendY = topPad - 18;
        foreach (var series in activeSeries)
        {
            context.FillRectangle(series.SolidBrush, new Rect(legendX, legendY + 2, 7, 7), 2);
            var legText = new FormattedText(series.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9.5, new ImmutableSolidColorBrush(Color.FromRgb(203, 213, 225)));
            context.DrawText(legText, new Point(legendX + 11, legendY));
            legendX += legText.Width + 24;
            if (legendX > w - 120) break;
        }

        // 9. Time Bounds Labels (Bottom)
        if (useTimeMapping)
        {
            string startStr = DateTimeOffset.FromUnixTimeMilliseconds(wStart / 1_000_000L).ToLocalTime().ToString("HH:mm:ss");
            string endStr = DateTimeOffset.FromUnixTimeMilliseconds(wEnd / 1_000_000L).ToLocalTime().ToString("HH:mm:ss");

            var startFt = new FormattedText(startStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9, Brushes.Gray);
            var endFt = new FormattedText(endStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9, Brushes.Gray);

            context.DrawText(startFt, new Point(leftPad, h - bottomPad + 6));
            context.DrawText(endFt, new Point(w - rightPad - endFt.Width, h - bottomPad + 6));
        }
        else if (Labels != null && Labels.Length > 0)
        {
            var startFt = new FormattedText(Labels[0], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9, Brushes.Gray);
            var endFt = new FormattedText(Labels[^1], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9, Brushes.Gray);

            context.DrawText(startFt, new Point(leftPad, h - bottomPad + 6));
            context.DrawText(endFt, new Point(w - rightPad - endFt.Width, h - bottomPad + 6));
        }

        // 10. Floating Multi-Series Tooltip
        if (_hoverPoint is { } hoverPt && hoverPt.X >= leftPad && hoverPt.X <= w - rightPad && hoverPt.Y >= topPad && hoverPt.Y <= topPad + plotH)
        {
            RenderTooltip(context, activeSeries, hoverPt, leftPad, plotW, zoom, pan, wStart, timeSpanNano, useTimeMapping, w, h, topPad);
        }

        // 11. Zoom Indicator & Reset Button
        if (zoom > 1.05)
        {
            double btnW = 76;
            double btnH = 20;
            double btnX = w - rightPad - btnW;
            double btnY = 3;
            var resetRect = new RoundedRect(new Rect(btnX, btnY, btnW, btnH), 4);
            context.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(220, 15, 23, 42)), TooltipBorder, resetRect);

            var resetText = new FormattedText("🔍 Reset", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9.5, new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)));
            context.DrawText(resetText, new Point(btnX + 12, btnY + 3));
        }
    }

    private static int FindClosestIndex(ChartSeriesModel series, double hoverX, double leftPad, double plotW,
        double zoom, double pan, long wStart, long timeSpanNano, bool useTimeMapping)
    {
        var vals = series.Values;
        if (vals.Length == 0) return -1;
        if (vals.Length == 1) return 0;

        double xFrac = Math.Clamp((hoverX - leftPad + pan) / (plotW * zoom), 0.0, 1.0);

        if (useTimeMapping && series.Timestamps.Length == vals.Length)
        {
            long targetTime = wStart + (long)(xFrac * timeSpanNano);
            int bestIdx = 0;
            long bestDiff = Math.Abs(series.Timestamps[0] - targetTime);

            // Binary search or linear scan for closest timestamp
            for (int i = 1; i < series.Timestamps.Length; i++)
            {
                long diff = Math.Abs(series.Timestamps[i] - targetTime);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        return Math.Clamp((int)Math.Round(xFrac * (vals.Length - 1)), 0, vals.Length - 1);
    }

    private static void RenderTooltip(DrawingContext context, List<ChartSeriesModel> activeSeries, Point pt,
        double leftPad, double plotW, double zoom, double pan, long wStart, long timeSpanNano, bool useTimeMapping,
        double w, double h, double topPad)
    {
        var lines = new List<(string Label, string Value, IBrush Brush)>();
        string timeHeader = "";

        foreach (var series in activeSeries)
        {
            int idx = FindClosestIndex(series, pt.X, leftPad, plotW, zoom, pan, wStart, timeSpanNano, useTimeMapping);
            if (idx >= 0 && idx < series.Values.Length)
            {
                if (string.IsNullOrEmpty(timeHeader) && series.Timestamps.Length > idx && series.Timestamps[idx] > 0)
                {
                    timeHeader = DateTimeOffset.FromUnixTimeMilliseconds(series.Timestamps[idx] / 1_000_000L)
                        .ToLocalTime().ToString("MM-dd HH:mm:ss");
                }
                lines.Add((series.Label, FormatMetricValue(series.Metric, series.Values[idx]), series.SolidBrush));
            }
        }

        if (string.IsNullOrEmpty(timeHeader))
        {
            double xFrac = Math.Clamp((pt.X - leftPad + pan) / (plotW * zoom), 0.0, 1.0);
            if (useTimeMapping)
            {
                long targetTime = wStart + (long)(xFrac * timeSpanNano);
                timeHeader = DateTimeOffset.FromUnixTimeMilliseconds(targetTime / 1_000_000L)
                    .ToLocalTime().ToString("MM-dd HH:mm:ss");
            }
        }

        // Measure tooltip dimensions
        double headerH = string.IsNullOrEmpty(timeHeader) ? 0 : 16;
        double rowH = 15;
        double tipH = headerH + (lines.Count * rowH) + 14;
        double maxLineW = 120;

        var headerFt = string.IsNullOrEmpty(timeHeader) ? null :
            new FormattedText(timeHeader, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoTypeface, 9.5, Brushes.LightCyan);
        if (headerFt != null && headerFt.Width + 24 > maxLineW)
            maxLineW = headerFt.Width + 24;

        foreach (var (lbl, val, _) in lines)
        {
            var textFt = new FormattedText($"{lbl}: {val}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 9.5, Brushes.White);
            if (textFt.Width + 28 > maxLineW)
                maxLineW = textFt.Width + 28;
        }

        double tipW = maxLineW;
        double tipX = Math.Min(w - tipW - 12, pt.X + 14);
        if (tipX < leftPad) tipX = leftPad;
        double tipY = Math.Max(topPad + 4, pt.Y - tipH - 8);
        if (tipY + tipH > h - 10) tipY = h - tipH - 10;

        var tipRect = new RoundedRect(new Rect(tipX, tipY, tipW, tipH), 6);
        context.DrawRectangle(TooltipBg, TooltipBorder, tipRect);

        double curY = tipY + 7;
        if (headerFt != null)
        {
            context.DrawText(headerFt, new Point(tipX + 10, curY));
            curY += headerH;
        }

        foreach (var (lbl, val, brush) in lines)
        {
            context.FillRectangle(brush, new Rect(tipX + 10, curY + 4, 6, 6), 2);
            var rowFt = new FormattedText($"{lbl}: {val}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 9.5, Brushes.White);
            context.DrawText(rowFt, new Point(tipX + 22, curY));
            curY += rowH;
        }
    }

    private static string FormatScaleValue(double v)
    {
        if (Math.Abs(v) >= 1_000_000_000) return $"{v / 1_000_000_000:F1}G";
        if (Math.Abs(v) >= 1_000_000) return $"{v / 1_000_000:F1}M";
        if (Math.Abs(v) >= 1_000) return $"{v / 1_000:F1}k";
        if (Math.Abs(v) < 0.01 && v != 0) return $"{v:E2}";
        return $"{v:F1}";
    }

    private static string FormatMetricValue(string metric, double v)
    {
        if (metric.StartsWith("cpu.") || metric.EndsWith("_pct") || metric.Contains("pct"))
            return $"{v:F1}%";
        if (metric.StartsWith("twamp."))
            return $"{v:F2} ms";
        if (metric.EndsWith("_ratio"))
            return $"{v:F2}x";
        if (metric.Contains("bytes"))
        {
            if (v >= 1_073_741_824) return $"{v / 1_073_741_824:F2} GB";
            if (v >= 1_048_576) return $"{v / 1_048_576:F1} MB";
            if (v >= 1024) return $"{v / 1024:F0} KB";
            return $"{v:F0} B";
        }
        return $"{v:F2}";
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var q = e.GetCurrentPoint(this);

        // Click on 🔍 Reset button
        if (ZoomLevel > 1.05 && new Rect(Bounds.Width - 80, 0, 80, 26).Contains(q.Position))
        {
            ZoomLevel = 1.0;
            PanOffset = 0.0;
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (q.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = q.Position;
            _dragStartPan = PanOffset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPoint = e.GetPosition(this);

        if (_isDragging)
        {
            double plotW = Math.Max(10, Bounds.Width - 68);
            double deltaX = _hoverPoint.Value.X - _dragStartPoint.X;
            PanOffset = Math.Clamp(_dragStartPan - deltaX, 0.0, (ZoomLevel - 1.0) * plotW);
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double plotW = Math.Max(10, Bounds.Width - 68);
        double mouseX = Math.Clamp((_hoverPoint ?? e.GetPosition(this)).X - 52, 0.0, plotW);

        double oldZoom = ZoomLevel;
        double newZoom = Math.Clamp(oldZoom + e.Delta.Y * 0.35, 1.0, 8.0);

        double frac = (mouseX + PanOffset) / (plotW * oldZoom);
        ZoomLevel = newZoom;
        PanOffset = Math.Clamp(frac * plotW * newZoom - mouseX, 0.0, (newZoom - 1.0) * plotW);

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverPoint = null;
        InvalidateVisual();
    }
}
