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

public sealed record PersonaOption(string Id, string DisplayName);

public partial class HeaderViewModel : ViewModelBase
{
    private readonly ILexiconService _lexiconService;
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly GlobalMetricsStore _metricsStore;
    private readonly IClusterAggregationService _aggregationService;

    public ObservableCollection<GlobalMetricItemViewModel> PinnedMetrics { get; } = new();

    [ObservableProperty]
    private ClusterTelemetrySummary _clusterSummary = new();

    [ObservableProperty]
    private string _currentPersona = "goose";

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isDetailPage;

    [ObservableProperty]
    private string _currentPageTitle = "";

    [ObservableProperty]
    private string _currentPageSubtitle = "";

    public event Action? NavigateBackRequested;

    [RelayCommand]
    public void NavigateBack()
    {
        NavigateBackRequested?.Invoke();
    }

    public IReadOnlyList<PersonaOption> AvailablePersonas { get; } = new List<PersonaOption>
    {
        new("goose", "🪿 Goose Farm"),
        new("standard", "🏢 Enterprise"),
        new("feline", "🐱 The Clowder")
    };

    [ObservableProperty]
    private PersonaOption _selectedPersona;

    public event Action? ToggleSidebarCollapseRequested;

    public HeaderViewModel(ILexiconService lexiconService, ITelemetryDataProvider telemetryProvider, GlobalMetricsStore? metricsStore = null, IClusterAggregationService? aggregationService = null)
    {
        _lexiconService = lexiconService;
        _telemetryProvider = telemetryProvider;
        _metricsStore = metricsStore ?? new GlobalMetricsStore();
        _aggregationService = aggregationService ?? new ClusterAggregationService(_telemetryProvider);

        ClusterSummary = _aggregationService.GetClusterSummary();
        RefreshPinnedMetrics();

        _aggregationService.AggregatedSnapshotAvailable += (_, snapshot) =>
        {
            void Apply()
            {
                ClusterSummary = snapshot.Summary;
                RefreshPinnedMetricsFromSnapshot(snapshot);
            }

            if (Avalonia.Threading.Dispatcher.UIThread != null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Apply, Avalonia.Threading.DispatcherPriority.Background);
            }
            else
            {
                Apply();
            }
        };

        _metricsStore.ConfigChanged += RefreshPinnedMetrics;

        CurrentPersona = _lexiconService.CurrentPack;

        _selectedPersona = AvailablePersonas.FirstOrDefault(p => p.Id.Equals(CurrentPersona, StringComparison.OrdinalIgnoreCase))
                           ?? AvailablePersonas[0];

        _lexiconService.LexiconChanged += (s, pack) =>
        {
            CurrentPersona = pack;
            var match = AvailablePersonas.FirstOrDefault(p => p.Id.Equals(pack, StringComparison.OrdinalIgnoreCase));
            if (match != null && match != SelectedPersona)
            {
                SelectedPersona = match;
            }
        };
    }

    public void RefreshPinnedMetrics()
    {
        var snapshot = _aggregationService.LatestSnapshot;
        ClusterSummary = snapshot.Summary;
        RefreshPinnedMetricsFromSnapshot(snapshot);
    }

    private void RefreshPinnedMetricsFromSnapshot(ClusterAggregationSnapshot snapshot)
    {
        var pinnedConfigs = _metricsStore.GetPinnedItems();

        // Synchronize PinnedMetrics items to match configs
        for (int i = 0; i < pinnedConfigs.Count; i++)
        {
            var cfg = pinnedConfigs[i];
            var def = GlobalMetricsStore.GetMetricDefinition(cfg.Key);
            if (def == null) continue;

            var existing = PinnedMetrics.FirstOrDefault(m => m.Key.Equals(cfg.Key, StringComparison.OrdinalIgnoreCase) && m.ModifierLabel.Equals(cfg.Modifier, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new GlobalMetricItemViewModel(def.Key, def.Name, def.Icon, def.Unit, def.ShortName);
                existing.ApplyModifier(cfg.Modifier);
                if (i < PinnedMetrics.Count)
                    PinnedMetrics.Insert(i, existing);
                else
                    PinnedMetrics.Add(existing);
            }
        }

        for (int i = PinnedMetrics.Count - 1; i >= 0; i--)
        {
            var m = PinnedMetrics[i];
            if (!pinnedConfigs.Any(cfg => cfg.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase) && cfg.Modifier.Equals(m.ModifierLabel, StringComparison.OrdinalIgnoreCase)))
            {
                PinnedMetrics.RemoveAt(i);
            }
        }

        // Compute live values from the unified aggregation pool
        foreach (var item in PinnedMetrics)
        {
            var (sum, avg, rate) = _aggregationService.GetMetricValues(item.Key);
            item.Update(sum, avg, rate, snapshot.ActiveNodeCount, item.ModifierLabel);
        }
    }

    private static (double Sum, double Avg) ComputeMetricSumAndAvg(string key, IReadOnlyList<FleetNodeModel> nodes, int count)
    {
        double sum = 0;
        switch (key.ToLowerInvariant())
        {
            case "network.ingress":
                sum = nodes.Sum(n => n.RxBytesPerSecond) * 8.0;
                break;
            case "network.egress":
                sum = nodes.Sum(n => n.TxBytesPerSecond) * 8.0;
                break;
            case "cpu.load":
                sum = nodes.Sum(n => n.CpuAvgPct);
                break;
            case "memory.bytes":
                sum = nodes.Sum(n => n.GetMetricValue("memory.used") ?? 0.0);
                break;
            case "disk.bytes.read":
                sum = nodes.Sum(n => n.GetMetricValue("disk.io.read_bytes") ?? 0.0);
                break;
            case "disk.bytes.write":
                sum = nodes.Sum(n => n.GetMetricValue("disk.io.write_bytes") ?? 0.0);
                break;
            case "disk.ops":
                sum = nodes.Sum(n => (n.GetMetricValue("disk.io.read_ops") ?? 0.0) + (n.GetMetricValue("disk.io.write_ops") ?? 0.0));
                break;
            case "twamp.rtt":
                var twampNodes = nodes.Where(n => n.Twamp.Available).ToList();
                double twampAvg = twampNodes.Count > 0 ? twampNodes.Average(n => n.Twamp.RttMs) : 0;
                double twampMax = twampNodes.Count > 0 ? twampNodes.Max(n => n.Twamp.RttMs) : 0;
                return (twampMax, twampAvg);
            default:
                sum = nodes.Sum(n => n.GetMetricValue(key) ?? 0.0);
                break;
        }

        double avg = count > 0 ? sum / count : 0;
        return (sum, avg);
    }

    partial void OnSelectedPersonaChanged(PersonaOption value)
    {
        if (value != null && !value.Id.Equals(_lexiconService.CurrentPack, StringComparison.OrdinalIgnoreCase))
        {
            SwitchPersona(value.Id);
        }
    }

    [RelayCommand]
    public void SwitchPersona(string persona)
    {
        _lexiconService.LoadLexicon(persona);
        CurrentPersona = persona;
    }

    [RelayCommand]
    public void ToggleSidebarCollapse()
    {
        ToggleSidebarCollapseRequested?.Invoke();
    }

    public event Action? OpenGlobalMetricsRequested;

    [RelayCommand]
    public void OpenGlobalMetrics()
    {
        OpenGlobalMetricsRequested?.Invoke();
    }
}

