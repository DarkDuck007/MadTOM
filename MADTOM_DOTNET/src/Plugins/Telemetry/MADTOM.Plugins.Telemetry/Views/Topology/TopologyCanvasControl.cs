using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MadTOM.ViewModels.Topology;

namespace MadTOM.Views.Topology;

public class TopologyCanvasControl : Control
{
    public static readonly StyledProperty<NetworkTopologyViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<TopologyCanvasControl, NetworkTopologyViewModel?>(nameof(ViewModel));

    public NetworkTopologyViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private Point _panOffset = new(40, 40);
    private double _zoom = 1.0;
    private bool _isPanning;
    private Point _lastPanPoint;

    private TopologyNodeItemViewModel? _draggedNode;
    private Point _dragStartNodePos;
    private Point _dragStartPointerPos;

    private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1);
    private readonly Pen _edgePen = new(new SolidColorBrush(Color.FromArgb(160, 59, 130, 246)), 2);
    private readonly Pen _defaultEdgePen = new(new SolidColorBrush(Color.FromArgb(240, 16, 185, 129)), 3);

    public TopologyCanvasControl()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is NetworkTopologyViewModel oldVm)
            {
                oldVm.CanvasNodes.CollectionChanged -= OnNodesChanged;
                oldVm.CanvasEdges.CollectionChanged -= OnEdgesChanged;
                oldVm.CenterGraphRequested -= OnCenterGraphRequested;
                oldVm.CanvasInvalidationRequested -= OnCanvasInvalidationRequested;
            }
            if (change.NewValue is NetworkTopologyViewModel newVm)
            {
                newVm.CanvasNodes.CollectionChanged += OnNodesChanged;
                newVm.CanvasEdges.CollectionChanged += OnEdgesChanged;
                newVm.CenterGraphRequested += OnCenterGraphRequested;
                newVm.CanvasInvalidationRequested += OnCanvasInvalidationRequested;
            }
            InvalidateVisual();
        }
    }

    private void OnCanvasInvalidationRequested()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private void OnCenterGraphRequested()
    {
        CenterGraph();
    }

    public void CenterGraph()
    {
        var vm = ViewModel;
        if (vm == null || vm.CanvasNodes.Count == 0) return;

        double minX = vm.CanvasNodes.Min(n => n.X);
        double maxX = vm.CanvasNodes.Max(n => n.X + 220);
        double minY = vm.CanvasNodes.Min(n => n.Y);
        double maxY = vm.CanvasNodes.Max(n => n.Y + 80);

        double graphWidth = maxX - minX;
        double graphHeight = maxY - minY;

        double viewportWidth = Bounds.Width > 0 ? Bounds.Width : 800;
        double viewportHeight = Bounds.Height > 0 ? Bounds.Height : 600;

        // Auto calculate comfortable zoom
        double scaleX = (viewportWidth - 120) / Math.Max(graphWidth, 100);
        double scaleY = (viewportHeight - 120) / Math.Max(graphHeight, 100);
        _zoom = Math.Clamp(Math.Min(scaleX, scaleY), 0.5, 1.2);

        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;

        _panOffset = new Point(
            viewportWidth / 2.0 - centerX * _zoom,
            viewportHeight / 2.0 - centerY * _zoom
        );

        InvalidateVisual();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;

        // Background
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(15, 17, 23)), bounds);

        // Grid lines with zoom & pan
        DrawBackgroundGrid(context, bounds);

        var vm = ViewModel;
        if (vm == null) return;

        var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y);
        using (context.PushTransform(transform))
        {
            // Draw Edges (Routing next-hop links)
            foreach (var edge in vm.CanvasEdges)
            {
                var src = vm.CanvasNodes.FirstOrDefault(n => n.Id == edge.SourceNodeId);
                var tgt = vm.CanvasNodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                if (src == null || tgt == null) continue;

                var start = new Point(src.X + 110, src.Y + 40);
                var end = new Point(tgt.X + 110, tgt.Y + 40);

                var pen = edge.IsDefaultRoute ? _defaultEdgePen : _edgePen;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(start, false);
                    var midX = (start.X + end.X) / 2;
                    ctx.CubicBezierTo(new Point(midX, start.Y), new Point(midX, end.Y), end);
                }
                context.DrawGeometry(null, pen, geo);

                // Edge label
                if (!string.IsNullOrEmpty(edge.Label))
                {
                    var text = new FormattedText(
                        $"{edge.Label} (m:{edge.Metric})",
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Inter, Segoe UI, sans-serif"),
                        10,
                        new SolidColorBrush(Color.FromRgb(156, 163, 175))
                    );
                    context.DrawText(text, new Point((start.X + end.X) / 2 - 20, (start.Y + end.Y) / 2 - 12));
                }
            }

            // Draw Node Cards (Interfaces and Gateways)
            foreach (var node in vm.CanvasNodes)
            {
                DrawNodeCard(context, node);
            }
        }
    }

    private void DrawBackgroundGrid(DrawingContext context, Rect bounds)
    {
        double spacing = 40 * _zoom;
        if (spacing < 15) return;

        double offsetX = _panOffset.X % spacing;
        double offsetY = _panOffset.Y % spacing;

        for (double x = offsetX; x < bounds.Width; x += spacing)
        {
            context.DrawLine(_gridPen, new Point(x, 0), new Point(x, bounds.Height));
        }
        for (double y = offsetY; y < bounds.Height; y += spacing)
        {
            context.DrawLine(_gridPen, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private TopologyNodeItemViewModel? _hoveredNode;

    private void DrawNodeCard(DrawingContext context, TopologyNodeItemViewModel node)
    {
        const double width = 220;
        const double textMaxWidth = 182; // width - 28px left margin - 10px right margin

        // Measure Title with wrapping
        var titleText = new FormattedText(
            node.Title,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold),
            12,
            Brushes.White
        )
        {
            MaxTextWidth = textMaxWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };

        // Measure Subtitle with wrapping
        var subText = new FormattedText(
            node.Subtitle,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif"),
            10,
            new SolidColorBrush(Color.FromRgb(156, 163, 175))
        )
        {
            MaxTextWidth = textMaxWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };

        // Live Rate text
        string rateDisplay = $"↓ {node.FormattedRxRate}  ↑ {node.FormattedTxRate}";
        var rateText = new FormattedText(
            rateDisplay,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas, monospace"),
            10,
            new SolidColorBrush(Color.FromRgb(96, 165, 250))
        )
        {
            MaxTextWidth = textMaxWidth
        };

        double contentHeight = 10 + titleText.Height + 4 + subText.Height + 6 + rateText.Height + 10;
        double cardHeight = Math.Max(80, contentHeight);
        node.Height = cardHeight;

        var rect = new Rect(node.X, node.Y, width, cardHeight);

        var bgBrush = node.IsSelected
            ? new SolidColorBrush(Color.FromRgb(30, 41, 59))
            : (node == _hoveredNode ? new SolidColorBrush(Color.FromRgb(28, 33, 40)) : new SolidColorBrush(Color.FromRgb(22, 27, 34)));

        var borderBrush = node.IsSelected
            ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
            : (node == _hoveredNode ? new SolidColorBrush(Color.FromRgb(88, 166, 255)) : new SolidColorBrush(Color.FromRgb(48, 54, 61)));

        context.DrawRectangle(bgBrush, new Pen(borderBrush, node.IsSelected ? 2 : 1), rect, 6, 6);

        // Status Indicator pill
        var statusColor = Color.Parse(node.StatusColor);
        context.DrawEllipse(new SolidColorBrush(statusColor), null, new Point(node.X + 14, node.Y + 18), 4, 4);

        // Icon Glyph (Emoji / Symbol)
        var iconText = new FormattedText(
            node.IconGlyph,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Emoji, Noto Color Emoji, Apple Color Emoji, sans-serif"),
            13,
            Brushes.White
        );
        context.DrawText(iconText, new Point(node.X + 24, node.Y + 11));

        // Type Badge (ETHERNET, LOOPBACK, BRIDGE, GATEWAY, etc.) in top right
        var typeBadgeColor = Color.Parse(node.IconBadgeColor);
        var typeText = new FormattedText(
            node.IconTypeTag,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Bold),
            8,
            new SolidColorBrush(typeBadgeColor)
        );
        double typeBadgeWidth = typeText.Width + 8;
        var badgeRect = new Rect(node.X + width - typeBadgeWidth - 8, node.Y + 10, typeBadgeWidth, 14);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(40, typeBadgeColor.R, typeBadgeColor.G, typeBadgeColor.B)),
            new Pen(new SolidColorBrush(Color.FromArgb(90, typeBadgeColor.R, typeBadgeColor.G, typeBadgeColor.B)), 1),
            badgeRect, 3, 3
        );
        context.DrawText(typeText, new Point(badgeRect.X + 4, badgeRect.Y + 1));

        // Draw text blocks
        double currentY = node.Y + 11;
        context.DrawText(titleText, new Point(node.X + 44, currentY));
        currentY += titleText.Height + 5;

        context.DrawText(subText, new Point(node.X + 24, currentY));
        currentY += subText.Height + 6;

        context.DrawText(rateText, new Point(node.X + 14, currentY));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y > 0 ? 1.1 : 0.9;
        double newZoom = Math.Clamp(_zoom * delta, 0.4, 3.0);

        var pos = e.GetPosition(this);
        _panOffset = new Point(
            pos.X - (pos.X - _panOffset.X) * (newZoom / _zoom),
            pos.Y - (pos.Y - _panOffset.Y) * (newZoom / _zoom)
        );
        _zoom = newZoom;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var worldPos = ScreenToWorld(pos);

        var vm = ViewModel;
        if (vm != null)
        {
            // Check if clicking a node card using exact measured node bounds
            var hit = vm.CanvasNodes.LastOrDefault(n =>
                worldPos.X >= n.X && worldPos.X <= n.X + 220 &&
                worldPos.Y >= n.Y && worldPos.Y <= n.Y + (n.Height > 0 ? n.Height : 80));

            if (hit != null)
            {
                _draggedNode = hit;
                _dragStartNodePos = new Point(hit.X, hit.Y);
                _dragStartPointerPos = worldPos;
                vm.SelectCanvasNode(hit);
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = pos;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_draggedNode != null)
        {
            var worldPos = ScreenToWorld(pos);
            double deltaX = worldPos.X - _dragStartPointerPos.X;
            double deltaY = worldPos.Y - _dragStartPointerPos.Y;
            _draggedNode.X = _dragStartNodePos.X + deltaX;
            _draggedNode.Y = _dragStartNodePos.Y + deltaY;
            InvalidateVisual();
            return;
        }

        if (_isPanning)
        {
            double dx = pos.X - _lastPanPoint.X;
            double dy = pos.Y - _lastPanPoint.Y;
            _panOffset = new Point(_panOffset.X + dx, _panOffset.Y + dy);
            _lastPanPoint = pos;
            InvalidateVisual();
            return;
        }

        // Hover tracking & Tooltip using exact measured node bounds
        var vm = ViewModel;
        if (vm != null)
        {
            var worldPos = ScreenToWorld(pos);
            var hit = vm.CanvasNodes.LastOrDefault(n =>
                worldPos.X >= n.X && worldPos.X <= n.X + 220 &&
                worldPos.Y >= n.Y && worldPos.Y <= n.Y + (n.Height > 0 ? n.Height : 80));

            if (hit != _hoveredNode)
            {
                _hoveredNode = hit;
                if (hit != null)
                {
                    ToolTip.SetTip(this, hit.FullDetailsText);
                }
                else
                {
                    ToolTip.SetTip(this, null);
                }
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_draggedNode != null)
        {
            _draggedNode = null;
            ViewModel?.SaveCurrentLayout();
        }
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    private Point ScreenToWorld(Point screen)
    {
        var transform = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y);
        if (transform.TryInvert(out var inv))
        {
            return screen.Transform(inv);
        }
        return new Point(
            (screen.X - _panOffset.X) / _zoom,
            (screen.Y - _panOffset.Y) / _zoom
        );
    }
}
