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

public sealed record SfTimeframeOption(string Label, int Seconds);

public sealed record SfKeyCountRow(string Key, int Count);

public sealed record SfTopUserRow(long UserId, string? Username, int Jobs)
{
    public string Label => !string.IsNullOrWhiteSpace(Username) ? Username : UserId.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class SfOverviewViewModel : ObservableObject
{
    public const int HistoryCapacity = 900;
    public const int MaxRawPoints = 1200;
    public const long RawRetentionNano = 900L * 1_000_000_000L;
    public const int CoarseBucketSeconds = 10;
    public const long CoarseBucketNano = CoarseBucketSeconds * 1_000_000_000L;
    public const int MaxCoarseBuckets = 8640;
    public const long CoarseRetentionNano = 86400L * 1_000_000_000L;
    public const int MaxChartPoints = 600;
    private const long LongRangeRebuildIntervalNano = 2_000_000_000L;

    private SfRpc? _rpc;
    private long _latestTimestampNano;
    private long _lastChartRebuildSampleTsNano = long.MinValue;

    private readonly CoarseRing _activeCoarse = new(MaxCoarseBuckets);
    private readonly CoarseRing _queuedCoarse = new(MaxCoarseBuckets);

    public IReadOnlyList<SfTimeframeOption> Timeframes { get; } = new SfTimeframeOption[]
    {
        new("1m", 60),
        new("5m", 300),
        new("15m", 900),
        new("1h", 3600),
        new("6h", 21600),
        new("24h", 86400),
    };

    [ObservableProperty] private SfTimeframeOption _selectedTimeframe;

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

    public int ActiveCoarseBucketCount => _activeCoarse.Count;
    public int QueuedCoarseBucketCount => _queuedCoarse.Count;

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

    partial void OnSelectedTimeframeChanged(SfTimeframeOption value)
    {
        if (value is null) return;
        UpdateWindowBounds(_latestTimestampNano, value.Seconds);
        RebuildChartSeries();
        _lastChartRebuildSampleTsNano = _latestTimestampNano > 0 ? _latestTimestampNano : long.MinValue;
    }

    public SfOverviewViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
        _selectedTimeframe = Timeframes[1]; // default 5m
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
        bool seriesDirty = false;

        if (m.TryGetValue("jobs_active", out double activeVal))
        {
            JobsActive = (int)Math.Round(activeVal);
            AppendRaw(ActiveJobsHistory, ts, activeVal);
            if (ts > 0)
                _activeCoarse.Append(ts, activeVal);
            _activeSeries.LatestValue = activeVal;
            seriesDirty = true;
        }

        if (m.TryGetValue("jobs_queued", out double queuedVal))
        {
            JobsQueued = (int)Math.Round(queuedVal);
            AppendRaw(QueuedJobsHistory, ts, queuedVal);
            if (ts > 0)
                _queuedCoarse.Append(ts, queuedVal);
            _queuedSeries.LatestValue = queuedVal;
            seriesDirty = true;
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
            _latestTimestampNano = ts;
            UpdateWindowBounds(ts, SelectedTimeframe.Seconds);
        }

        if (seriesDirty)
        {
            bool throttleLongRange =
                SelectedTimeframe.Seconds >= 3600 &&
                ts > 0 &&
                _lastChartRebuildSampleTsNano != long.MinValue &&
                (ts - _lastChartRebuildSampleTsNano) < LongRangeRebuildIntervalNano;

            if (!throttleLongRange)
            {
                RebuildChartSeries();
                if (ts > 0)
                    _lastChartRebuildSampleTsNano = ts;
            }
        }
    }

    private void UpdateWindowBounds(long latestTs, int timeframeSeconds)
    {
        if (latestTs <= 0) return;
        WindowEnd = latestTs;
        WindowStart = Math.Max(0, latestTs - timeframeSeconds * 1_000_000_000L);
    }

    private static void AppendRaw(ObservableCollection<SfHistoryPoint> list, long ts, double value)
    {
        list.Add(new SfHistoryPoint(ts, value));
        if (ts > 0)
        {
            long cutoff = ts - RawRetentionNano;
            while (list.Count > 0 && list[0].TimestampUnixNano <= cutoff)
                list.RemoveAt(0);
        }
        while (list.Count > MaxRawPoints)
            list.RemoveAt(0);
    }

    private void RebuildChartSeries()
    {
        int tfSeconds = SelectedTimeframe.Seconds;
        long windowEnd = _latestTimestampNano;
        long windowStart = windowEnd > 0 ? Math.Max(0, windowEnd - tfSeconds * 1_000_000_000L) : 0;

        if (tfSeconds <= 900)
        {
            SyncFromRaw(_activeSeries, ActiveJobsHistory, windowStart, windowEnd);
            SyncFromRaw(_queuedSeries, QueuedJobsHistory, windowStart, windowEnd);
        }
        else
        {
            SyncFromCoarse(_activeSeries, _activeCoarse, windowStart, windowEnd);
            SyncFromCoarse(_queuedSeries, _queuedCoarse, windowStart, windowEnd);
        }
    }

