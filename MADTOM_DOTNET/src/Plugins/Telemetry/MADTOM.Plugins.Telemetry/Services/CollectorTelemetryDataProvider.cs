using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MadTOM.Models;
using MadTOM.Common;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

/// <summary>
/// Production telemetry data provider that communicates directly with
/// one or more madtom-collector instances via MultiCollectorManager.
/// </summary>
public sealed class CollectorTelemetryDataProvider : ITelemetryDataProvider
{
    private readonly MultiCollectorManager _collectorManager;
    private readonly ConcurrentDictionary<string, FleetNodeModel> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streamCancelTokens = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSampleReceived = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _refreshTimer;
    private int _polling;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<string, NodeDisplaySnapshot> _pendingDisplaySnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NodeIngestState> _nodeIngestStates = new(StringComparer.OrdinalIgnoreCase);
    private int _displayDrainScheduled;

    public TelemetryHistoryCache HistoryCache { get; }
    private readonly HistoryQueryCoordinator _historyQueries = new();
    private readonly ConcurrentDictionary<(string, string), (NodeConfig Config, DateTime Expires)> _configs = new();
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public MultiCollectorManager CollectorManager => _collectorManager;
    public ClientCompressionSnapshot GetCompressionDiagnostics() => _collectorManager.GetCompressionDiagnostics(HistoryCache);

    public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
#pragma warning disable CS0067 // Event required by ITelemetryDataProvider; remote log collection is not yet available on the collector
    public event EventHandler<LogEntryModel>? LogReceived;
#pragma warning restore CS0067

    public CollectorTelemetryDataProvider(MultiCollectorManager collectorManager, TelemetryHistoryCache? historyCache = null)
    {
        _collectorManager = collectorManager;
        HistoryCache = historyCache ?? new TelemetryHistoryCache();
        if (historyCache == null)
        {
            var settings = new TelemetryCacheSettingsStore().Load();
            HistoryCache.Configure(settings.RetentionMinutes, settings.LiveLimitMiB * 1048576L,
                settings.StoredLimitMiB * 1048576L, settings.StoredRetentionSeconds);
        }

        // Poll collectors every 3 seconds for new/updated nodes
        _refreshTimer = new Timer(OnPollCollectorsTick, null, 100, 3000);
        UiPerformanceDiagnostics.CacheStatsProvider = () => HistoryCache.CompactStats;
    }

