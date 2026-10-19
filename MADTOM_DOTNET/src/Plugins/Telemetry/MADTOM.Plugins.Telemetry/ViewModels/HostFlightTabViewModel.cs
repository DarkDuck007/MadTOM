using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels.Topology;

namespace MadTOM.ViewModels;

public partial class HostFlightTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;

    [ObservableProperty]
    private string _dropRate = "No interface measurements";

    public ObservableCollection<DropRuleModel> DropRules { get; } = new();

    public NetworkTopologyViewModel Topology { get; }

    public HostFlightTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;

        var collectorMgr = telemetryProvider.CollectorManager ?? new MultiCollectorManager();
        Topology = new NetworkTopologyViewModel(collectorMgr);

        _telemetryProvider.NodeTelemetryUpdated += (_, node) =>
        {
            if (_target == node.Id || _target == "all") SetTargetHost(_target);
        };
    }

    private string _target = "";
    public ObservableCollection<NetworkInterfaceRow> Interfaces { get; } = new();

    public void SetTargetHost(string hostId)
    {
        _target = hostId;
        Interfaces.Clear();

        var nodes = _telemetryProvider.GetFleetNodes();
        var selectedNode = nodes.FirstOrDefault(n => n.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));

        foreach (var node in nodes.Where(n => hostId == "all" || n.Id == hostId))
        {
            foreach (var nic in node.Interfaces)
            {
                Interfaces.Add(new NetworkInterfaceRow(
                    node.Id,
                    nic.Name,
                    nic.CarrierUp ? "Up" : "Down",
                    nic.RxBytes,
                    nic.TxBytes,
                    nic.RxDropped + nic.TxDropped,
                    nic.RxErrors + nic.TxErrors,
                    nic.Mtu,
                    nic.LinkSpeedMbps));
            }
        }

        DropRate = $"{Interfaces.Sum(i => (double)i.Drops):N0} dropped packets since boot";

        // Activate or update real-time Network & Topology visualizer
        if (!string.IsNullOrEmpty(hostId) && hostId != "all" && hostId != "aggregated")
        {
            Topology.ActivateForNode(hostId, selectedNode);
        }
        else
        {
            Topology.Deactivate();
        }
    }
}
