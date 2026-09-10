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

public sealed partial class HostOptionItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    [ObservableProperty] private string _displayText = string.Empty;
    public override string ToString() => DisplayText;
}

public partial class HostDetailViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly ILexiconService _lexiconService;

    [ObservableProperty]
    private string _selectedHostId = "";

    [ObservableProperty]
    private HostOptionItem? _selectedHostOption;

    [ObservableProperty]
    private string _activeTab = "metrics"; // metrics, processes, logs, flight

    [ObservableProperty]
    private string _hostTitle = "";

    [ObservableProperty]
    private string _roleBadge = "";

    [ObservableProperty]
    private bool _isBaremetal = true;

    [ObservableProperty]
    private bool _isAggregated;

    [ObservableProperty]
    private string _cpuSpec = "";

    [ObservableProperty]
    private string _threadsSpec = "";

    [ObservableProperty]
    private string _ramSpec = "";

    [ObservableProperty]
    private string _ramUsedSpec = "";

    [ObservableProperty]
    private string _twampUpText = "";

    [ObservableProperty]
    private string _twampDownText = "";

    [ObservableProperty]
    private string _twampAsymText = "";

    public bool IsMetricsTab => ActiveTab == "metrics";
    public bool IsProcessesTab => ActiveTab == "processes";
    public bool IsLogsTab => ActiveTab == "logs";
    public bool IsFlightTab => ActiveTab == "flight";
    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsMetricsTab)); OnPropertyChanged(nameof(IsProcessesTab));
        OnPropertyChanged(nameof(IsLogsTab)); OnPropertyChanged(nameof(IsFlightTab));
    }
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
        UpdateHostView(SelectedHostOption?.Id ?? "");

        _telemetryProvider.NodeTelemetryUpdated += (_, node) =>
        {
            var option = HostOptions.FirstOrDefault(o => o.Id == node.Id);
            if (option == null) { option = new HostOptionItem { Id = node.Id }; HostOptions.Add(option); }
            option.DisplayText = $"{node.Id} ({node.Cores}T)";
            if (IsAggregated || SelectedHostId == node.Id) UpdateHostView(SelectedHostId);
        };
    }

    private void PopulateHostOptions()
    {
        HostOptions.Clear();
        HostOptions.Add(new HostOptionItem { Id = "aggregated", DisplayText = "Aggregated (All Hosts)" });

        foreach (var n in _telemetryProvider.GetFleetNodes())
        {
            string roleLabel = n.Role;
            HostOptions.Add(new HostOptionItem { Id = n.Id, DisplayText = $"{n.Id} ({roleLabel} - {n.Cores}T)" });
        }

        SelectedHostOption = HostOptions.FirstOrDefault(o => o.Id == "") ?? HostOptions.FirstOrDefault();
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
        if (opt == null)
        {
            var node = _telemetryProvider.GetNode(hostId) ?? _telemetryProvider.GetFleetNodes().FirstOrDefault(n => n.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));
            if (node != null)
            {
                string roleLabel = node.Role;
                opt = new HostOptionItem { Id = node.Id, DisplayText = $"{node.Id} ({roleLabel} - {node.Cores}T)" };
                HostOptions.Add(opt);
            }
        }

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
            HostTitle = $"Cluster ({allNodes.Count} Hosts)";
            RoleBadge = "Aggregated";
            IsBaremetal = false;
            CpuSpec = "All processors";
            ThreadsSpec = $"{allNodes.Sum(n => n.Cores)} Logical Threads";
            var total = allNodes.Sum(n => (double)n.MemoryTotalBytes);
            RamSpec = $"{total / 1073741824:F1} GB Total RAM";
            RamUsedSpec = total > 0 ? $"{allNodes.Sum(n => n.RamUsedPct * n.MemoryTotalBytes) / total:F1}% Used" : "Unavailable";
            TwampUpText = "Unavailable"; TwampDownText = ""; TwampAsymText = "No TWAMP probe configured";

            MetricsTab.UpdateForNode("aggregated", null, allNodes);
            ProcessesTab.SetTargetHost("all");
            FlightTab.SetTargetHost("all");
            return;
        }

        IsAggregated = false;
        var node = allNodes.FirstOrDefault(n => n.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));

        if (node == null)
        {
            HostTitle = hostId;
            IsBaremetal = false;
            RoleBadge = "Connecting...";
            CpuSpec = "Waiting for node metrics...";
            ThreadsSpec = "Probing telemetry stream...";
            RamSpec = "-";
            RamUsedSpec = "-";
            TwampUpText = "-";
            TwampDownText = "-";
            TwampAsymText = "-";
            MetricsTab.UpdateForNode(hostId, null, allNodes);
            ProcessesTab.SetTargetHost(hostId);
            FlightTab.SetTargetHost(hostId);
            return;
        }

        HostTitle = node.Id;
        IsBaremetal = node.Role == "baremetal";
        RoleBadge = $"{node.Role.ToUpperInvariant()} · {node.Status}";

        CpuSpec = node.CpuModel;
        ThreadsSpec = $"{node.Cores} Logical Threads";
        RamSpec = node.RamTotal;
        RamUsedSpec = $"{node.RamUsedPct:F1}% Used (Active Allocation)";

        TwampUpText = node.Twamp.DisplayText;
        TwampDownText = node.Twamp.OneWayAvailable ? $"↑ {node.Twamp.ForwardMs:F2} / ↓ {node.Twamp.ReverseMs:F2} ms" : "";
        TwampAsymText = node.Twamp.OneWayAvailable ? $"Asymmetry: {node.Twamp.AsymmetryMs:F2} ms" : node.Twamp.Available ? "One-way delay requires synchronized clocks" : node.Twamp.Error;

        MetricsTab.UpdateForNode(node.Id, node, allNodes);
        ProcessesTab.SetTargetHost(node.Id);
        FlightTab.SetTargetHost(node.Id);
    }

    [RelayCommand]
    public void SwitchTab(string tab)
    {
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenGraphSettings()
    {
        ActiveTab = "metrics";
        MetricsTab.IsCustomizationModalOpen = true;
    }

    [RelayCommand]
    public void BackToFleet()
    {
        BackToFleetRequested?.Invoke();
    }
}

