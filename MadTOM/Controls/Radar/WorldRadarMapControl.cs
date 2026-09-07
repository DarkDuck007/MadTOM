using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MadTOM.Controls.Radar;

public sealed class WorldRadarMapControl : Control
{
    private sealed class RegionData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DefaultFill { get; set; } = "#334155";
        public string Traffic { get; set; } = string.Empty;
        public string Rtt { get; set; } = string.Empty;
        public List<string> Paths { get; set; } = new();

        public List<Geometry> Geometries { get; } = new();
        public IBrush CachedBrush { get; set; } = Brushes.SlateGray;
        public Rect Bounds { get; set; }
    }

    private sealed class TransitArc
    {
        public Point P1 { get; set; }
        public Point Control { get; set; }
        public Point P2 { get; set; }
        public Color Color { get; set; }
        public double Width { get; set; }
    }

    private sealed class PopNode
    {
        public string Name { get; set; } = string.Empty;
        public Point Location { get; set; }
        public Color Color { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public static readonly StyledProperty<string> ScopedNodeIdProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(ScopedNodeId), "aggregated");

    public static readonly StyledProperty<string> HoveredRegionNameProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredRegionName), string.Empty);

    public static readonly StyledProperty<string> HoveredTrafficRateProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredTrafficRate), string.Empty);

    public static readonly StyledProperty<string> HoveredRttProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredRtt), string.Empty);

    public static readonly StyledProperty<bool> IsRegionHoveredProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, bool>(nameof(IsRegionHovered), false);

    private static readonly IBrush OceanBrush = new ImmutableSolidColorBrush(Color.FromRgb(5, 8, 17));
    private static readonly IPen PathPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(15, 23, 42)), 0.5);
    private static readonly Typeface MonoBoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private readonly List<RegionData> _regions = new();
    private readonly List<TransitArc> _transitArcs = new();
    private readonly List<PopNode> _popNodes = new();
    private readonly DispatcherTimer _animTimer;

    private double _dashPhase;
    private double _pulseRadius = 14.0;
    private bool _pulseExpanding = true;
    private RegionData? _activeHoverRegion;

    public string ScopedNodeId
    {
        get => GetValue(ScopedNodeIdProperty);
        set => SetValue(ScopedNodeIdProperty, value);
    }

    public string HoveredRegionName
    {
        get => GetValue(HoveredRegionNameProperty);
        set => SetValue(HoveredRegionNameProperty, value);
    }

    public string HoveredTrafficRate
    {
        get => GetValue(HoveredTrafficRateProperty);
        set => SetValue(HoveredTrafficRateProperty, value);
    }

    public string HoveredRtt
    {
        get => GetValue(HoveredRttProperty);
        set => SetValue(HoveredRttProperty, value);
    }

    public bool IsRegionHovered
    {
        get => GetValue(IsRegionHoveredProperty);
        set => SetValue(IsRegionHoveredProperty, value);
    }

    static WorldRadarMapControl()
    {
        AffectsRender<WorldRadarMapControl>(ScopedNodeIdProperty, IsRegionHoveredProperty);
    }

    public WorldRadarMapControl()
    {
        ClipToBounds = true;
        LoadRegions();
        InitializeTransitNetwork();

        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _animTimer.Tick += (s, e) =>
        {
            _dashPhase = (_dashPhase + 1.2) % 24.0;

            if (_pulseExpanding)
            {
                _pulseRadius += 0.35;
                if (_pulseRadius >= 24.0) _pulseExpanding = false;
            }
            else
            {
                _pulseRadius -= 0.35;
                if (_pulseRadius <= 14.0) _pulseExpanding = true;
            }

            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _animTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animTimer.Stop();
    }

    private void LoadRegions()
    {
        try
        {
            var uri = new Uri("avares://MadTOM/Assets/Maps/world-regions.json");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<List<RegionData>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    foreach (var r in parsed)
                    {
                        Color.TryParse(r.DefaultFill, out var col);
                        r.CachedBrush = new ImmutableSolidColorBrush(col);
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

                        foreach (var pathStr in r.Paths)
                        {
                            try
                            {
                                var geom = Geometry.Parse(pathStr);
                                r.Geometries.Add(geom);
                                var b = geom.Bounds;
                                if (b.Left < minX) minX = b.Left;
                                if (b.Top < minY) minY = b.Top;
                                if (b.Right > maxX) maxX = b.Right;
                                if (b.Bottom > maxY) maxY = b.Bottom;
                            }
                            catch { }
                        }
                        r.Bounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                        _regions.Add(r);
                    }
                }
            }
        }
        catch { }
    }

    private void InitializeTransitNetwork()
    {
        _transitArcs.Add(new TransitArc { P1 = new Point(590, 255), Control = new Point(1150, 50), P2 = new Point(1715, 270), Color = Color.FromRgb(249, 115, 22), Width = 3.5 });
        _transitArcs.Add(new TransitArc { P1 = new Point(1045, 200), Control = new Point(1380, 120), P2 = new Point(1715, 270), Color = Color.FromRgb(245, 158, 11), Width = 3.0 });
        _transitArcs.Add(new TransitArc { P1 = new Point(590, 255), Control = new Point(815, 140), P2 = new Point(1045, 200), Color = Color.FromRgb(245, 158, 11), Width = 2.5 });
        _transitArcs.Add(new TransitArc { P1 = new Point(1572, 495), Control = new Point(1660, 380), P2 = new Point(1715, 270), Color = Color.FromRgb(234, 88, 12), Width = 2.5 });

        _popNodes.Add(new PopNode { Name = "TYO", Location = new Point(1715, 270), Color = Color.FromRgb(6, 182, 212), Label = "TYO (Gander-01) - 1.8ms" });
        _popNodes.Add(new PopNode { Name = "IAD", Location = new Point(590, 255), Color = Color.FromRgb(249, 115, 22), Label = "IAD (Edge-01) - 78ms" });
        _popNodes.Add(new PopNode { Name = "FRA", Location = new Point(1045, 200), Color = Color.FromRgb(245, 158, 11), Label = "FRA (Cache-04) - 142ms" });
        _popNodes.Add(new PopNode { Name = "SIN", Location = new Point(1572, 495), Color = Color.FromRgb(16, 185, 129), Label = "SIN (Gateway) - 62ms" });
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 50 || h < 50) return;

        // Background
        context.FillRectangle(OceanBrush, new Rect(0, 0, w, h));

        // SVG native coordinate space is 2000 x 857
        const double mapW = 2000.0;
        const double mapH = 857.0;

        double scale = Math.Min(w / mapW, h / mapH);
        double offsetX = (w - (mapW * scale)) / 2.0;
        double offsetY = (h - (mapH * scale)) / 2.0;

        using (context.PushTransform(Matrix.CreateTranslation(offsetX, offsetY) * Matrix.CreateScale(scale, scale)))
        {
            // Draw Regions
            foreach (var reg in _regions)
            {
                bool isHovered = reg == _activeHoverRegion;
                IBrush fillBrush = isHovered ? Brushes.LightCyan : reg.CachedBrush;

                foreach (var geom in reg.Geometries)
                {
                    context.DrawGeometry(fillBrush, PathPen, geom);
                }
            }

            // Draw Animated Transit Arcs
            var dashStyle = new ImmutableDashStyle([12, 8], _dashPhase);
            foreach (var arc in _transitArcs)
            {
                var pen = new ImmutablePen(new ImmutableSolidColorBrush(arc.Color), arc.Width, dashStyle);
                var geom = new StreamGeometry();
                using (var c = geom.Open())
                {
                    c.BeginFigure(arc.P1, false);
                    c.QuadraticBezierTo(arc.Control, arc.P2);
                }
                context.DrawGeometry(null, pen, geom);
            }

            // Draw POP Stations
            foreach (var pop in _popNodes)
            {
                var fillBrush = new ImmutableSolidColorBrush(pop.Color);
                var pulsePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, pop.Color.R, pop.Color.G, pop.Color.B)), 1.8);

                context.DrawEllipse(fillBrush, null, pop.Location, 7.5, 7.5);
                context.DrawEllipse(null, pulsePen, pop.Location, _pulseRadius, _pulseRadius);

                var ft = new FormattedText(
                    pop.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoBoldTypeface,
                    16.0,
                    new ImmutableSolidColorBrush(Color.FromRgb(203, 213, 225)));

                double textX = pop.Location.X + 16;
                double textY = pop.Location.Y - 8;
                if (pop.Name == "IAD") textX = pop.Location.X - 180;

                context.DrawText(ft, new Point(textX, textY));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        const double mapW = 2000.0;
        const double mapH = 857.0;

        double scale = Math.Min(Bounds.Width / mapW, Bounds.Height / mapH);
        if (scale <= 0) return;

        double offsetX = (Bounds.Width - (mapW * scale)) / 2.0;
        double offsetY = (Bounds.Height - (mapH * scale)) / 2.0;

        double mapX = (pt.X - offsetX) / scale;
        double mapY = (pt.Y - offsetY) / scale;
        var mapPt = new Point(mapX, mapY);

        RegionData? found = null;
        foreach (var reg in _regions)
        {
            if (reg.Bounds.Contains(mapPt))
            {
                foreach (var geom in reg.Geometries)
                {
                    if (geom.FillContains(mapPt))
                    {
                        found = reg;
                        break;
                    }
                }
                if (found != null) break;
            }
        }

        if (found != _activeHoverRegion)
        {
            _activeHoverRegion = found;
            if (found != null)
            {
                HoveredRegionName = found.Name;
                HoveredTrafficRate = found.Traffic;
                HoveredRtt = found.Rtt;
                IsRegionHovered = true;
            }
            else
            {
                IsRegionHovered = false;
            }
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _activeHoverRegion = null;
        IsRegionHovered = false;
        InvalidateVisual();
    }
}

