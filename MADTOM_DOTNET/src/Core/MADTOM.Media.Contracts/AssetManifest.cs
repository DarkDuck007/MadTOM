using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Media.Contracts;

public sealed class AssetManifest
{
    public string Schema { get; set; } = "madtom.asset/1";
    public string AssetId { get; set; } = Guid.NewGuid().ToString("N");
    public string FeedId { get; set; } = string.Empty;
    public string DeviceTrustId { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; } = DateTime.UtcNow;
    public DateTime EndUtc { get; set; } = DateTime.UtcNow;
    public ClockInfo Clock { get; set; } = new();
    public PipelineInfo Pipeline { get; set; } = new();
    public List<RenditionInfo> Renditions { get; set; } = new();

    public bool VerifyRenditionOverlap(TimeSpan tolerance)
    {
        if (Renditions.Count < 2) return true;
        var first = Renditions[0];
        for (int i = 1; i < Renditions.Count; i++)
        {
            var other = Renditions[i];
            var startDiff = (first.StartUtc - other.StartUtc).Duration();
            var endDiff = (first.EndUtc - other.EndUtc).Duration();
            if (startDiff > tolerance || endDiff > tolerance)
                return false;
        }
        return true;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public static AssetManifest? FromJson(string json)
    {
        return JsonSerializer.Deserialize<AssetManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task WriteSidecarAsync(string directory)
    {
        var path = Path.Combine(directory, $"{AssetId}.manifest.json");
        await File.WriteAllTextAsync(path, ToJson());
    }
}

public sealed class ClockInfo
{
    public string Source { get; set; } = "control-probe";
    public double OffsetMs { get; set; }
    public double RttMs { get; set; }
}

public sealed class PipelineInfo
{
    public StreamProfile? Requested { get; set; }
    public StreamProfile? Granted { get; set; }
}

public sealed class RenditionInfo
{
    public string Role { get; set; } = "original"; // "original" | "proxy"
    public string Path { get; set; } = string.Empty;
    public string Codec { get; set; } = "hevc";
    public uint Width { get; set; } = 1920;
    public uint Height { get; set; } = 1080;
    public uint Fps { get; set; } = 30;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public long DurationMs { get; set; }
    public long FileSizeBytes { get; set; }
}

public static class ClockSyncCalculator
{
    /// <summary>
    /// Calculates clock offset and RTT from standard 4-timestamp probe exchange.
    /// t1: Client send
    /// t2: Server receive
    /// t3: Server reply
    /// t4: Client receive
    /// </summary>
    public static (double OffsetMs, double RttMs) Calculate(long t1, long t2, long t3, long t4)
    {
        var offset = ((t2 - t1) + (t3 - t4)) / 2.0;
        var rtt = (t4 - t1) - (t3 - t2);
        return (offset, Math.Max(0, rtt));
    }
}
