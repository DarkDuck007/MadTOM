using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public sealed class OriginShareItem
{
    public string CountryFlag { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public string Latency { get; set; } = string.Empty;
    public string LatencyColor { get; set; } = "#22d3ee";
    public string BarColor { get; set; } = "#06b6d4";
}

public sealed class ProtocolSplitItem
{
    public string Name { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public string Color { get; set; } = "#06b6d4";
}

public sealed class AsnPeeringItem
{
    public string AsnName { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public string Color { get; set; } = "#06b6d4";
}

public sealed class PortTrafficItem
{
    public string PortName { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public string Color { get; set; } = "#22d3ee";
}

public partial class GlobalRadarViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _scopedNodeId = "aggregated";

    [ObservableProperty]
    private string _timeScope = "1h";

    [ObservableProperty]
    private string _egressRate = "184.2 Gbps";

    [ObservableProperty]
    private string _sampleCount = "1.42M";

    [ObservableProperty]
    private string _activeVectorsBadge = "6 Active Transit Vectors";

    public ObservableCollection<string> ScopedNodeOptions { get; } = new();
    public ObservableCollection<OriginShareItem> TopOrigins { get; } = new();
    public ObservableCollection<ProtocolSplitItem> ProtocolSplit { get; } = new();
    public ObservableCollection<AsnPeeringItem> TopAsn { get; } = new();
    public ObservableCollection<PortTrafficItem> PortTraffic { get; } = new();

    public GlobalRadarViewModel(ITelemetryDataProvider telemetryProvider)
    {
        ScopedNodeOptions.Add("Aggregated (All Hosts - Global Fleet)");
        foreach (var n in telemetryProvider.GetFleetNodes())
        {
            ScopedNodeOptions.Add(n.Id);
        }

        InitializeBreakdownData();
    }

    private void InitializeBreakdownData()
    {
        TopOrigins.Add(new OriginShareItem { CountryFlag = "🇯🇵", CountryName = "Japan", Percentage = 52.4, Latency = "1.8ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });
        TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 23.9, Latency = "78ms", BarColor = "#10b981", LatencyColor = "#34d399" });
        TopOrigins.Add(new OriginShareItem { CountryFlag = "🇩🇪", CountryName = "Germany", Percentage = 12.1, Latency = "142ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });
        TopOrigins.Add(new OriginShareItem { CountryFlag = "🇸🇬", CountryName = "Singapore", Percentage = 7.4, Latency = "62ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });

        ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 48.2, Color = "#06b6d4" });
        ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 38.5, Color = "#6366f1" });
        ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 8.1, Color = "#f59e0b" });
        ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 5.2, Color = "#a855f7" });

        TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 34.2, Color = "#22d3ee" });
        TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 22.8, Color = "#34d399" });
        TopAsn.Add(new AsnPeeringItem { AsnName = "AS2914 (NTT Comms)", Percentage = 18.4, Color = "#818cf8" });
        TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 14.1, Color = "#fbbf24" });
        TopAsn.Add(new AsnPeeringItem { AsnName = "AS6939 (Hurricane Electric)", Percentage = 10.5, Color = "#c084fc" });

        PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 76.4, Color = "#22d3ee" });
        PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 11.2, Color = "#fbbf24" });
        PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 6.8, Color = "#c084fc" });
        PortTraffic.Add(new PortTrafficItem { PortName = "Port 53 (DNS / DoH)", Percentage = 3.4, Color = "#34d399" });
        PortTraffic.Add(new PortTrafficItem { PortName = "Port 22 (SSH Admin)", Percentage = 2.2, Color = "#94a3b8" });
    }

    [RelayCommand]
    public void SetTimeScope(string scope)
    {
        TimeScope = scope;
        NotificationService.Instance.ShowToast($"Radar observation scope set to {scope}");
    }
}

