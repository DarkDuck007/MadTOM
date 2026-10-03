using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MadTOM.Models;

namespace MadTOM.Services;

public sealed record AggregatedMetricValue(
    double Sum,
    double Avg,
    double Min,
    double Max,
    int ContributingNodes
);

public sealed class ClusterAggregationSnapshot
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public long TimestampUnixNano { get; init; }
    public int ActiveNodeCount { get; init; }
    public ClusterTelemetrySummary Summary { get; init; } = new();
    public IReadOnlyDictionary<string, AggregatedMetricValue> AggregatedMetrics { get; init; } = new Dictionary<string, AggregatedMetricValue>();
    public IReadOnlyDictionary<string, double> MetricRatesPerSecond { get; init; } = new Dictionary<string, double>();

    public bool TryGetMetric(string metricKey, out AggregatedMetricValue value)
    {
        return AggregatedMetrics.TryGetValue(metricKey, out value!);
    }
}

public interface IClusterAggregationService : IDisposable
{
    event EventHandler<ClusterAggregationSnapshot>? AggregatedSnapshotAvailable;
    ClusterAggregationSnapshot LatestSnapshot { get; }
    (double Sum, double Avg, double Rate) GetMetricValues(string key);
    ClusterTelemetrySummary GetClusterSummary();
    void ForceFlush();
}

