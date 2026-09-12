using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class MetricGraphViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private long[] _timestamps = Array.Empty<long>();
    [ObservableProperty] private long _windowStart;
    [ObservableProperty] private long _windowEnd;
    [ObservableProperty] private double[] _values = Array.Empty<double>();
    [ObservableProperty] private string[] _labels = Array.Empty<string>();
    [ObservableProperty] private string _status = "Waiting for measurements";

    public ObservableCollection<ChartSeriesModel> Series { get; } = new();

    public string Metric => Series.FirstOrDefault()?.Metric ?? Title;
    public bool IsMerged => Series.Count > 1;
    public bool IsRateOfChange => Series.FirstOrDefault()?.IsRateOfChange ?? false;

    public MetricGraphViewModel(string metric, string? colorHex = null)
    {
        Title = metric;
        string color = colorHex ?? GraphLayoutStore.GetDefaultColor(metric);
        Series.Add(new ChartSeriesModel(metric, metric, color));
        Series.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsMerged));
            OnPropertyChanged(nameof(Metric));
            OnPropertyChanged(nameof(IsRateOfChange));
        };
    }

    public MetricGraphViewModel(string title, IEnumerable<ChartSeriesModel> series)
    {
        Title = title;
        foreach (var s in series) Series.Add(s);
        Series.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsMerged));
            OnPropertyChanged(nameof(Metric));
            OnPropertyChanged(nameof(IsRateOfChange));
        };
    }
}

