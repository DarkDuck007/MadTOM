using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace MadTOM.Controls.Radar;

public sealed class PolarDropRadarControl : Control
{
    private sealed class DropBlip
    {
        public double R { get; set; }
        public double Theta { get; set; }
        public double Alpha { get; set; }
    }

    private static readonly IPen RingPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(30, 41, 59)), 1);
    private static readonly IPen CrosshairPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(30, 41, 59)), 1);
    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(4, 6, 11));
    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(30, 41, 59)), 1);

    private readonly List<DropBlip> _blips = new();
    private readonly DispatcherTimer _animationTimer;
    private double _sweepAngle;

    public PolarDropRadarControl()
    {
        ClipToBounds = true;
        var random = new Random(101);
        for (int i = 0; i < 14; i++)
        {
            _blips.Add(new DropBlip
            {
                R = random.NextDouble() * 0.80 + 0.10,
                Theta = random.NextDouble() * Math.PI * 2.0,
                Alpha = random.NextDouble() * 0.70 + 0.30
            });
        }

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
        };
        _animationTimer.Tick += (s, e) =>
        {
            _sweepAngle += 0.045;
            if (_sweepAngle > Math.PI * 2.0)
            {
                _sweepAngle -= Math.PI * 2.0;
            }
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _animationTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animationTimer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 20 || h < 20) return;

        double size = Math.Min(w, h);
        double cx = w / 2.0;
        double cy = h / 2.0;
        double radius = (size / 2.0) - 4.0;
        if (radius <= 0) return;

        // Background Circle
        context.DrawEllipse(BackgroundBrush, BorderPen, new Point(cx, cy), radius, radius);

        // Concentric Rings
        double[] ringRatios = [0.25, 0.50, 0.75, 0.95];
        foreach (double ratio in ringRatios)
        {
            double r = radius * ratio;
            context.DrawEllipse(null, RingPen, new Point(cx, cy), r, r);
        }

        // Crosshairs
        context.DrawLine(CrosshairPen, new Point(cx - radius, cy), new Point(cx + radius, cy));
        context.DrawLine(CrosshairPen, new Point(cx, cy - radius), new Point(cx, cy + radius));

        // Draw Blip Dots
        foreach (var blip in _blips)
        {
            double r = radius * blip.R;
            double x = cx + r * Math.Cos(blip.Theta);
            double y = cy + r * Math.Sin(blip.Theta);

            byte a = (byte)Math.Clamp(blip.Alpha * 255.0, 0.0, 255.0);
            var blipBrush = new ImmutableSolidColorBrush(Color.FromArgb(a, 244, 63, 94));
            context.DrawEllipse(blipBrush, null, new Point(x, y), 3.0, 3.0);
        }

        // Draw Sweep Beam
        double beamX = cx + radius * Math.Cos(_sweepAngle);
        double beamY = cy + radius * Math.Sin(_sweepAngle);
        var sweepPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 6, 182, 212)), 1.5);
        context.DrawLine(sweepPen, new Point(cx, cy), new Point(beamX, beamY));

        // Draw Sweep Wedge (gradient tail)
        var sweepGeometry = new StreamGeometry();
        using (var sctx = sweepGeometry.Open())
        {
            sctx.BeginFigure(new Point(cx, cy), true);
            const int tailSteps = 12;
            const double tailAngle = 0.45; // ~25 degrees
            for (int i = 0; i <= tailSteps; i++)
            {
                double a = _sweepAngle - (tailAngle * (1.0 - (double)i / tailSteps));
                double tx = cx + radius * Math.Cos(a);
                double ty = cy + radius * Math.Sin(a);
                sctx.LineTo(new Point(tx, ty));
            }
        }

        var sweepGrad = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(90, 6, 182, 212), 0.0),
                new GradientStop(Color.FromArgb(20, 6, 182, 212), 0.7),
                new GradientStop(Color.FromArgb(0, 6, 182, 212), 1.0)
            }
        };

        context.DrawGeometry(sweepGrad, null, sweepGeometry);
    }
}