    private static void SyncFromRaw(ChartSeriesModel series, ObservableCollection<SfHistoryPoint> list, long windowStart, long windowEnd)
    {
        int n = list.Count;
        if (n == 0)
        {
            series.Timestamps = Array.Empty<long>();
            series.Values = Array.Empty<double>();
            return;
        }

        int firstIdx = 0;
        if (windowEnd > 0)
        {
            while (firstIdx < n && list[firstIdx].TimestampUnixNano < windowStart)
                firstIdx++;
        }

        int lastIdx = n - 1;
        if (windowEnd > 0)
        {
            while (lastIdx >= firstIdx && list[lastIdx].TimestampUnixNano > windowEnd)
                lastIdx--;
        }

        int count = lastIdx - firstIdx + 1;
        if (count <= 0)
        {
            series.Timestamps = Array.Empty<long>();
            series.Values = Array.Empty<double>();
            return;
        }

        var ts = new long[count];
        var vals = new double[count];
        for (int i = 0; i < count; i++)
        {
            var pt = list[firstIdx + i];
            ts[i] = pt.TimestampUnixNano;
            vals[i] = pt.Value;
        }

        series.Timestamps = ts;
        series.Values = vals;
    }

    private static void SyncFromCoarse(ChartSeriesModel series, CoarseRing ring, long windowStart, long windowEnd)
    {
        int total = ring.Count;
        if (total == 0)
        {
            series.Timestamps = Array.Empty<long>();
            series.Values = Array.Empty<double>();
            return;
        }

        int firstIdx = 0;
        if (windowEnd > 0)
        {
            while (firstIdx < total && ring[firstIdx].TimestampEndNano < windowStart)
                firstIdx++;
        }

        int lastIdx = total - 1;
        if (windowEnd > 0)
        {
            while (lastIdx >= firstIdx && ring[lastIdx].BucketStartNano > windowEnd)
                lastIdx--;
        }

        int count = lastIdx - firstIdx + 1;
        if (count <= 0)
        {
            series.Timestamps = Array.Empty<long>();
            series.Values = Array.Empty<double>();
            return;
        }

        if (count <= MaxChartPoints)
        {
            var ts = new long[count];
            var vals = new double[count];
            for (int i = 0; i < count; i++)
            {
                var b = ring[firstIdx + i];
                ts[i] = b.TimestampEndNano;
                vals[i] = b.Average;
            }
            series.Timestamps = ts;
            series.Values = vals;
            return;
        }

        int groupSize = (count + MaxChartPoints - 1) / MaxChartPoints;
        int outCount = (count + groupSize - 1) / groupSize;
        var outTs = new long[outCount];
        var outVals = new double[outCount];

        int outIdx = 0;
        for (int i = 0; i < count; i += groupSize)
        {
            int end = Math.Min(i + groupSize, count);
            double sum = 0.0;
            int samples = 0;
            long groupEndTs = 0;

            for (int j = i; j < end; j++)
            {
                var b = ring[firstIdx + j];
                sum += b.Sum;
                samples += b.Count;
                groupEndTs = b.TimestampEndNano;
            }

            outTs[outIdx] = groupEndTs;
            outVals[outIdx] = samples > 0 ? sum / samples : 0.0;
            outIdx++;
        }

        series.Timestamps = outTs;
        series.Values = outVals;
    }

    private struct CoarseBucket
    {
        public long BucketIndex;
        public long BucketStartNano;
        public long TimestampEndNano;
        public double Sum;
        public int Count;
        public double Average => Count > 0 ? Sum / Count : 0.0;
    }

    private sealed class CoarseRing
    {
        private readonly CoarseBucket[] _buffer;
        private int _head;
        private int _count;

        public CoarseRing(int capacity)
        {
            _buffer = new CoarseBucket[capacity];
        }

        public int Count => _count;

        public CoarseBucket this[int index] => _buffer[(_head + index) % _buffer.Length];

        public void Append(long tsNano, double value)
        {
            long bucketIndex = tsNano / CoarseBucketNano;

            if (_count > 0)
            {
                int tailPos = (_head + _count - 1) % _buffer.Length;
                ref var tail = ref _buffer[tailPos];
                if (tail.BucketIndex == bucketIndex)
                {
                    tail.Sum += value;
                    tail.Count++;
                    return;
                }
                if (bucketIndex < tail.BucketIndex)
                {
                    return;
                }
            }

            if (_count == _buffer.Length)
            {
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }

            int insertPos = (_head + _count) % _buffer.Length;
            _buffer[insertPos] = new CoarseBucket
            {
                BucketIndex = bucketIndex,
                BucketStartNano = bucketIndex * CoarseBucketNano,
                TimestampEndNano = (bucketIndex + 1) * CoarseBucketNano,
                Sum = value,
                Count = 1,
            };
            _count++;

            long cutoff = tsNano - CoarseRetentionNano;
            while (_count > 0 && _buffer[_head].TimestampEndNano < cutoff)
            {
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }
        }
    }
}
