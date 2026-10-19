using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels.Topology;

public enum TopologyItemType
{
    Interface,
    Gateway,
    Router,
    Subnet
}

public partial class TopologyNodeItemViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private TopologyItemType _itemType = TopologyItemType.Interface;
    [ObservableProperty] private string _kind = "ethernet"; // ethernet, wifi, wireguard, bridge, etc.
    [ObservableProperty] private bool _isUp = true;
    [ObservableProperty] private bool _carrierUp = true;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _height = 80;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isHovered;

    // Traffic Counters
    [ObservableProperty] private double _rxRateBps;
    [ObservableProperty] private double _txRateBps;
    [ObservableProperty] private ulong _rxDropped;
    [ObservableProperty] private ulong _txDropped;
    [ObservableProperty] private ulong _rxErrors;
    [ObservableProperty] private ulong _txErrors;

    // Details for inspector
    [ObservableProperty] private string _macAddress = "";
    [ObservableProperty] private uint _mtu = 1500;
    [ObservableProperty] private uint _speedMbps;
    [ObservableProperty] private string _duplex = "unknown";
    [ObservableProperty] private string _namespace = "root";
    [ObservableProperty] private string _driver = "";
    [ObservableProperty] private string _pciSlot = "";

    public ObservableCollection<string> IpAddresses { get; } = new();

    public string FullDetailsText
    {
        get
        {
            var parts = new List<string> { Title, Subtitle };
            if (IpAddresses.Count > 0)
            {
                parts.Add($"IPs: {string.Join(", ", IpAddresses)}");
            }
            if (ItemType == TopologyItemType.Interface)
            {
                parts.Add($"MAC: {(string.IsNullOrEmpty(MacAddress) ? "None" : MacAddress)}");
                parts.Add($"State: {StatusBadgeText} | MTU: {Mtu}");
            }
            return string.Join("\n", parts);
        }
    }

    public string StatusBadgeText => CarrierUp ? (IsUp ? "UP" : "ADMIN-DOWN") : "NO-CARRIER";
    public string StatusColor => CarrierUp ? (IsUp ? "#10B981" : "#F59E0B") : "#EF4444";

    public string IconGlyph => ItemType switch
    {
        TopologyItemType.Gateway or TopologyItemType.Router => "🌐",
        TopologyItemType.Subnet => "🗄️",
        _ => Kind.ToLowerInvariant() switch
        {
            "loopback" => "🔄",
            "bridge" => "🌉",
            "veth" => "📦",
            "wireguard" => "🛡️",
            "wifi" or "wlan" => "📶",
            "bond" => "🔗",
            "vlan" => "🏷️",
            _ => Title.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ? "📦"
               : Title.StartsWith("lo", StringComparison.OrdinalIgnoreCase) ? "🔄"
               : Title.StartsWith("br", StringComparison.OrdinalIgnoreCase) || Title.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ? "🌉"
               : Title.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ? "🛡️"
               : Title.StartsWith("wl", StringComparison.OrdinalIgnoreCase) ? "📶"
               : "🔌"
        }
    };

    public string IconTypeTag => ItemType switch
    {
        TopologyItemType.Gateway or TopologyItemType.Router => "GATEWAY",
        TopologyItemType.Subnet => "SUBNET",
        _ => Kind.ToLowerInvariant() switch
        {
            "loopback" => "LOOPBACK",
            "bridge" => "BRIDGE",
            "veth" => "VETH / CONTAINER",
            "wireguard" => "WIREGUARD",
            "wifi" or "wlan" => "WI-FI",
            "bond" => "BOND",
            "vlan" => "VLAN",
            _ => Title.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ? "VETH"
               : Title.StartsWith("lo", StringComparison.OrdinalIgnoreCase) ? "LOOPBACK"
               : Title.StartsWith("br", StringComparison.OrdinalIgnoreCase) || Title.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ? "BRIDGE"
               : Title.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ? "WIREGUARD"
               : Title.StartsWith("wl", StringComparison.OrdinalIgnoreCase) ? "WI-FI"
               : "ETHERNET"
        }
    };

    public string IconBadgeColor => ItemType switch
    {
        TopologyItemType.Gateway or TopologyItemType.Router => "#8957E5", // Purple
        _ => Kind.ToLowerInvariant() switch
        {
            "loopback" => "#6E7681", // Gray
            "bridge" => "#1F6FEB",   // Blue
            "veth" => "#D29922",     // Amber
            "wireguard" => "#238636",// Green
            "wifi" or "wlan" => "#388BFD",
            _ => Title.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ? "#D29922"
               : Title.StartsWith("lo", StringComparison.OrdinalIgnoreCase) ? "#6E7681"
               : Title.StartsWith("br", StringComparison.OrdinalIgnoreCase) || Title.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ? "#1F6FEB"
               : Title.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ? "#238636"
               : "#388BFD"
        }
    };

    public string FormattedRxRate => FormatBitrate(RxRateBps);
    public string FormattedTxRate => FormatBitrate(TxRateBps);

    partial void OnRxRateBpsChanged(double value) => OnPropertyChanged(nameof(FormattedRxRate));
    partial void OnTxRateBpsChanged(double value) => OnPropertyChanged(nameof(FormattedTxRate));
    partial void OnCarrierUpChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusColor));
    }
    partial void OnIsUpChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusColor));
    }

    private static string FormatBitrate(double bps)
    {
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000:F2} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000:F2} Mbps";
        if (bps >= 1_000) return $"{bps / 1_000:F1} Kbps";
        return $"{bps:F0} bps";
    }
}

public partial class TopologyEdgeItemViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _sourceNodeId = "";
    [ObservableProperty] private string _targetNodeId = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _protocol = "kernel"; // kernel, dhcp, static, bgp
    [ObservableProperty] private uint _metric;
    [ObservableProperty] private bool _isDefaultRoute;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private double _startX;
    [ObservableProperty] private double _startY;
    [ObservableProperty] private double _endX;
    [ObservableProperty] private double _endY;
}
