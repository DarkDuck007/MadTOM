using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MadTOM.Models;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfHistoryPoint(long TimestampUnixNano, double Value);

public sealed record SfKeyCountRow(string Key, int Count);

public sealed record SfTopUserRow(long UserId, string? Username, int Jobs)
{
    public string Label => !string.IsNullOrWhiteSpace(Username) ? Username : UserId.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class SfOverviewViewModel : ObservableObject
{
    public const int HistoryCapacity = 600;

    private SfRpc? _rpc;

    [ObservableProperty] private int _jobs7d;
    [ObservableProperty] private double _failureRate7d;
    [ObservableProperty] private double _avgDurationS7d;
    [ObservableProperty] private int _active;
    [ObservableProperty] private int _usersTotal;

    [ObservableProperty] private int _jobsActive;
    [ObservableProperty] private int _jobsQueued;
    [ObservableProperty] private int _jobsWaitingInput;
    [ObservableProperty] private bool _bridgeOk;
    [ObservableProperty] private bool _sandboxOk;
    [ObservableProperty] private bool _agyOk;
    [ObservableProperty] private int _quotaUsed7dTotal;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private long _windowStart;
    [ObservableProperty] private long _windowEnd;

    public Dictionary<string, int> JobsByStatus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> JobsByFormat7d { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<SfKeyCountRow> StatusBreakdown { get; } = new();
    public ObservableCollection<SfKeyCountRow> FormatBreakdown { get; } = new();
    public ObservableCollection<SfTopUserRow> TopUsers { get; } = new();

    public ObservableCollection<SfHistoryPoint> ActiveJobsHistory { get; } = new();
    public ObservableCollection<SfHistoryPoint> QueuedJobsHistory { get; } = new();

    public ObservableCollection<ChartSeriesModel> JobsSeries { get; } = new();
    private readonly ChartSeriesModel _activeSeries = new("jobs_active", "Active Jobs", "#10B981");
    private readonly ChartSeriesModel _queuedSeries = new("jobs_queued", "Queued Jobs", "#F59E0B");

    public string FailureRateDisplay => $"{FailureRate7d * 100.0:F1}%";
    public string AvgDurationDisplay => $"{AvgDurationS7d:F1}s";
    public string BridgeBadge => BridgeOk ? "ONLINE" : "OFFLINE";
    public string SandboxBadge => SandboxOk ? "ONLINE" : "OFFLINE";
    public string AgyBadge => AgyOk ? "ONLINE" : "OFFLINE";

    partial void OnFailureRate7dChanged(double value) => OnPropertyChanged(nameof(FailureRateDisplay));
    partial void OnAvgDurationS7dChanged(double value) => OnPropertyChanged(nameof(AvgDurationDisplay));
    partial void OnBridgeOkChanged(bool value) => OnPropertyChanged(nameof(BridgeBadge));
    partial void OnSandboxOkChanged(bool value) => OnPropertyChanged(nameof(SandboxBadge));
    partial void OnAgyOkChanged(bool value) => OnPropertyChanged(nameof(AgyBadge));

    public SfOverviewViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
        JobsSeries.Add(_activeSeries);
        JobsSeries.Add(_queuedSeries);
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("get_overview", null, ct);

            Jobs7d = SfJsonHelpers.GetInt(root, "jobs_7d");
            FailureRate7d = SfJsonHelpers.GetDouble(root, "failure_rate_7d");
            AvgDurationS7d = SfJsonHelpers.GetDouble(root, "avg_duration_s_7d");
            Active = SfJsonHelpers.GetInt(root, "active");
            UsersTotal = SfJsonHelpers.GetInt(root, "users_total");

            JobsByStatus.Clear();
            StatusBreakdown.Clear();
            if (root.TryGetProperty("jobs_by_status", out var byStatus) && byStatus.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in byStatus.EnumerateObject())
                {
                    int c = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int v) ? v : 0;
                    JobsByStatus[prop.Name] = c;
                    StatusBreakdown.Add(new SfKeyCountRow(prop.Name, c));
                }
            }
            OnPropertyChanged(nameof(JobsByStatus));

            JobsByFormat7d.Clear();
            FormatBreakdown.Clear();
            if (root.TryGetProperty("jobs_by_format_7d", out var byFormat) && byFormat.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in byFormat.EnumerateObject())
                {
                    int c = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int v) ? v : 0;
                    JobsByFormat7d[prop.Name] = c;
                    FormatBreakdown.Add(new SfKeyCountRow(prop.Name, c));
                }
            }
            OnPropertyChanged(nameof(JobsByFormat7d));

            TopUsers.Clear();
            if (root.TryGetProperty("top_users_7d", out var topUsers) && topUsers.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in topUsers.EnumerateArray())
                {
                    TopUsers.Add(new SfTopUserRow(
                        SfJsonHelpers.GetLong(item, "user_id"),
                        SfJsonHelpers.GetStringOrNull(item, "username"),
                        SfJsonHelpers.GetInt(item, "jobs")));
                }
            }

            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyTelemetry(SfTelemetrySample sample)
    {
        if (sample is null) return;

        var m = sample.Metrics;
        long ts = sample.TimestampUnixNano;

        if (m.TryGetValue("jobs_active", out double activeVal))
        {
            JobsActive = (int)Math.Round(activeVal);
            AppendBounded(ActiveJobsHistory, ts, activeVal);
            SyncChartSeries(_activeSeries, ActiveJobsHistory, activeVal);
        }

        if (m.TryGetValue("jobs_queued", out double queuedVal))
        {
            JobsQueued = (int)Math.Round(queuedVal);
            AppendBounded(QueuedJobsHistory, ts, queuedVal);
            SyncChartSeries(_queuedSeries, QueuedJobsHistory, queuedVal);
        }

        if (m.TryGetValue("jobs_waiting_input", out double waitingVal))
            JobsWaitingInput = (int)Math.Round(waitingVal);

        if (m.TryGetValue("bridge_ok", out double bridgeVal))
            BridgeOk = bridgeVal >= 0.5;

        if (m.TryGetValue("sandbox_ok", out double sandboxVal))
            SandboxOk = sandboxVal >= 0.5;

        if (m.TryGetValue("agy_ok", out double agyVal))
            AgyOk = agyVal >= 0.5;

        if (m.TryGetValue("quota_used_7d_total", out double quotaVal))
            QuotaUsed7dTotal = (int)Math.Round(quotaVal);

        if (ts > 0)
        {
            WindowEnd = ts;
            WindowStart = Math.Max(0, ts - 600_000_000_000L);
        }
    }

    private static void AppendBounded(ObservableCollection<SfHistoryPoint> list, long ts, double value)
    {
        list.Add(new SfHistoryPoint(ts, value));
        while (list.Count > HistoryCapacity)
            list.RemoveAt(0);
    }

    private static void SyncChartSeries(ChartSeriesModel series, ObservableCollection<SfHistoryPoint> list, double latest)
    {
        var ts = new long[list.Count];
        var vals = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            ts[i] = list[i].TimestampUnixNano;
            vals[i] = list[i].Value;
        }
        series.Timestamps = ts;
        series.Values = vals;
        series.LatestValue = latest;
    }
}
