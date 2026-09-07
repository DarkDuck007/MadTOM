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

namespace MadTOM.Controls.Radar;

public sealed class WorldRadarMapControl : Control
{
    private sealed class CountryData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Paths { get; set; } = new();

        public List<Geometry> Geometries { get; } = new();
        public Rect Bounds { get; set; }
        public Point Center { get; set; }
    }

    private sealed class PopNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Point Location { get; set; }
        public Color Color { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    private sealed class TransitVector
    {
        public Point Origin { get; set; }
        public Point Destination { get; set; }
        public Point ControlPoint { get; set; }
        public Color Color { get; set; }
        public double Thickness { get; set; } = 2.0;
    }

    public static readonly StyledProperty<string> ScopedNodeIdProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(ScopedNodeId), "Aggregated (All Hosts - Global Fleet)");

    public static readonly StyledProperty<string> HoveredCountryNameProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredCountryName), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> HoveredRegionNameProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredRegionName), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> HoveredTrafficRateProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredTrafficRate), string.Empty);

    public static readonly StyledProperty<string> HoveredRttProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, string>(nameof(HoveredRtt), string.Empty);

    public static readonly StyledProperty<bool> IsCountryHoveredProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, bool>(nameof(IsCountryHovered), false);

    public static readonly StyledProperty<bool> IsRegionHoveredProperty =
        AvaloniaProperty.Register<WorldRadarMapControl, bool>(nameof(IsRegionHovered), false);

    private static readonly IBrush OceanBrush = new ImmutableSolidColorBrush(Color.FromRgb(5, 8, 17));
    private static readonly IBrush DefaultCountryBrush = new ImmutableSolidColorBrush(Color.FromRgb(25, 36, 52));
    private static readonly IPen CountryBorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(15, 23, 42)), 0.6);
    private static readonly IBrush HoveredCountryBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IPen HoveredCountryPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(186, 230, 253)), 1.5);
    private static readonly Typeface MonoBoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private readonly List<CountryData> _countries = new();
    private readonly List<PopNode> _popNodes = new();
    private CountryData? _activeHoverCountry;

    public string ScopedNodeId
    {
        get => GetValue(ScopedNodeIdProperty);
        set => SetValue(ScopedNodeIdProperty, value);
    }

    public string HoveredCountryName
    {
        get => GetValue(HoveredCountryNameProperty);
        set
        {
            SetValue(HoveredCountryNameProperty, value);
            SetValue(HoveredRegionNameProperty, value);
        }
    }

    public string HoveredRegionName
    {
        get => GetValue(HoveredRegionNameProperty);
        set
        {
            SetValue(HoveredRegionNameProperty, value);
            SetValue(HoveredCountryNameProperty, value);
        }
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

    public bool IsCountryHovered
    {
        get => GetValue(IsCountryHoveredProperty);
        set
        {
            SetValue(IsCountryHoveredProperty, value);
            SetValue(IsRegionHoveredProperty, value);
        }
    }

    public bool IsRegionHovered
    {
        get => GetValue(IsRegionHoveredProperty);
        set
        {
            SetValue(IsRegionHoveredProperty, value);
            SetValue(IsCountryHoveredProperty, value);
        }
    }

    static WorldRadarMapControl()
    {
        AffectsRender<WorldRadarMapControl>(ScopedNodeIdProperty, IsCountryHoveredProperty, IsRegionHoveredProperty);
    }

    public WorldRadarMapControl()
    {
        ClipToBounds = true;
        LoadMapGeometries();
        InitializePopNodes();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScopedNodeIdProperty)
        {
            InvalidateVisual();
        }
    }

    private void LoadMapGeometries()
    {
        try
        {
            Uri uri = new("avares://MadTOM/Assets/Maps/world-countries.json");
            if (!AssetLoader.Exists(uri))
            {
                uri = new Uri("avares://MadTOM/Assets/Maps/world-regions.json");
            }

            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<List<CountryData>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    foreach (var c in parsed)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

                        foreach (var pathStr in c.Paths)
                        {
                            try
                            {
                                var geom = Geometry.Parse(pathStr);
                                c.Geometries.Add(geom);
                                var b = geom.Bounds;
                                if (b.Left < minX) minX = b.Left;
                                if (b.Top < minY) minY = b.Top;
                                if (b.Right > maxX) maxX = b.Right;
                                if (b.Bottom > maxY) maxY = b.Bottom;
                            }
                            catch { }
                        }

                        if (minX != double.MaxValue)
                        {
                            c.Bounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                            c.Center = new Point(minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);
                            _countries.Add(c);
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void InitializePopNodes()
    {
        _popNodes.Add(new PopNode { Id = "gander-epyc-01", Name = "TYO", Location = new Point(1715, 270), Color = Color.FromRgb(6, 182, 212), Label = "TYO (Tokyo) - 1.8ms" });
        _popNodes.Add(new PopNode { Id = "iad-edge-01", Name = "IAD", Location = new Point(590, 255), Color = Color.FromRgb(249, 115, 22), Label = "IAD (US East) - 78ms" });
        _popNodes.Add(new PopNode { Id = "fra-cache-04", Name = "FRA", Location = new Point(1045, 200), Color = Color.FromRgb(245, 158, 11), Label = "FRA (Frankfurt) - 142ms" });
        _popNodes.Add(new PopNode { Id = "sin-gateway-01", Name = "SIN", Location = new Point(1572, 495), Color = Color.FromRgb(16, 185, 129), Label = "SIN (Singapore) - 62ms" });
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
            string scoped = ScopedNodeId ?? string.Empty;
            string scopedLower = scoped.ToLowerInvariant();
            bool isAggregated = scopedLower.Contains("aggregated") || string.IsNullOrWhiteSpace(scoped);

            // 1. Draw Country Polygons
            foreach (var country in _countries)
            {
                bool isHovered = country == _activeHoverCountry;
                IBrush fillBrush;
                IPen borderPen;

                if (isHovered)
                {
                    fillBrush = HoveredCountryBrush;
                    borderPen = HoveredCountryPen;
                }
                else
                {
                    fillBrush = GetCountryChoroplethBrush(country.Name, country.Id, scopedLower);
                    borderPen = CountryBorderPen;
                }

                foreach (var geom in country.Geometries)
                {
                    context.DrawGeometry(fillBrush, borderPen, geom);
                }
            }

            // 2. Draw Static Transit Vectors
            var vectors = GetTransitVectors(scopedLower);
            foreach (var vec in vectors)
            {
                var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(160, vec.Color.R, vec.Color.G, vec.Color.B)), vec.Thickness);
                var geom = new StreamGeometry();
                using (var c = geom.Open())
                {
                    c.BeginFigure(vec.Origin, false);
                    c.QuadraticBezierTo(vec.ControlPoint, vec.Destination);
                }
                context.DrawGeometry(null, pen, geom);
            }

            // 3. Draw POP Stations
            foreach (var pop in _popNodes)
            {
                bool isTargetNode = !isAggregated && (scopedLower.Contains(pop.Id) || scopedLower.Contains(pop.Name.ToLowerInvariant()));
                var fillBrush = new ImmutableSolidColorBrush(pop.Color);

                if (isTargetNode)
                {
                    // Emphasized target node: concentric rings & brighter core
                    var outerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(120, pop.Color.R, pop.Color.G, pop.Color.B)), 3.0);
                    var innerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 2.0);
                    context.DrawEllipse(null, outerPen, pop.Location, 18, 18);
                    context.DrawEllipse(null, innerPen, pop.Location, 12, 12);
                    context.DrawEllipse(fillBrush, null, pop.Location, 7.5, 7.5);
                }
                else
                {
                    var ringPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(80, pop.Color.R, pop.Color.G, pop.Color.B)), 1.5);
                    context.DrawEllipse(null, ringPen, pop.Location, 12, 12);
                    context.DrawEllipse(fillBrush, null, pop.Location, 5.5, 5.5);
                }

                var labelBrush = isTargetNode
                    ? new ImmutableSolidColorBrush(Color.FromRgb(255, 255, 255))
                    : new ImmutableSolidColorBrush(Color.FromRgb(148, 163, 184));

                var ft = new FormattedText(
                    pop.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoBoldTypeface,
                    isTargetNode ? 14.0 : 12.0,
                    labelBrush);

                double textX = pop.Location.X + 14;
                double textY = pop.Location.Y - 7;
                if (pop.Name == "IAD") textX = pop.Location.X - 165;

                context.DrawText(ft, new Point(textX, textY));
            }
        }
    }

    private static IBrush GetCountryChoroplethBrush(string name, string id, string scopedLower)
    {
        string n = name.ToLowerInvariant();
        string c = id.ToUpperInvariant();

        if (scopedLower.Contains("gander"))
        {
            if (n.Contains("japan") || c == "JP") return new ImmutableSolidColorBrush(Color.FromRgb(234, 88, 12));
            if (n.Contains("united states") || c == "US") return new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129));
            if (n.Contains("singapore") || c == "SG") return new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212));
            if (n.Contains("germany") || c == "DE") return new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        else if (scopedLower.Contains("iad") || scopedLower.Contains("edge"))
        {
            if (n.Contains("united states") || c == "US") return new ImmutableSolidColorBrush(Color.FromRgb(234, 88, 12));
            if (n.Contains("germany") || c == "DE") return new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
            if (n.Contains("united kingdom") || c == "GB") return new ImmutableSolidColorBrush(Color.FromRgb(99, 102, 241));
            if (n.Contains("japan") || c == "JP") return new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212));
        }
        else if (scopedLower.Contains("fra") || scopedLower.Contains("cache"))
        {
            if (n.Contains("germany") || c == "DE") return new ImmutableSolidColorBrush(Color.FromRgb(234, 88, 12));
            if (n.Contains("france") || c == "FR") return new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212));
            if (n.Contains("united kingdom") || c == "GB") return new ImmutableSolidColorBrush(Color.FromRgb(99, 102, 241));
            if (n.Contains("united states") || c == "US") return new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else if (scopedLower.Contains("sin") || scopedLower.Contains("gateway"))
        {
            if (n.Contains("singapore") || c == "SG") return new ImmutableSolidColorBrush(Color.FromRgb(234, 88, 12));
            if (n.Contains("japan") || c == "JP") return new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212));
            if (n.Contains("australia") || c == "AU") return new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129));
            if (n.Contains("united states") || c == "US") return new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
        }
        else
        {
            // Global Aggregated
            if (n.Contains("japan") || c == "JP") return new ImmutableSolidColorBrush(Color.FromRgb(234, 88, 12));
            if (n.Contains("united states") || c == "US") return new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129));
            if (n.Contains("germany") || c == "DE") return new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11));
            if (n.Contains("singapore") || c == "SG") return new ImmutableSolidColorBrush(Color.FromRgb(99, 102, 241));
            if (n.Contains("united kingdom") || c == "GB") return new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));
            if (n.Contains("france") || c == "FR") return new ImmutableSolidColorBrush(Color.FromRgb(14, 165, 233));
            if (n.Contains("australia") || c == "AU") return new ImmutableSolidColorBrush(Color.FromRgb(168, 85, 247));
            if (n.Contains("canada") || c == "CA") return new ImmutableSolidColorBrush(Color.FromRgb(34, 197, 94));
        }

        return DefaultCountryBrush;
    }

    private static List<TransitVector> GetTransitVectors(string scopedLower)
    {
        var list = new List<TransitVector>();

        Point tyo = new(1715, 270);
        Point iad = new(590, 255);
        Point fra = new(1045, 200);
        Point sin = new(1572, 495);

        if (scopedLower.Contains("gander"))
        {
            // Vectors arriving at TYO
            list.Add(new TransitVector { Origin = iad, ControlPoint = new Point(1150, 80), Destination = tyo, Color = Color.FromRgb(249, 115, 22), Thickness = 2.5 });
            list.Add(new TransitVector { Origin = sin, ControlPoint = new Point(1660, 380), Destination = tyo, Color = Color.FromRgb(6, 182, 212), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = fra, ControlPoint = new Point(1380, 120), Destination = tyo, Color = Color.FromRgb(245, 158, 11), Thickness = 1.8 });
        }
        else if (scopedLower.Contains("iad") || scopedLower.Contains("edge"))
        {
            // Vectors arriving at IAD
            list.Add(new TransitVector { Origin = fra, ControlPoint = new Point(815, 140), Destination = iad, Color = Color.FromRgb(245, 158, 11), Thickness = 2.5 });
            list.Add(new TransitVector { Origin = new Point(1005, 160), ControlPoint = new Point(800, 180), Destination = iad, Color = Color.FromRgb(99, 102, 241), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = tyo, ControlPoint = new Point(1150, 80), Destination = iad, Color = Color.FromRgb(6, 182, 212), Thickness = 1.8 });
        }
        else if (scopedLower.Contains("fra") || scopedLower.Contains("cache"))
        {
            // Vectors arriving at FRA
            list.Add(new TransitVector { Origin = iad, ControlPoint = new Point(815, 140), Destination = fra, Color = Color.FromRgb(249, 115, 22), Thickness = 2.5 });
            list.Add(new TransitVector { Origin = new Point(995, 210), ControlPoint = new Point(1020, 205), Destination = fra, Color = Color.FromRgb(6, 182, 212), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = new Point(1005, 160), ControlPoint = new Point(1025, 180), Destination = fra, Color = Color.FromRgb(99, 102, 241), Thickness = 2.0 });
        }
        else if (scopedLower.Contains("sin") || scopedLower.Contains("gateway"))
        {
            // Vectors arriving at SIN
            list.Add(new TransitVector { Origin = tyo, ControlPoint = new Point(1660, 380), Destination = sin, Color = Color.FromRgb(6, 182, 212), Thickness = 2.5 });
            list.Add(new TransitVector { Origin = new Point(1740, 700), ControlPoint = new Point(1680, 600), Destination = sin, Color = Color.FromRgb(16, 185, 129), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = iad, ControlPoint = new Point(1100, 380), Destination = sin, Color = Color.FromRgb(245, 158, 11), Thickness = 1.8 });
        }
        else
        {
            // Global Aggregate Transit Grid
            list.Add(new TransitVector { Origin = iad, ControlPoint = new Point(1150, 70), Destination = tyo, Color = Color.FromRgb(249, 115, 22), Thickness = 2.5 });
            list.Add(new TransitVector { Origin = fra, ControlPoint = new Point(1380, 120), Destination = tyo, Color = Color.FromRgb(245, 158, 11), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = iad, ControlPoint = new Point(815, 140), Destination = fra, Color = Color.FromRgb(245, 158, 11), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = sin, ControlPoint = new Point(1660, 380), Destination = tyo, Color = Color.FromRgb(16, 185, 129), Thickness = 2.0 });
            list.Add(new TransitVector { Origin = fra, ControlPoint = new Point(1300, 350), Destination = sin, Color = Color.FromRgb(99, 102, 241), Thickness = 1.8 });
        }

        return list;
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

        CountryData? found = null;
        foreach (var country in _countries)
        {
            if (country.Bounds.Contains(mapPt))
            {
                foreach (var geom in country.Geometries)
                {
                    if (geom.FillContains(mapPt))
                    {
                        found = country;
                        break;
                    }
                }
                if (found != null) break;
            }
        }

        if (found != _activeHoverCountry)
        {
            _activeHoverCountry = found;
            if (found != null)
            {
                HoveredCountryName = found.Name;
                HoveredRegionName = found.Name;
                var (traffic, rtt) = GetCountryTelemetry(found.Name, found.Id, ScopedNodeId);
                HoveredTrafficRate = traffic;
                HoveredRtt = rtt;
                IsCountryHovered = true;
                IsRegionHovered = true;
            }
            else
            {
                HoveredCountryName = string.Empty;
                HoveredRegionName = string.Empty;
                IsCountryHovered = false;
                IsRegionHovered = false;
            }
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _activeHoverCountry = null;
        HoveredCountryName = string.Empty;
        HoveredRegionName = string.Empty;
        IsCountryHovered = false;
        IsRegionHovered = false;
        InvalidateVisual();
    }

    private static (string traffic, string rtt) GetCountryTelemetry(string name, string id, string scoped)
    {
        string n = name.ToLowerInvariant();
        string c = id.ToUpperInvariant();
        string s = (scoped ?? string.Empty).ToLowerInvariant();

        if (n.Contains("japan") || c == "JP")
        {
            if (s.Contains("gander")) return ("28.3 Gbps", "1.2ms");
            return ("44.8 Gbps", "1.8ms");
        }
        if (n.Contains("united states") || c == "US")
        {
            if (s.Contains("iad")) return ("41.4 Gbps", "11ms");
            return ("23.9 Gbps", "78ms");
        }
        if (n.Contains("germany") || c == "DE")
        {
            if (s.Contains("fra")) return ("18.8 Gbps", "3.5ms");
            return ("12.1 Gbps", "142ms");
        }
        if (n.Contains("singapore") || c == "SG")
        {
            if (s.Contains("sin")) return ("16.2 Gbps", "1.5ms");
            return ("7.4 Gbps", "62ms");
        }
        if (n.Contains("united kingdom") || c == "GB") return ("9.5 Gbps", "72ms");
        if (n.Contains("france") || c == "FR") return ("7.1 Gbps", "88ms");
        if (n.Contains("australia") || c == "AU") return ("5.4 Gbps", "98ms");
        if (n.Contains("canada") || c == "CA") return ("4.8 Gbps", "82ms");
        if (n.Contains("brazil") || c == "BR") return ("3.2 Gbps", "165ms");
        if (n.Contains("china") || c == "CN") return ("8.4 Gbps", "52ms");
        if (n.Contains("india") || c == "IN") return ("6.1 Gbps", "85ms");

        int hash = Math.Abs(name.GetHashCode());
        double rate = 0.5 + (hash % 40) / 10.0;
        int latency = 30 + (hash % 150);
        return ($"{rate:F1} Gbps", $"{latency}ms");
    }
}

