using System;
using System.Collections.Generic;
using System.Linq;

namespace MadTOM.Services;

public static class GraphHistoryResolution
{
    public static int PointBudget(double plotWidth, double multiplier = 3) => double.IsFinite(plotWidth)
        ? (int)Math.Clamp(Math.Ceiling(plotWidth * (GraphPerformanceSettings.IsValidHistory(multiplier) ? multiplier : 3)), 4, 100000) : 2400;

    public static List<int> DownsampleIndices(Func<int, long> getTimestamp, Func<int, double> getValue, int count, int budget, long start, long end, int visibleFirst, int visibleEnd)
    {
        int buckets = Math.Max(1, (budget - 2) / 4 - 1);
        long bucketWidth = Math.Max(1L, (long)Math.Ceiling((end - (decimal)start) / buckets));
        var result = new List<int>(budget);
        if (visibleFirst > 0) result.Add(visibleFirst - 1);
        int first = visibleFirst;
        long Bucket(long timestamp) => (long)Math.Floor(timestamp / (decimal)bucketWidth);
        int[] picks = new int[4];
        while (first < visibleEnd)
        {
            int last = first;
            long bucket = Bucket(getTimestamp(first));
            int minIdx = first, maxIdx = first;
            double minVal = getValue(first);
            double maxVal = minVal;
            while (last + 1 < visibleEnd && Bucket(getTimestamp(last + 1)) == bucket)
            {
                last++;
                double val = getValue(last);
                if (val < minVal) { minVal = val; minIdx = last; }
                if (val > maxVal) { maxVal = val; maxIdx = last; }
            }
            int pickCount = 0;
            picks[pickCount++] = first;
            if (minIdx != first) picks[pickCount++] = minIdx;
            if (maxIdx != first && maxIdx != minIdx) picks[pickCount++] = maxIdx;
            if (last != first && last != minIdx && last != maxIdx) picks[pickCount++] = last;

            if (pickCount < 4 && last > first)
            {
                int mid1 = first + (last - first) / 3;
                bool exists = false;
                for (int p = 0; p < pickCount; p++) if (picks[p] == mid1) { exists = true; break; }
                if (!exists) picks[pickCount++] = mid1;

                if (pickCount < 4)
                {
                    int mid2 = first + 2 * (last - first) / 3;
                    exists = false;
                    for (int p = 0; p < pickCount; p++) if (picks[p] == mid2) { exists = true; break; }
                    if (!exists) picks[pickCount++] = mid2;
                }
            }
            for (int i = 1; i < pickCount; i++)
            {
                int key = picks[i];
                int j = i - 1;
                while (j >= 0 && picks[j] > key)
                {
                    picks[j + 1] = picks[j];
                    j--;
                }
                picks[j + 1] = key;
            }
            for (int i = 0; i < pickCount; i++) result.Add(picks[i]);
            first = last + 1;
        }
        if (visibleEnd < count) result.Add(visibleEnd);
        return result;
    }

    // Time buckets give dense cached and sparse collector sections the same horizontal scale.
    // Retain extrema and endpoints; never overwrite the full-resolution session cache.
    public static IReadOnlyList<LODPoint> Downsample(IReadOnlyList<LODPoint> points, int budget, long start, long end)
    {
        budget = Math.Clamp(budget, 4, 100000);
        if (points.Count <= budget || end <= start) return points;
        int visibleFirst = 0;
        while (visibleFirst < points.Count && points[visibleFirst].TimestampUnixNano < start) visibleFirst++;
        int visibleEnd = visibleFirst;
        while (visibleEnd < points.Count && points[visibleEnd].TimestampUnixNano <= end) visibleEnd++;
        if (budget < 10)
            return new[] { points[Math.Max(0, visibleFirst - 1)], points[Math.Min(points.Count - 1, visibleEnd)] };
        var indices = DownsampleIndices(i => points[i].TimestampUnixNano, i => points[i].Value, points.Count, budget, start, end, visibleFirst, visibleEnd);
        var result = new List<LODPoint>(indices.Count);
        for (int i = 0; i < indices.Count; i++) result.Add(points[indices[i]]);
        return result;
    }
}
