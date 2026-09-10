using System;
using System.Collections.Generic;
using System.Linq;
using MadTOM.Models;
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
    private string _egressRate = "";

    [ObservableProperty]
    private string _sampleCount = "";

    [ObservableProperty]
    private string _activeVectorsBadge = "";

    public ObservableCollection<string> ScopedNodeOptions { get; } = new();
    public ObservableCollection<OriginShareItem> TopOrigins { get; } = new();
    public ObservableCollection<ProtocolSplitItem> ProtocolSplit { get; } = new();
    public ObservableCollection<AsnPeeringItem> TopAsn { get; } = new();
    public ObservableCollection<PortTrafficItem> PortTraffic { get; } = new();

    private readonly ITelemetryDataProvider _provider;
    public ObservableCollection<NetworkInterfaceRow> Interfaces { get; } = new();
    public ObservableCollection<string> Topology { get; } = new();
    public GlobalRadarViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _provider = telemetryProvider;
        ScopedNodeOptions.Add("Aggregated (All Hosts - Global Fleet)");
        foreach (var n in _provider.GetFleetNodes()) ScopedNodeOptions.Add(n.Id);
        _provider.NodeTelemetryUpdated += (_, node) =>
        {
            if (!ScopedNodeOptions.Contains(node.Id)) ScopedNodeOptions.Add(node.Id);
            UpdateScopedData(ScopedNodeId);
        };
        UpdateScopedData(ScopedNodeId);
    }
    private void UpdateScopedData(string scoped)
    {
        var nodes = _provider.GetFleetNodes().Where(n => scoped.StartsWith("Aggregated") || n.Id == scoped).ToArray();
        EgressRate = $"{nodes.Sum(n => n.TxBytesPerSecond) * 8 / 1e6:F3} Mbps";
        SampleCount = nodes.Count(n => n.TimestampUnixNano > 0).ToString();
        ActiveVectorsBadge = $"{nodes.Length} nodes · {nodes.Select(n => n.CollectorEndpoint).Distinct().Count()} collectors";
        Interfaces.Clear(); Topology.Clear();
        foreach (var node in nodes)
        {
            Topology.Add($"{node.Id} → {node.CollectorName} ({node.CollectorEndpoint}) · {node.Role} · {node.Status}");
            foreach (var nic in node.Interfaces) Interfaces.Add(new NetworkInterfaceRow(node.Id, nic.Name, nic.CarrierUp ? "Up" : "Down", nic.RxBytes, nic.TxBytes, nic.RxDropped + nic.TxDropped, nic.RxErrors + nic.TxErrors, nic.Mtu, nic.LinkSpeedMbps));
        }
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


public sealed record NetworkInterfaceRow(string Node, string Name, string State, ulong RxBytes, ulong TxBytes, ulong Drops, ulong Errors, uint Mtu, uint SpeedMbps);
