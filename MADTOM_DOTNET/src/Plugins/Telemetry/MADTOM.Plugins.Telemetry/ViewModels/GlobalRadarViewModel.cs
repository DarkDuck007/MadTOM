using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class OriginShareItem : ObservableObject
{
    [ObservableProperty]
    private string _countryFlag = string.Empty;

    [ObservableProperty]
    private string _countryName = string.Empty;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _latency = string.Empty;

    [ObservableProperty]
    private string _latencyColor = "#22d3ee";

    [ObservableProperty]
    private string _barColor = "#06b6d4";

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isHovered;
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
    private string _scopedNodeId = "Aggregated (All Hosts - Global Fleet)";

    partial void OnScopedNodeIdChanged(string value)
    {
        UpdateScopedData(value);
    }

    [ObservableProperty]
    private string _hoveredCountry = string.Empty;

    partial void OnHoveredCountryChanged(string value)
    {
        UpdateHoverState(value);
    }

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

        UpdateScopedData(_scopedNodeId);
    }

    private void UpdateScopedData(string scoped)
    {
        TopOrigins.Clear();
        ProtocolSplit.Clear();
        TopAsn.Clear();
        PortTraffic.Clear();

        string lower = scoped.ToLowerInvariant();
        if (lower.Contains("aggregated"))
        {
            EgressRate = "184.2 Gbps";
            SampleCount = "1.42M";
            ActiveVectorsBadge = "6 Active Transit Vectors";

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
        else if (lower.Contains("gander"))
        {
            EgressRate = "48.6 Gbps";
            SampleCount = "380K";
            ActiveVectorsBadge = "TYO Ingress (4 Vectors)";

            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇯🇵", CountryName = "Japan", Percentage = 58.2, Latency = "1.2ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 22.4, Latency = "74ms", BarColor = "#10b981", LatencyColor = "#34d399" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇸🇬", CountryName = "Singapore", Percentage = 12.8, Latency = "46ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇩🇪", CountryName = "Germany", Percentage = 6.6, Latency = "178ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });

            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 54.1, Color = "#06b6d4" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 33.7, Color = "#6366f1" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 7.4, Color = "#f59e0b" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 4.8, Color = "#a855f7" });

            TopAsn.Add(new AsnPeeringItem { AsnName = "AS2914 (NTT Comms)", Percentage = 44.5, Color = "#818cf8" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 26.3, Color = "#22d3ee" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 18.2, Color = "#34d399" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 11.0, Color = "#fbbf24" });

            PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 81.2, Color = "#22d3ee" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 10.4, Color = "#fbbf24" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 5.2, Color = "#c084fc" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 53 (DNS / DoH)", Percentage = 3.2, Color = "#34d399" });
        }
        else if (lower.Contains("iad") || lower.Contains("edge"))
        {
            EgressRate = "64.2 Gbps";
            SampleCount = "520K";
            ActiveVectorsBadge = "IAD Ingress (4 Vectors)";

            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 64.5, Latency = "11ms", BarColor = "#10b981", LatencyColor = "#34d399" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇩🇪", CountryName = "Germany", Percentage = 16.2, Latency = "84ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇬🇧", CountryName = "United Kingdom", Percentage = 11.8, Latency = "68ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇯🇵", CountryName = "Japan", Percentage = 7.5, Latency = "142ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });

            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 46.5, Color = "#06b6d4" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 41.2, Color = "#6366f1" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 8.6, Color = "#f59e0b" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 3.7, Color = "#a855f7" });

            TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 38.1, Color = "#22d3ee" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 27.4, Color = "#fbbf24" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 21.3, Color = "#34d399" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS6939 (Hurricane Electric)", Percentage = 13.2, Color = "#c084fc" });

            PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 74.8, Color = "#22d3ee" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 12.6, Color = "#fbbf24" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 8.1, Color = "#c084fc" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 53 (DNS / DoH)", Percentage = 4.5, Color = "#34d399" });
        }
        else if (lower.Contains("fra") || lower.Contains("cache"))
        {
            EgressRate = "36.8 Gbps";
            SampleCount = "290K";
            ActiveVectorsBadge = "FRA Ingress (4 Vectors)";

            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇩🇪", CountryName = "Germany", Percentage = 51.2, Latency = "3.5ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇫🇷", CountryName = "France", Percentage = 21.4, Latency = "14ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇬🇧", CountryName = "United Kingdom", Percentage = 17.1, Latency = "19ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 10.3, Latency = "89ms", BarColor = "#10b981", LatencyColor = "#34d399" });

            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 49.0, Color = "#06b6d4" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 37.8, Color = "#6366f1" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 8.9, Color = "#f59e0b" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 4.3, Color = "#a855f7" });

            TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 33.7, Color = "#22d3ee" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 25.1, Color = "#34d399" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 22.8, Color = "#fbbf24" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS6939 (Hurricane Electric)", Percentage = 18.4, Color = "#c084fc" });

            PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 75.2, Color = "#22d3ee" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 11.5, Color = "#fbbf24" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 7.9, Color = "#c084fc" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 53 (DNS / DoH)", Percentage = 5.4, Color = "#34d399" });
        }
        else if (lower.Contains("sin") || lower.Contains("gateway"))
        {
            EgressRate = "34.6 Gbps";
            SampleCount = "230K";
            ActiveVectorsBadge = "SIN Ingress (4 Vectors)";

            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇸🇬", CountryName = "Singapore", Percentage = 46.8, Latency = "1.5ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇯🇵", CountryName = "Japan", Percentage = 26.2, Latency = "48ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇦🇺", CountryName = "Australia", Percentage = 17.5, Latency = "58ms", BarColor = "#10b981", LatencyColor = "#34d399" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 9.5, Latency = "165ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });

            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 51.5, Color = "#06b6d4" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 35.2, Color = "#6366f1" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 8.1, Color = "#f59e0b" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 5.2, Color = "#a855f7" });

            TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 36.4, Color = "#22d3ee" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS2914 (NTT Comms)", Percentage = 28.1, Color = "#818cf8" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 20.3, Color = "#34d399" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 15.2, Color = "#fbbf24" });

            PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 78.4, Color = "#22d3ee" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 10.9, Color = "#fbbf24" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 6.4, Color = "#c084fc" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 53 (DNS / DoH)", Percentage = 4.3, Color = "#34d399" });
        }
        else
        {
            EgressRate = "28.4 Gbps";
            SampleCount = "195K";
            ActiveVectorsBadge = $"{scoped} Ingress";

            TopOrigins.Add(new OriginShareItem { CountryFlag = "🌐", CountryName = "Local Ingress", Percentage = 45.0, Latency = "5ms", BarColor = "#06b6d4", LatencyColor = "#22d3ee" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇺🇸", CountryName = "United States", Percentage = 30.0, Latency = "65ms", BarColor = "#10b981", LatencyColor = "#34d399" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇩🇪", CountryName = "Germany", Percentage = 15.0, Latency = "110ms", BarColor = "#f59e0b", LatencyColor = "#fbbf24" });
            TopOrigins.Add(new OriginShareItem { CountryFlag = "🇯🇵", CountryName = "Japan", Percentage = 10.0, Latency = "140ms", BarColor = "#6366f1", LatencyColor = "#818cf8" });

            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/3 & QUIC (UDP 443)", Percentage = 45.0, Color = "#06b6d4" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "HTTP/2 (TLS 1.3)", Percentage = 40.0, Color = "#6366f1" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "TWAMP (RFC 5357)", Percentage = 10.0, Color = "#f59e0b" });
            ProtocolSplit.Add(new ProtocolSplitItem { Name = "gRPC Internal (Port 50051)", Percentage = 5.0, Color = "#a855f7" });

            TopAsn.Add(new AsnPeeringItem { AsnName = "AS13335 (Cloudflare)", Percentage = 40.0, Color = "#22d3ee" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS15169 (Google LLC)", Percentage = 30.0, Color = "#34d399" });
            TopAsn.Add(new AsnPeeringItem { AsnName = "AS3356 (Lumen / Level3)", Percentage = 30.0, Color = "#fbbf24" });

            PortTraffic.Add(new PortTrafficItem { PortName = "Port 443 (HTTPS / QUIC)", Percentage = 75.0, Color = "#22d3ee" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 862 (TWAMP Probe)", Percentage = 15.0, Color = "#fbbf24" });
            PortTraffic.Add(new PortTrafficItem { PortName = "Port 50051 (gRPC Stream)", Percentage = 10.0, Color = "#c084fc" });
        }

        UpdateHoverState(HoveredCountry);
    }

    public void UpdateHoverState(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            foreach (var item in TopOrigins)
            {
                item.IsMuted = false;
                item.IsHovered = false;
            }
        }
        else
        {
            foreach (var item in TopOrigins)
            {
                bool isMatch = string.Equals(item.CountryName, country, StringComparison.OrdinalIgnoreCase) ||
                               item.CountryName.Contains(country, StringComparison.OrdinalIgnoreCase) ||
                               country.Contains(item.CountryName, StringComparison.OrdinalIgnoreCase);

                item.IsHovered = isMatch;
                item.IsMuted = !isMatch;
            }
        }
    }

    [RelayCommand]
    public void SetTimeScope(string scope)
    {
        TimeScope = scope;
        NotificationService.Instance.ShowToast($"Radar observation scope set to {scope}");
    }
}