public sealed class ClusterAggregationService : IClusterAggregationService
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly TimeSpan _bucketTimeout;
    private readonly Timer _bucketTimer;
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<string, FleetNodeModel> _latestNodeSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedNodesInBucket = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _previousSums = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _previousFlushTime = DateTime.MinValue;

    private ClusterAggregationSnapshot _latestSnapshot = new();
    private bool _disposed;

    public event EventHandler<ClusterAggregationSnapshot>? AggregatedSnapshotAvailable;

    public ClusterAggregationSnapshot LatestSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _latestSnapshot;
            }
        }
        private set
        {
            lock (_lock)
            {
                _latestSnapshot = value;
            }
        }
    }

    public ClusterAggregationService(ITelemetryDataProvider telemetryProvider, TimeSpan? bucketTimeout = null)
    {
        _telemetryProvider = telemetryProvider ?? throw new ArgumentNullException(nameof(telemetryProvider));
        _bucketTimeout = bucketTimeout ?? TimeSpan.FromMilliseconds(1000);

        _telemetryProvider.NodeTelemetryUpdated += OnNodeTelemetryUpdated;

        // Initial snapshot
        ForceFlush();

        // Background timer to guarantee that even if some nodes fail to report,
        // a bucket timeout flushes partial/latest data periodically without stalling the UI.
        _bucketTimer = new Timer(OnBucketTimeout, null, _bucketTimeout, _bucketTimeout);
    }

    private void OnNodeTelemetryUpdated(object? sender, FleetNodeModel node)
    {
        if (_disposed || node == null) return;

        bool shouldFlush = false;
        lock (_lock)
        {
            _latestNodeSnapshots[node.Id] = node;
            _reportedNodesInBucket.Add(node.Id);

            var fleet = _telemetryProvider.GetFleetNodes();
            int activeFleetCount = fleet.Count(n => !string.Equals(n.Status, "offline", StringComparison.OrdinalIgnoreCase));
            if (activeFleetCount > 0 && _reportedNodesInBucket.Count >= activeFleetCount)
            {
                shouldFlush = true;
            }
        }

        if (shouldFlush)
        {
            FlushBucket(false);
        }
    }

    private void OnBucketTimeout(object? state)
    {
        if (_disposed) return;
        FlushBucket(false);
    }

    public void ForceFlush()
    {
        FlushBucket(true);
    }

    private void FlushBucket(bool isForced = false)
    {
        ClusterAggregationSnapshot snapshot;
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            double dt = _previousFlushTime == DateTime.MinValue ? 1.0 : (now - _previousFlushTime).TotalSeconds;
            if (!isForced && dt <= 0.05)
            {
                // Prevent duplicate auto flushes in tiny fractions of a second
                return;
            }
            if (dt <= 0.001) dt = 1.0;

            var fleet = _telemetryProvider.GetFleetNodes();
            var activeNodes = fleet.Count > 0 ? fleet : _latestNodeSnapshots.Values.ToList();
            int count = activeNodes.Count;

            var aggregated = new Dictionary<string, AggregatedMetricValue>(StringComparer.OrdinalIgnoreCase);
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Compute relevant cluster-wide metrics.
            // Explicitly exclude individual per-thread metrics (e.g. cpu.core.*) from merged pool.
            var keysToAggregate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cpu.total", "cpu.load", "cpu.user", "cpu.system", "cpu.iowait",
                "memory.used", "memory.total", "memory.available", "memory.bytes",
                "memory.swap_used", "memory.swap_total",
                "network.ingress", "network.egress", "network.rx_bytes", "network.tx_bytes",
                "disk.bytes.read", "disk.bytes.write", "disk.io.read_bytes", "disk.io.write_bytes",
                "disk.ops", "disk.io.read_ops", "disk.io.write_ops",
                "twamp.rtt", "twamp.forward", "twamp.reverse"
            };

            // Collect any additional dynamic keys from latest values, but strictly ignore per-core
            foreach (var node in activeNodes)
            {
                foreach (var k in node.LatestMetricValues.Keys)
                {
                    if (IsRelevantClusterMetric(k))
                    {
                        keysToAggregate.Add(k);
                    }
                }
            }

            foreach (var key in keysToAggregate)
            {
                var values = new List<double>();
                foreach (var n in activeNodes)
                {
                    double? val = n.GetMetricValue(key);
                    if (val.HasValue)
                    {
                        values.Add(val.Value);
                    }
                }

                if (values.Count > 0)
                {
                    double sum = values.Sum();
                    double avg = values.Average();
                    double min = values.Min();
                    double max = values.Max();

                    aggregated[key] = new AggregatedMetricValue(sum, avg, min, max, values.Count);

                    if (_previousSums.TryGetValue(key, out double prevSum))
                    {
                        rates[key] = (sum - prevSum) / dt;
                    }
                    _previousSums[key] = sum;
                }
            }

            long tsNano = activeNodes.Count > 0
                ? activeNodes.Max(n => n.TimestampUnixNano)
                : now.Ticks * 100L;

            snapshot = new ClusterAggregationSnapshot
            {
                TimestampUtc = now,
                TimestampUnixNano = tsNano,
                ActiveNodeCount = count,
                Summary = _telemetryProvider.GetClusterSummary(),
                AggregatedMetrics = aggregated,
                MetricRatesPerSecond = rates
            };

            _previousFlushTime = now;
            _reportedNodesInBucket.Clear();
            _latestSnapshot = snapshot;
        }

        AggregatedSnapshotAvailable?.Invoke(this, snapshot);
    }

    public static bool IsRelevantClusterMetric(string metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey)) return false;

        // Explicitly reject per-thread / per-core metrics
        if (metricKey.StartsWith("cpu.core.", StringComparison.OrdinalIgnoreCase) ||
            metricKey.Contains(".cpu.core.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Relevant cluster metrics:
        return metricKey.StartsWith("cpu.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("memory.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("swap.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("zram.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("network", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("nic.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("disk.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("twamp.", StringComparison.OrdinalIgnoreCase) ||
               metricKey.StartsWith("power.", StringComparison.OrdinalIgnoreCase);
    }

    public (double Sum, double Avg, double Rate) GetMetricValues(string key)
    {
        var snap = LatestSnapshot;
        if (snap.TryGetMetric(key, out var agg))
        {
            snap.MetricRatesPerSecond.TryGetValue(key, out double rate);
            return (agg.Sum, agg.Avg, rate);
        }

        // Fallback calculation if key not in pre-aggregated table
        var nodes = _telemetryProvider.GetFleetNodes();
        if (nodes.Count == 0) return (0, 0, 0);

        var vals = nodes.Select(n => n.GetMetricValue(key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (vals.Count == 0) return (0, 0, 0);

        double s = vals.Sum();
        double a = vals.Average();
        snap.MetricRatesPerSecond.TryGetValue(key, out double r);
        return (s, a, r);
    }

    public ClusterTelemetrySummary GetClusterSummary()
    {
        return LatestSnapshot.Summary;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _telemetryProvider.NodeTelemetryUpdated -= OnNodeTelemetryUpdated;
        _bucketTimer.Dispose();
    }
}
