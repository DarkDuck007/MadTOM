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
using MadTOM.Services;

namespace MadTOM.Controls.Charts;

/// <summary>
/// High-performance time-series chart control supporting single-metric and
/// multi-metric aggregated graphs with smooth gradient area shading and full-area hover.
/// </summary>
public sealed class MetricHistoryChartControl : Control
{
    public static readonly StyledProperty<int> HistoryPointBudgetProperty =
        AvaloniaProperty.Register<MetricHistoryChartControl, int>(nameof(HistoryPointBudget), 2400);
    public int HistoryPointBudget { get => GetValue(HistoryPointBudgetProperty); set => SetValue(HistoryPointBudgetProperty, value); }

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

    private readonly Dictionary<long, Point> _activeTouchPoints = new();
    private double _multiTouchStartDistance;
    private double _multiTouchStartZoom = 1.0;
    private double _multiTouchStartPan;
    private Point _multiTouchStartCenter;
    private Point? _touchStartPoint;
    private bool _touchCaptured;

    private bool _geometryCacheValid;
    private long _dirtySince;
    private void MarkGeometryDirty()
    {
        _geometryCacheValid = false;
        if (UiPerformanceDiagnostics.Enabled && _dirtySince == 0)
            _dirtySince = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private double _cachedWidth;
    private double _cachedHeight;
    private double _cachedZoom;
    private double _cachedPan;
    private long _cachedWindowStart;
    private long _cachedWindowEnd;
    private readonly List<(IBrush FillBrush, IPen LinePen, StreamGeometry FillGeom, StreamGeometry LineGeom)> _cachedSeriesGeometries = new();

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
        if (change.Property == BoundsProperty && Bounds.Width > 68)
            SetCurrentValue(HistoryPointBudgetProperty, GraphHistoryResolution.PointBudget(Bounds.Width - 68, GraphPerformanceSettings.Current.HistoryPointsPerPixel));
        if (change.Property == ValuesProperty ||
            change.Property == TimestampsProperty ||
            change.Property == WindowStartProperty ||
            change.Property == WindowEndProperty ||
            change.Property == ZoomLevelProperty ||
            change.Property == PanOffsetProperty ||
            change.Property == SeriesListProperty)
        {
            MarkGeometryDirty();
        }

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
        MarkGeometryDirty();
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
        if (e.PropertyName is nameof(ChartSeriesModel.Values) or
                               nameof(ChartSeriesModel.Timestamps) or
                               nameof(ChartSeriesModel.IsVisible) or
                               nameof(ChartSeriesModel.ColorHex))
        {
            MarkGeometryDirty();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        using var renderTiming = UiPerformanceDiagnostics.Measure("chart.render-cpu");
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;
        if (_dirtySince != 0)
        {
            UiPerformanceDiagnostics.Record("chart.dirty-to-render", System.Diagnostics.Stopwatch.GetElapsedTime(_dirtySince).TotalMilliseconds);
            _dirtySince = 0;
        }

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
            if (useTimeMapping)
            {
                xFrac = (double)(tsNano - wStart) / timeSpanNano;
            }
            else
            {
                xFrac = totalCount <= 1 ? 1.0 : (double)idx / (totalCount - 1);
            }
            double x = leftPad + (xFrac * plotW * zoom) - pan;
            double safeVal = double.IsNaN(val) || double.IsInfinity(val) ? minY : val;
            double y = topPad + plotH * (1.0 - (safeVal - minY) / rangeY);
            return new Point(x, y);
        }

        bool cacheMatches = _geometryCacheValid
            && Math.Abs(_cachedWidth - w) < 0.5
            && Math.Abs(_cachedHeight - h) < 0.5
            && Math.Abs(_cachedZoom - zoom) < 0.001
            && Math.Abs(_cachedPan - pan) < 0.001
            && _cachedWindowStart == wStart
            && _cachedWindowEnd == wEnd;

        if (!cacheMatches)
        {
            long visibleStart = wStart + (long)(pan / (plotW * zoom) * timeSpanNano);
            long visibleEnd = visibleStart + (long)(timeSpanNano / zoom);
            using (UiPerformanceDiagnostics.Measure("chart.geometry"))
                RebuildGeometryCache(activeSeries, MapPoint, topPad + plotH, plotW, visibleStart, visibleEnd);
            _cachedWidth = w;
            _cachedHeight = h;
            _cachedZoom = zoom;
            _cachedPan = pan;
            _cachedWindowStart = wStart;
            _cachedWindowEnd = wEnd;
            _geometryCacheValid = true;
        }

        // 6. Draw each series: Shaded Area under curve + Line Stroke
        using (context.PushClip(new Rect(leftPad, topPad, plotW, plotH)))
        {
            foreach (var (fillBrush, linePen, fillGeom, lineGeom) in _cachedSeriesGeometries)
            {
                context.DrawGeometry(fillBrush, null, fillGeom);
                context.DrawGeometry(null, linePen, lineGeom);
            }

            foreach (var series in activeSeries)
            {
                if (series.Values.Length == 1)
                {
                    var pt = MapPoint(series.Timestamps.Length > 0 ? series.Timestamps[0] : 0, series.Values[0], 0, 1);
                    context.FillRectangle(series.SolidBrush, new Rect(pt.X - 3, pt.Y - 3, 6, 6), 3);
                }
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
            bool isRate = series.IsRateOfChange || series.Label.Contains("/s") || series.Label.Contains("rate");

            if (idx >= 0 && idx < series.Values.Length)
            {
                string seriesLabel = (series.PointLabels != null && idx < series.PointLabels.Length && !string.IsNullOrEmpty(series.PointLabels[idx]))
                    ? series.PointLabels[idx]
                    : series.Label;

                if (useTimeMapping && series.Timestamps.Length > idx && series.Timestamps[idx] > 0)
                {
                    double xFrac = Math.Clamp((pt.X - leftPad + pan) / (plotW * zoom), 0.0, 1.0);
                    long targetTime = wStart + (long)(xFrac * timeSpanNano);
                    long maxGapNano = Math.Max(180_000_000_000L, (long)(timeSpanNano / zoom * 0.25));
                    if (Math.Abs(series.Timestamps[idx] - targetTime) > maxGapNano)
                    {
                        lines.Add((seriesLabel, "N/A", series.SolidBrush));
                        continue;
                    }

                    if (string.IsNullOrEmpty(timeHeader))
                    {
                        timeHeader = DateTimeOffset.FromUnixTimeMilliseconds(series.Timestamps[idx] / 1_000_000L)
                            .ToLocalTime().ToString("MM-dd HH:mm:ss");
                    }
                }
                lines.Add((seriesLabel, FormatMetricValue(series.Metric, series.Values[idx], isRate), series.SolidBrush));
            }
            else
            {
                lines.Add((series.Label, "N/A", series.SolidBrush));
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

    public static string FormatMetricValue(string metric, double v, bool isRate = false)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "N/A";

        string suffix = isRate ? "/s" : "";
        if (metric.StartsWith("cpu.") || metric.Contains("cpu.") || metric.Contains(".cpu") || metric.EndsWith("_pct") || metric.Contains("pct"))
            return isRate ? $"{v:+0.0;-0.0;0.0}%/s" : $"{v:F1}%";
        if (metric.StartsWith("twamp."))
            return $"{v:F2} ms{suffix}";
        if (metric.EndsWith("_ratio"))
            return $"{v:F2}x{suffix}";
        if (metric.Contains("bytes") || metric.StartsWith("memory.") || metric.Contains("mem"))
        {
            double abs = Math.Abs(v);
            string prefix = isRate && v < 0 ? "-" : (isRate && v > 0 ? "+" : "");
            if (abs >= 1_099_511_627_776) return $"{prefix}{abs / 1_099_511_627_776:F2} TB{suffix}";
            if (abs >= 1_073_741_824) return $"{prefix}{abs / 1_073_741_824:F2} GB{suffix}";
            if (abs >= 1_048_576) return $"{prefix}{abs / 1_048_576:F1} MB{suffix}";
            if (abs >= 1024) return $"{prefix}{abs / 1024:F0} KB{suffix}";
            return $"{prefix}{abs:F0} B{suffix}";
        }
        return isRate ? $"{v:F2}/s" : $"{v:F2}";
    }

    private void RebuildGeometryCache(List<ChartSeriesModel> activeSeries, Func<long, double, int, int, Point> mapPoint, double baselineY, double plotW, long visibleStart, long visibleEnd)
    {
        _cachedSeriesGeometries.Clear();

        foreach (var series in activeSeries)
        {
            var vals = series.Values;
            int count = vals.Length;
            if (count < 2) continue;

            var points = BuildDecimatedPoints(series, mapPoint, plotW, visibleStart, visibleEnd);
            if (points.Count < 2) continue;

            var fillGeom = new StreamGeometry();
            var lineGeom = new StreamGeometry();

            using (var fctx = fillGeom.Open())
            using (var lctx = lineGeom.Open())
            {
                Point first = points[0];
                fctx.BeginFigure(new Point(first.X, baselineY), true);
                fctx.LineTo(first);
                lctx.BeginFigure(first, false);

                for (int i = 1; i < points.Count; i++)
                {
                    fctx.LineTo(points[i]);
                    lctx.LineTo(points[i]);
                }

                Point last = points[^1];
                fctx.LineTo(new Point(last.X, baselineY));
            }

            _cachedSeriesGeometries.Add((series.FillBrush, series.LinePen, fillGeom, lineGeom));
        }
    }

    private static List<Point> BuildDecimatedPoints(ChartSeriesModel series, Func<long, double, int, int, Point> mapPoint, double plotW, long visibleStart, long visibleEnd)
    {
        var mapped = new List<Point>(series.Values.Length);
        for (int i = 0; i < series.Values.Length; i++)
            mapped.Add(mapPoint(series.Timestamps.Length > i ? series.Timestamps[i] : 0,
                series.Values[i], i, series.Values.Length));
        return GraphDrawingResolution.Reduce(mapped, 52, plotW, GraphPerformanceSettings.Current.PointsPerPixel, series.Timestamps, visibleStart, visibleEnd);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        GraphPerformanceSettings.Current.Changed += OnDrawingSettingsChanged;
        OnDrawingSettingsChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        GraphPerformanceSettings.Current.Changed -= OnDrawingSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDrawingSettingsChanged()
    {
        if (Bounds.Width > 68)
            SetCurrentValue(HistoryPointBudgetProperty, GraphHistoryResolution.PointBudget(Bounds.Width - 68, GraphPerformanceSettings.Current.HistoryPointsPerPixel));
        MarkGeometryDirty();
        InvalidateVisual();
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
            MarkGeometryDirty();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (e.Pointer.Type == PointerType.Touch)
        {
            _activeTouchPoints[e.Pointer.Id] = q.Position;
            if (_activeTouchPoints.Count == 2)
            {
                _isDragging = false;
                var pts = _activeTouchPoints.Values.ToArray();
                _multiTouchStartDistance = Math.Max(10.0, Math.Abs(pts[0].X - pts[1].X));
                _multiTouchStartZoom = ZoomLevel;
                _multiTouchStartPan = PanOffset;
                _multiTouchStartCenter = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);

                e.PreventGestureRecognition();
                e.Pointer.Capture(this);
                _touchCaptured = true;
                e.Handled = true;
            }
            else if (_activeTouchPoints.Count == 1)
            {
                _touchStartPoint = q.Position;
                _dragStartPan = PanOffset;
                _isDragging = false;
                _touchCaptured = false;
            }
        }
        else if (q.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = q.Position;
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

        const double leftPad = 52.0;
        const double rightPad = 16.0;
        double plotW = Math.Max(10.0, Bounds.Width - leftPad - rightPad);
        double maxPan = Math.Max(0.0, (ZoomLevel - 1.0) * plotW);

        if (e.Pointer.Type == PointerType.Touch && _activeTouchPoints.ContainsKey(e.Pointer.Id))
        {
            _activeTouchPoints[e.Pointer.Id] = e.GetPosition(this);

            if (_activeTouchPoints.Count >= 2)
            {
                e.PreventGestureRecognition();
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
                MarkGeometryDirty();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            else if (_activeTouchPoints.Count == 1 && _touchStartPoint.HasValue)
            {
                Point cur = e.GetPosition(this);
                double dx = cur.X - _touchStartPoint.Value.X;
                double dy = cur.Y - _touchStartPoint.Value.Y;

                if (!_isDragging && !_touchCaptured)
                {
                    if (Math.Abs(dx) > 8 && Math.Abs(dx) > Math.Abs(dy) * 1.2)
                    {
                        // User intended horizontal pan on the graph: lock gesture and capture pointer
                        e.PreventGestureRecognition();
                        e.Pointer.Capture(this);
                        _isDragging = true;
                        _touchCaptured = true;
                        _dragStartPoint = cur;
                    }
                    else if (Math.Abs(dy) > 8 && Math.Abs(dy) > Math.Abs(dx))
                    {
                        // User intended vertical scroll: let parent ScrollViewer handle it
                        return;
                    }
                }

                if (_isDragging)
                {
                    e.PreventGestureRecognition();
                    double deltaX = cur.X - _dragStartPoint.X;
                    PanOffset = Math.Clamp(_dragStartPan - deltaX, 0.0, maxPan);
                    MarkGeometryDirty();
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }

        if (_isDragging && _hoverPoint.HasValue && e.Pointer.Type != PointerType.Touch)
        {
            double deltaX = _hoverPoint.Value.X - _dragStartPoint.X;
            PanOffset = Math.Clamp(_dragStartPan - deltaX, 0.0, maxPan);
            MarkGeometryDirty();
            Cursor = new Cursor(StandardCursorType.Hand);
        }
        else if (e.Pointer.Type != PointerType.Touch)
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
                _touchCaptured = false;
                _touchStartPoint = null;
                e.Pointer.Capture(null);
            }
        }
        else if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            Cursor = ZoomLevel > 1.05 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        _touchCaptured = false;
        _touchStartPoint = null;
        _activeTouchPoints.Clear();
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        const double leftPad = 52.0;
        const double rightPad = 16.0;
        double plotW = Math.Max(10.0, Bounds.Width - leftPad - rightPad);
        double maxPan = Math.Max(0.0, (ZoomLevel - 1.0) * plotW);

        // Trackpad horizontal scroll
        if (Math.Abs(e.Delta.X) > 0.001 && maxPan > 0)
        {
            PanOffset = Math.Clamp(PanOffset - (e.Delta.X * 24.0), 0.0, maxPan);
            MarkGeometryDirty();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Mouse wheel zoom only when Ctrl is pressed!
        // Without Ctrl, allow the wheel event to bubble to parent ScrollViewer for vertical page scroll.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // Mouse wheel zoom
        double mouseX = Math.Clamp((_hoverPoint ?? e.GetPosition(this)).X - leftPad, 0.0, plotW);
        double oldZoom = ZoomLevel;
        double newZoom = Math.Clamp(oldZoom + e.Delta.Y * 0.35, 1.0, 8.0);

        double frac = (mouseX + PanOffset) / (plotW * oldZoom);
        ZoomLevel = newZoom;
        PanOffset = Math.Clamp(frac * plotW * newZoom - mouseX, 0.0, (newZoom - 1.0) * plotW);
        MarkGeometryDirty();

        e.Handled = true;
        InvalidateVisual();
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
