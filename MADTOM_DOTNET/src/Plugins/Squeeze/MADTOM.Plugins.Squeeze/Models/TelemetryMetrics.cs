using System;

namespace SQUEEZE.Models;

public class TelemetryMetrics
{
    public double Fps { get; set; }
    public double SpeedFactor { get; set; }
    public int BitrateKbps { get; set; }
    public TimeSpan Eta { get; set; }
    public long CurrentFrame { get; set; }
    public long TotalFrames { get; set; }
    public double? ProgressPercent { get; set; }
    public double Percentage => ProgressPercent ?? (TotalFrames > 0
        ? Math.Min(100.0, (CurrentFrame / (double)TotalFrames) * 100.0)
        : 0.0);
    public string StatusText { get; set; } = "IDLE";
}

