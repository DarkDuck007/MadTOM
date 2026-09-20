using System;
using System.Collections.Generic;
using System.Linq;

namespace SQUEEZE.Models;

public class ServerNodeInfo
{
    public string ConnectionLabel => IsOnline ? "● Connected" : "● Offline";

    public string NodeName { get; set; } = "DISCONNECTED";
    public bool IsOnline { get; set; } = false;
    public string LanAddress { get; set; } = "NOT CONNECTED";
    public string Version { get; set; } = "0.0.0";
    public double UptimeSeconds { get; set; }
    public int CpuCores { get; set; }
    public double? LoadAverage { get; set; }
    public ulong ProcessMemoryBytes { get; set; }
    public ulong SystemMemoryBytes { get; set; }
    public ulong AvailableMemoryBytes { get; set; }
    public int MaxConcurrent { get; set; }
    public List<string> Encoders { get; set; } = new();
    public List<string> HardwareEncoders { get; set; } = new();
    public string HardwareStatus { get; set; } = "OFFLINE";
    public bool PauseSupported { get; set; }
    public bool HasHardwareEncoders => HardwareEncoders != null && HardwareEncoders.Count > 0;

    public string MemoryUsage
    {
        get
        {
            if (!IsOnline || SystemMemoryBytes == 0) return "-- / --";
            double usedGb = (SystemMemoryBytes - AvailableMemoryBytes) / (1024.0 * 1024.0 * 1024.0);
            double totalGb = SystemMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            return $"{usedGb:F1}G/{totalGb:F0}G";
        }
    }

    public string CpuUsageText
    {
        get
        {
            if (!IsOnline) return "--";
            if (LoadAverage.HasValue)
            {
                return CpuCores > 0 ? $"{LoadAverage.Value:F2} ({CpuCores}C)" : $"{LoadAverage.Value:F2}";
            }
            return CpuCores > 0 ? $"{CpuCores} CORES" : "--";
        }
    }

    public string GpuStatus
    {
        get
        {
            if (!IsOnline) return "OFFLINE";
            if (HardwareEncoders.Count == 1)
            {
                return $"HW: {HardwareEncoders[0]}";
            }
            if (HardwareEncoders.Count > 1)
            {
                var types = new List<string>();
                if (HardwareEncoders.Any(e => e.Contains("nvenc", StringComparison.OrdinalIgnoreCase))) types.Add("NVENC");
                if (HardwareEncoders.Any(e => e.Contains("qsv", StringComparison.OrdinalIgnoreCase))) types.Add("QSV");
                if (HardwareEncoders.Any(e => e.Contains("vaapi", StringComparison.OrdinalIgnoreCase))) types.Add("VAAPI");
                if (HardwareEncoders.Any(e => e.Contains("amf", StringComparison.OrdinalIgnoreCase))) types.Add("AMF");
                if (types.Count > 0) return $"HW: {string.Join("/", types)} ({HardwareEncoders.Count})";
                return $"HW ({HardwareEncoders.Count})";
            }
            return "SW ONLY (CPU)";
        }
    }

    public string AccessLevel => IsOnline ? "NODE: CONNECTED" : "NODE: OFFLINE";
    public string StatusBadgeText => IsOnline ? $"● {NodeName}: ONLINE" : "○ DISCONNECTED";
    public string StatusBadgeColor => IsOnline ? "#3FB950" : "#8B949E";

    private static bool IsHardwareEncoderName(string name) =>
        name.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_v4l2m2m", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_mf", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<EncoderInfo> DetailedEncoders
    {
        get
        {
            var hwSet = new HashSet<string>(HardwareEncoders ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var list = new List<EncoderInfo>();
            foreach (var enc in Encoders ?? Enumerable.Empty<string>())
            {
                bool isHw = IsHardwareEncoderName(enc);
                bool isSupported = !isHw || hwSet.Contains(enc);

                if (isHw && isSupported)
                {
                    list.Add(new EncoderInfo
                    {
                        Name = enc,
                        IsHardware = true,
                        IsSupported = true,
                        BadgeText = "⚡ HW",
                        BorderColor = "#3FB950",
                        TextColor = "#3FB950",
                        Opacity = 1.0,
                        ToolTip = "Hardware acceleration active and verified on host device"
                    });
                }
                else if (isHw && !isSupported)
                {
                    list.Add(new EncoderInfo
                    {
                        Name = enc,
                        IsHardware = true,
                        IsSupported = false,
                        BadgeText = "○ NO HW",
                        BorderColor = "#21262D",
                        TextColor = "#6E7681",
                        Opacity = 0.4,
                        ToolTip = "Compiled in FFmpeg, but incompatible with host GPU / driver"
                    });
                }
                else
                {
                    list.Add(new EncoderInfo
                    {
                        Name = enc,
                        IsHardware = false,
                        IsSupported = true,
                        BadgeText = "CPU",
                        BorderColor = "#30363D",
                        TextColor = "#C9D1D9",
                        Opacity = 1.0,
                        ToolTip = "Software encoder (Runs on host CPU)"
                    });
                }
            }
            return list;
        }
    }
}

public class EncoderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsHardware { get; set; }
    public bool IsSupported { get; set; }
    public string BadgeText { get; set; } = string.Empty;
    public string BorderColor { get; set; } = "#30363D";
    public string TextColor { get; set; } = "#C9D1D9";
    public double Opacity { get; set; } = 1.0;
    public string ToolTip { get; set; } = string.Empty;
}
