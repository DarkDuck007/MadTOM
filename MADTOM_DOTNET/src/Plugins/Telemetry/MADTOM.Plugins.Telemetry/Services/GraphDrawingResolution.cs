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
        if (end - start <= budget) return source.Skip(start).Take(end - start).ToList();
        if (budget < 6) return new List<Point> { source[start], source[end - 1] };
        if (timestamps?.Count == source.Count && windowEnd > windowStart)
        {
            var timed = new List<LODPoint>(end - start);
            var originals = new Dictionary<long, Point>(end - start);
            for (int i = start; i < end; i++)
            {
                timed.Add(new LODPoint(timestamps[i], source[i].Y, 0, 0));
                originals[timestamps[i]] = source[i];
            }
            return GraphHistoryResolution.Downsample(timed, budget, windowStart, windowEnd)
                .Select(p => originals[p.TimestampUnixNano]).ToList();
        }

        var visible = new List<LODPoint>(last - first);
        for (int i = first; i < last; i++)
            visible.Add(new LODPoint((long)((source[i].X - left) * 1_000_000), source[i].Y, 0, 0));
        var sampled = GraphHistoryResolution.Downsample(visible, budget - 2, 0, (long)(width * 1_000_000));
        var result = new List<Point>(budget);
        if (first > 0) result.Add(source[first - 1]);
        result.AddRange(sampled.Select(p => new Point(left + p.TimestampUnixNano / 1_000_000.0, p.Value)));
        if (last < source.Count) result.Add(source[last]);
        return result;
    }
}
