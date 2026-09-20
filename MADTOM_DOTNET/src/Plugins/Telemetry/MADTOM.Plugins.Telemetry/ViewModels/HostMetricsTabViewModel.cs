using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Common;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels;

public partial class MetricGraphViewModel : ViewModelBase
{
    [ObservableProperty] private int _historyPointBudget = 2400;
    public event Action? HistoryResolutionChanged;
    partial void OnHistoryPointBudgetChanged(int value) => HistoryResolutionChanged?.Invoke();
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
    private readonly Dictionary<ChartSeriesModel, (long[] Times, double[] Values)> _displaySources = new();
    private readonly HashSet<MetricGraphViewModel> _resolutionGraphs = new();
    private CancellationTokenSource? _resolutionRefreshCts;

    private async void OnHistoryResolutionChanged()
    {
        _resolutionRefreshCts?.Cancel();
        var cts = _resolutionRefreshCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            await RefreshHistoryAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_resolutionRefreshCts, cts)) _resolutionRefreshCts = null;
            cts.Dispose();
        }
    }

    private void TrackGraphResolutions()
    {
        foreach (var graph in _resolutionGraphs.Where(g => !Graphs.Contains(g)).ToArray())
        {
            graph.HistoryResolutionChanged -= OnHistoryResolutionChanged;
            _resolutionGraphs.Remove(graph);
        }
        foreach (var graph in Graphs)
            if (_resolutionGraphs.Add(graph)) graph.HistoryResolutionChanged += OnHistoryResolutionChanged;
        var activeSeries = Graphs.SelectMany(g => g.Series).ToHashSet();
        foreach (var series in _displaySources.Keys.Where(s => !activeSeries.Contains(s)).ToArray())
            _displaySources.Remove(series);
    }

    public static (long[] DisplayTimestamps, double[] DisplayValues) SampleToDisplayBudget(long[] rawTimestamps, double[] rawValues, int budget, long start, long end)
    {
        if (rawValues.Length <= Math.Clamp(budget, 4, 100000)) return (rawTimestamps, rawValues);
        var points = new LODPoint[rawValues.Length];
        for (int i = 0; i < rawValues.Length; i++)
        {
            points[i] = new LODPoint(rawTimestamps[i], rawValues[i], rawValues[i], rawValues[i]);
        }
        var sampled = GraphHistoryResolution.Downsample(points, budget, start, end);
        var ts = new long[sampled.Count];
        var val = new double[sampled.Count];
        for (int i = 0; i < sampled.Count; i++)
        {
            ts[i] = sampled[i].TimestampUnixNano;
            val[i] = sampled[i].Value;
        }
        return (ts, val);
    }

    public sealed record SeriesDisplayData(
        ChartSeriesModel Series,
        long[] DisplayTimestamps,
        double[] DisplayValues,
        long[] SourceTimestamps,
        double[] SourceValues,
        double? LatestValue,
        double? PrevRawValue,
        long? PrevRawTimestamp);

    public sealed record GraphDisplaySnapshot(
        MetricGraphViewModel Graph,
        List<SeriesDisplayData> SeriesData,
        long[] Timestamps,
        double[] Values,
        string[] Labels,
        string Status,
        long WindowStart,
        long WindowEnd);

    private void ApplyDisplayBudget(ChartSeriesModel series, int budget, long start, long end)
    {
        // Live appends must start from source data, never repeatedly reduce an already sampled curve.
        _displaySources[series] = (series.Timestamps, series.Values);
        var (ts, vals) = SampleToDisplayBudget(series.Timestamps, series.Values, budget, start, end);
        series.Timestamps = ts;
        series.Values = vals;
    }

    private readonly ITelemetryDataProvider? _provider;
    private readonly GraphLayoutStore? _layoutStore;
    private readonly GraphPresetStore _presetStore;
    private CancellationTokenSource? _queryCts;
    private long _refreshGeneration;
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

    [ObservableProperty] private bool _isCustomProcessModalOpen;
    [ObservableProperty] private string _customProcessSearchFilter = string.Empty;
    public ObservableCollection<ProcessInfoModel> SelectableProcesses { get; } = new();
    public ObservableCollection<ProcessInfoModel> FilteredSelectableProcesses { get; } = new();
    [ObservableProperty] private ProcessInfoModel? _selectedCustomProcess;

    [ObservableProperty] private bool _isOptInPromptOpen;
    [ObservableProperty] private string _optInPromptMetric = string.Empty;
    [ObservableProperty] private string _optInPromptHostId = string.Empty;
    [ObservableProperty] private bool _isOptInProcessing;

    private static readonly string[] BaseMetrics = new[]
    {
        "cpu.total", "cpu.user", "cpu.system", "cpu.iowait",
        "memory.used", "memory.available", "memory.total", "memory.swap_used", "memory.swap_total",
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
        Graphs.CollectionChanged += (_, _) => TrackGraphResolutions();
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
                        if (!AvailableMetrics.Contains(sc.Metric) && !sc.Metric.StartsWith("proc.cpu.")) AvailableMetrics.Add(sc.Metric);
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

    public void PopulateAvailableMetrics(FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
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

        var disks = (IsAggregatedMode ? allNodes.SelectMany(n => n.Disks) : (node?.Disks ?? Array.Empty<MADTOM.Plugins.Telemetry.Proto.V1.DiskIoDevice>()))
            .Select(d => d.Name)
            .Distinct();

        foreach (var name in disks)
        {
            foreach (string suffix in new[] { "read_bytes", "write_bytes", "read_ops", "write_ops" })
            {
                AvailableMetrics.Add($"disk.io.{name}.{suffix}");
            }
        }

        int coreCount = IsAggregatedMode 
            ? (allNodes.Count > 0 ? allNodes.Max(n => n.Cores) : 0) 
            : (node?.Cores ?? 0);
        for (int c = 0; c < coreCount; c++)
        {
            AvailableMetrics.Add($"cpu.core.{c}");
        }

        var swapDevs = (IsAggregatedMode ? allNodes.SelectMany(n => n.SwapDevices) : (node?.SwapDevices ?? Array.Empty<SwapDevice>()))
            .Select(d => d.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct();
        foreach (var name in swapDevs)
        {
            AvailableMetrics.Add($"swap.{name}.total_bytes");
            AvailableMetrics.Add($"swap.{name}.used_bytes");
        }

        var zramDevs = (IsAggregatedMode ? allNodes.SelectMany(n => n.ZramDevices) : (node?.ZramDevices ?? Array.Empty<ZramDevice>()))
            .Select(d => d.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct();
        foreach (var name in zramDevs)
        {
            AvailableMetrics.Add($"zram.{name}.disksize_bytes");
            AvailableMetrics.Add($"zram.{name}.mem_used_bytes");
            AvailableMetrics.Add($"zram.{name}.orig_data_bytes");
            AvailableMetrics.Add($"zram.{name}.compr_data_bytes");
        }

        AvailableMetrics.Add("process.breakdown");
        AvailableMetrics.Add("custom.process");

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
    public async Task AddGraphAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedMetric)) return;
        if (SelectedMetric == "process.breakdown")
        {
            QuickMergeProcessCpu();
            return;
        }
        if (SelectedMetric == "custom.process")
        {
            OpenCustomProcessSelector();
            return;
        }

        // Interactive opt-in check: if the metric is currently OFF on the target host, prompt immediately!
        if (_provider != null && !IsAggregatedMode && !string.IsNullOrEmpty(TargetHostId))
        {
            var config = await _provider.GetNodeConfigAsync(TargetHostId);
            if (config != null)
            {
                var mode = TelemetryOptInResolver.GetMetricOptInMode(config, SelectedMetric);
                if (mode == TelemetryOptInMode.OptInOff)
                {
                    OptInPromptMetric = SelectedMetric;
                    OptInPromptHostId = TargetHostId;
                    IsOptInPromptOpen = true;
                    return;
                }
            }
        }

        ExecuteAddGraph(SelectedMetric);
    }

    public void AddGraph() => _ = AddGraphAsync();

    [RelayCommand]
    public async Task OptInAndAddGraphAsync(string modeStr)
    {
        string metric = OptInPromptMetric;
        string hostId = OptInPromptHostId;
        if (string.IsNullOrEmpty(metric) || string.IsNullOrEmpty(hostId) || _provider == null)
        {
            IsOptInPromptOpen = false;
            return;
        }

        IsOptInProcessing = true;
        try
        {
            var mode = modeStr == "store" 
                ? TelemetryOptInMode.OptInMonitorAndStore 
                : TelemetryOptInMode.OptInMonitorOnly;

            var cfg = await _provider.GetNodeConfigAsync(hostId);
            if (cfg != null)
            {
                TelemetryOptInResolver.SetMetricOptInMode(cfg, metric, mode);
                await _provider.UpdateNodeConfigAsync(hostId, cfg);
            }
        }
        finally
        {
            IsOptInProcessing = false;
            IsOptInPromptOpen = false;
        }

        ExecuteAddGraph(metric);
    }

    [RelayCommand]
    public void CancelOptInPrompt()
    {
        IsOptInPromptOpen = false;
    }

    private void ExecuteAddGraph(string metricName)
    {
        bool isRate = IsAddMetricRateOfChange;
        string label = isRate ? $"{metricName} (rate/s)" : metricName;
        string title = label;
        if (Graphs.Any(g => g.Series.Count == 1 && g.Series[0].Metric == metricName && g.Series[0].IsRateOfChange == isRate)) return;

        var series = new ChartSeriesModel(metricName, label, GraphLayoutStore.GetDefaultColor(metricName))
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

    [RelayCommand]
    public void QuickMergeDiskIo()
    {
        var diskMetrics = new HashSet<string>(new[] { "disk.io.read_bytes", "disk.io.write_bytes" });
        RemoveGraphsMatching(g => g.Series.All(s => diskMetrics.Contains(s.Metric)));

        var series = new[]
        {
            new ChartSeriesModel("disk.io.read_bytes", "Read Throughput", "#06B6D4") { IsRateOfChange = true },
            new ChartSeriesModel("disk.io.write_bytes", "Write Throughput", "#F59E0B") { IsRateOfChange = true }
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("Disk I/O Throughput", series);
        Groups.Add(new MetricGraphGroupViewModel(merged));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeProcessCpu()
    {
        var targetNode = _provider?.GetNode(TargetHostId) ?? ClusterNodes.FirstOrDefault(n => n.Id == TargetHostId);
        var activeNodes = IsAggregatedMode ? (IEnumerable<FleetNodeModel>)ClusterNodes : (targetNode != null ? new[] { targetNode } : ClusterNodes);
        var procNames = activeNodes.SelectMany(n => n.Processes)
                                   .OrderByDescending(p => p.Cpu)
                                   .Select(p => FleetNodeModel.SanitizeMetricName(p.Name))
                                   .Distinct()
                                   .Take(8)
                                   .ToList();

        var seriesList = new List<ChartSeriesModel>
        {
            new ChartSeriesModel("cpu.total", "Total CPU", "#06B6D4")
        };

        string[] palette = new[] { "#F59E0B", "#10B981", "#8B5CF6", "#EC4899", "#3B82F6", "#14B8A6", "#E11D48", "#84CC16", "#6366F1", "#F97316" };
        int colorIdx = 0;
        foreach (var name in procNames)
        {
            string metric = $"proc.cpu.{name}";
            seriesList.Add(new ChartSeriesModel(metric, name, palette[colorIdx % palette.Length]));
            colorIdx++;
        }
        seriesList.Add(new ChartSeriesModel("proc.cpu.other", "Other", "#64748B"));

        foreach (var s in seriesList) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("Process Breakdown", seriesList);
        Groups.Insert(0, new MetricGraphGroupViewModel(merged));
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
                    if (!AvailableMetrics.Contains(sc.Metric) && !sc.Metric.StartsWith("proc.cpu.")) AvailableMetrics.Add(sc.Metric);
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

    partial void OnCustomProcessSearchFilterChanged(string value)
    {
        FilterSelectableProcesses();
    }

    public void OpenCustomProcessSelector()
    {
        SelectableProcesses.Clear();
        CustomProcessSearchFilter = string.Empty;

        var targetNode = _provider?.GetNode(TargetHostId) ?? ClusterNodes.FirstOrDefault(n => n.Id == TargetHostId);
        var activeNodes = IsAggregatedMode ? (IEnumerable<FleetNodeModel>)ClusterNodes : (targetNode != null ? new[] { targetNode } : ClusterNodes);

        var procs = activeNodes.SelectMany(n => n.Processes)
                               .GroupBy(p => FleetNodeModel.SanitizeMetricName(p.Name))
                               .Select(g => new ProcessInfoModel
                               {
                                   Name = g.First().Name,
                                   Pid = g.First().Pid,
                                   User = g.First().User,
                                   Cpu = g.Sum(p => p.Cpu),
                                   Mem = g.First().Mem,
                                   Threads = g.Sum(p => p.Threads)
                               })
                               .OrderByDescending(p => p.Cpu)
                               .Take(1000)
                               .ToList();

        foreach (var p in procs)
        {
            SelectableProcesses.Add(p);
        }

        FilterSelectableProcesses();
        SelectedCustomProcess = FilteredSelectableProcesses.FirstOrDefault();
        IsCustomProcessModalOpen = true;
    }

    private void FilterSelectableProcesses()
    {
        FilteredSelectableProcesses.Clear();
        string q = CustomProcessSearchFilter.Trim().ToLowerInvariant();
        var matches = SelectableProcesses.Where(p =>
            string.IsNullOrEmpty(q) ||
            p.Name.ToLowerInvariant().Contains(q) ||
            p.Pid.ToString().Contains(q) ||
            p.User.ToLowerInvariant().Contains(q));

        foreach (var m in matches)
        {
            FilteredSelectableProcesses.Add(m);
        }

        if (SelectedCustomProcess == null || !FilteredSelectableProcesses.Contains(SelectedCustomProcess))
        {
            SelectedCustomProcess = FilteredSelectableProcesses.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void ConfirmAddCustomProcess()
    {
        if (SelectedCustomProcess == null) return;
        string cleanName = FleetNodeModel.SanitizeMetricName(SelectedCustomProcess.Name);
        string metric = $"proc.cpu.{cleanName}";
        string title = $"Process: {SelectedCustomProcess.Name}";

        bool isRate = IsAddMetricRateOfChange;
        if (Graphs.Any(g => g.Series.Count == 1 && g.Series[0].Metric == metric && g.Series[0].IsRateOfChange == isRate))
        {
            IsCustomProcessModalOpen = false;
            return;
        }

        var series = new ChartSeriesModel(metric, metric, GraphLayoutStore.GetDefaultColor(metric))
        {
            IsRateOfChange = isRate
        };
        series.ConfigurationChanged += SaveLayout;
        var newGraph = new MetricGraphViewModel(title, new[] { series });
        Groups.Add(new MetricGraphGroupViewModel(newGraph));
        SyncGraphsFromGroups();
        SaveLayout();
        _ = RefreshHistoryAsync();

        IsCustomProcessModalOpen = false;
    }

    [RelayCommand]
    public void CancelCustomProcess()
    {
        IsCustomProcessModalOpen = false;
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
        using var liveWork = UiPerformanceDiagnostics.Measure("graph.live-update");

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

        using var liveTiming = _pendingLiveTimingRefresh != null
            ? HistoryTiming.Begin("first-live-after-history", TargetHostId) : null;
        liveTiming?.Mark("arrival", $"historyRefresh={_pendingLiveTimingRefresh}; sampleNano={timestampNano}; receivedUtc={node?.TelemetryReceivedUtc:O}; uiObservedUtc={DateTime.UtcNow:O}");
        _pendingLiveTimingRefresh = null;
        long windowEndNano = timestampNano;
        long windowStartNano = windowEndNano - windowSpanNano;

        foreach (var graph in Graphs)
        {
            if (!graph.IsVisible) continue;

            liveTiming?.Mark("window-before", $"graph={graph.Title}; startNano={graph.WindowStart}; endNano={graph.WindowEnd}; newestDisplayedNano={graph.Timestamps.LastOrDefault()}");
            graph.WindowStart = windowStartNano;
            graph.WindowEnd = windowEndNano;
            liveTiming?.Mark("window-after", $"graph={graph.Title}; startNano={graph.WindowStart}; endNano={graph.WindowEnd}");

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
                    // Repeated/late notifications must not replace an existing observation or rate.
                    var previousTimes = _displaySources.TryGetValue(series, out var previousSource)
                        ? previousSource.Times : series.Timestamps;
                    if ((!IsAggregatedMode && previousTimes.Length > 0 && timestampNano <= previousTimes[^1]) ||
                        (series.IsRateOfChange && timestampNano <= series.PreviousRawSampleTimestampNano))
                    {
                        maxPoints = Math.Max(maxPoints, series.Values.Length);
                        continue;
                    }

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

                    var source = _displaySources.TryGetValue(series, out var saved) ? saved : (series.Timestamps, series.Values);
                    var currentTs = source.Item1;
                    var currentVals = source.Item2;

                    long tsSec = timestampNano / 1_000_000_000L;
                    int startIdx = 0;
                    long pruneThreshold = windowStartNano - 5_000_000_000L; // keep small grace margin to avoid gaps at edge
                    while (startIdx < currentTs.Length && currentTs[startIdx] < pruneThreshold)
                        startIdx++;

                    bool replaceLast = currentTs.Length > 0 && (IsAggregatedMode ? (currentTs[^1] / 1_000_000_000L) == tsSec : currentTs[^1] == timestampNano);

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
                    ApplyDisplayBudget(series, graph.HistoryPointBudget, windowStartNano, windowEndNano);
                    maxPoints = Math.Max(maxPoints, series.Values.Length);
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

    private string? _pendingLiveTimingRefresh;

    public async Task RefreshHistoryAsync()
    {
        _pendingLiveTimingRefresh = null;
        _queryCts?.Cancel(); _queryCts?.Dispose();
        var cts = _queryCts = new CancellationTokenSource();
        long generation = Interlocked.Increment(ref _refreshGeneration);
        _lastRefresh = DateTime.UtcNow;
        if (_provider == null || !TryRange(out var start, out var end)) return;
        using var timing = HistoryTiming.Begin("scope-refresh", TargetHostId, newRefresh: true);
        timing?.Mark("range", $"start={start:O}; end={end:O}; graphs={Graphs.Count}");
        _isQueryRunning = true;
        var ids = IsAggregatedMode ? ClusterNodes.Select(n => n.Id).ToArray() : new[] { TargetHostId };

        foreach (var g in Graphs) g.Status = "Loading…";

        try
        {
            var graphTasks = Graphs.ToArray().Select(async graph =>
            {
                using var graphTiming = HistoryTiming.Begin("graph", TargetHostId, graph.Title);
                long windowStartNano = new DateTimeOffset(start).ToUnixTimeMilliseconds() * 1_000_000L;
                long windowEndNano = new DateTimeOffset(end).ToUnixTimeMilliseconds() * 1_000_000L;

                var seriesList = graph.Series.ToArray();
                var seriesDisplayList = new List<SeriesDisplayData>(seriesList.Length);
                int maxPoints = 0;

                foreach (var series in seriesList)
                {
                    using var seriesTiming = HistoryTiming.Begin("series", TargetHostId, series.Metric);
                    var results = await Task.WhenAll(ids.Select(id => _provider.QueryHistoryWithResolutionAsync(id, series.Metric, start, end, graph.HistoryPointBudget, cts.Token)));
                    seriesTiming?.Mark("data-ready", $"points={results.Sum(r => r.Count)}");
                    if (cts.IsCancellationRequested || Volatile.Read(ref _refreshGeneration) != generation)
                    {
                        seriesTiming?.Mark("cancelled");
                        return null;
                    }

                    var prepared = await Task.Run(() =>
                    {
                        using var seriesWork = UiPerformanceDiagnostics.Measure("graph.history-transform-publish");
                        long timestampUnit = IsAggregatedMode ? 1_000_000_000L : 1L;
                        var points = results.SelectMany(s => s.GroupBy(p => p.TimestampUnixNano / timestampUnit).Select(g => g.OrderBy(p => p.TimestampUnixNano).Last()))
                                            .GroupBy(p => p.TimestampUnixNano / timestampUnit)
                                            .OrderBy(g => g.Key)
                                            .ToArray();

                        var rawTs = points.Select(g => g.Key * timestampUnit).ToArray();
                        var rawVals = points.Select(g => series.Metric.StartsWith("twamp.") || series.Metric.StartsWith("cpu.") || series.Metric.EndsWith("_pct") || series.Metric.EndsWith("_ratio") ? g.Average(p => p.Value) : g.Sum(p => p.Value)).ToArray();

                        double? prevVal = null;
                        long? prevTs = null;
                        if (series.IsRateOfChange)
                        {
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
                                prevVal = rawVals[^1];
                                prevTs = rawTs[^1];
                                rawTs = rateTs;
                                rawVals = rateVals;
                            }
                            else
                            {
                                rawTs = Array.Empty<long>();
                                rawVals = Array.Empty<double>();
                            }
                        }

                        seriesTiming?.Mark("transformed");
                        var (dispTs, dispVals) = SampleToDisplayBudget(rawTs, rawVals, graph.HistoryPointBudget, windowStartNano, windowEndNano);
                        seriesTiming?.Mark("sampled", $"points={dispVals.Length}");
                        double? latestVal = dispVals.Length > 0 ? dispVals[^1] : null;

                        return new SeriesDisplayData(series, dispTs, dispVals, rawTs, rawVals, latestVal, prevVal, prevTs);
                    }, cts.Token);

                    seriesDisplayList.Add(prepared);
                    maxPoints = Math.Max(maxPoints, prepared.DisplayValues.Length);
                }

                var primary = seriesDisplayList.FirstOrDefault();
                long[] primaryTimestamps = primary?.DisplayTimestamps ?? Array.Empty<long>();
                double[] primaryValues = primary?.DisplayValues ?? Array.Empty<double>();
                string[] labels = primaryTimestamps.Length > 0
                    ? primaryTimestamps.Select(t => DateTimeOffset.FromUnixTimeSeconds(t / 1_000_000_000L).ToLocalTime().ToString("MM-dd HH:mm:ss")).ToArray()
                    : Array.Empty<string>();

                string status;
                if (maxPoints == 0)
                {
                    status = "No measurements in this time window";
                }
                else
                {
                    long latestNano = primaryTimestamps.LastOrDefault();
                    if (latestNano > 0 && (windowEndNano - latestNano) > 60_000_000_000L)
                    {
                        var latestDt = DateTimeOffset.FromUnixTimeMilliseconds(latestNano / 1_000_000L).ToLocalTime();
                        status = $"{maxPoints} points · window {start.ToLocalTime():HH:mm:ss}–{end.ToLocalTime():HH:mm:ss} (data ends at {latestDt:HH:mm:ss})";
                    }
                    else
                    {
                        status = $"{maxPoints} points · {start.ToLocalTime():HH:mm:ss} – {end.ToLocalTime():HH:mm:ss}";
                    }
                }

                return new GraphDisplaySnapshot(graph, seriesDisplayList, primaryTimestamps, primaryValues, labels, status, windowStartNano, windowEndNano);
            });

            var snapshots = await Task.WhenAll(graphTasks);
            if (cts.IsCancellationRequested || Volatile.Read(ref _refreshGeneration) != generation) return;

            void ApplySnapshots()
            {
                if (cts.IsCancellationRequested || Volatile.Read(ref _refreshGeneration) != generation) return;
                using var publishTiming = UiPerformanceDiagnostics.Measure("graph.batch-publish");
                foreach (var snap in snapshots)
                {
                    if (snap == null) continue;
                    snap.Graph.WindowStart = snap.WindowStart;
                    snap.Graph.WindowEnd = snap.WindowEnd;
                    foreach (var s in snap.SeriesData)
                    {
                        _displaySources[s.Series] = (s.SourceTimestamps, s.SourceValues);
                        s.Series.Timestamps = s.DisplayTimestamps;
                        s.Series.Values = s.DisplayValues;
                        if (s.LatestValue.HasValue) s.Series.LatestValue = s.LatestValue.Value;
                        if (s.PrevRawValue.HasValue) s.Series.PreviousRawSampleValue = s.PrevRawValue.Value;
                        if (s.PrevRawTimestamp.HasValue) s.Series.PreviousRawSampleTimestampNano = s.PrevRawTimestamp.Value;
                    }
                    snap.Graph.Timestamps = snap.Timestamps;
                    snap.Graph.Values = snap.Values;
                    snap.Graph.Labels = snap.Labels;
                    snap.Graph.Status = snap.Status;
                }
            }

            ApplySnapshots();

            timing?.Mark(cts.IsCancellationRequested ? "cancelled" : "complete");
            if (!cts.IsCancellationRequested && Volatile.Read(ref _refreshGeneration) == generation)
            {
                _hasLoadedHistory = true;
                _pendingLiveTimingRefresh = timing?.RefreshId;
            }
        }
        catch (OperationCanceledException) { timing?.Mark("cancelled"); }
        catch (Exception ex) { timing?.Mark("error", ex.GetType().Name); if (!cts.IsCancellationRequested && Volatile.Read(ref _refreshGeneration) == generation) foreach (var graph in Graphs) graph.Status = $"Query failed: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_queryCts, cts)) _isQueryRunning = false;
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
