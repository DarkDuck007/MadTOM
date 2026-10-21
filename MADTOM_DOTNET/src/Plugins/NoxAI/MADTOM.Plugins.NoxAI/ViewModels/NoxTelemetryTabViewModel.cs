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
using MADTOM.Plugins.Telemetry.Proto.V1;
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

    partial void OnSelectedTimeRangeChanged(string value)
    {
        UpdateTimeWindowAndFetchHistory();
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value != null)
        {
            UpdateTimeWindowAndFetchHistory();
        }
        else
        {
            TokenThroughputSummary = "0 in / 0 out";
            ProcessMemorySummary = "RSS: 0 MB · VMS: 0 MB";
            CpuSummary = "CPU: 0.0%";
            RuntimeSummary = "Loop Lag: 0.0ms · Tasks: 0";
            ClearSeries();
        }
    }

    private void ClearSeries()
    {
        foreach (var s in TokenSeries.Concat(RuntimeSeries))
        {
            s.Timestamps = Array.Empty<long>();
            s.Values = Array.Empty<double>();
            s.LatestValue = 0;
        }
    }

    private long GetSelectedSpanSeconds() => SelectedTimeRange switch
    {
        "5M" => 300,
        "15M" => 900,
        "1H" => 3600,
        "6H" => 21600,
        "24H" => 86400,
        "7D" => 604800,
        _ => 900
    };

    private void UpdateTimeWindowAndFetchHistory()
    {
        long spanSec = GetSelectedSpanSeconds();
        long nowNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        if (_tokensInSeries.Timestamps.Length > 0)
        {
            long latestTs = _tokensInSeries.Timestamps[^1];
            if (latestTs > nowNano - 86400_000_000_000L)
            {
                nowNano = Math.Max(nowNano, latestTs);
            }
        }

        WindowEnd = nowNano;
        WindowStart = Math.Max(0, nowNano - (spanSec * 1_000_000_000L));

        if (SelectedNode != null)
        {
            _ = FetchHistoricalTelemetryAsync(SelectedNode, WindowStart, WindowEnd);
        }
    }

    private async Task FetchHistoricalTelemetryAsync(NoxNodeModel node, long startNano, long endNano)
    {
        try
        {
            var client = _nodeManager.GetClient(node);
            var metrics = new[]
            {
                "llm.tokens.in",
                "llm.tokens.out",
                "llm.tokens.cached",
                "py.cpu.total_pct",
                "py.mem.rss_bytes",
                "py.runtime.event_loop_lag_ms"
            };

            var tasks = metrics.Select(m => client.QueryTelemetryRangeAsync(new RangeQueryRequest
            {
                NodeId = node.Id,
                MetricName = m,
                StartTimeUnixNano = startNano,
                EndTimeUnixNano = endNano,
                TargetPoints = 1200
            }).ResponseAsync).ToList();

            var responses = await Task.WhenAll(tasks);

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var resp in responses)
                {
                    if (resp == null || resp.Points == null) continue;
                    var (series, isRss) = resp.MetricName switch
                    {
                        "llm.tokens.in" => (_tokensInSeries, false),
                        "llm.tokens.out" => (_tokensOutSeries, false),
                        "llm.tokens.cached" => (_tokensCachedSeries, false),
                        "py.cpu.total_pct" => (_cpuSeries, false),
                        "py.mem.rss_bytes" => (_rssSeries, true),
                        "py.runtime.event_loop_lag_ms" => (_lagSeries, false),
                        _ => ((ChartSeriesModel?)null, false)
                    };
                    if (series == null) continue;

                    if (resp.Points.Count > 0)
                    {
                        var ts = new long[resp.Points.Count];
                        var vals = new double[resp.Points.Count];
                        for (int i = 0; i < resp.Points.Count; i++)
                        {
                            ts[i] = resp.Points[i].TimestampUnixNano;
                            vals[i] = isRss ? (resp.Points[i].Value / 1048576.0) : resp.Points[i].Value;
                        }
                        series.Timestamps = ts;
                        series.Values = vals;
                        series.LatestValue = vals[^1];
                    }
                }
            });
        }
        catch { }
    }
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

        long spanSec = GetSelectedSpanSeconds();
        long sampleNano = sample.TimestampUnixNano;
        WindowStart = Math.Max(0, sampleNano - (spanSec * 1_000_000_000L));
        WindowEnd = sampleNano;

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

        // Append to series buffers (timestamps in nanoseconds)
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

        tsList.Add(timestampNano);
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
