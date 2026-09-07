using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Localization;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public sealed class HostOptionItem
{
    public string Id { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public override string ToString() => DisplayText;
}

public partial class HostDetailViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly ILexiconService _lexiconService;

    [ObservableProperty]
    private string _selectedHostId = "gander-epyc-01";

    [ObservableProperty]
    private HostOptionItem? _selectedHostOption;

    [ObservableProperty]
    private string _activeTab = "metrics"; // metrics, processes, logs, flight

    [ObservableProperty]
    private string _hostTitle = "gander-epyc-01";

    [ObservableProperty]
    private string _roleBadge = "Gander (Anchor)";

    [ObservableProperty]
    private bool _isBaremetal = true;

    [ObservableProperty]
    private bool _isAggregated;

    [ObservableProperty]
    private string _cpuSpec = "Dual AMD EPYC 9996";

    [ObservableProperty]
    private string _threadsSpec = "1024 Logical Threads (512C/1024T)";

    [ObservableProperty]
    private string _ramSpec = "512 GB DDR5 ECC";

    [ObservableProperty]
    private string _ramUsedSpec = "148 GB Used (28.9%)";

    [ObservableProperty]
    private string _twampUpText = "↑ 1.84ms";

    [ObservableProperty]
    private string _twampDownText = "↓ 3.12ms";

    [ObservableProperty]
    private string _twampAsymText = "Path Asymmetry Δ: 1.28ms";

    public ObservableCollection<HostOptionItem> HostOptions { get; } = new();

    public HostMetricsTabViewModel MetricsTab { get; }
    public HostProcessesTabViewModel ProcessesTab { get; }
    public HostLogsTabViewModel LogsTab { get; }
    public HostFlightTabViewModel FlightTab { get; }

    public event Action? BackToFleetRequested;

    public HostDetailViewModel(
        ITelemetryDataProvider telemetryProvider,
        ILexiconService lexiconService,
        HostMetricsTabViewModel metricsTab,
        HostProcessesTabViewModel processesTab,
        HostLogsTabViewModel logsTab,
        HostFlightTabViewModel flightTab)
    {
        _telemetryProvider = telemetryProvider;
        _lexiconService = lexiconService;

        MetricsTab = metricsTab;
        ProcessesTab = processesTab;
        LogsTab = logsTab;
        FlightTab = flightTab;

        PopulateHostOptions();
        UpdateHostView("gander-epyc-01");
    }

    private void PopulateHostOptions()
    {
        HostOptions.Clear();
        HostOptions.Add(new HostOptionItem { Id = "aggregated", DisplayText = "Aggregated (All Hosts - 1,904 Cores)" });

        foreach (var n in _telemetryProvider.GetFleetNodes())
        {
            string roleLabel = n.Role == "baremetal" ? "Gander" : "Gosling";
            HostOptions.Add(new HostOptionItem { Id = n.Id, DisplayText = $"{n.Id} ({roleLabel} - {n.Cores}T)" });
        }

        SelectedHostOption = HostOptions.FirstOrDefault(o => o.Id == "gander-epyc-01");
    }

    partial void OnSelectedHostOptionChanged(HostOptionItem? value)
    {
        if (value != null && value.Id != SelectedHostId)
        {
            UpdateHostView(value.Id);
        }
    }

    public void SelectHost(string hostId)
    {
        var opt = HostOptions.FirstOrDefault(o => o.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));
        if (opt != null)
        {
            SelectedHostOption = opt;
        }
        else
        {
            UpdateHostView(hostId);
        }
    }

    private void UpdateHostView(string hostId)
    {
        SelectedHostId = hostId;
        var allNodes = _telemetryProvider.GetFleetNodes();

        if (hostId.Equals("aggregated", StringComparison.OrdinalIgnoreCase))
        {
            IsAggregated = true;
            HostTitle = "Cluster Fabric (All 6 Hosts)";
            RoleBadge = "Aggregated Mesh";
            IsBaremetal = false;

            CpuSpec = "AMD EPYC + Xeon Heterogeneous";
            ThreadsSpec = "1,904 Total Logical Cores (Partitioned)";
            RamSpec = "1,264 GB Total Cluster RAM";
            RamUsedSpec = "498 GB Used (39.4% Pool)";
            TwampUpText = "↑ 1.84ms (P95)";
            TwampDownText = "↓ 3.12ms (P95)";
            TwampAsymText = "Cluster Transit Δ: 1.28ms";

            MetricsTab.UpdateForNode("aggregated", null, allNodes);
            ProcessesTab.SetTargetHost("all");
            return;
        }

        IsAggregated = false;
        var node = allNodes.FirstOrDefault(n => n.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase)) ?? allNodes[0];

        HostTitle = node.Id;
        IsBaremetal = node.Role == "baremetal";
        RoleBadge = IsBaremetal ? _lexiconService["baremetalBadge"] : _lexiconService["vmBadge"];

        CpuSpec = node.CpuModel;
        ThreadsSpec = $"{node.Cores} Logical Threads ({node.Cores / 2}C/{node.Cores}T)";
        RamSpec = $"{node.RamTotal} DDR5 ECC";
        RamUsedSpec = $"{node.RamUsedPct:F1}% Used (Active Allocation)";

        TwampUpText = $"↑ {node.Twamp.ForwardMs:F2}ms";
        TwampDownText = $"↓ {node.Twamp.ReverseMs:F2}ms";
        TwampAsymText = $"Path Asymmetry Δ: {node.Twamp.AsymmetryMs:F2}ms";

        MetricsTab.UpdateForNode(node.Id, node, allNodes);
        ProcessesTab.SetTargetHost(node.Id);
    }

    [RelayCommand]
    public void SwitchTab(string tab)
    {
        ActiveTab = tab;
    }

    [RelayCommand]
    public void BackToFleet()
    {
        BackToFleetRequested?.Invoke();
    }
}