public partial class HostMetricsTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider? _provider;
    private readonly GraphLayoutStore? _layoutStore;
    private readonly GraphPresetStore _presetStore;
    private CancellationTokenSource? _queryCts;
    private DateTime _lastRefresh;
    private volatile bool _isQueryRunning;
    private bool _hasLoadedHistory;
    private long _latestSeenTimestampNano;

    [ObservableProperty] private string _selectedScope = "5m";
    [ObservableProperty] private bool _isCustomScopeModalOpen;
    [ObservableProperty] private bool _isCustomizationModalOpen;
    [ObservableProperty] private bool _isSavePresetModalOpen;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private string _newPresetDescription = "";
    [ObservableProperty] private string _presetErrorMessage = "";
    [ObservableProperty] private GraphPreset? _selectedPreset;
    [ObservableProperty] private DateTimeOffset? _customStartDate = DateTimeOffset.Now.AddHours(-1);
    [ObservableProperty] private TimeSpan? _customStartTime = DateTime.Now.AddHours(-1).TimeOfDay;
    [ObservableProperty] private DateTimeOffset? _customEndDate = DateTimeOffset.Now;
    [ObservableProperty] private TimeSpan? _customEndTime = DateTime.Now.TimeOfDay;
    [ObservableProperty] private string _scopeError = "";
    [ObservableProperty] private double[] _forwardSeries = Array.Empty<double>();
    [ObservableProperty] private double[] _reverseSeries = Array.Empty<double>();
    [ObservableProperty] private double[] _asymmetrySeries = Array.Empty<double>();
    [ObservableProperty] private string[] _timeLabels = Array.Empty<string>();
    [ObservableProperty] private bool _isAggregatedMode;
    [ObservableProperty] private bool _viewCoreMatrix = true;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private float[] _coreLoads = Array.Empty<float>();
    [ObservableProperty] private IReadOnlyList<FleetNodeModel> _clusterNodes = Array.Empty<FleetNodeModel>();
    [ObservableProperty] private string _targetHostId = "";
    [ObservableProperty] private string _selectedMetric = "cpu.total";
    [ObservableProperty] private bool _isAddMetricRateOfChange;

    private static readonly string[] BaseMetrics = new[]
    {
        "cpu.total", "cpu.user", "cpu.system", "cpu.iowait",
        "memory.used", "memory.available", "memory.total", "memory.swap_free", "memory.zram_ratio",
        "power.battery_pct", "power.rate_watts",
        "twamp.rtt", "twamp.forward", "twamp.reverse",
        "disk.io.read_bytes", "disk.io.write_bytes", "disk.io.read_ops", "disk.io.write_ops"
    };

    public ObservableCollection<string> AvailableMetrics { get; } = new();
    public ObservableCollection<MetricGraphGroupViewModel> Groups { get; } = new();
    public ObservableCollection<MetricGraphViewModel> Graphs { get; } = new();
    public ObservableCollection<GraphPreset> AvailablePresets { get; } = new();

    public bool IsScope1m => SelectedScope == "1m";
    public bool IsScope5m => SelectedScope == "5m";
    public bool IsScope30m => SelectedScope == "30m";
    public bool IsScope2h => SelectedScope == "2h";
    public bool IsScope6h => SelectedScope == "6h";
    public bool IsScope12h => SelectedScope == "12h";
    public bool IsScope24h => SelectedScope == "24h";
    public bool IsScopeCustom => SelectedScope == "custom";

    public bool CanDeleteSelectedPreset => SelectedPreset != null && !SelectedPreset.IsBuiltIn;

    partial void OnSelectedScopeChanged(string value)
    {
        foreach (string p in new[] { nameof(IsScope1m), nameof(IsScope5m), nameof(IsScope30m), nameof(IsScope2h), nameof(IsScope6h), nameof(IsScope12h), nameof(IsScope24h), nameof(IsScopeCustom) })
            OnPropertyChanged(p);
    }

    partial void OnSelectedPresetChanged(GraphPreset? value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedPreset));
    }

    public HostMetricsTabViewModel(
        ITelemetryDataProvider? provider = null,
        GraphLayoutStore? layoutStore = null,
        GraphPresetStore? presetStore = null)
    {
        _provider = provider;
        _layoutStore = layoutStore;
        _presetStore = presetStore ?? new GraphPresetStore();
        PopulateAvailableMetrics(null, Array.Empty<FleetNodeModel>());
        LoadPresets();
        LoadGraphsForNode(TargetHostId);
    }

    public void LoadPresets()
    {
        AvailablePresets.Clear();
        foreach (var p in _presetStore.LoadAllPresets())
        {
            AvailablePresets.Add(p);
        }
        SelectedPreset = AvailablePresets.FirstOrDefault();
    }

    private void SyncGraphsFromGroups()
    {
        Graphs.Clear();
        foreach (var grp in Groups)
        {
            foreach (var g in grp.Graphs)
            {
                Graphs.Add(g);
            }
        }
    }

    public void LoadGraphsForNode(string hostId)
    {
        Groups.Clear();
        Graphs.Clear();
        string key = string.IsNullOrEmpty(hostId) ? "aggregated" : hostId;
        var loadedGroups = _layoutStore?.LoadGroupConfigs(key);
        if (loadedGroups != null && loadedGroups.Count > 0)
        {
            foreach (var groupCfg in loadedGroups)
            {
                var groupVm = new MetricGraphGroupViewModel(groupCfg.Title);
                foreach (var cfg in groupCfg.Graphs)
                {
                    var seriesList = new List<ChartSeriesModel>();
                    foreach (var sc in cfg.Series)
                    {
                        if (!AvailableMetrics.Contains(sc.Metric)) AvailableMetrics.Add(sc.Metric);
                        var s = new ChartSeriesModel(sc.Metric, sc.Label, sc.ColorHex)
                        {
                            IsRateOfChange = sc.IsRateOfChange
                        };
                        s.ConfigurationChanged += SaveLayout;
                        seriesList.Add(s);
                    }
                    if (seriesList.Count > 0)
                    {
                        groupVm.Graphs.Add(new MetricGraphViewModel(cfg.Title, seriesList));
                    }
                }
                if (groupVm.Graphs.Count > 0)
                {
                    Groups.Add(groupVm);
                }
            }
        }

        if (Groups.Count == 0)
        {
            var s1 = new ChartSeriesModel("cpu.total", "cpu.total", "#06B6D4");
            s1.ConfigurationChanged += SaveLayout;
            var g1 = new MetricGraphViewModel("cpu.total", new[] { s1 });
            Groups.Add(new MetricGraphGroupViewModel(g1));

            var s2 = new ChartSeriesModel("memory.used", "memory.used", "#10B981");
            s2.ConfigurationChanged += SaveLayout;
            var g2 = new MetricGraphViewModel("memory.used", new[] { s2 });
            Groups.Add(new MetricGraphGroupViewModel(g2));
        }

        SyncGraphsFromGroups();
    }

    private void PopulateAvailableMetrics(FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        AvailableMetrics.Clear();
        foreach (var m in BaseMetrics) AvailableMetrics.Add(m);

        var ifaces = (IsAggregatedMode ? allNodes.SelectMany(n => n.Interfaces) : (node?.Interfaces ?? Array.Empty<MADTOM.Plugins.Telemetry.Proto.V1.NicMetric>()))
            .Select(i => i.Name)
            .Distinct();

        foreach (var name in ifaces)
        {
            foreach (string suffix in new[] { "rx_bytes", "tx_bytes" })
            {
                AvailableMetrics.Add($"nic.{name}.{suffix}");
            }
        }

        if (!AvailableMetrics.Contains(SelectedMetric))
        {
            SelectedMetric = "cpu.total";
        }
    }

    [RelayCommand]
    public void OpenCustomizationModal() => IsCustomizationModalOpen = true;

    [RelayCommand]
    public void CloseCustomizationModal() => IsCustomizationModalOpen = false;

    public MetricGraphGroupViewModel? FindGroupForGraph(MetricGraphViewModel graph)
    {
        return Groups.FirstOrDefault(g => g.Graphs.Contains(graph));
    }

    [RelayCommand]
    public void AddGraph()
    {
        if (string.IsNullOrWhiteSpace(SelectedMetric)) return;
        bool isRate = IsAddMetricRateOfChange;
        string label = isRate ? $"{SelectedMetric} (rate/s)" : SelectedMetric;
        string title = label;
        if (Graphs.Any(g => g.Series.Count == 1 && g.Series[0].Metric == SelectedMetric && g.Series[0].IsRateOfChange == isRate)) return;

        var series = new ChartSeriesModel(SelectedMetric, label, GraphLayoutStore.GetDefaultColor(SelectedMetric))
        {
            IsRateOfChange = isRate
        };
        series.ConfigurationChanged += SaveLayout;
        var newGraph = new MetricGraphViewModel(title, new[] { series });
        Groups.Add(new MetricGraphGroupViewModel(newGraph));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void RemoveGraph(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group != null)
        {
            group.Graphs.Remove(graph);
            if (group.Graphs.Count == 0)
            {
                Groups.Remove(group);
            }
        }
        SyncGraphsFromGroups();
        SaveLayout();
    }

    [RelayCommand]
    public void MergeWithNext(MetricGraphViewModel graph)
    {
        int idx = Graphs.IndexOf(graph);
        if (idx < 0) return;
        MetricGraphViewModel? target = null;
        MetricGraphViewModel? source = null;
        if (idx < Graphs.Count - 1)
        {
            target = graph;
            source = Graphs[idx + 1];
        }
        else if (idx > 0)
        {
            target = Graphs[idx - 1];
            source = graph;
        }
        if (target == null || source == null || target == source) return;

        foreach (var s in source.Series.ToList())
        {
            if (!target.Series.Any(existing => existing.Metric == s.Metric))
            {
                s.ConfigurationChanged += SaveLayout;
                target.Series.Add(s);
            }
        }
        target.Title = $"{target.Series[0].Label} + {target.Series.Count - 1} more";
        
        var sourceGroup = FindGroupForGraph(source);
        if (sourceGroup != null)
        {
            sourceGroup.Graphs.Remove(source);
            if (sourceGroup.Graphs.Count == 0)
            {
                Groups.Remove(sourceGroup);
            }
        }
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void SplitGraph(MetricGraphViewModel graph)
    {
        if (graph.Series.Count <= 1) return;
        var group = FindGroupForGraph(graph);
        var toSplit = graph.Series.Skip(1).ToList();
        while (graph.Series.Count > 1)
        {
            graph.Series.RemoveAt(graph.Series.Count - 1);
        }
        graph.Title = graph.Series[0].Label;

        foreach (var s in toSplit)
        {
            s.ConfigurationChanged += SaveLayout;
            var newGraph = new MetricGraphViewModel(s.Label, new[] { s });
            if (group != null)
            {
                int insertIdx = group.Graphs.IndexOf(graph) + 1;
                group.Graphs.Insert(insertIdx, newGraph);
            }
            else
            {
                Groups.Add(new MetricGraphGroupViewModel(newGraph));
            }
        }
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void RemoveSeries(ChartSeriesModel series)
    {
        var parentGraph = Graphs.FirstOrDefault(g => g.Series.Contains(series));
        if (parentGraph == null) return;
        parentGraph.Series.Remove(series);
        if (parentGraph.Series.Count == 0)
        {
            RemoveGraph(parentGraph);
        }
        else
        {
            parentGraph.Title = parentGraph.Series.Count == 1 ? parentGraph.Series[0].Label : $"{parentGraph.Series[0].Label} + {parentGraph.Series.Count - 1} more";
            SaveLayout();
            _ = RefreshHistoryAsync();
        }
    }

    [RelayCommand]
    public void MoveGraphLeft(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null) return;
        int idx = group.Graphs.IndexOf(graph);
        if (idx > 0)
        {
            group.Graphs.Move(idx, idx - 1);
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void MoveGraphRight(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null) return;
        int idx = group.Graphs.IndexOf(graph);
        if (idx >= 0 && idx < group.Graphs.Count - 1)
        {
            group.Graphs.Move(idx, idx + 1);
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void MoveGraphUp(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null) return;
        int grpIdx = Groups.IndexOf(group);
        if (grpIdx > 0)
        {
            if (group.Graphs.Count == 1)
            {
                Groups.Move(grpIdx, grpIdx - 1);
            }
            else
            {
                group.Graphs.Remove(graph);
                Groups[grpIdx - 1].Graphs.Add(graph);
            }
            SyncGraphsFromGroups();
            SaveLayout();
        }
        else if (group.Graphs.Count > 1)
        {
            group.Graphs.Remove(graph);
            Groups.Insert(0, new MetricGraphGroupViewModel(graph));
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void MoveGraphDown(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null) return;
        int grpIdx = Groups.IndexOf(group);
        if (grpIdx >= 0 && grpIdx < Groups.Count - 1)
        {
            if (group.Graphs.Count == 1)
            {
                Groups.Move(grpIdx, grpIdx + 1);
            }
            else
            {
                group.Graphs.Remove(graph);
                Groups[grpIdx + 1].Graphs.Insert(0, graph);
            }
            SyncGraphsFromGroups();
            SaveLayout();
        }
        else if (group.Graphs.Count > 1)
        {
            group.Graphs.Remove(graph);
            Groups.Add(new MetricGraphGroupViewModel(graph));
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void SeparateGraphToNewRow(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null || group.Graphs.Count <= 1) return;
        int grpIdx = Groups.IndexOf(group);
        group.Graphs.Remove(graph);
        Groups.Insert(grpIdx + 1, new MetricGraphGroupViewModel(graph));
        SyncGraphsFromGroups();
        SaveLayout();
    }

    [RelayCommand]
    public void CombineWithNextRow(MetricGraphViewModel graph)
    {
        var group = FindGroupForGraph(graph);
        if (group == null) return;
        CombineGroupWithNext(group);
    }

    [RelayCommand]
    public void CombineGroupWithNext(MetricGraphGroupViewModel group)
    {
        int grpIdx = Groups.IndexOf(group);
        if (grpIdx < 0 || grpIdx >= Groups.Count - 1) return;
        var nextGroup = Groups[grpIdx + 1];
        var toMove = nextGroup.Graphs.ToList();
        foreach (var g in toMove)
        {
            nextGroup.Graphs.Remove(g);
            group.Graphs.Add(g);
        }
        Groups.Remove(nextGroup);
        SyncGraphsFromGroups();
        SaveLayout();
    }

    [RelayCommand]
    public void MoveGroupUp(MetricGraphGroupViewModel group)
    {
        int idx = Groups.IndexOf(group);
        if (idx > 0)
        {
            Groups.Move(idx, idx - 1);
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void MoveGroupDown(MetricGraphGroupViewModel group)
    {
        int idx = Groups.IndexOf(group);
        if (idx >= 0 && idx < Groups.Count - 1)
        {
            Groups.Move(idx, idx + 1);
            SyncGraphsFromGroups();
            SaveLayout();
        }
    }

    [RelayCommand]
    public void QuickMergeCpu()
    {
        var cpuMetrics = new HashSet<string>(new[] { "cpu.total", "cpu.user", "cpu.system", "cpu.iowait" });
        RemoveGraphsMatching(g => g.Series.All(s => cpuMetrics.Contains(s.Metric)));

        var series = new[]
        {
            new ChartSeriesModel("cpu.total", "Total", "#06B6D4"),
            new ChartSeriesModel("cpu.user", "User", "#3B82F6"),
            new ChartSeriesModel("cpu.system", "System", "#8B5CF6"),
            new ChartSeriesModel("cpu.iowait", "IOWait", "#F59E0B")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("CPU Breakdown", series);
        Groups.Insert(0, new MetricGraphGroupViewModel(merged));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeMemory()
    {
        var memMetrics = new HashSet<string>(new[] { "memory.used", "memory.available" });
        RemoveGraphsMatching(g => g.Series.All(s => memMetrics.Contains(s.Metric)));

        var series = new[]
        {
            new ChartSeriesModel("memory.used", "Used", "#10B981"),
            new ChartSeriesModel("memory.available", "Available", "#34D399")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("Memory Breakdown", series);
        Groups.Add(new MetricGraphGroupViewModel(merged));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeTwamp()
    {
        var twampMetrics = new HashSet<string>(new[] { "twamp.rtt", "twamp.forward", "twamp.reverse" });
        RemoveGraphsMatching(g => g.Series.All(s => twampMetrics.Contains(s.Metric)));

        var series = new[]
        {
            new ChartSeriesModel("twamp.rtt", "RTT", "#06B6D4"),
            new ChartSeriesModel("twamp.forward", "Forward (Up)", "#6366F1"),
            new ChartSeriesModel("twamp.reverse", "Reverse (Down)", "#A855F7")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("TWAMP Latency Breakdown", series);
        Groups.Add(new MetricGraphGroupViewModel(merged));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    private void RemoveGraphsMatching(Func<MetricGraphViewModel, bool> predicate)
    {
        foreach (var grp in Groups.ToList())
        {
            var matches = grp.Graphs.Where(predicate).ToList();
            foreach (var m in matches) grp.Graphs.Remove(m);
            if (grp.Graphs.Count == 0) Groups.Remove(grp);
        }
    }

    [RelayCommand]
    public void ResetToDefaultGraphs()
    {
        Groups.Clear();
        var s1 = new ChartSeriesModel("cpu.total", "cpu.total", "#06B6D4");
        s1.ConfigurationChanged += SaveLayout;
        var g1 = new MetricGraphViewModel("cpu.total", new[] { s1 });
        Groups.Add(new MetricGraphGroupViewModel(g1));

        var s2 = new ChartSeriesModel("memory.used", "memory.used", "#10B981");
        s2.ConfigurationChanged += SaveLayout;
        var g2 = new MetricGraphViewModel("memory.used", new[] { s2 });
        Groups.Add(new MetricGraphGroupViewModel(g2));

        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void ApplyPreset(GraphPreset? preset = null)
    {
        preset ??= SelectedPreset;
        if (preset == null || preset.Groups == null || preset.Groups.Count == 0) return;

        Groups.Clear();
        foreach (var groupCfg in preset.Groups)
        {
            var groupVm = new MetricGraphGroupViewModel(groupCfg.Title);
            foreach (var cfg in groupCfg.Graphs)
            {
                var seriesList = new List<ChartSeriesModel>();
                foreach (var sc in cfg.Series)
                {
                    if (!AvailableMetrics.Contains(sc.Metric)) AvailableMetrics.Add(sc.Metric);
                    var s = new ChartSeriesModel(sc.Metric, sc.Label, sc.ColorHex)
                    {
                        IsRateOfChange = sc.IsRateOfChange
                    };
                    s.ConfigurationChanged += SaveLayout;
                    seriesList.Add(s);
                }
                if (seriesList.Count > 0)
                {
                    groupVm.Graphs.Add(new MetricGraphViewModel(cfg.Title, seriesList));
                }
            }
            if (groupVm.Graphs.Count > 0)
            {
                Groups.Add(groupVm);
            }
        }
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void OpenSavePresetModal()
    {
        NewPresetName = "";
        NewPresetDescription = "";
        PresetErrorMessage = "";
        IsSavePresetModalOpen = true;
    }

    [RelayCommand]
    public void CloseSavePresetModal()
    {
        IsSavePresetModalOpen = false;
    }

    [RelayCommand]
    public void ConfirmSavePreset()
    {
        if (string.IsNullOrWhiteSpace(NewPresetName))
        {
            PresetErrorMessage = "Preset name cannot be empty.";
            return;
        }

        var groupConfigs = ToGroupConfigs();
        _presetStore.SaveUserPreset(NewPresetName, groupConfigs, NewPresetDescription);
        IsSavePresetModalOpen = false;
        LoadPresets();
        SelectedPreset = AvailablePresets.FirstOrDefault(p => p.Name.Equals(NewPresetName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    public void DeletePreset(GraphPreset? preset)
    {
        preset ??= SelectedPreset;
        if (preset == null || preset.IsBuiltIn) return;
        _presetStore.DeleteUserPreset(preset.Id);
        LoadPresets();
    }

    public List<GraphGroupConfig> ToGroupConfigs()
    {
        return Groups.Select(grp => new GraphGroupConfig
        {
            Title = grp.Title,
            Graphs = grp.Graphs.Select(g => new GraphItemConfig
            {
                Title = g.Title,
                Series = g.Series.Select(s => new GraphSeriesConfig
                {
                    Metric = s.Metric,
                    Label = s.Label,
                    ColorHex = s.ColorHex,
                    IsRateOfChange = s.IsRateOfChange
                }).ToList()
            }).ToList()
        }).ToList();
    }

    private void SaveLayout()
    {
        try
        {
            string key = string.IsNullOrEmpty(TargetHostId) ? "aggregated" : TargetHostId;
            var groupConfigs = ToGroupConfigs();
            _layoutStore?.SaveGroupConfigs(key, groupConfigs);
        }
        catch (Exception ex)
        {
            ScopeError = $"Could not save graph layout: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SetScope(string scope)
    {
        SelectedScope = scope;
        if (scope == "custom") { IsCustomScopeModalOpen = true; return; }
        _hasLoadedHistory = false;
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void ApplyCustomScope()
    {
        if (!TryRange(out _, out _)) { ScopeError = "Choose a start time before the end time."; return; }
        ScopeError = ""; IsCustomScopeModalOpen = false; _hasLoadedHistory = false; _ = RefreshHistoryAsync();
    }

    [RelayCommand] public void CancelCustomScope() { IsCustomScopeModalOpen = false; SetScope("5m"); }

    private bool TryRange(out DateTime start, out DateTime end)
    {
        end = DateTime.UtcNow;
        // Anchor window end to the latest known sample timestamp for clock-skew resilience.
        // If the daemon's clock is ahead of the UI machine, the stored data points would fall
        // outside [start, UtcNow] — extending to the latest seen timestamp covers them.
        if (_latestSeenTimestampNano > 0)
        {
            var latestSample = DateTimeOffset.FromUnixTimeMilliseconds(_latestSeenTimestampNano / 1_000_000).UtcDateTime;
            if (latestSample > end) end = latestSample;
        }
        start = end.AddMinutes(SelectedScope switch { "1m" => -1, "30m" => -30, "2h" => -120, "6h" => -360, "12h" => -720, "24h" => -1440, _ => -5 });
        if (!IsScopeCustom) return true;
        if (CustomStartDate == null || CustomEndDate == null || CustomStartTime == null || CustomEndTime == null) return false;
        start = DateTime.SpecifyKind(CustomStartDate.Value.Date + CustomStartTime.Value, DateTimeKind.Local).ToUniversalTime();
        end = DateTime.SpecifyKind(CustomEndDate.Value.Date + CustomEndTime.Value, DateTimeKind.Local).ToUniversalTime();
        return start < end;
    }

    public void UpdateForNode(string hostId, FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        bool changed = TargetHostId != hostId;
        if (changed)
        {
            if (!string.IsNullOrEmpty(TargetHostId))
            {
                SaveLayout();
            }

            TargetHostId = hostId;
            ClusterNodes = allNodes;
            IsAggregatedMode = hostId == "aggregated";

            PopulateAvailableMetrics(node, allNodes);
            LoadGraphsForNode(hostId);

            _hasLoadedHistory = false;
            _latestSeenTimestampNano = 0;

            if (_provider != null)
                _ = RefreshHistoryAsync();
            else
                _hasLoadedHistory = true;
        }

        ClusterNodes = allNodes;
        IsAggregatedMode = hostId == "aggregated";
        ThreadCount = IsAggregatedMode ? allNodes.Sum(n => n.Cores) : node?.Cores ?? 0;
        ViewCoreMatrix = node?.ViewCpuMatrix ?? true;
        foreach (var graph in Graphs) graph.IsVisible = node == null ||
            (graph.Series.Any(s => s.Metric.StartsWith("power.")) ? node.ViewPowerBattery :
             graph.Series.Any(s => s.Metric.StartsWith("nic.")) ? node.ViewNetworkCounters :
             graph.Series.Any(s => s.Metric.Contains("swap") || s.Metric.Contains("zram")) ? node.ViewSwapZram : true);
        CoreLoads = IsAggregatedMode ? allNodes.SelectMany(n => n.CoreLoads).ToArray() : node?.CoreLoads ?? Array.Empty<float>();

        var currentIfaces = (IsAggregatedMode ? allNodes.SelectMany(n => n.Interfaces) : (node?.Interfaces ?? Array.Empty<MADTOM.Plugins.Telemetry.Proto.V1.NicMetric>()));
        foreach (var nic in currentIfaces)
        {
            foreach (string suffix in new[] { "rx_bytes", "tx_bytes" })
            {
                string metric = $"nic.{nic.Name}.{suffix}";
                if (!AvailableMetrics.Contains(metric)) AvailableMetrics.Add(metric);
            }
        }

        if (node != null)
            _latestSeenTimestampNano = Math.Max(_latestSeenTimestampNano, node.TimestampUnixNano);

        if (!_hasLoadedHistory && _provider != null && !_isQueryRunning)
        {
            _ = RefreshHistoryAsync();
        }

        long tsNano = node?.TimestampUnixNano ?? 0;
        if (tsNano > 0)
        {
            PushLiveSampleToGraphs(tsNano, node, allNodes);
        }
    }

    private void PushLiveSampleToGraphs(long timestampNano, FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        if (timestampNano <= 0 || IsScopeCustom) return;

        long windowSpanNano = SelectedScope switch
        {
            "1m"  => 60L * 1_000_000_000L,
            "5m"  => 300L * 1_000_000_000L,
            "30m" => 1800L * 1_000_000_000L,
            "2h"  => 7200L * 1_000_000_000L,
            "6h"  => 21600L * 1_000_000_000L,
            "12h" => 43200L * 1_000_000_000L,
            "24h" => 86400L * 1_000_000_000L,
            _     => 300L * 1_000_000_000L
        };

        long windowEndNano = timestampNano;
        long windowStartNano = windowEndNano - windowSpanNano;

        foreach (var graph in Graphs)
        {
            if (!graph.IsVisible) continue;

            graph.WindowStart = windowStartNano;
            graph.WindowEnd = windowEndNano;

            int maxPoints = 0;
            foreach (var series in graph.Series)
            {
                double? sampleVal = null;
                if (IsAggregatedMode)
                {
                    var vals = allNodes.Select(n => n.GetMetricValue(series.Metric))
                                       .Where(v => v.HasValue)
                                       .Select(v => v!.Value)
                                       .ToList();
                    if (vals.Count > 0)
                    {
                        sampleVal = series.Metric.StartsWith("twamp.") || series.Metric.StartsWith("cpu.") || series.Metric.EndsWith("_pct") || series.Metric.EndsWith("_ratio")
                            ? vals.Average()
                            : vals.Sum();
                    }
                }
                else if (node != null)
                {
                    sampleVal = node.GetMetricValue(series.Metric);
                }

                if (sampleVal.HasValue)
                {
                    double currentRaw = sampleVal.Value;
                    double plotVal = currentRaw;

                    if (series.IsRateOfChange)
                    {
                        if (series.PreviousRawSampleValue.HasValue && series.PreviousRawSampleTimestampNano > 0)
                        {
                            double dt = (timestampNano - series.PreviousRawSampleTimestampNano) / 1e9;
                            if (dt <= 0.001) dt = 1.0;
                            double delta = currentRaw - series.PreviousRawSampleValue.Value;
                            if (delta < 0 && series.Metric.Contains("bytes")) delta = 0;
                            plotVal = delta / dt;
                        }
                        else
                        {
                            series.PreviousRawSampleValue = currentRaw;
                            series.PreviousRawSampleTimestampNano = timestampNano;
                            maxPoints = Math.Max(maxPoints, series.Values.Length);
                            continue;
                        }
                        series.PreviousRawSampleValue = currentRaw;
                        series.PreviousRawSampleTimestampNano = timestampNano;
                    }

                    var currentTs = series.Timestamps;
                    var currentVals = series.Values;

                    long tsSec = timestampNano / 1_000_000_000L;
                    int startIdx = 0;
                    long pruneThreshold = windowStartNano - 5_000_000_000L; // keep small grace margin to avoid gaps at edge
                    while (startIdx < currentTs.Length && currentTs[startIdx] < pruneThreshold)
                        startIdx++;

                    bool replaceLast = currentTs.Length > 0 && (currentTs[^1] / 1_000_000_000L) == tsSec;

                    int newCount = (currentTs.Length - startIdx) + (replaceLast ? 0 : 1);
                    var newTs = new long[newCount];
                    var newVals = new double[newCount];

                    int copyLen = currentTs.Length - startIdx - (replaceLast ? 1 : 0);
                    if (copyLen > 0)
                    {
                        Array.Copy(currentTs, startIdx, newTs, 0, copyLen);
                        Array.Copy(currentVals, startIdx, newVals, 0, copyLen);
                    }

                    newTs[^1] = timestampNano;
                    newVals[^1] = plotVal;

                    series.Timestamps = newTs;
                    series.Values = newVals;
                    series.LatestValue = plotVal;
                    maxPoints = Math.Max(maxPoints, newCount);
                }
                else
                {
                    maxPoints = Math.Max(maxPoints, series.Values.Length);
                }
            }

            var primary = graph.Series.FirstOrDefault();
            if (primary != null)
            {
                graph.Timestamps = primary.Timestamps;
                graph.Values = primary.Values;
                graph.Labels = primary.Timestamps.Select(t => DateTimeOffset.FromUnixTimeSeconds(t / 1_000_000_000L).ToLocalTime().ToString("MM-dd HH:mm:ss")).ToArray();
            }

            if (maxPoints > 0)
            {
                var startDt = DateTimeOffset.FromUnixTimeMilliseconds(windowStartNano / 1_000_000L).ToLocalTime();
                var endDt = DateTimeOffset.FromUnixTimeMilliseconds(windowEndNano / 1_000_000L).ToLocalTime();
                graph.Status = $"{maxPoints} points · {startDt:HH:mm:ss} – {endDt:HH:mm:ss}";
            }
        }
    }

    public async Task RefreshHistoryAsync()
    {
        _queryCts?.Cancel(); _queryCts?.Dispose();
        var cts = _queryCts = new CancellationTokenSource();
        _lastRefresh = DateTime.UtcNow;
        if (_provider == null || !TryRange(out var start, out var end)) return;
        _isQueryRunning = true;
        var ids = IsAggregatedMode ? ClusterNodes.Select(n => n.Id).ToArray() : new[] { TargetHostId };
        try
        {
            await Task.WhenAll(Graphs.ToArray().Select(async graph =>
            {
                graph.Status = "Loading…";
                long windowStartNano = new DateTimeOffset(start).ToUnixTimeMilliseconds() * 1_000_000L;
                long windowEndNano = new DateTimeOffset(end).ToUnixTimeMilliseconds() * 1_000_000L;
                graph.WindowStart = windowStartNano;
                graph.WindowEnd = windowEndNano;

                int maxPoints = 0;
                foreach (var series in graph.Series.ToArray())
                {
                    var results = await Task.WhenAll(ids.Select(id => _provider.QueryHistoryAsync(id, series.Metric, start, end, cts.Token)));
                    if (cts.IsCancellationRequested) return;

                    var points = results.SelectMany(s => s.GroupBy(p => p.TimestampUnixNano / 1_000_000_000L).Select(g => g.OrderBy(p => p.TimestampUnixNano).Last()))
                                        .GroupBy(p => p.TimestampUnixNano / 1_000_000_000L)
                                        .OrderBy(g => g.Key)
                                        .ToArray();

                    series.Timestamps = points.Select(g => g.Key * 1_000_000_000L).ToArray();
                    series.Values = points.Select(g => series.Metric.StartsWith("twamp.") || series.Metric.StartsWith("cpu.") || series.Metric.EndsWith("_pct") || series.Metric.EndsWith("_ratio") ? g.Average(p => p.Value) : g.Sum(p => p.Value)).ToArray();

                    if (series.IsRateOfChange)
                    {
                        var rawTs = series.Timestamps;
                        var rawVals = series.Values;
                        if (rawTs.Length >= 2)
                        {
                            var rateTs = new long[rawTs.Length - 1];
                            var rateVals = new double[rawVals.Length - 1];
                            for (int i = 1; i < rawTs.Length; i++)
                            {
                                double dt = (rawTs[i] - rawTs[i - 1]) / 1e9;
                                if (dt <= 0.001) dt = 1.0;
                                double delta = rawVals[i] - rawVals[i - 1];
                                if (delta < 0 && series.Metric.Contains("bytes")) delta = 0;
                                rateTs[i - 1] = rawTs[i];
                                rateVals[i - 1] = delta / dt;
                            }
                            series.Timestamps = rateTs;
                            series.Values = rateVals;
                            series.PreviousRawSampleValue = rawVals[^1];
                            series.PreviousRawSampleTimestampNano = rawTs[^1];
                        }
                        else
                        {
                            series.Timestamps = Array.Empty<long>();
                            series.Values = Array.Empty<double>();
                        }
                    }

                    series.LatestValue = series.Values.LastOrDefault();
                    maxPoints = Math.Max(maxPoints, series.Values.Length);
                }

                var primary = graph.Series.FirstOrDefault();
                if (primary != null)
                {
                    graph.Timestamps = primary.Timestamps;
                    graph.Values = primary.Values;
                    graph.Labels = primary.Timestamps.Select(t => DateTimeOffset.FromUnixTimeSeconds(t / 1_000_000_000L).ToLocalTime().ToString("MM-dd HH:mm:ss")).ToArray();
                }
                else
                {
                    graph.Timestamps = Array.Empty<long>();
                    graph.Values = Array.Empty<double>();
                    graph.Labels = Array.Empty<string>();
                }

                if (maxPoints == 0)
                {
                    graph.Status = "No measurements in this time window";
                }
                else
                {
                    long latestNano = primary?.Timestamps.LastOrDefault() ?? 0;
                    if (latestNano > 0 && (windowEndNano - latestNano) > 60_000_000_000L)
                    {
                        var latestDt = DateTimeOffset.FromUnixTimeMilliseconds(latestNano / 1_000_000L).ToLocalTime();
                        graph.Status = $"{maxPoints} points · window {start.ToLocalTime():HH:mm:ss}–{end.ToLocalTime():HH:mm:ss} (data ends at {latestDt:HH:mm:ss})";
                    }
                    else
                    {
                        graph.Status = $"{maxPoints} points · {start.ToLocalTime():HH:mm:ss} – {end.ToLocalTime():HH:mm:ss}";
                    }
                }
            }));
            if (!cts.IsCancellationRequested)
            {
                _hasLoadedHistory = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) foreach (var graph in Graphs) graph.Status = $"Query failed: {ex.Message}"; }
        finally
        {
            _isQueryRunning = false;
        }
    }

    public void PushLiveSample(double fwd, double rev)
    {
        ForwardSeries = ForwardSeries.Append(fwd).TakeLast(1200).ToArray();
        ReverseSeries = ReverseSeries.Append(rev).TakeLast(1200).ToArray();
        AsymmetrySeries = AsymmetrySeries.Append(Math.Abs(fwd - rev)).TakeLast(1200).ToArray();
        TimeLabels = TimeLabels.Append(DateTime.Now.ToString("HH:mm:ss")).TakeLast(1200).ToArray();
    }
}
