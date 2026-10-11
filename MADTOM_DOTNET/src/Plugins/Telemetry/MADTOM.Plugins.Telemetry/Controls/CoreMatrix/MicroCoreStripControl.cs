using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MadTOM.Common.Utils;
using MadTOM.Models;

namespace MadTOM.Controls.CoreMatrix;

public sealed class MicroCoreStripControl : Control
{
    public static readonly StyledProperty<int> CoreCountProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, int>(nameof(CoreCount), 0);

    public static readonly StyledProperty<float[]> CoreLoadsProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, float[]>(nameof(CoreLoads), Array.Empty<float>());

    public static readonly StyledProperty<string> HostIdProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, string>(nameof(HostId), string.Empty);

    public static readonly StyledProperty<CpuVisualMode> VisualModeProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, CpuVisualMode>(nameof(VisualMode), CpuVisualMode.CoreMatrix);

    public static readonly StyledProperty<double[]> SparkCpuProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, double[]>(nameof(SparkCpu), Array.Empty<double>());

    public static readonly StyledProperty<double[][]> SparkCoreLoadsProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, double[][]>(nameof(SparkCoreLoads), Array.Empty<double[]>());

    public static readonly StyledProperty<int> HoveredIndexProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, int>(nameof(HoveredIndex), -1);

    public static readonly StyledProperty<float> HoveredLoadProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, float>(nameof(HoveredLoad), 0.0f);

    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<MicroCoreStripControl, bool>(nameof(IsHovered), false);

