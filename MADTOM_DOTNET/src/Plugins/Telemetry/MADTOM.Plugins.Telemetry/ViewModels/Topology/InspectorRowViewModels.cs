using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels.Topology;

public partial class RouteRowViewModel : ObservableObject
{
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private string _interfaceName = "";
    [ObservableProperty] private string _protocol = "";
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private uint _metric;
    [ObservableProperty] private uint _table;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isIpv6;
    [ObservableProperty] private string _type = "unicast";
    [ObservableProperty] private string _preferredSource = "";
}

public partial class FirewallRuleRowViewModel : ObservableObject
{
    [ObservableProperty] private ulong _handle;
    [ObservableProperty] private string _table = "";
    [ObservableProperty] private string _chain = "";
    [ObservableProperty] private string _statement = "";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private ulong _packets;
    [ObservableProperty] private ulong _bytes;
    [ObservableProperty] private string _rawJson = "";

    public string VerdictColor => Verdict.ToLowerInvariant() switch
    {
        "accept" => "#10B981",
        "drop" => "#EF4444",
        "reject" => "#DC2626",
        "masquerade" => "#3B82F6",
        "dnat" or "snat" => "#8B5CF6",
        _ => "#6B7280"
    };
}

public partial class SocketRowViewModel : ObservableObject
{
    [ObservableProperty] private string _socketCookie = "";
    [ObservableProperty] private string _localEndpoint = "";
    [ObservableProperty] private string _remoteEndpoint = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _processName = "";
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _interfaceName = "";
    [ObservableProperty] private string _congestionAlgorithm = "";
    [ObservableProperty] private double _deliveryRateMbps;
    [ObservableProperty] private double _pacingRateMbps;
    [ObservableProperty] private double _rttMs;
    [ObservableProperty] private uint _cwnd;
    [ObservableProperty] private uint _retransmits;
    [ObservableProperty] private string _caState = "";

    public string ProcessDisplay => Pid > 0 ? $"{ProcessName} ({Pid})" : (string.IsNullOrEmpty(ProcessName) ? "kernel/system" : ProcessName);

    public string FormattedDeliveryRate => DeliveryRateMbps > 0 ? $"{DeliveryRateMbps:F2} Mbps" : "-";
    public string FormattedRtt => RttMs > 0 ? $"{RttMs:F1} ms" : "-";
}

public partial class NeighborRowViewModel : ObservableObject
{
    [ObservableProperty] private string _ipAddress = "";
    [ObservableProperty] private string _macAddress = "";
    [ObservableProperty] private string _interfaceName = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _isRouter;

    public string StateColor => State switch
    {
        "REACHABLE" or "PERMANENT" => "#10B981",
        "STALE" or "DELAY" => "#F59E0B",
        "FAILED" => "#EF4444",
        _ => "#6B7280"
    };
}
