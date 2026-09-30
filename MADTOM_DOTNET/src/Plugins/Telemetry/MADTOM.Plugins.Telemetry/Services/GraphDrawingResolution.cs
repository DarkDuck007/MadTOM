using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace MadTOM.Services;

public static class GraphDrawingResolution
{
    public static int PointBudget(double width, double density) =>
        (int)Math.Clamp(Math.Floor(Math.Max(0, width) * (GraphPerformanceSettings.IsValid(density) ? density : 1)), 2, 100000);

    // Input is in screen coordinates after zoom/pan. Keep only the visible window
    // and its nearest neighbours, so off-screen history does not consume the budget.
    public static List<Point> Reduce(IReadOnlyList<Point> source, double left, double width, double density, IReadOnlyList<long>? timestamps = null, long windowStart = 0, long windowEnd = 0)
    {
        int budget = PointBudget(width, density);
        int first = 0;
        while (first < source.Count && source[first].X < left) first++;
        int last = first;
        while (last < source.Count && source[last].X <= left + width) last++;
        int start = Math.Max(0, first - 1), end = Math.Min(source.Count, last + 1);
        if (end - start <= budget)
        {
            var list = new List<Point>(end - start);
            for (int i = start; i < end; i++) list.Add(source[i]);
            return list;
        }
        if (budget < 6) return new List<Point> { source[start], source[end - 1] };
        if (timestamps?.Count == source.Count && windowEnd > windowStart)
        {
            var indices = GraphHistoryResolution.DownsampleIndices(i => timestamps[i], i => source[i].Y, source.Count, budget, windowStart, windowEnd, first, last);
            var result = new List<Point>(indices.Count);
            for (int i = 0; i < indices.Count; i++) result.Add(source[indices[i]]);
            return result;
        }

        var visible = new List<LODPoint>(last - first);
        for (int i = first; i < last; i++)
            visible.Add(new LODPoint((long)((source[i].X - left) * 1_000_000), source[i].Y, 0, 0));
        var sampled = GraphHistoryResolution.Downsample(visible, budget - 2, 0, (long)(width * 1_000_000));
        var fallbackResult = new List<Point>(budget);
        if (first > 0) fallbackResult.Add(source[first - 1]);
        fallbackResult.AddRange(sampled.Select(p => new Point(left + p.TimestampUnixNano / 1_000_000.0, p.Value)));
        if (last < source.Count) fallbackResult.Add(source[last]);
        return fallbackResult;
    }
}