    private static IPen HoverPen => new ImmutablePen(new ImmutableSolidColorBrush(Theming.ThemeService.Instance.GetColor("TextPrimary", Colors.White)), 1.5);
    private static readonly IBrush TooltipBg = new ImmutableSolidColorBrush(Color.FromArgb(235, 10, 15, 29));
    private static readonly IPen TooltipBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, 6, 182, 212)), 1.0);
    private static readonly IBrush CellBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IPen CellBorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 0.6);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 0.6);
    private static readonly IPen CrosshairPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, 148, 163, 184)), 1.0);
    private static readonly IPen OverallCpuPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(240, 6, 182, 212)), 1.5);
    private static readonly IBrush OverallAreaBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(90, 6, 182, 212), 0.0),
            new GradientStop(Color.FromArgb(10, 6, 182, 212), 1.0)
        }
    }.ToImmutable();

    private static readonly ConcurrentDictionary<int, (Color Color, IPen Pen)> _corePenCache = new();

    private CoreLayoutResult _currentLayout;
    private Point? _lastPointerPos;

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

    public CpuVisualMode VisualMode
    {
        get => GetValue(VisualModeProperty);
        set => SetValue(VisualModeProperty, value);
    }

    public double[] SparkCpu
    {
        get => GetValue(SparkCpuProperty);
        set => SetValue(SparkCpuProperty, value);
    }

    public double[][] SparkCoreLoads
    {
        get => GetValue(SparkCoreLoadsProperty);
        set => SetValue(SparkCoreLoadsProperty, value);
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

    private static readonly Typeface MonoTypeface = new("Consolas, Courier New, Monospace");

    static MicroCoreStripControl()
    {
        AffectsRender<MicroCoreStripControl>(
            CoreCountProperty,
            CoreLoadsProperty,
            HoveredIndexProperty,
            VisualModeProperty,
            SparkCpuProperty,
            SparkCoreLoadsProperty);
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

        // Transparent background for seamless hit-testing across entire control
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        const double padX = 4.0;
        const double padY = 3.0;
        double availW = Math.Max(20.0, w - (padX * 2.0));
        double availH = Math.Max(20.0, h - (padY * 2.0));

        int count = CoreCount;
        if (count <= 0) count = 1;

        switch (VisualMode)
        {
            case CpuVisualMode.CoreMatrix:
                RenderCoreMatrix(context, availW, availH, padX, padY, count);
                break;
            case CpuVisualMode.PerCoreGraphs:
                RenderPerCoreGraphs(context, availW, availH, padX, padY, count);
                break;
            case CpuVisualMode.OverallGraph:
                RenderOverallGraph(context, availW, availH, padX, padY);
                break;
            case CpuVisualMode.AggregatedPerCore:
                RenderAggregatedPerCore(context, availW, availH, padX, padY, count);
                break;
        }

        // Floating tooltip beside mouse cursor
        if (_lastPointerPos.HasValue && (IsHovered || HoveredIndex >= 0))
        {
            RenderHoverTooltip(context, w, h, _lastPointerPos.Value, count);
        }
    }

    private void RenderCoreMatrix(DrawingContext context, double availW, double availH, double padX, double padY, int count)
    {
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

    public static (int cols, int rows) ComputeEvenPerCoreGrid(double availW, double availH, int coreCount)
    {
        if (coreCount <= 0) coreCount = 1;
        if (coreCount == 1) return (1, 1);

        int bestCols = 1;
        int bestRows = coreCount;
        double bestScore = double.NegativeInfinity;

        int maxCols = Math.Min(coreCount, 32);
        for (int cols = 1; cols <= maxCols; cols++)
        {
            int rows = (int)Math.Ceiling((double)coreCount / cols);
            int emptySlots = (cols * rows) - coreCount;

            const double gap = 2.0;
            double cellW = (availW - (cols - 1) * gap) / cols;
            double cellH = (availH - (rows - 1) * gap) / rows;

            if (cellW < 12.0 || cellH < 8.0) continue;

            double aspect = cellW / Math.Max(1.0, cellH);
            double aspectScore = (aspect >= 1.2 && aspect <= 4.0) ? 50.0 : -Math.Abs(aspect - 2.0) * 15.0;

            double score = cellW * 2.0 + cellH * 3.0 + aspectScore;

            // Strong bonus for even column count
            if (cols % 2 == 0) score += 60.0;
            // Bonus for even row count when multi-row
            if (rows > 1 && rows % 2 == 0) score += 40.0;
            // Heavy penalty for empty orphan slots
            score -= emptySlots * 45.0;
            if (emptySlots == 0) score += 50.0;

            if (score > bestScore)
            {
                bestScore = score;
                bestCols = cols;
                bestRows = rows;
            }
        }

        return (bestCols, bestRows);
    }

    private void RenderPerCoreGraphs(DrawingContext context, double availW, double availH, double padX, double padY, int count)
    {
        var (cols, rows) = ComputeEvenPerCoreGrid(availW, availH, count);
        const double gap = 2.0;
        double cellW = (availW - (cols - 1) * gap) / cols;
        double cellH = (availH - (rows - 1) * gap) / rows;

        var loads = CoreLoads;
        var sparkLoads = SparkCoreLoads;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            double x = padX + col * (cellW + gap);
            double y = padY + row * (cellH + gap);

            var cellRect = new RoundedRect(new Rect(x, y, cellW, cellH), 2.0);
            context.DrawRectangle(CellBgBrush, CellBorderPen, cellRect);

            double[]? history = (sparkLoads != null && i < sparkLoads.Length) ? sparkLoads[i] : null;
            float curLoad = (loads != null && i < loads.Length) ? loads[i] : 0.0f;

            if (history != null && history.Length > 1)
            {
                var (_, pen) = GetCoreColorAndPen(i);
                DrawMiniSparkline(context, history, pen, x + 1, y + 1, cellW - 2, cellH - 2);
            }
            else
            {
                // Fallback bar
                double barH = Math.Clamp((cellH - 2) * curLoad, 1.0, cellH - 2);
                var barBrush = ColorInterpolator.InterpolateLoadBrush(curLoad);
                context.FillRectangle(barBrush, new Rect(x + 1, y + cellH - 1 - barH, cellW - 2, barH));
            }

            // Small label for core identifier if space allows
            if (cellW >= 18.0 && cellH >= 12.0)
            {
                var label = new FormattedText(
                    $"#{i}",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    7.0,
                    Brushes.White);
                context.DrawText(label, new Point(x + 2, y + 1));
            }

            if (i == HoveredIndex)
            {
                context.DrawRectangle(null, HoverPen, cellRect);
            }
        }
    }

    private static void DrawMiniSparkline(DrawingContext context, double[] history, IPen pen, double startX, double startY, double w, double h)
    {
        int n = history.Length;
        if (n < 2 || w <= 0 || h <= 0) return;

        double step = w / (n - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double val = Math.Clamp(history[i], 0.0, 100.0);
                double px = startX + i * step;
                double py = startY + (1.0 - (val / 100.0)) * h;

                if (i == 0) ctx.BeginFigure(new Point(px, py), false);
                else ctx.LineTo(new Point(px, py));
            }
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private void RenderOverallGraph(DrawingContext context, double availW, double availH, double padX, double padY)
    {
        // Gridlines
        context.DrawLine(GridPen, new Point(padX, padY + availH * 0.5), new Point(padX + availW, padY + availH * 0.5));
        context.DrawLine(GridPen, new Point(padX, padY + availH), new Point(padX + availW, padY + availH));

        var data = SparkCpu ?? Array.Empty<double>();
        int count = data.Length;
        if (count < 2) return;

        double step = availW / (count - 1);
        var lineGeo = new StreamGeometry();
        var areaGeo = new StreamGeometry();

        using (var lineCtx = lineGeo.Open())
        using (var areaCtx = areaGeo.Open())
        {
            areaCtx.BeginFigure(new Point(padX, padY + availH), true);

            for (int i = 0; i < count; i++)
            {
                double val = Math.Clamp(data[i], 0.0, 100.0);
                double x = padX + i * step;
                double y = padY + (1.0 - (val / 100.0)) * availH;

                if (i == 0) lineCtx.BeginFigure(new Point(x, y), false);
                else lineCtx.LineTo(new Point(x, y));

                areaCtx.LineTo(new Point(x, y));
            }

            areaCtx.LineTo(new Point(padX + availW, padY + availH));
        }

        context.DrawGeometry(OverallAreaBrush, null, areaGeo);
        context.DrawGeometry(null, OverallCpuPen, lineGeo);

        // Hover Crosshair
        if (_lastPointerPos.HasValue && IsHovered)
        {
            double mouseX = Math.Clamp(_lastPointerPos.Value.X, padX, padX + availW);
            int idx = Math.Clamp((int)Math.Round((mouseX - padX) / step), 0, count - 1);
            double targetX = padX + idx * step;
            double val = Math.Clamp(data[idx], 0.0, 100.0);
            double dotY = padY + (1.0 - (val / 100.0)) * availH;

            context.DrawLine(CrosshairPen, new Point(targetX, padY), new Point(targetX, padY + availH));
            context.DrawEllipse(OverallCpuPen.Brush, null, new Point(targetX, dotY), 2.5, 2.5);
        }
    }

    private void RenderAggregatedPerCore(DrawingContext context, double availW, double availH, double padX, double padY, int count)
    {
        // Gridlines
        context.DrawLine(GridPen, new Point(padX, padY + availH * 0.5), new Point(padX + availW, padY + availH * 0.5));
        context.DrawLine(GridPen, new Point(padX, padY + availH), new Point(padX + availW, padY + availH));

        var sparkLoads = SparkCoreLoads;
        var loads = CoreLoads;

        for (int i = 0; i < count; i++)
        {
            var (_, pen) = GetCoreColorAndPen(i);
            double[]? history = (sparkLoads != null && i < sparkLoads.Length) ? sparkLoads[i] : null;

            if (history != null && history.Length > 1)
            {
                double step = availW / (history.Length - 1);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    for (int pt = 0; pt < history.Length; pt++)
                    {
                        double val = Math.Clamp(history[pt], 0.0, 100.0);
                        double x = padX + pt * step;
                        double y = padY + (1.0 - (val / 100.0)) * availH;

                        if (pt == 0) ctx.BeginFigure(new Point(x, y), false);
                        else ctx.LineTo(new Point(x, y));
                    }
                }
                context.DrawGeometry(null, pen, geo);
            }
            else if (loads != null && i < loads.Length)
            {
                double val = Math.Clamp(loads[i] * 100.0, 0.0, 100.0);
                double y = padY + (1.0 - (val / 100.0)) * availH;
                context.DrawLine(pen, new Point(padX, y), new Point(padX + availW, y));
            }
        }

        // Hover Crosshair
        if (_lastPointerPos.HasValue && IsHovered && sparkLoads != null && sparkLoads.Length > 0 && sparkLoads[0].Length > 1)
        {
            int sampleCount = sparkLoads[0].Length;
            double step = availW / (sampleCount - 1);
            double mouseX = Math.Clamp(_lastPointerPos.Value.X, padX, padX + availW);
            int idx = Math.Clamp((int)Math.Round((mouseX - padX) / step), 0, sampleCount - 1);
            double targetX = padX + idx * step;

            context.DrawLine(CrosshairPen, new Point(targetX, padY), new Point(targetX, padY + availH));
        }
    }

    public static (Color Color, IPen Pen) GetCoreColorAndPen(int coreIndex)
    {
        return _corePenCache.GetOrAdd(coreIndex, idx =>
        {
            var color = GetNonOverlappingCoreColor(idx);
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(color), 1.2);
            return (color, pen);
        });
    }

    public static Color GetNonOverlappingCoreColor(int coreIndex)
    {
        // Golden angle in degrees (~137.50776405°) guarantees maximum distance between sequential hues
        double hue = (coreIndex * 137.507764050037854) % 360.0;
        // High vibrancy with alternating saturation/value for maximal contrast between adjacent lines
        double sat = 0.75 + ((coreIndex % 3) * 0.10); // 0.75, 0.85, 0.95
        double val = 0.85 + ((coreIndex % 2) * 0.12); // 0.85, 0.97
        return HsvToRgb(hue, sat, val);
    }

    public static Color HsvToRgb(double hue, double sat, double val)
    {
        double c = val * sat;
        double hPrime = (hue % 360.0) / 60.0;
        double x = c * (1.0 - Math.Abs((hPrime % 2.0) - 1.0));
        double m = val - c;

        double r = 0, g = 0, b = 0;
        if (hPrime < 1.0) { r = c; g = x; b = 0; }
        else if (hPrime < 2.0) { r = x; g = c; b = 0; }
        else if (hPrime < 3.0) { r = 0; g = c; b = x; }
        else if (hPrime < 4.0) { r = 0; g = x; b = c; }
        else if (hPrime < 5.0) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        // Math.Clamp to 0-255 ensures complete safety against any byte overflow
        byte rByte = (byte)Math.Clamp((int)Math.Round((r + m) * 255.0), 0, 255);
        byte gByte = (byte)Math.Clamp((int)Math.Round((g + m) * 255.0), 0, 255);
        byte bByte = (byte)Math.Clamp((int)Math.Round((b + m) * 255.0), 0, 255);

        return Color.FromArgb(255, rByte, gByte, bByte);
    }

    private void RenderHoverTooltip(DrawingContext context, double w, double h, Point pt, int count)
    {
        string text;
        const double padX = 4.0;
        double availW = Math.Max(20.0, w - (padX * 2.0));

        switch (VisualMode)
        {
            case CpuVisualMode.CoreMatrix:
            {
                var loads = CoreLoads;
                float currentLoad = (loads != null && HoveredIndex >= 0 && HoveredIndex < loads.Length)
                    ? loads[HoveredIndex]
                    : HoveredLoad;
                text = $"T#{HoveredIndex}: {currentLoad * 100:F1}%";
                break;
            }
            case CpuVisualMode.PerCoreGraphs:
            {
                var loads = CoreLoads;
                float currentLoad = (loads != null && HoveredIndex >= 0 && HoveredIndex < loads.Length)
                    ? loads[HoveredIndex]
                    : HoveredLoad;
                text = $"Core #{HoveredIndex}: {currentLoad * 100:F1}%";
                break;
            }
            case CpuVisualMode.OverallGraph:
            {
                var data = SparkCpu ?? Array.Empty<double>();
                if (data.Length >= 2)
                {
                    double step = availW / (data.Length - 1);
                    double mouseX = Math.Clamp(pt.X, padX, padX + availW);
                    int idx = Math.Clamp((int)Math.Round((mouseX - padX) / step), 0, data.Length - 1);
                    text = $"CPU: {data[idx]:F1}%";
                }
                else
                {
                    text = "CPU: --";
                }
                break;
            }
            case CpuVisualMode.AggregatedPerCore:
            {
                var sparkLoads = SparkCoreLoads;
                var loads = CoreLoads;
                if (sparkLoads != null && sparkLoads.Length > 0 && sparkLoads[0].Length >= 2)
                {
                    int sampleCount = sparkLoads[0].Length;
                    double step = availW / (sampleCount - 1);
                    double mouseX = Math.Clamp(pt.X, padX, padX + availW);
                    int idx = Math.Clamp((int)Math.Round((mouseX - padX) / step), 0, sampleCount - 1);

                    int topCore = 0;
                    double maxVal = double.MinValue;
                    double sum = 0.0;
                    for (int c = 0; c < sparkLoads.Length; c++)
                    {
                        double v = (idx < sparkLoads[c].Length) ? sparkLoads[c][idx] : 0.0;
                        sum += v;
                        if (v > maxVal)
                        {
                            maxVal = v;
                            topCore = c;
                        }
                    }
                    double avg = sum / Math.Max(1, sparkLoads.Length);
                    text = $"C#{topCore}: {maxVal:F1}% | Avg: {avg:F1}%";
                }
                else if (loads != null && loads.Length > 0)
                {
                    text = $"{count} Cores Active";
                }
                else
                {
                    text = "Per-Core Aggregated";
                }
                break;
            }
            default:
                text = "";
                break;
        }

        if (string.IsNullOrEmpty(text)) return;

        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            9.0,
            Brushes.White);

        double padH = 6.0;
        double padV = 3.0;
        double tipW = ft.Width + (padH * 2.0);
        double tipH = ft.Height + (padV * 2.0);

        double tipX = pt.X + 10.0;
        double tipY = pt.Y - tipH - 3.0;

        if (tipX + tipW > w - 2.0)
        {
            tipX = pt.X - tipW - 6.0;
        }
        if (tipX < 2.0)
        {
            tipX = 2.0;
        }

        if (tipY < 2.0)
        {
            tipY = pt.Y + 12.0;
        }
        if (tipY + tipH > h - 2.0)
        {
            tipY = h - tipH - 2.0;
        }

        var tipRect = new RoundedRect(new Rect(tipX, tipY, tipW, tipH), 4.0);
        context.DrawRectangle(TooltipBg, TooltipBorder, tipRect);
        context.DrawText(ft, new Point(tipX + padH, tipY + padV));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        _lastPointerPos = pt;

        const double padX = 4.0;
        const double padY = 3.0;
        double w = Bounds.Width;
        double h = Bounds.Height;
        double availW = Math.Max(20.0, w - (padX * 2.0));
        double availH = Math.Max(20.0, h - (padY * 2.0));

        switch (VisualMode)
        {
            case CpuVisualMode.CoreMatrix:
            {
                var L = _currentLayout;
                double startX = padX + L.StartX;
                double startY = padY + L.StartY;
                double relX = pt.X - startX;
                double relY = pt.Y - startY;

                if (relX >= 0 && relY >= 0 && L.Cols > 0)
                {
                    double step = L.CellSize + L.Gap;
                    int col = (int)(relX / step);
                    int row = (int)(relY / step);

                    if (col >= 0 && col < L.Cols && row >= 0 && row < L.Rows)
                    {
                        int idx = (row * L.Cols) + col;
                        if (idx >= 0 && idx < CoreCount)
                        {
                            HoveredIndex = idx;
                            IsHovered = true;
                            HoveredLoad = (CoreLoads != null && idx < CoreLoads.Length) ? CoreLoads[idx] : 0.0f;
                            InvalidateVisual();
                            return;
                        }
                    }
                }
                break;
            }
            case CpuVisualMode.PerCoreGraphs:
            {
                int count = Math.Max(1, CoreCount);
                var (cols, rows) = ComputeEvenPerCoreGrid(availW, availH, count);
                const double gap = 2.0;
                double cellW = (availW - (cols - 1) * gap) / cols;
                double cellH = (availH - (rows - 1) * gap) / rows;

                double relX = pt.X - padX;
                double relY = pt.Y - padY;

                if (relX >= 0 && relY >= 0 && cols > 0 && rows > 0)
                {
                    int col = (int)(relX / (cellW + gap));
                    int row = (int)(relY / (cellH + gap));

                    if (col >= 0 && col < cols && row >= 0 && row < rows)
                    {
                        int idx = (row * cols) + col;
                        if (idx >= 0 && idx < count)
                        {
                            HoveredIndex = idx;
                            IsHovered = true;
                            HoveredLoad = (CoreLoads != null && idx < CoreLoads.Length) ? CoreLoads[idx] : 0.0f;
                            InvalidateVisual();
                            return;
                        }
                    }
                }
                break;
            }
            case CpuVisualMode.OverallGraph:
            case CpuVisualMode.AggregatedPerCore:
            {
                if (pt.X >= padX && pt.X <= w - padX && pt.Y >= padY && pt.Y <= h - padY)
                {
                    IsHovered = true;
                    InvalidateVisual();
                    return;
                }
                break;
            }
        }

        if (HoveredIndex != -1 || IsHovered)
        {
            HoveredIndex = -1;
            IsHovered = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredIndex = -1;
        _lastPointerPos = null;
        IsHovered = false;
        InvalidateVisual();
    }
}