    private async void OnPollCollectorsTick(object? state)
    {
        if (_disposeCts.IsCancellationRequested || Interlocked.Exchange(ref _polling, 1) != 0) return;

        try
        {
            HistoryCache.Prune();

            // Stream watchdog: if a node has an active stream registered but hasn't received
            // any telemetry sample in >8 seconds, abort the hung stream token so it will reconnect.
            var now = DateTime.UtcNow;
            foreach (var kvp in _streamCancelTokens)
            {
                if (_lastSampleReceived.TryGetValue(kvp.Key, out var lastRx) && (now - lastRx) > TimeSpan.FromSeconds(8))
                {
                    try { kvp.Value.Cancel(); } catch { }
                    _streamCancelTokens.TryRemove(kvp.Key, out _);
                }
            }

            var discoveredNodes = await _collectorManager.FetchAllNodesAsync(_disposeCts.Token);
            var discoveredIds = new HashSet<string>(discoveredNodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var node in discoveredNodes)
            {
                if (_nodes.TryAdd(node.Id, node))
                {
                    // Start live subscription for newly discovered node
                    StartNodeLiveStream(node);
                    Dispatcher.UIThread.Post(() => NodeTelemetryUpdated?.Invoke(this, node));
                }
                else if (_nodes.TryGetValue(node.Id, out var existing))
                {
                    StartNodeLiveStream(existing);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposeCts.IsCancellationRequested) return;
                        existing.Status = node.Status;
                        existing.CollectorName = node.CollectorName;
                        existing.CollectorEndpoint = node.CollectorEndpoint;
                        NodeTelemetryUpdated?.Invoke(this, existing);
                    });
                }
            }

            // If a previously discovered node was not returned in this cycle, mark it offline
            foreach (var kvp in _nodes)
            {
                if (!discoveredIds.Contains(kvp.Key) && kvp.Value.Status != "offline")
                {
                    var stale = kvp.Value;
                    stale.Status = "offline";
                    Dispatcher.UIThread.Post(() => NodeTelemetryUpdated?.Invoke(this, stale));
                }
            }
        }
        catch
        {
            // Transient network failure: mark nodes as offline if they haven't received telemetry recently
            var now = DateTime.UtcNow;
            foreach (var kvp in _nodes)
            {
                if (_lastSampleReceived.TryGetValue(kvp.Key, out var lastRx) && (now - lastRx) > TimeSpan.FromSeconds(6))
                {
                    var stale = kvp.Value;
                    if (stale.Status != "offline")
                    {
                        stale.Status = "offline";
                        Dispatcher.UIThread.Post(() => NodeTelemetryUpdated?.Invoke(this, stale));
                    }
                }
            }
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void StartNodeLiveStream(FleetNodeModel node)
    {
        if (_streamCancelTokens.ContainsKey(node.Id)) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        if (!_streamCancelTokens.TryAdd(node.Id, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _collectorManager.StartLiveStreamAsync(node, ev =>
                {
                    if (ev.Metrics != null)
                    {
                        UpdateNodeFromMetrics(node, ev.Metrics);
                    }
                }, cts.Token);
            }
            catch
            {
                // Reconnect will be triggered on next poll
            }
            finally
            {
                _streamCancelTokens.TryRemove(node.Id, out _);
                cts.Dispose();
            }
        }, cts.Token);
    }

    public static double[] PushSparkline(double[]? existing, double newVal, int maxCapacity = 60)
    {
        if (existing == null || existing.Length == 0)
        {
            var initial = new double[maxCapacity];
            initial[^1] = Math.Round(newVal, 2);
            return initial;
        }

        var next = new double[maxCapacity];
        int copyLen = Math.Min(existing.Length, maxCapacity - 1);
        int srcOffset = Math.Max(0, existing.Length - copyLen);
        int destOffset = maxCapacity - 1 - copyLen;
        Array.Copy(existing, srcOffset, next, destOffset, copyLen);
        next[maxCapacity - 1] = Math.Round(newVal, 2);
        return next;
    }

    public void IngestMetricsForTesting(FleetNodeModel node, SystemMetrics s) => UpdateNodeFromMetrics(node, s);
    public void FlushDisplayUpdatesForTesting() => DrainDisplayUpdates();

    private void UpdateNodeFromMetrics(FleetNodeModel node, SystemMetrics s)
    {
        if (_disposeCts.IsCancellationRequested) return;

        var receivedUtc = DateTime.UtcNow;
        _lastSampleReceived[node.Id] = receivedUtc;

        var state = _nodeIngestStates.GetOrAdd(node.Id, _ => new NodeIngestState());

        NodeDisplaySnapshot snapshot;
        lock (state)
        {
            if (s.TimestampUnixNano <= state.LastTimestampNano) return;

            double seconds = state.HadSample && s.TimestampUnixNano > state.LastTimestampNano
                ? (s.TimestampUnixNano - state.LastTimestampNano) / 1e9
                : 0;
            bool hadSample = state.HadSample;

            // Network rates
            NicMetric[] interfaces = Array.Empty<NicMetric>();
            double txRate = 0, rxRate = 0;
            if (s.Network != null && s.Network.Interfaces.Count > 0)
            {
                interfaces = s.Network.Interfaces.ToArray();
                if (hadSample && seconds > 0)
                {
                    double txSum = 0, rxSum = 0;
                    foreach (var i in interfaces)
                    {
                        if (state.LastInterfaces.TryGetValue(i.Name, out var prev))
                        {
                            if (i.TxBytes >= prev.Tx) txSum += (i.TxBytes - prev.Tx) / seconds;
                            if (i.RxBytes >= prev.Rx) rxSum += (i.RxBytes - prev.Rx) / seconds;
                        }
                    }
                    txRate = txSum;
                    rxRate = rxSum;
                }

                state.LastInterfaces.Clear();
                foreach (var i in interfaces)
                {
                    state.LastInterfaces[i.Name] = (i.RxBytes, i.TxBytes);
                }
            }

            // Disk rates
            DiskIoDevice[] disks = Array.Empty<DiskIoDevice>();
            double diskReadRate = 0, diskWriteRate = 0;
            if (s.DiskIo != null)
            {
                if (s.DiskIo.Devices.Count > 0)
                {
                    disks = s.DiskIo.Devices.ToArray();
                }

                if (hadSample && seconds > 0)
                {
                    if (s.DiskIo.ReadBytes >= state.LastDiskRead)
                        diskReadRate = (s.DiskIo.ReadBytes - state.LastDiskRead) / seconds;
                    if (s.DiskIo.WriteBytes >= state.LastDiskWrite)
                        diskWriteRate = (s.DiskIo.WriteBytes - state.LastDiskWrite) / seconds;
                }

                state.LastDiskRead = s.DiskIo.ReadBytes;
                state.LastDiskWrite = s.DiskIo.WriteBytes;
            }

            state.LastTimestampNano = s.TimestampUnixNano;
            state.HadSample = true;

            // Populate all raw/computed metric keys for live graph streaming
            node.LatestMetricValues.Clear();

            if (s.Cpu != null)
            {
                node.LatestMetricValues["cpu.total"] = s.Cpu.TotalPct;
                node.LatestMetricValues["cpu.user"] = s.Cpu.UserPct;
                node.LatestMetricValues["cpu.system"] = s.Cpu.SystemPct;
                node.LatestMetricValues["cpu.iowait"] = s.Cpu.IowaitPct;
                for (int i = 0; i < s.Cpu.PerCorePct.Count; i++)
                {
                    node.LatestMetricValues[$"cpu.core.{i}"] = s.Cpu.PerCorePct[i];
                }
            }

            double ramUsedPct = 0;
            string ramTotal = "Unknown";
            double zramRatio = 0;
            double swapUsedPct = 0;
            if (s.Memory != null && s.Memory.MemTotalBytes > 0)
            {
                double usedBytes = (double)(s.Memory.MemTotalBytes - Math.Min(s.Memory.MemTotalBytes, s.Memory.MemAvailableBytes));
                ramUsedPct = Math.Round((usedBytes / s.Memory.MemTotalBytes) * 100.0, 1);
                ramTotal = $"{Math.Round((double)s.Memory.MemTotalBytes / (1024 * 1024 * 1024), 1)} GB";
                zramRatio = Math.Round(s.Memory.ZramRatio, 2);

                if (s.Memory.SwapTotalBytes > 0)
                {
                    double swapUsed = s.Memory.SwapUsedBytes > 0
                        ? s.Memory.SwapUsedBytes
                        : (double)(s.Memory.SwapTotalBytes - Math.Min(s.Memory.SwapTotalBytes, s.Memory.SwapFreeBytes));
                    swapUsedPct = Math.Round((swapUsed / s.Memory.SwapTotalBytes) * 100.0, 1);
                }

                node.LatestMetricValues["memory.total"] = s.Memory.MemTotalBytes;
                node.LatestMetricValues["memory.available"] = s.Memory.MemAvailableBytes;
                node.LatestMetricValues["memory.used"] = usedBytes;
                node.LatestMetricValues["memory.swap_total"] = s.Memory.SwapTotalBytes;
                node.LatestMetricValues["memory.swap_used"] = s.Memory.SwapUsedBytes > 0
                    ? s.Memory.SwapUsedBytes
                    : (double)(s.Memory.SwapTotalBytes - Math.Min(s.Memory.SwapTotalBytes, s.Memory.SwapFreeBytes));

                if (s.Memory.SwapDevices != null)
                {
                    foreach (var swapDev in s.Memory.SwapDevices)
                    {
                        if (swapDev != null && !string.IsNullOrEmpty(swapDev.Name))
                        {
                            node.LatestMetricValues[$"swap.{swapDev.Name}.total_bytes"] = swapDev.TotalBytes;
                            node.LatestMetricValues[$"swap.{swapDev.Name}.used_bytes"] = swapDev.UsedBytes;
                        }
                    }
                }

                if (s.Memory.ZramDevices != null)
                {
                    foreach (var zramDev in s.Memory.ZramDevices)
                    {
                        if (zramDev != null && !string.IsNullOrEmpty(zramDev.Name))
                        {
                            node.LatestMetricValues[$"zram.{zramDev.Name}.disksize_bytes"] = zramDev.DisksizeBytes;
                            node.LatestMetricValues[$"zram.{zramDev.Name}.mem_used_bytes"] = zramDev.MemUsedBytes;
                            node.LatestMetricValues[$"zram.{zramDev.Name}.orig_data_bytes"] = zramDev.OrigDataBytes;
                            node.LatestMetricValues[$"zram.{zramDev.Name}.compr_data_bytes"] = zramDev.ComprDataBytes;
                        }
                    }
                }
            }

            if (s.Power != null)
            {
                node.LatestMetricValues["power.battery_pct"] = s.Power.BatteryPct;
                node.LatestMetricValues["power.rate_watts"] = s.Power.RateWatts;
            }

            if (s.Twamp != null && s.Twamp.Available)
            {
                node.LatestMetricValues["twamp.rtt"] = s.Twamp.RttMs;
                if (s.Twamp.OneWayAvailable)
                {
                    node.LatestMetricValues["twamp.forward"] = s.Twamp.ForwardMs;
                    node.LatestMetricValues["twamp.reverse"] = s.Twamp.ReverseMs;
                }
            }

            if (s.Network != null)
            {
                foreach (var nic in s.Network.Interfaces)
                {
                    node.LatestMetricValues[$"nic.{nic.Name}.rx_bytes"] = nic.RxBytes;
                    node.LatestMetricValues[$"nic.{nic.Name}.tx_bytes"] = nic.TxBytes;
                }
            }

            if (s.DiskIo != null)
            {
                node.LatestMetricValues["disk.io.read_bytes"] = s.DiskIo.ReadBytes;
                node.LatestMetricValues["disk.io.write_bytes"] = s.DiskIo.WriteBytes;
                node.LatestMetricValues["disk.io.read_ops"] = s.DiskIo.ReadOps;
                node.LatestMetricValues["disk.io.write_ops"] = s.DiskIo.WriteOps;
                foreach (var dev in s.DiskIo.Devices)
                {
                    node.LatestMetricValues[$"disk.io.{dev.Name}.read_bytes"] = dev.ReadBytes;
                    node.LatestMetricValues[$"disk.io.{dev.Name}.write_bytes"] = dev.WriteBytes;
                    node.LatestMetricValues[$"disk.io.{dev.Name}.read_ops"] = dev.ReadOps;
                    node.LatestMetricValues[$"disk.io.{dev.Name}.write_ops"] = dev.WriteOps;
                }
            }

            ProcessInfoModel[] processModels = Array.Empty<ProcessInfoModel>();
            if (s.Processes != null && s.Processes.Count > 0)
            {
                processModels = s.Processes.Select(p => new ProcessInfoModel
                {
                    Pid = p.Pid,
                    Name = p.Name,
                    User = p.User,
                    Threads = (int)p.Threads,
                    Cpu = p.CpuPct,
                    Mem = $"{p.RssBytes / 1048576.0:F1} MiB"
                }).ToArray();

                var grouped = s.Processes
                    .GroupBy(p => FleetNodeModel.SanitizeMetricName(p.Name))
                    .Select(g => new { Name = g.Key, Cpu = g.Sum(x => x.CpuPct) })
                    .OrderByDescending(x => x.Cpu)
                    .ToList();

                double topSum = 0;
                foreach (var proc in grouped)
                {
                    node.LatestMetricValues[$"proc.cpu.{proc.Name}"] = proc.Cpu;
                    topSum += proc.Cpu;
                }
                node.LatestMetricValues["proc.cpu.other"] = Math.Max(0.0, s.Cpu != null ? s.Cpu.TotalPct - topSum : 0.0);
            }

            // Record into HistoryCache off UI thread
            HistoryCache.Record(node.CollectorEndpoint, node.Id, s.TimestampUnixNano, node.LatestMetricValues);

            long posted = UiPerformanceDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            snapshot = new NodeDisplaySnapshot
            {
                Node = node,
                TimestampUnixNano = s.TimestampUnixNano,
                ReceivedUtc = receivedUtc,
                Status = "online",
                CpuModel = s.CpuModel,
                Os = s.Os,
                Arch = s.Arch,
                ProcessesAvailable = s.ProcessesAvailable,
                Processes = processModels,
                CpuAvgPct = s.Cpu != null ? Math.Round(s.Cpu.TotalPct, 1) : 0,
                Cores = s.Cpu?.PerCorePct.Count ?? 0,
                CoreLoads = s.Cpu?.PerCorePct.Select(v => (float)(v / 100.0)).ToArray(),
                RamUsedPct = ramUsedPct,
                MemoryTotalBytes = s.Memory?.MemTotalBytes ?? 0,
                RamTotal = ramTotal,
                ZramRatio = zramRatio,
                SwapDevices = s.Memory?.SwapDevices?.ToArray(),
                ZramDevices = s.Memory?.ZramDevices?.ToArray(),
                SwapUsedPct = swapUsedPct,
                HasBattery = s.Power?.BatteryPresent ?? false,
                BatteryPct = s.Power != null ? Math.Round(s.Power.BatteryPct, 1) : 0,
                Interfaces = interfaces,
                TxBytesPerSecond = txRate,
                RxBytesPerSecond = rxRate,
                Disks = disks,
                DiskReadBytesPerSecond = diskReadRate,
                DiskWriteBytesPerSecond = diskWriteRate,
                TwampAvailable = s.Twamp?.Available ?? false,
                TwampOneWayAvailable = s.Twamp?.OneWayAvailable ?? false,
                TwampRttMs = s.Twamp?.RttMs ?? 0,
                TwampForwardMs = s.Twamp?.ForwardMs ?? 0,
                TwampReverseMs = s.Twamp?.ReverseMs ?? 0,
                TwampError = s.Twamp?.Error ?? "No TWAMP measurements",
                PostedTimestamp = posted
            };
        }

        _pendingDisplaySnapshots[node.Id] = snapshot;

        if (Interlocked.CompareExchange(ref _displayDrainScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(DrainDisplayUpdates);
        }
    }

    public void FlushDisplayUpdates() => DrainDisplayUpdates();

    private void DrainDisplayUpdates()
    {
        Interlocked.Exchange(ref _displayDrainScheduled, 0);
        if (_disposeCts.IsCancellationRequested) return;

        using var uiWork = UiPerformanceDiagnostics.Measure("live.project-cache-publish");

        foreach (var key in _pendingDisplaySnapshots.Keys.ToArray())
        {
            if (!_pendingDisplaySnapshots.TryRemove(key, out var snapshot)) continue;
            var node = snapshot.Node;
            if (snapshot.TimestampUnixNano <= node.TimestampUnixNano) continue;

            if (snapshot.PostedTimestamp != 0)
            {
                UiPerformanceDiagnostics.Record("live.dispatch-wait",
                    System.Diagnostics.Stopwatch.GetElapsedTime(snapshot.PostedTimestamp).TotalMilliseconds);
            }

            node.Status = snapshot.Status;
            node.TimestampUnixNano = snapshot.TimestampUnixNano;
            node.TelemetryReceivedUtc = snapshot.ReceivedUtc;

            node.Twamp.Available = snapshot.TwampAvailable;
            node.Twamp.OneWayAvailable = snapshot.TwampOneWayAvailable;
            node.Twamp.RttMs = snapshot.TwampRttMs;
            node.Twamp.ForwardMs = snapshot.TwampForwardMs;
            node.Twamp.ReverseMs = snapshot.TwampReverseMs;
            node.Twamp.Error = snapshot.TwampError;

            node.CpuModel = snapshot.CpuModel ?? string.Empty;
            node.Os = snapshot.Os ?? string.Empty;
            node.Arch = snapshot.Arch ?? string.Empty;
            node.ProcessesAvailable = snapshot.ProcessesAvailable;
            node.Processes = snapshot.Processes;

            // CPU
            node.CpuAvgPct = snapshot.CpuAvgPct;
            if (snapshot.Cores > 0)
            {
                node.Cores = snapshot.Cores;
                node.CoreLoads = snapshot.CoreLoads ?? Array.Empty<float>();
            }
            node.SparkCpu = PushSparkline(node.SparkCpu, snapshot.CpuAvgPct);

            // Memory
            if (snapshot.MemoryTotalBytes > 0)
            {
                node.RamUsedPct = snapshot.RamUsedPct;
                node.MemoryTotalBytes = snapshot.MemoryTotalBytes;
                node.RamTotal = snapshot.RamTotal ?? "Unknown";
                node.ZramRatio = snapshot.ZramRatio;
                if (snapshot.SwapDevices != null && snapshot.SwapDevices.Length > 0)
                    node.SwapDevices = snapshot.SwapDevices;
                if (snapshot.ZramDevices != null && snapshot.ZramDevices.Length > 0)
                    node.ZramDevices = snapshot.ZramDevices;
                node.SwapUsedPct = snapshot.SwapUsedPct;
                node.SparkRam = PushSparkline(node.SparkRam, snapshot.RamUsedPct);
            }

            // Power
            node.HasBattery = snapshot.HasBattery;
            node.BatteryPct = snapshot.BatteryPct;

            // Network
            node.Interfaces = snapshot.Interfaces;
            node.TxBytesPerSecond = snapshot.TxBytesPerSecond;
            node.RxBytesPerSecond = snapshot.RxBytesPerSecond;
            node.SparkNetUp = PushSparkline(node.SparkNetUp, snapshot.TxBytesPerSecond / 1024);
            node.SparkNetDown = PushSparkline(node.SparkNetDown, snapshot.RxBytesPerSecond / 1024);

            // Disks
            node.Disks = snapshot.Disks;
            node.DiskReadBytesPerSecond = snapshot.DiskReadBytesPerSecond;
            node.DiskWriteBytesPerSecond = snapshot.DiskWriteBytesPerSecond;

            NodeTelemetryUpdated?.Invoke(this, node);
        }
    }

    public IReadOnlyList<FleetNodeModel> GetFleetNodes()
    {
        var list = _nodes.Values.ToList();
        return list;
    }

    public FleetNodeModel? GetNode(string hostId)
    {
        _nodes.TryGetValue(hostId, out var node);
        return node;
    }

    public ClusterTelemetrySummary GetClusterSummary()
    {
        var nodes = _nodes.Values.ToList();
        var rtts = nodes.Where(n => n.Twamp.Available).Select(n => n.Twamp.RttMs).OrderBy(v => v).ToArray();
        return new ClusterTelemetrySummary
        {
            HostCount = nodes.Count,
            TwampSummary = rtts.Length == 0 ? "Unavailable" : $"RTT {rtts[(int)Math.Ceiling(rtts.Length * .95) - 1]:F2} ms",
            GanderCount = nodes.Count(n => n.Role is "push" or "reverse_push"),
            GoslingCount = nodes.Count(n => n.Role == "pull"),
            P95ForwardMs = 0,
            P95ReverseMs = 0,
            GlobalIngressGbps = nodes.Sum(n => n.RxBytesPerSecond) * 8 / 1e9,
            GlobalEgressGbps = nodes.Sum(n => n.TxBytesPerSecond) * 8 / 1e9
        };
    }

    public IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId) => hostId == "all"
        ? _nodes.Values.SelectMany(n => n.Processes).ToArray()
        : GetNode(hostId)?.Processes ?? Array.Empty<ProcessInfoModel>();

    public Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, CancellationToken ct = default)
        => QueryHistoryWithResolutionAsync(hostId, metric, start, end, 2400, ct);

    public async Task<IReadOnlyList<LODPoint>> QueryHistoryWithResolutionAsync(string hostId, string metric, DateTime start, DateTime end, int targetPoints, CancellationToken ct = default)
    {
        var node = GetNode(hostId);
        var client = node == null ? null : _collectorManager.GetClientForNode(node);
        if (node == null) return Array.Empty<LODPoint>();
        using var timing = HistoryTiming.Begin("history", hostId, metric);
        timing?.Mark("request", $"collector={node.CollectorEndpoint}; start={start:O}; end={end:O}; budget={targetPoints}");
        var cfg = await GetNodeConfigAsync(hostId, ct);
        timing?.Mark("configuration-ready", cfg == null ? "unavailable" : "available");
        bool localOnly = client == null || (cfg != null && TelemetryOptInResolver.GetMetricOptInMode(cfg, metric) != TelemetryOptInMode.OptInMonitorAndStore);
        return await HistoryCache.QueryAsync(node.CollectorEndpoint, hostId, metric, start, end, localOnly,
            (from, to, token) => _historyQueries.QueryAsync(
                new(node.CollectorEndpoint, hostId, metric, from.ToUniversalTime().Ticks, to.ToUniversalTime().Ticks, Math.Clamp(targetPoints, 4, 100000)),
                sharedToken => client!.QueryRangeAsync(hostId, metric, from, to, targetPoints: Math.Clamp(targetPoints, 4, 100000), ct: sharedToken), token), ct, targetPoints);
    }

    public async Task<NodeConfig?> GetNodeConfigAsync(string hostId, CancellationToken ct = default)
    {
        var node = GetNode(hostId);
        var client = node == null ? null : _collectorManager.GetClientForNode(node);
        if (client == null || node == null) return null;
        var key = (node.CollectorEndpoint, hostId);
        using var timing = HistoryTiming.Begin("configuration", hostId);
        await _configGate.WaitAsync(ct);
        timing?.Mark("gate-acquired");
        try
        {
            if (_configs.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow)
            { timing?.Mark("cache-hit"); return entry.Config.Clone(); }
            var config = await client.GetNodeConfigAsync(hostId, ct);
            timing?.Mark("rpc-complete", config == null ? "unavailable" : "success");
            ct.ThrowIfCancellationRequested();
            if (config != null) _configs[key] = (config.Clone(), DateTime.UtcNow.AddSeconds(30));
            return config;
        }
        finally { _configGate.Release(); }
    }

    public async Task<bool> UpdateNodeConfigAsync(string hostId, NodeConfig cfg, CancellationToken ct = default)
    {
        var node = GetNode(hostId);
        var client = node == null ? null : _collectorManager.GetClientForNode(node);
        if (client == null || node == null) return false;
        await _configGate.WaitAsync(ct);
        try
        {
            bool success = await client.UpdateNodeConfigAsync(hostId, cfg, ct);
            if (success) _configs[(node.CollectorEndpoint, hostId)] = (cfg.Clone(), DateTime.UtcNow.AddSeconds(30));
            return success;
        }
        finally { _configGate.Release(); }
    }
    public IReadOnlyList<DropRuleModel> GetDropRules(string hostId) => Array.Empty<DropRuleModel>();
    public IReadOnlyList<RegionTrafficModel> GetRegions(string hostId) => Array.Empty<RegionTrafficModel>();

    public void SendSignal(string hostId, int pid, int signal) { NotificationService.Instance.ShowToast("Remote process signals are not supported by the collector."); }
    public void PauseLogs(bool paused) { }
    public void ClearLogs() { }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _refreshTimer.Dispose();
        foreach (var cts in _streamCancelTokens.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _streamCancelTokens.Clear();
        _historyQueries.Dispose();
        HistoryCache.Clear();
        _configs.Clear();
        _pendingDisplaySnapshots.Clear();
        _nodeIngestStates.Clear();
    }

    private sealed class NodeIngestState
    {
        public long LastTimestampNano;
        public readonly Dictionary<string, (ulong Rx, ulong Tx)> LastInterfaces = new(StringComparer.OrdinalIgnoreCase);
        public ulong LastDiskRead;
        public ulong LastDiskWrite;
        public bool HadSample;
    }

    private sealed class NodeDisplaySnapshot
    {
        public required FleetNodeModel Node { get; init; }
        public required long TimestampUnixNano { get; init; }
        public required DateTime ReceivedUtc { get; init; }
        public required string Status { get; init; }
        public string? CpuModel { get; init; }
        public string? Os { get; init; }
        public string? Arch { get; init; }
        public bool ProcessesAvailable { get; init; }
        public required ProcessInfoModel[] Processes { get; init; }
        public double CpuAvgPct { get; init; }
        public int Cores { get; init; }
        public float[]? CoreLoads { get; init; }
        public double RamUsedPct { get; init; }
        public ulong MemoryTotalBytes { get; init; }
        public string? RamTotal { get; init; }
        public double ZramRatio { get; init; }
        public SwapDevice[]? SwapDevices { get; init; }
        public ZramDevice[]? ZramDevices { get; init; }
        public double SwapUsedPct { get; init; }
        public bool HasBattery { get; init; }
        public double BatteryPct { get; init; }
        public required NicMetric[] Interfaces { get; init; }
        public double TxBytesPerSecond { get; init; }
        public double RxBytesPerSecond { get; init; }
        public required DiskIoDevice[] Disks { get; init; }
        public double DiskReadBytesPerSecond { get; init; }
        public double DiskWriteBytesPerSecond { get; init; }
        public bool TwampAvailable { get; init; }
        public bool TwampOneWayAvailable { get; init; }
        public double TwampRttMs { get; init; }
        public double TwampForwardMs { get; init; }
        public double TwampReverseMs { get; init; }
        public string TwampError { get; init; } = string.Empty;
        public long PostedTimestamp { get; init; }
    }
}
