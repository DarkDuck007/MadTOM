using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MadTOM.Controls.Charts;
using MadTOM.Models;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class NoxTelemetryTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;
    private readonly TelemetryHistoryCache _historyCache;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _selectedTimeRange = "15M";
    [ObservableProperty] private string _tokenThroughputSummary = "0 in / 0 out";
    [ObservableProperty] private string _processMemorySummary = "RSS: 0 MB · VMS: 0 MB";
    [ObservableProperty] private string _cpuSummary = "CPU: 0.0%";
    [ObservableProperty] private string _runtimeSummary = "Loop Lag: 0.0ms · Tasks: 0";

    public ObservableCollection<string> TimeRanges { get; } = new()
    {
        "5M", "15M", "1H", "6H", "24H", "7D"
    };

    [ObservableProperty] private long _windowStart;
    [ObservableProperty] private long _windowEnd;

    public ObservableCollection<ChartSeriesModel> TokenSeries { get; } = new();
    public ObservableCollection<ChartSeriesModel> RuntimeSeries { get; } = new();

    private readonly ChartSeriesModel _tokensInSeries = new("llm.tokens.in", "Prompt Tokens", "#06B6D4");
    private readonly ChartSeriesModel _tokensOutSeries = new("llm.tokens.out", "Completion Tokens", "#10B981");
    private readonly ChartSeriesModel _tokensCachedSeries = new("llm.tokens.cached", "Cached Tokens", "#A855F7");

    private readonly ChartSeriesModel _cpuSeries = new("py.cpu.total_pct", "Process CPU %", "#F59E0B");
    private readonly ChartSeriesModel _rssSeries = new("py.mem.rss_bytes", "RSS Memory (MiB)", "#3B82F6");
    private readonly ChartSeriesModel _lagSeries = new("py.runtime.event_loop_lag_ms", "Event Loop Lag (ms)", "#EF4444");

    public NoxTelemetryTabViewModel(NoxNodeManager nodeManager, TelemetryHistoryCache historyCache)
    {
        _nodeManager = nodeManager;
        _historyCache = historyCache;
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();

        TokenSeries.Add(_tokensInSeries);
        TokenSeries.Add(_tokensOutSeries);
        TokenSeries.Add(_tokensCachedSeries);

        RuntimeSeries.Add(_cpuSeries);
        RuntimeSeries.Add(_rssSeries);
        RuntimeSeries.Add(_lagSeries);
    }

    public void OnTelemetrySampleReceived(NoxTelemetrySample sample)
    {
        if (SelectedNode == null) return;

        var dict = sample.Metrics.ToDictionary(k => k.Key, v => v.Value);
        _historyCache.Record("noxchat", SelectedNode.Id, sample.TimestampUnixNano, dict);

        long nowSec = sample.TimestampUnixNano / 1_000_000_000L;
        long windowSpanSec = SelectedTimeRange switch
        {
            "5M" => 300,
            "15M" => 900,
            "1H" => 3600,
            "6H" => 21600,
            "24H" => 86400,
            "7D" => 604800,
            _ => 900
        };
        WindowStart = Math.Max(0, nowSec - windowSpanSec);
        WindowEnd = nowSec;

        // Update live status summaries
        if (dict.TryGetValue("llm.tokens.in", out double tIn) && dict.TryGetValue("llm.tokens.out", out double tOut))
        {
            TokenThroughputSummary = $"Tokens: {tIn:N0} in · {tOut:N0} out";
        }
        if (dict.TryGetValue("py.mem.rss_bytes", out double rss) && dict.TryGetValue("py.mem.vms_bytes", out double vms))
        {
            ProcessMemorySummary = $"RSS: {rss / 1048576.0:F1} MiB · VMS: {vms / 1048576.0:F1} MiB";
        }
        if (dict.TryGetValue("py.cpu.total_pct", out double cpu))
        {
            CpuSummary = $"CPU: {cpu:F1}%";
        }
        if (dict.TryGetValue("py.runtime.event_loop_lag_ms", out double lag) && dict.TryGetValue("py.runtime.asyncio_tasks", out double tasks))
        {
            RuntimeSummary = $"Loop Lag: {lag:F1}ms · Tasks: {tasks:N0}";
        }

        // Append to series buffers
        AppendSampleToSeries(_tokensInSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("llm.tokens.in", 0));
        AppendSampleToSeries(_tokensOutSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("llm.tokens.out", 0));
        AppendSampleToSeries(_tokensCachedSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("llm.tokens.cached", 0));

        AppendSampleToSeries(_cpuSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("py.cpu.total_pct", 0));
        AppendSampleToSeries(_rssSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("py.mem.rss_bytes", 0) / 1048576.0);
        AppendSampleToSeries(_lagSeries, sample.TimestampUnixNano, dict.GetValueOrDefault("py.runtime.event_loop_lag_ms", 0));
    }

    private static void AppendSampleToSeries(ChartSeriesModel series, long timestampNano, double value)
    {
        var tsList = series.Timestamps.ToList();
        var valList = series.Values.ToList();

        tsList.Add(timestampNano / 1_000_000_000L);
        valList.Add(value);

        // Limit to 1200 points in memory
        if (tsList.Count > 1200)
        {
            tsList.RemoveAt(0);
            valList.RemoveAt(0);
        }

        series.Timestamps = tsList.ToArray();
        series.Values = valList.ToArray();
        series.LatestValue = value;
    }
}
