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

    public MetricGraphViewModel(string metric, string? colorHex = null)
    {
        Title = metric;
        string color = colorHex ?? GraphLayoutStore.GetDefaultColor(metric);
        Series.Add(new ChartSeriesModel(metric, metric, color));
        Series.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsMerged));
            OnPropertyChanged(nameof(Metric));
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
        };
    }
}

public partial class HostMetricsTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider? _provider;
    private readonly GraphLayoutStore? _layoutStore;
    private CancellationTokenSource? _queryCts;
    private DateTime _lastRefresh;

    [ObservableProperty] private string _selectedScope = "5m";
    [ObservableProperty] private bool _isCustomScopeModalOpen;
    [ObservableProperty] private bool _isCustomizationModalOpen;
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

    public ObservableCollection<string> AvailableMetrics { get; } = new()
    {
        "cpu.total", "cpu.user", "cpu.system", "cpu.iowait",
        "memory.used", "memory.available", "memory.total", "memory.swap_free", "memory.zram_ratio",
        "power.battery_pct", "power.rate_watts",
        "twamp.rtt", "twamp.forward", "twamp.reverse"
    };

    public ObservableCollection<MetricGraphViewModel> Graphs { get; } = new();

    public bool IsScope1m => SelectedScope == "1m";
    public bool IsScope5m => SelectedScope == "5m";
    public bool IsScope30m => SelectedScope == "30m";
    public bool IsScope2h => SelectedScope == "2h";
    public bool IsScope24h => SelectedScope == "24h";
    public bool IsScopeCustom => SelectedScope == "custom";

    partial void OnSelectedScopeChanged(string value)
    {
        foreach (string p in new[] { nameof(IsScope1m), nameof(IsScope5m), nameof(IsScope30m), nameof(IsScope2h), nameof(IsScope24h), nameof(IsScopeCustom) })
            OnPropertyChanged(p);
    }

    public HostMetricsTabViewModel(ITelemetryDataProvider? provider = null, GraphLayoutStore? layoutStore = null)
    {
        _provider = provider;
        _layoutStore = layoutStore;

        var loadedConfigs = layoutStore?.LoadConfigs();
        if (loadedConfigs != null && loadedConfigs.Count > 0)
        {
            foreach (var cfg in loadedConfigs)
            {
                var seriesList = new List<ChartSeriesModel>();
                foreach (var sc in cfg.Series)
                {
                    if (!AvailableMetrics.Contains(sc.Metric)) AvailableMetrics.Add(sc.Metric);
                    var s = new ChartSeriesModel(sc.Metric, sc.Label, sc.ColorHex);
                    s.ConfigurationChanged += SaveLayout;
                    seriesList.Add(s);
                }
                if (seriesList.Count > 0)
                {
                    Graphs.Add(new MetricGraphViewModel(cfg.Title, seriesList));
                }
            }
        }
        else
        {
            var s1 = new ChartSeriesModel("cpu.total", "cpu.total", "#06B6D4");
            s1.ConfigurationChanged += SaveLayout;
            Graphs.Add(new MetricGraphViewModel("cpu.total", new[] { s1 }));

            var s2 = new ChartSeriesModel("memory.used", "memory.used", "#10B981");
            s2.ConfigurationChanged += SaveLayout;
            Graphs.Add(new MetricGraphViewModel("memory.used", new[] { s2 }));
        }
    }

    [RelayCommand]
    public void OpenCustomizationModal() => IsCustomizationModalOpen = true;

    [RelayCommand]
    public void CloseCustomizationModal() => IsCustomizationModalOpen = false;

    [RelayCommand]
    public void AddGraph()
    {
        if (string.IsNullOrWhiteSpace(SelectedMetric) || Graphs.Any(g => g.Series.Count == 1 && g.Series[0].Metric == SelectedMetric)) return;
        var series = new ChartSeriesModel(SelectedMetric, SelectedMetric, GraphLayoutStore.GetDefaultColor(SelectedMetric));
        series.ConfigurationChanged += SaveLayout;
        Graphs.Add(new MetricGraphViewModel(SelectedMetric, new[] { series }));
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void RemoveGraph(MetricGraphViewModel graph)
    {
        Graphs.Remove(graph);
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
        Graphs.Remove(source);
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void SplitGraph(MetricGraphViewModel graph)
    {
        if (graph.Series.Count <= 1) return;
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
            Graphs.Add(newGraph);
        }
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
            Graphs.Remove(parentGraph);
        }
        else
        {
            parentGraph.Title = parentGraph.Series.Count == 1 ? parentGraph.Series[0].Label : $"{parentGraph.Series[0].Label} + {parentGraph.Series.Count - 1} more";
        }
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeCpu()
    {
        var cpuMetrics = new HashSet<string>(new[] { "cpu.total", "cpu.user", "cpu.system", "cpu.iowait" });
        var toRemove = Graphs.Where(g => g.Series.All(s => cpuMetrics.Contains(s.Metric))).ToList();
        foreach (var g in toRemove) Graphs.Remove(g);

        var series = new[]
        {
            new ChartSeriesModel("cpu.total", "Total", "#06B6D4"),
            new ChartSeriesModel("cpu.user", "User", "#3B82F6"),
            new ChartSeriesModel("cpu.system", "System", "#8B5CF6"),
            new ChartSeriesModel("cpu.iowait", "IOWait", "#F59E0B")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("CPU Breakdown", series);
        Graphs.Insert(0, merged);
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeMemory()
    {
        var memMetrics = new HashSet<string>(new[] { "memory.used", "memory.available" });
        var toRemove = Graphs.Where(g => g.Series.All(s => memMetrics.Contains(s.Metric))).ToList();
        foreach (var g in toRemove) Graphs.Remove(g);

        var series = new[]
        {
            new ChartSeriesModel("memory.used", "Used", "#10B981"),
            new ChartSeriesModel("memory.available", "Available", "#34D399")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("Memory Breakdown", series);
        Graphs.Add(merged);
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void QuickMergeTwamp()
    {
        var twampMetrics = new HashSet<string>(new[] { "twamp.rtt", "twamp.forward", "twamp.reverse" });
        var toRemove = Graphs.Where(g => g.Series.All(s => twampMetrics.Contains(s.Metric))).ToList();
        foreach (var g in toRemove) Graphs.Remove(g);

        var series = new[]
        {
            new ChartSeriesModel("twamp.rtt", "RTT", "#06B6D4"),
            new ChartSeriesModel("twamp.forward", "Forward (Up)", "#6366F1"),
            new ChartSeriesModel("twamp.reverse", "Reverse (Down)", "#A855F7")
        };
        foreach (var s in series) s.ConfigurationChanged += SaveLayout;
        var merged = new MetricGraphViewModel("TWAMP Latency Breakdown", series);
        Graphs.Add(merged);
        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void ResetToDefaultGraphs()
    {
        Graphs.Clear();
        var s1 = new ChartSeriesModel("cpu.total", "cpu.total", "#06B6D4");
        s1.ConfigurationChanged += SaveLayout;
        Graphs.Add(new MetricGraphViewModel("cpu.total", new[] { s1 }));

        var s2 = new ChartSeriesModel("memory.used", "memory.used", "#10B981");
        s2.ConfigurationChanged += SaveLayout;
        Graphs.Add(new MetricGraphViewModel("memory.used", new[] { s2 }));

        SaveLayout();
        _ = RefreshHistoryAsync();
    }

    private void SaveLayout()
    {
        try
        {
            var configs = Graphs.Select(g => new GraphItemConfig
            {
                Title = g.Title,
                Series = g.Series.Select(s => new GraphSeriesConfig
                {
                    Metric = s.Metric,
                    Label = s.Label,
                    ColorHex = s.ColorHex
                }).ToList()
            }).ToList();
            _layoutStore?.SaveConfigs(configs);
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
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    public void ApplyCustomScope()
    {
        if (!TryRange(out _, out _)) { ScopeError = "Choose a start time before the end time."; return; }
        ScopeError = ""; IsCustomScopeModalOpen = false; _ = RefreshHistoryAsync();
    }

    [RelayCommand] public void CancelCustomScope() { IsCustomScopeModalOpen = false; SetScope("5m"); }

    private bool TryRange(out DateTime start, out DateTime end)
    {
        end = DateTime.UtcNow;
        start = end.AddMinutes(SelectedScope switch { "1m" => -1, "30m" => -30, "2h" => -120, "24h" => -1440, _ => -5 });
        if (!IsScopeCustom) return true;
        if (CustomStartDate == null || CustomEndDate == null || CustomStartTime == null || CustomEndTime == null) return false;
        start = DateTime.SpecifyKind(CustomStartDate.Value.Date + CustomStartTime.Value, DateTimeKind.Local).ToUniversalTime();
        end = DateTime.SpecifyKind(CustomEndDate.Value.Date + CustomEndTime.Value, DateTimeKind.Local).ToUniversalTime();
        return start < end;
    }

    public void UpdateForNode(string hostId, FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        bool changed = TargetHostId != hostId;
        TargetHostId = hostId; ClusterNodes = allNodes; IsAggregatedMode = hostId == "aggregated";
        ThreadCount = IsAggregatedMode ? allNodes.Sum(n => n.Cores) : node?.Cores ?? 0;
        ViewCoreMatrix = node?.ViewCpuMatrix ?? true;
        foreach (var graph in Graphs) graph.IsVisible = node == null ||
            (graph.Series.Any(s => s.Metric.StartsWith("power.")) ? node.ViewPowerBattery :
             graph.Series.Any(s => s.Metric.StartsWith("nic.")) ? node.ViewNetworkCounters :
             graph.Series.Any(s => s.Metric.Contains("swap") || s.Metric.Contains("zram")) ? node.ViewSwapZram : true);
        CoreLoads = IsAggregatedMode ? allNodes.SelectMany(n => n.CoreLoads).ToArray() : node?.CoreLoads ?? Array.Empty<float>();
        foreach (var nic in (node == null ? allNodes : new[] { node }).SelectMany(n => n.Interfaces))
            foreach (string suffix in new[] { "rx_bytes", "tx_bytes" })
            {
                string metric = $"nic.{nic.Name}.{suffix}";
                if (!AvailableMetrics.Contains(metric)) AvailableMetrics.Add(metric);
            }
        if (changed)
        {
            foreach (var graph in Graphs)
            {
                graph.Values = Array.Empty<double>();
                graph.Labels = Array.Empty<string>();
                foreach (var s in graph.Series)
                {
                    s.Values = Array.Empty<double>();
                    s.Timestamps = Array.Empty<long>();
                }
            }
        }
        TimeSpan minRefresh = SelectedScope switch
        {
            "1m" => TimeSpan.FromSeconds(0.9),
            "5m" => TimeSpan.FromSeconds(2.0),
            _ => TimeSpan.FromSeconds(5.0)
        };
        if (changed || (!IsScopeCustom && DateTime.UtcNow - _lastRefresh >= minRefresh))
            _ = RefreshHistoryAsync();
    }

    public async Task RefreshHistoryAsync()
    {
        _queryCts?.Cancel(); _queryCts?.Dispose();
        var cts = _queryCts = new CancellationTokenSource();
        _lastRefresh = DateTime.UtcNow;
        if (_provider == null || !TryRange(out var start, out var end)) return;
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
                    series.LatestValue = series.Values.LastOrDefault();
                    maxPoints = Math.Max(maxPoints, points.Length);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) foreach (var graph in Graphs) graph.Status = $"Query failed: {ex.Message}"; }
    }

    public void PushLiveSample(double fwd, double rev)
    {
        ForwardSeries = ForwardSeries.Append(fwd).TakeLast(1200).ToArray();
        ReverseSeries = ReverseSeries.Append(rev).TakeLast(1200).ToArray();
        AsymmetrySeries = AsymmetrySeries.Append(Math.Abs(fwd - rev)).TakeLast(1200).ToArray();
        TimeLabels = TimeLabels.Append(DateTime.Now.ToString("HH:mm:ss")).TakeLast(1200).ToArray();
    }
}
