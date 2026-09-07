using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MadTOM.Common.Utils;
using MadTOM.Models;

namespace MadTOM.Controls.CoreMatrix;

public sealed class DetailedCoreMatrixControl : Control
{
    public static readonly StyledProperty<bool> IsAggregatedModeProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, bool>(nameof(IsAggregatedMode), false);

    public static readonly StyledProperty<int> ThreadCountProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, int>(nameof(ThreadCount), 1024);

    public static readonly StyledProperty<float[]> CoreLoadsProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, float[]>(nameof(CoreLoads), Array.Empty<float>());

    public static readonly StyledProperty<IReadOnlyList<FleetNodeModel>> ClusterNodesProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, IReadOnlyList<FleetNodeModel>>(nameof(ClusterNodes), Array.Empty<FleetNodeModel>());

    public static readonly StyledProperty<string> TargetHostIdProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, string>(nameof(TargetHostId), "gander-epyc-01");

    public static readonly StyledProperty<int> HoveredThreadIndexProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, int>(nameof(HoveredThreadIndex), -1);

    public static readonly StyledProperty<string> HoveredHostIdProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, string>(nameof(HoveredHostId), string.Empty);

    public static readonly StyledProperty<float> HoveredLoadProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, float>(nameof(HoveredLoad), 0.0f);

    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<DetailedCoreMatrixControl, bool>(nameof(IsHovered), false);

