using System;
using System.Collections.Generic;
using System.Linq;

namespace MadTOM.Services;

public static class GraphHistoryResolution
{
    public static int PointBudget(double plotWidth, double multiplier = 3) => double.IsFinite(plotWidth)
        ? (int)Math.Clamp(Math.Ceiling(plotWidth * (GraphPerformanceSettings.IsValidHistory(multiplier) ? multiplier : 3)), 4, 100000) : 2400;

    // Time buckets give dense cached and sparse collector sections the same horizontal scale.
    // Retain extrema and endpoints; never overwrite the full-resolution session cache.
    public static IReadOnlyList<LODPoint> Downsample(IReadOnlyList<LODPoint> points, int budget, long start, long end)
    {
        budget = Math.Clamp(budget, 4, 100000);
        if (points.Count <= budget || end <= start) return points;
        // Absolute boundaries remain fixed as a constant-duration window moves.
        // Reserve two edge neighbours and one partially covered bucket.
        int buckets = Math.Max(1, (budget - 2) / 4 - 1);
        long bucketWidth = Math.Max(1, (long)Math.Ceiling((end - (decimal)start) / buckets));
        int visibleFirst = 0;
        while (visibleFirst < points.Count && points[visibleFirst].TimestampUnixNano < start) visibleFirst++;
        int visibleEnd = visibleFirst;
        while (visibleEnd < points.Count && points[visibleEnd].TimestampUnixNano <= end) visibleEnd++;
        if (budget < 10)
            return new[] { points[Math.Max(0, visibleFirst - 1)], points[Math.Min(points.Count - 1, visibleEnd)] };
        var result = new List<LODPoint>(budget);
        if (visibleFirst > 0) result.Add(points[visibleFirst - 1]);
        int first = visibleFirst;
        long Bucket(long timestamp) => (long)Math.Floor(timestamp / (decimal)bucketWidth);
        while (first < visibleEnd)
        {
            int last = first;
            long bucket = Bucket(points[first].TimestampUnixNano);
            int min = first, max = first;
            while (last + 1 < visibleEnd && Bucket(points[last + 1].TimestampUnixNano) == bucket)
            {
                last++;
                if (points[last].Value < points[min].Value) min = last;
                if (points[last].Value > points[max].Value) max = last;
            }
            var selected = new SortedSet<int> { first, min, max, last };
            // Flat/monotonic buckets still retain zoom detail instead of collapsing to two points.
            if (selected.Count < 4) selected.Add(first + (last - first) / 3);
            if (selected.Count < 4) selected.Add(first + 2 * (last - first) / 3);
            foreach (int index in selected) result.Add(points[index]);
            first = last + 1;
        }
        if (visibleEnd < points.Count) result.Add(points[visibleEnd]);
        return result;
    }
}
