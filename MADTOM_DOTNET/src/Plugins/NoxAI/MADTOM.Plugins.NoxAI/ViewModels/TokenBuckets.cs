using System;
using System.Collections.Generic;

namespace MADTOM.Plugins.NoxAI.ViewModels;

/// <summary>
/// Turns NoxChat's cumulative token counters (llm.tokens.*, counted since NoxChat started) into
/// "tokens used per bucket" for the Studio graphs.
/// </summary>
public static class TokenBuckets
{
    /// <summary>Bucket size for a graph window: about 30-170 bars per window.</summary>
    public static long BucketSecondsFor(long windowSeconds) => windowSeconds switch
    {
        <= 300 => 10,
        <= 900 => 30,
        <= 3600 => 60,
        <= 21600 => 300,
        <= 86400 => 900,
        _ => 3600,
    };

    public static string BucketLabel(long bucketSeconds) => bucketSeconds switch
    {
        < 60 => $"{bucketSeconds} s",
        < 3600 => $"{bucketSeconds / 60} min",
        _ => $"{bucketSeconds / 3600} h",
    };

    /// <summary>
    /// Sums the increase of a cumulative counter per bucket in [windowStart, windowEnd]. A drop means the
    /// counter restarted (NoxChat restart), so the new value itself is the increase. Every bucket in the
    /// window gets a point (0 when nothing happened), so the graph returns to zero between bursts.
    /// </summary>
    public static (long[] Timestamps, double[] Values) FromCumulative(
        IReadOnlyList<long> timestamps, IReadOnlyList<double> cumulative,
        long windowStartNano, long windowEndNano, long bucketNanos)
    {
        if (bucketNanos <= 0 || windowEndNano <= windowStartNano)
            return (Array.Empty<long>(), Array.Empty<double>());
        long first = windowStartNano - Mod(windowStartNano, bucketNanos);
        int count = (int)((windowEndNano - first) / bucketNanos) + 1;
        var ts = new long[count];
        var vals = new double[count];
        for (int i = 0; i < count; i++) ts[i] = first + i * bucketNanos;

        int n = Math.Min(timestamps.Count, cumulative.Count);
        for (int i = 1; i < n; i++)
        {
            long t = timestamps[i];
            if (t < windowStartNano || t > windowEndNano) continue;
            double delta = cumulative[i] - cumulative[i - 1];
            if (delta < 0) delta = cumulative[i];
            int b = (int)((t - first) / bucketNanos);
            if (b >= 0 && b < count) vals[b] += delta;
        }
        return (ts, vals);
    }

    /// <summary>Increase of a cumulative counter between two times (restarts handled as above).</summary>
    public static double Increase(IReadOnlyList<long> timestamps, IReadOnlyList<double> cumulative,
        long windowStartNano, long windowEndNano)
    {
        double sum = 0;
        int n = Math.Min(timestamps.Count, cumulative.Count);
        for (int i = 1; i < n; i++)
        {
            long t = timestamps[i];
            if (t < windowStartNano || t > windowEndNano) continue;
            double delta = cumulative[i] - cumulative[i - 1];
            sum += delta < 0 ? cumulative[i] : delta;
        }
        return sum;
    }

    public static string Format(double tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000:F2}M",
        >= 1_000 => $"{tokens / 1_000:F1}k",
        _ => $"{tokens:F0}",
    };

    private static long Mod(long a, long m) => ((a % m) + m) % m;
}
