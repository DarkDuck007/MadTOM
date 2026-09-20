using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.VNA.ViewModels;

public sealed partial class NetworkFlowItem : ObservableObject
{
    public string FlowId { get; init; } = string.Empty;
    public string Protocol { get; init; } = "TCP";
    public string SourceEndpoint { get; init; } = string.Empty;
    public string DestinationEndpoint { get; init; } = string.Empty;
    public string BandwidthRate { get; init; } = "0 B/s";
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _captureStatus = "Coming soon";

    [ObservableProperty]
    private string _topologyState = "Network Topology: Standby";

    [ObservableProperty]
    private NetworkFlowItem? _selectedFlow;

    public ObservableCollection<NetworkFlowItem> ActiveFlows { get; } = new();

    public string ModuleTitle => "MADTOM VISUAL NETWORK ANALYZER (VNA)";
    public string ModuleSubtitle => "Interactive packet flow visualization, protocol graphs, and latency topology mapping.";
    public string ComingSoonMessage => "This module is in active development and will integrate with local capture engines (libpcap, eBPF) and collector topology streams.";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void StartCapture()
    {
        CaptureStatus = "Coming soon";
    }

    [RelayCommand]
    private void RefreshTopology()
    {
        CaptureStatus = "Coming soon";
    }
}

