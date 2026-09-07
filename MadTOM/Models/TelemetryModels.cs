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
    public int HostCount { get; set; } = 6;
    public int GanderCount { get; set; } = 2;
    public int GoslingCount { get; set; } = 4;
    public double P95ForwardMs { get; set; } = 1.8;
    public double P95ReverseMs { get; set; } = 2.4;
    public double GlobalEgressGbps { get; set; } = 148.2;
}

