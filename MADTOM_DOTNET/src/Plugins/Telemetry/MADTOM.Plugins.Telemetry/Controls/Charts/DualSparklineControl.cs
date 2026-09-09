using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MadTOM.Controls.Charts;

public enum SparklineMode
{
    Network,
    Resources
}

public sealed class DualSparklineControl : Control
{
    public static readonly StyledProperty<SparklineMode> ModeProperty =
        AvaloniaProperty.Register<DualSparklineControl, SparklineMode>(nameof(Mode), SparklineMode.Network);

    public static readonly StyledProperty<double[]> Series1Property =
        AvaloniaProperty.Register<DualSparklineControl, double[]>(nameof(Series1), Array.Empty<double>());

    public static readonly StyledProperty<double[]> Series2Property =
        AvaloniaProperty.Register<DualSparklineControl, double[]>(nameof(Series2), Array.Empty<double>());

    private static readonly IPen CyanPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)), 1.5);
    private static readonly IPen IndigoPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(129, 140, 248)), 1.5);
    private static readonly IPen AmberPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11)), 1.5);
    private static readonly IPen PurplePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(168, 85, 247)), 1.5);

    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(80, 30, 41, 59)), 1.0);
    private static readonly IPen CrosshairPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 100, 116, 139)), 1.0, DashStyle.Dash);

    private static readonly IBrush TooltipBg = new ImmutableSolidColorBrush(Color.FromArgb(230, 4, 6, 11));
    private static readonly IPen TooltipBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(51, 65, 85)), 1.0);
    private static readonly IBrush AxisTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly IBrush WhiteTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(248, 250, 252));

    private static readonly Typeface MonoTypeface = new("Consolas, Courier New, Monospace");

    private Point? _hoverPoint;

    public SparklineMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double[] Series1
    {
        get => GetValue(Series1Property);
        set => SetValue(Series1Property, value);
    }

    public double[] Series2
    {
        get => GetValue(Series2Property);
        set => SetValue(Series2Property, value);
    }

    static DualSparklineControl()
    {
        AffectsRender<DualSparklineControl>(ModeProperty, Series1Property, Series2Property);
    }

    public DualSparklineControl()
    {
        ClipToBounds = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverPoint = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 16) return;

        // Ensure full container area is hit-testable even over empty space
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        var s1 = Series1 ?? Array.Empty<double>();
        var s2 = Series2 ?? Array.Empty<double>();
        int count = Math.Max(s1.Length, s2.Length);

        const double yAxisWidth = 26.0;
        double chartW = w - yAxisWidth - 2.0;

        // Calculate combined bounds for y-axis range
        double minVal = double.MaxValue;
        double maxVal = double.MinValue;

        for (int i = 0; i < s1.Length; i++)
        {
            if (s1[i] < minVal) minVal = s1[i];
            if (s1[i] > maxVal) maxVal = s1[i];
        }
        for (int i = 0; i < s2.Length; i++)
        {
            if (s2[i] < minVal) minVal = s2[i];
            if (s2[i] > maxVal) maxVal = s2[i];
        }

        if (minVal == double.MaxValue || maxVal == double.MinValue)
        {
            minVal = 0.0;
            maxVal = 1.0;
        }

        double range = Math.Max(0.1, maxVal - minVal);

        // Draw Y-axis guide gridlines
        context.DrawLine(GridPen, new Point(yAxisWidth, 4), new Point(w, 4));
        context.DrawLine(GridPen, new Point(yAxisWidth, h - 4), new Point(w, h - 4));

        // Format Y-axis labels
        string maxLabel = Mode == SparklineMode.Network ? $"{maxVal:0.0}G" : $"{maxVal:0}%";
        string minLabel = Mode == SparklineMode.Network ? $"{minVal:0.0}G" : $"{minVal:0}%";

        var topText = new FormattedText(
            maxLabel,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            8.0,
            AxisTextBrush);

        var bottomText = new FormattedText(
            minLabel,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            8.0,
            AxisTextBrush);

        context.DrawText(topText, new Point(1, 0));
        context.DrawText(bottomText, new Point(1, h - 10));

        // Draw Series
        IPen pen1 = Mode == SparklineMode.Network ? CyanPen : AmberPen;
        IPen pen2 = Mode == SparklineMode.Network ? IndigoPen : PurplePen;

        DrawSeriesLine(context, s1, pen1, yAxisWidth, chartW, h, minVal, range, isSecondary: false);
        DrawSeriesLine(context, s2, pen2, yAxisWidth, chartW, h, minVal, range, isSecondary: true);

        // Hover Crosshair & Info Tooltip across the whole container
        if (_hoverPoint.HasValue && count >= 2 && chartW > 0)
        {
            double minMouseX = yAxisWidth;
            double maxMouseX = Math.Max(minMouseX, w);
            double mouseX = Math.Clamp(_hoverPoint.Value.X, minMouseX, maxMouseX);
            double step = chartW / (count - 1);
            int idx = Math.Clamp((int)Math.Round((mouseX - yAxisWidth) / step), 0, count - 1);
            double targetX = yAxisWidth + idx * step;

            // Draw vertical crosshair line cutting the curves
            context.DrawLine(CrosshairPen, new Point(targetX, 2), new Point(targetX, h - 2));

            double v1 = idx < s1.Length ? s1[idx] : 0.0;
            double v2 = idx < s2.Length ? s2[idx] : 0.0;

            // Draw curve dots
            double dotY1 = ComputeY(v1, minVal, range, h, isSecondary: false);
            double dotY2 = ComputeY(v2, minVal, range, h, isSecondary: true);
            context.DrawEllipse(pen1.Brush, null, new Point(targetX, dotY1), 2.5, 2.5);
            context.DrawEllipse(pen2.Brush, null, new Point(targetX, dotY2), 2.5, 2.5);

            // Format hover tooltip
            string info = Mode == SparklineMode.Network
                ? $"TX:{v1:0.00}G RX:{v2:0.00}G"
                : $"CPU:{v1:0.0}% RAM:{v2:0.0}%";

            var tooltipText = new FormattedText(
                info,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                8.5,
                WhiteTextBrush);

            double tipW = tooltipText.Width + 8;
            double tipH = tooltipText.Height + 4;
            double minTipX = yAxisWidth + 2;
            double maxTipX = Math.Max(minTipX, w - tipW - 2);
            double tipX = Math.Clamp(targetX - tipW / 2.0, minTipX, maxTipX);
            double tipY = (dotY1 + dotY2) / 2.0 > h / 2.0 ? 2 : Math.Max(0, h - tipH - 2);

            var tipRect = new RoundedRect(new Rect(tipX, tipY, tipW, tipH), 3);
            context.DrawRectangle(TooltipBg, TooltipBorder, tipRect);
            context.DrawText(tooltipText, new Point(tipX + 4, tipY + 2));
        }
    }

    private static double ComputeY(double val, double min, double range, double h, bool isSecondary)
    {
        double targetMinY = isSecondary ? h * 0.48 : h * 0.12;
        double targetMaxY = isSecondary ? h * 0.88 : h * 0.52;
        double targetHeight = targetMaxY - targetMinY;
        double normalized = (val - min) / range;
        return targetMaxY - (normalized * targetHeight);
    }

    private static void DrawSeriesLine(DrawingContext context, double[]? data, IPen pen, double startX, double chartW, double h, double min, double range, bool isSecondary)
    {
        int count = data?.Length ?? 0;
        if (count < 2)
        {
            double baseY = isSecondary ? h * 0.65 : h * 0.35;
            context.DrawLine(pen, new Point(startX, baseY), new Point(startX + chartW, baseY));
            return;
        }

        double step = chartW / (count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < count; i++)
            {
                double x = startX + i * step;
                double y = ComputeY(data![i], min, range, h, isSecondary);

                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y));
                }
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
