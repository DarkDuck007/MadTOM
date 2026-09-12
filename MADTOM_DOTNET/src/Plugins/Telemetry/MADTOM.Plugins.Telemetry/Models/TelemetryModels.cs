namespace MadTOM.Models;

public sealed class DropRuleModel
{
    public string Rule { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string Proto { get; set; } = string.Empty;
}

public sealed class RegionTrafficModel
{
    public string RegionId { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string TrafficRate { get; set; } = string.Empty;
    public string Rtt { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#334155";
}

public sealed class ClusterTelemetrySummary
{
    public string TwampSummary { get; set; } = "Unavailable";
    public int HostCount { get; set; }
    public int GanderCount { get; set; }
    public int GoslingCount { get; set; }
    public double P95ForwardMs { get; set; }
    public double P95ReverseMs { get; set; }
    public double GlobalIngressGbps { get; set; }
    public double GlobalEgressGbps { get; set; }
}