    private static readonly IPen HoverPen = new ImmutablePen(Brushes.White, 2.0);
    private static readonly Typeface MonospaceTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly Dictionary<string, (Color Color, IPen Pen, IBrush Brush)> NodeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gander-epyc-01"] = (Color.FromRgb(6, 182, 212), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212)), 1), new ImmutableSolidColorBrush(Color.FromRgb(6, 182, 212))),
        ["gander-storage-02"] = (Color.FromRgb(168, 85, 247), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(168, 85, 247)), 1), new ImmutableSolidColorBrush(Color.FromRgb(168, 85, 247))),
        ["gosling-edge-01"] = (Color.FromRgb(16, 185, 129), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129)), 1), new ImmutableSolidColorBrush(Color.FromRgb(16, 185, 129))),
        ["gosling-cache-04"] = (Color.FromRgb(56, 189, 248), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248)), 1), new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248))),
        ["gander-transatlantic-01"] = (Color.FromRgb(245, 158, 11), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11)), 1), new ImmutableSolidColorBrush(Color.FromRgb(245, 158, 11))),
        ["gosling-ingress-gw"] = (Color.FromRgb(244, 63, 94), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(244, 63, 94)), 1), new ImmutableSolidColorBrush(Color.FromRgb(244, 63, 94)))
    };

    private CoreLayoutResult _singleLayout;
    private int _aggCols = 56;
    private double _aggCellW;
    private double _aggCellH;
    private double _aggGap = 1.0;

    public bool IsAggregatedMode
    {
        get => GetValue(IsAggregatedModeProperty);
        set => SetValue(IsAggregatedModeProperty, value);
    }

    public int ThreadCount
    {
        get => GetValue(ThreadCountProperty);
        set => SetValue(ThreadCountProperty, value);
    }

    public float[] CoreLoads
    {
        get => GetValue(CoreLoadsProperty);
        set => SetValue(CoreLoadsProperty, value);
    }

    public IReadOnlyList<FleetNodeModel> ClusterNodes
    {
        get => GetValue(ClusterNodesProperty);
        set => SetValue(ClusterNodesProperty, value);
    }

    public string TargetHostId
    {
        get => GetValue(TargetHostIdProperty);
        set => SetValue(TargetHostIdProperty, value);
    }

    public int HoveredThreadIndex
    {
        get => GetValue(HoveredThreadIndexProperty);
        set => SetValue(HoveredThreadIndexProperty, value);
    }

    public string HoveredHostId
    {
        get => GetValue(HoveredHostIdProperty);
        set => SetValue(HoveredHostIdProperty, value);
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

    static DetailedCoreMatrixControl()
    {
        AffectsRender<DetailedCoreMatrixControl>(
            IsAggregatedModeProperty,
            ThreadCountProperty,
            CoreLoadsProperty,
            ClusterNodesProperty,
            HoveredThreadIndexProperty);
    }

    public DetailedCoreMatrixControl()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 20 || h < 20) return;

        if (IsAggregatedMode)
        {
            RenderAggregatedMatrix(context, w, h);
        }
        else
        {
            RenderSingleHostMatrix(context, w, h);
        }
    }

    private void RenderSingleHostMatrix(DrawingContext context, double w, double h)
    {
        int count = ThreadCount <= 0 ? 1024 : ThreadCount;
        _singleLayout = CoreLayoutSolver.ComputeVerticalFillSquareLayout(w, h, count);
        var L = _singleLayout;

        double s = L.CellSize;
        double gap = L.Gap;
        int cols = L.Cols;
        double startX = L.StartX;
        double startY = L.StartY;

        var loads = CoreLoads;
        bool hasLoads = loads != null && loads.Length >= count;
        int hovered = HoveredThreadIndex;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            double x = startX + col * (s + gap);
            double y = startY + row * (s + gap);

            double load = hasLoads ? loads![i] : 0.45;
            var brush = ColorInterpolator.InterpolateLoadBrush(load);
            var rect = new Rect(x, y, s, s);

            context.FillRectangle(brush, rect);

            if (i == hovered)
            {
                context.DrawRectangle(null, HoverPen, rect);
            }
        }
    }

    private void RenderAggregatedMatrix(DrawingContext context, double w, double h)
    {
        var nodes = ClusterNodes;
        if (nodes == null || nodes.Count == 0) return;

        int totalCores = 0;
        foreach (var node in nodes) totalCores += node.Cores;
        if (totalCores <= 0) totalCores = 1904;

        _aggCols = 56;
        int rows = (int)Math.Ceiling((double)totalCores / _aggCols);
        _aggGap = 1.0;
        _aggCellW = (w - (_aggCols - 1) * _aggGap) / _aggCols;
        _aggCellH = (h - (rows - 1) * _aggGap) / rows;

        int coreCounter = 0;
        int hovered = HoveredThreadIndex;

        foreach (var node in nodes)
        {
            int startIdx = coreCounter;
            int endIdx = startIdx + node.Cores - 1;

            NodeColors.TryGetValue(node.Id, out var nodeTheme);
            var pen = nodeTheme.Pen ?? new ImmutablePen(Brushes.Cyan, 1);
            var fgBrush = nodeTheme.Brush ?? Brushes.Cyan;

            for (int c = 0; c < node.Cores; c++)
            {
                int globalIdx = startIdx + c;
                int col = globalIdx % _aggCols;
                int row = globalIdx / _aggCols;
                double x = col * (_aggCellW + _aggGap);
                double y = row * (_aggCellH + _aggGap);

                double load = (node.CoreLoads != null && c < node.CoreLoads.Length) ? node.CoreLoads[c] : 0.4;
                var brush = ColorInterpolator.InterpolateLoadBrush(load);
                var rect = new Rect(x, y, _aggCellW, _aggCellH);

                context.FillRectangle(brush, rect);

                if (globalIdx == hovered)
                {
                    context.DrawRectangle(null, HoverPen, rect);
                }
            }

            // Machine boundary outline
            int startCol = startIdx % _aggCols;
            int startRow = startIdx / _aggCols;
            int endCol = endIdx % _aggCols;
            int endRow = endIdx / _aggCols;

            double boxX = (startRow == endRow ? startCol : 0) * (_aggCellW + _aggGap);
            double boxY = startRow * (_aggCellH + _aggGap);
            double boxW = (startRow == endRow ? (endCol - startCol + 1) : _aggCols) * (_aggCellW + _aggGap);
            double boxH = (endRow - startRow + 1) * (_aggCellH + _aggGap);

            context.DrawRectangle(null, pen, new Rect(boxX, boxY, boxW, boxH));

            // Machine label tag
            var text = new FormattedText(
                $"{node.Id} ({node.Cores}T)",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonospaceTypeface,
                9.0,
                fgBrush);

            context.DrawText(text, new Point(boxX + 3, boxY + 2));

            coreCounter += node.Cores;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        if (IsAggregatedMode)
        {
            var nodes = ClusterNodes;
            if (nodes == null || nodes.Count == 0) return;

            int totalCores = 0;
            foreach (var n in nodes) totalCores += n.Cores;

            double stepX = _aggCellW + _aggGap;
            double stepY = _aggCellH + _aggGap;
            int col = (int)(pt.X / stepX);
            int row = (int)(pt.Y / stepY);
            int idx = (row * _aggCols) + col;

            if (idx >= 0 && idx < totalCores)
            {
                HoveredThreadIndex = idx;
                IsHovered = true;

                // Find which host owns this index
                int sum = 0;
                foreach (var node in nodes)
                {
                    if (idx >= sum && idx < sum + node.Cores)
                    {
                        HoveredHostId = node.Id;
                        int localIdx = idx - sum;
                        HoveredLoad = (node.CoreLoads != null && localIdx < node.CoreLoads.Length) ? node.CoreLoads[localIdx] : 0.0f;
                        break;
                    }
                    sum += node.Cores;
                }
                return;
            }
        }
        else
        {
            var L = _singleLayout;
            double relX = pt.X - L.StartX;
            double relY = pt.Y - L.StartY;

            if (relX >= 0 && relY >= 0 && L.Cols > 0)
            {
                double step = L.CellSize + L.Gap;
                int col = (int)(relX / step);
                int row = (int)(relY / step);

                if (col >= 0 && col < L.Cols && row >= 0 && row < L.Rows)
                {
                    int idx = (row * L.Cols) + col;
                    if (idx >= 0 && idx < ThreadCount)
                    {
                        HoveredThreadIndex = idx;
                        IsHovered = true;
                        HoveredHostId = TargetHostId;
                        HoveredLoad = (CoreLoads != null && idx < CoreLoads.Length) ? CoreLoads[idx] : 0.0f;
                        return;
                    }
                }
            }
        }

        if (HoveredThreadIndex != -1)
        {
            HoveredThreadIndex = -1;
            IsHovered = false;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredThreadIndex = -1;
        IsHovered = false;
    }
}

