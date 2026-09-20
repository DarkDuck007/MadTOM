using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.ConnectionToolkit.ViewModels;

public sealed partial class ConnectionEndpointItem : ObservableObject
{
    public string ConnectionName { get; init; } = string.Empty;
    public string ConnectionType { get; init; } = "SSH Tunnel";
    public string RemoteHost { get; init; } = string.Empty;
    public string LocalPort { get; init; } = string.Empty;
    public string RemotePort { get; init; } = string.Empty;
    public string Status { get; init; } = "Standby";
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _tunnelStatus = "Coming soon";

    [ObservableProperty]
    private string _vpnStatus = "VPN / WireGuard: Standby";

    [ObservableProperty]
    private ConnectionEndpointItem? _selectedEndpoint;

    public ObservableCollection<ConnectionEndpointItem> ConfiguredEndpoints { get; } = new();

    public string ModuleTitle => "MADTOM CONNECTION TOOLKIT";
    public string ModuleSubtitle => "Unified connection manager for SSH tunnels, WireGuard/VPN links, serial bridges, and port forwards.";
    public string ComingSoonMessage => "This module is in active development and will provide centralized tunnel management, proxy chaining, and credential storage.";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void ConnectAll()
    {
        TunnelStatus = "Coming soon";
    }

    [RelayCommand]
    private void DisconnectAll()
    {
        TunnelStatus = "Coming soon";
    }
}

