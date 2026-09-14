using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class HostProcessesTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly List<ProcessInfoModel> _allProcesses = new();
    private const int PageSize = 100;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _targetHostId = "";

    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    public bool CanPreviousPage => Page > 1;
    public bool CanNextPage => Page < PageCount;

    public bool CanSendSignals => _telemetryProvider is not CollectorTelemetryDataProvider && TargetHostId != "all";
    public ObservableCollection<ProcessInfoModel> Processes { get; } = new();

    public event Action<int, string, int, string>? ActionConfirmationRequested;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    public bool IsProcessListTabSelected => SelectedTabIndex == 0;
    public bool IsProcessOverviewTabSelected => SelectedTabIndex == 1;

    [RelayCommand]
    public void SelectProcessListTab() => SelectedTabIndex = 0;

    [RelayCommand]
    public void SelectProcessOverviewTab() => SelectedTabIndex = 1;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsProcessListTabSelected));
        OnPropertyChanged(nameof(IsProcessOverviewTabSelected));
    }

    [ObservableProperty]
    private string _selectedScope = "5m";

    public bool IsScope1m => SelectedScope == "1m";
    public bool IsScope5m => SelectedScope == "5m";
    public bool IsScope30m => SelectedScope == "30m";
    public bool IsScope2h => SelectedScope == "2h";
    public bool IsScope6h => SelectedScope == "6h";
    public bool IsScope12h => SelectedScope == "12h";
    public bool IsScope24h => SelectedScope == "24h";
    public bool IsScopeCustom => SelectedScope == "custom";

    partial void OnSelectedScopeChanged(string value)
    {
        OnPropertyChanged(nameof(IsScope1m));
        OnPropertyChanged(nameof(IsScope5m));
        OnPropertyChanged(nameof(IsScope30m));
        OnPropertyChanged(nameof(IsScope2h));
        OnPropertyChanged(nameof(IsScope6h));
        OnPropertyChanged(nameof(IsScope12h));
        OnPropertyChanged(nameof(IsScope24h));
        OnPropertyChanged(nameof(IsScopeCustom));
        UpdateOverviewSeriesData();
    }

    [RelayCommand]
    public void SetScope(string scope)
    {
        SelectedScope = scope;
        _ = RefreshHistoryAsync();
    }

    public long GetScopeSpanNano() => SelectedScope switch
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

    [ObservableProperty]
    private int _overviewTopN = 5;

    partial void OnOverviewTopNChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 10);
        if (_overviewTopN != clamped)
        {
            _overviewTopN = clamped;
            OnPropertyChanged(nameof(OverviewTopN));
        }
        RebuildOverviewSeries();
        UpdateOverviewSeriesData();
    }

    public ObservableCollection<ChartSeriesModel> OverviewSeries { get; } = new();

    [ObservableProperty] private long[] _overviewTimestamps = Array.Empty<long>();
    [ObservableProperty] private long _overviewWindowStart;
    [ObservableProperty] private long _overviewWindowEnd;
    [ObservableProperty] private double _overviewZoomLevel = 1.0;
    [ObservableProperty] private double _overviewPanOffset = 0.0;

    private static readonly string[] RankColors = new[]
    {
        "#06B6D4", // Rank 1: Cyan
        "#10B981", // Rank 2: Emerald
        "#3B82F6", // Rank 3: Blue
        "#8B5CF6", // Rank 4: Purple
        "#F59E0B", // Rank 5: Amber
        "#F43F5E", // Rank 6: Rose
        "#6366F1", // Rank 7: Indigo
        "#84CC16", // Rank 8: Lime
        "#F97316", // Rank 9: Orange
        "#14B8A6"  // Rank 10: Teal
    };

    public ObservableCollection<ProcessRankLeaderModel> CurrentRankLeaders { get; } = new();

    private sealed class OverviewSample
    {
        public long TimestampNano { get; set; }
        public List<(string Name, double Cpu)> TopProcesses { get; } = new();
    }

    private readonly List<OverviewSample> _historySamples = new();

    [ObservableProperty]
    private bool _isProcessCollectionDisabled;

    private System.Threading.CancellationTokenSource? _queryCts;

    public HostProcessesTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;
        RebuildOverviewSeries();
        LoadProcesses();
        _ = RefreshHistoryAsync();
        _telemetryProvider.NodeTelemetryUpdated += (_, node) => { if (TargetHostId == "all" || node.Id == TargetHostId) LoadProcesses(); };
    }

    public void SetTargetHost(string hostId)
    {
        TargetHostId = hostId;
        OnPropertyChanged(nameof(CanSendSignals));
        _historySamples.Clear();
        LoadProcesses();
        _ = RefreshHistoryAsync();
    }

    private void RebuildOverviewSeries()
    {
        OverviewSeries.Clear();
        for (int r = 0; r < OverviewTopN; r++)
        {
            int rankNumber = r + 1;
            string color = RankColors[r % RankColors.Length];
            var series = new ChartSeriesModel($"proc.cpu.rank{rankNumber}", $"#{rankNumber}", color);
            OverviewSeries.Add(series);
        }
    }

    private void LoadProcesses()
    {
        _allProcesses.Clear();
        foreach (var p in _telemetryProvider.GetProcesses(TargetHostId))
        {
            _allProcesses.Add(p);
        }

        if (TargetHostId != "all")
        {
            var node = _telemetryProvider.GetNode(TargetHostId);
            IsProcessCollectionDisabled = node != null && !node.ProcessesAvailable;
        }
        else
        {
            var nodes = _telemetryProvider.GetFleetNodes();
            IsProcessCollectionDisabled = nodes.Count > 0 && nodes.All(n => !n.ProcessesAvailable);
        }

        RecordOverviewSample();
        ApplyFilter();
    }

    private void RecordOverviewSample()
    {
        long nowNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        var top = _allProcesses
            .GroupBy(p => p.Name)
            .Select(g => (Name: g.Key, Cpu: Math.Round(g.Sum(p => p.Cpu), 1)))
            .OrderByDescending(p => p.Cpu)
            .Take(10)
            .ToList();

        // If _historySamples is empty and we have data, seed a baseline 1s earlier so lines draw immediately
        if (_historySamples.Count == 0 && top.Count > 0)
        {
            var seed = new OverviewSample { TimestampNano = nowNano - 1_000_000_000L };
            seed.TopProcesses.AddRange(top);
            _historySamples.Add(seed);
        }

        var sample = new OverviewSample { TimestampNano = nowNano };
        sample.TopProcesses.AddRange(top);
        _historySamples.Add(sample);

        long windowSpanNano = GetScopeSpanNano();
        long pruneThreshold = nowNano - windowSpanNano - 15_000_000_000L;
        _historySamples.RemoveAll(s => s.TimestampNano < pruneThreshold);

        UpdateOverviewSeriesData();
    }

    public async System.Threading.Tasks.Task RefreshHistoryAsync()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        var cts = _queryCts = new System.Threading.CancellationTokenSource();

        if (string.IsNullOrEmpty(TargetHostId)) return;

        long spanNano = GetScopeSpanNano();
        DateTime end = DateTime.UtcNow;
        DateTime start = end.AddSeconds(-spanNano / 1_000_000_000.0);

        var topProcNames = _allProcesses
            .GroupBy(p => p.Name)
            .OrderByDescending(g => g.Sum(p => p.Cpu))
            .Select(g => g.Key)
            .Take(15)
            .ToList();

        if (topProcNames.Count == 0) return;

        var hostIds = TargetHostId == "all"
            ? _telemetryProvider.GetFleetNodes().Select(n => n.Id).ToArray()
            : new[] { TargetHostId };

        var historicalByProc = new Dictionary<string, Dictionary<long, double>>();

        try
        {
            foreach (var procName in topProcNames)
            {
                if (cts.IsCancellationRequested) return;
                string cleanName = FleetNodeModel.SanitizeMetricName(procName);
                string metricName = $"proc.cpu.{cleanName}";

                var results = await System.Threading.Tasks.Task.WhenAll(hostIds.Select(id => _telemetryProvider.QueryHistoryAsync(id, metricName, start, end, cts.Token)));
                var points = results.SelectMany(r => r).ToList();
                if (points.Count > 0)
                {
                    var bySec = points
                        .GroupBy(p => p.TimestampUnixNano / 1_000_000_000L)
                        .ToDictionary(g => g.Key, g => g.Average(p => p.Value));
                    historicalByProc[procName] = bySec;
                }
            }

            if (cts.IsCancellationRequested) return;

            if (historicalByProc.Count > 0)
            {
                var allSecs = historicalByProc.Values
                    .SelectMany(d => d.Keys)
                    .Distinct()
                    .OrderBy(sec => sec)
                    .ToList();

                if (allSecs.Count >= 2)
                {
                    _historySamples.Clear();
                    foreach (var sec in allSecs)
                    {
                        long nano = sec * 1_000_000_000L;
                        var sample = new OverviewSample { TimestampNano = nano };
                        var procsAtSec = historicalByProc
                            .Select(kv => (Name: kv.Key, Cpu: kv.Value.TryGetValue(sec, out var val) ? val : 0.0))
                            .Where(p => p.Cpu > 0)
                            .OrderByDescending(p => p.Cpu)
                            .Take(10)
                            .ToList();

                        sample.TopProcesses.AddRange(procsAtSec);
                        _historySamples.Add(sample);
                    }

                    UpdateOverviewSeriesData();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private void UpdateOverviewSeriesData()
    {
        if (OverviewSeries.Count != OverviewTopN)
        {
            RebuildOverviewSeries();
        }

        int m = _historySamples.Count;
        if (m == 0)
        {
            OverviewTimestamps = Array.Empty<long>();
            CurrentRankLeaders.Clear();
            foreach (var s in OverviewSeries)
            {
                s.Values = Array.Empty<double>();
                s.Timestamps = Array.Empty<long>();
                s.PointLabels = Array.Empty<string>();
            }
            return;
        }

        var timestamps = new long[m];
        for (int i = 0; i < m; i++)
        {
            timestamps[i] = _historySamples[i].TimestampNano;
        }
        OverviewTimestamps = timestamps;

        long windowSpanNano = GetScopeSpanNano();
        long latest = timestamps[m - 1];
        OverviewWindowEnd = latest;
        OverviewWindowStart = latest - windowSpanNano;

        CurrentRankLeaders.Clear();
        var latestSample = _historySamples[m - 1];

        for (int r = 0; r < OverviewTopN && r < OverviewSeries.Count; r++)
        {
            var vals = new double[m];
            var pointLabels = new string[m];

            for (int i = 0; i < m; i++)
            {
                var s = _historySamples[i];
                if (r < s.TopProcesses.Count)
                {
                    vals[i] = s.TopProcesses[r].Cpu;
                    pointLabels[i] = $"{s.TopProcesses[r].Name} (#{r + 1})";
                }
                else
                {
                    vals[i] = 0.0;
                    pointLabels[i] = $"None (#{r + 1})";
                }
            }

            var series = OverviewSeries[r];
            series.Timestamps = timestamps;
            series.Values = vals;
            series.PointLabels = pointLabels;
            series.LatestValue = vals[m - 1];

            string curProc = r < latestSample.TopProcesses.Count ? latestSample.TopProcesses[r].Name : "None";
            double curCpu = r < latestSample.TopProcesses.Count ? latestSample.TopProcesses[r].Cpu : 0.0;

            CurrentRankLeaders.Add(new ProcessRankLeaderModel
            {
                Rank = r + 1,
                ColorHex = RankColors[r % RankColors.Length],
                ProcessName = curProc,
                Cpu = curCpu
            });
        }
    }

    partial void OnSearchFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Processes.Clear();
        string q = SearchFilter.Trim().ToLowerInvariant();

        var filtered = _allProcesses.Where(p =>
            string.IsNullOrEmpty(q) ||
            p.Name.ToLowerInvariant().Contains(q) ||
            p.User.ToLowerInvariant().Contains(q) ||
            p.Pid.ToString().Contains(q));

        var list = filtered.ToList();
        PageCount = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        Page = Math.Clamp(Page, 1, PageCount);
        foreach (var p in list.Skip((Page - 1) * PageSize).Take(PageSize))
        {
            Processes.Add(p);
        }
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    partial void OnPageChanged(int value) => ApplyFilter();

    [RelayCommand] public void PreviousPage() { if (CanPreviousPage) Page--; }
    [RelayCommand] public void NextPage() { if (CanNextPage) Page++; }

    [RelayCommand]
    public void DispatchTerm(ProcessInfoModel proc)
    {
        if (proc != null && CanSendSignals)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 15, TargetHostId);
        }
    }

    [RelayCommand]
    public void DispatchKill(ProcessInfoModel proc)
    {
        if (proc != null && CanSendSignals)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 9, TargetHostId);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        LoadProcesses();
        NotificationService.Instance.ShowToast("Showing latest received process snapshot");
    }
}

public sealed class ProcessRankLeaderModel : ObservableObject
{
    public int Rank { get; set; }
    public string ColorHex { get; set; } = "#06B6D4";
    public string ProcessName { get; set; } = "-";
    public double Cpu { get; set; }
}
