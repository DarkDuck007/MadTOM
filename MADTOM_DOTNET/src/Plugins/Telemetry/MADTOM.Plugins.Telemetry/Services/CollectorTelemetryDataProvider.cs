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
    private readonly Timer _refreshTimer;
    private int _polling;
    private readonly CancellationTokenSource _disposeCts = new();

    public TelemetryHistoryCache HistoryCache { get; }
    private readonly ConcurrentDictionary<(string, string), (NodeConfig Config, DateTime Expires)> _configs = new();
    private readonly SemaphoreSlim _configGate = new(1, 1);

    public MultiCollectorManager CollectorManager => _collectorManager;

    public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
    public event EventHandler<LogEntryModel>? LogReceived;

    public CollectorTelemetryDataProvider(MultiCollectorManager collectorManager, TelemetryHistoryCache? historyCache = null)
    {
        _collectorManager = collectorManager;
        HistoryCache = historyCache ?? new TelemetryHistoryCache(new TelemetryCacheSettingsStore().LoadMinutes());

        // Poll collectors every 3 seconds for new/updated nodes
        _refreshTimer = new Timer(OnPollCollectorsTick, null, 100, 3000);
    }

    private async void OnPollCollectorsTick(object? state)
    {
        if (_disposeCts.IsCancellationRequested || Interlocked.Exchange(ref _polling, 1) != 0) return;

        try
        {
            HistoryCache.Prune();
            var discoveredNodes = await _collectorManager.FetchAllNodesAsync(_disposeCts.Token);
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
        }
        catch
        {
            // Transient network failure handled silently
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

    private void UpdateNodeFromMetrics(FleetNodeModel node, SystemMetrics s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposeCts.IsCancellationRequested || s.TimestampUnixNano <= node.TimestampUnixNano) return;
            double seconds = (s.TimestampUnixNano - node.TimestampUnixNano) / 1e9;
            var previousInterfaces = node.Interfaces.ToDictionary(i => i.Name);
            bool hadSample = node.TimestampUnixNano > 0;
            node.TimestampUnixNano = s.TimestampUnixNano;
            node.Twamp.Available = s.Twamp?.Available ?? false;
            node.Twamp.OneWayAvailable = s.Twamp?.OneWayAvailable ?? false;
            node.Twamp.RttMs = s.Twamp?.RttMs ?? 0;
            node.Twamp.ForwardMs = s.Twamp?.ForwardMs ?? 0;
            node.Twamp.ReverseMs = s.Twamp?.ReverseMs ?? 0;
            node.Twamp.Error = s.Twamp?.Error ?? "No TWAMP measurements";
            node.CpuModel = s.CpuModel;
            node.Os = s.Os;
            node.Arch = s.Arch;
            node.ProcessesAvailable = s.ProcessesAvailable;
            node.Processes = s.Processes.Select(p => new ProcessInfoModel { Pid = p.Pid, Name = p.Name, User = p.User, Threads = (int)p.Threads, Cpu = p.CpuPct, Mem = $"{p.RssBytes / 1048576.0:F1} MiB" }).ToArray();
            // CPU
            if (s.Cpu != null)
            {
                node.CpuAvgPct = Math.Round(s.Cpu.TotalPct, 1);
                if (s.Cpu.PerCorePct.Count > 0)
                {
                    node.Cores = s.Cpu.PerCorePct.Count;
                    node.CoreLoads = s.Cpu.PerCorePct.Select(v => (float)(v / 100.0)).ToArray();
                }

                // Append to CPU sparkline (keep 60 points)
                var sparkCpu = (node.SparkCpu ?? Array.Empty<double>()).ToList();
                sparkCpu.Add(s.Cpu.TotalPct);
                if (sparkCpu.Count > 60) sparkCpu.RemoveAt(0);
                node.SparkCpu = sparkCpu.ToArray();
            }

            // Memory
            if (s.Memory != null && s.Memory.MemTotalBytes > 0)
            {
                double usedBytes = (double)(s.Memory.MemTotalBytes - Math.Min(s.Memory.MemTotalBytes, s.Memory.MemAvailableBytes));
                node.RamUsedPct = Math.Round((usedBytes / s.Memory.MemTotalBytes) * 100.0, 1);
                node.MemoryTotalBytes = s.Memory.MemTotalBytes;
                node.RamTotal = $"{Math.Round((double)s.Memory.MemTotalBytes / (1024 * 1024 * 1024), 1)} GB";
                node.ZramRatio = Math.Round(s.Memory.ZramRatio, 2);

                if (s.Memory.SwapDevices != null && s.Memory.SwapDevices.Count > 0)
                {
                    node.SwapDevices = s.Memory.SwapDevices.ToArray();
                }
                if (s.Memory.ZramDevices != null && s.Memory.ZramDevices.Count > 0)
                {
                    node.ZramDevices = s.Memory.ZramDevices.ToArray();
                }

                if (s.Memory.SwapTotalBytes > 0)
                {
                    double swapUsed = s.Memory.SwapUsedBytes > 0 
                        ? s.Memory.SwapUsedBytes 
                        : (double)(s.Memory.SwapTotalBytes - Math.Min(s.Memory.SwapTotalBytes, s.Memory.SwapFreeBytes));
                    node.SwapUsedPct = Math.Round((swapUsed / s.Memory.SwapTotalBytes) * 100.0, 1);
                }

                var sparkRam = (node.SparkRam ?? Array.Empty<double>()).ToList();
                sparkRam.Add(node.RamUsedPct);
                if (sparkRam.Count > 60) sparkRam.RemoveAt(0);
                node.SparkRam = sparkRam.ToArray();
            }

            // Power
            if (s.Power != null)
            {
                node.HasBattery = s.Power.BatteryPresent;
                node.BatteryPct = Math.Round(s.Power.BatteryPct, 1);
            }

            node.Interfaces = Array.Empty<NicMetric>();
            node.TxBytesPerSecond = 0; node.RxBytesPerSecond = 0;
            // Network
            if (s.Network != null && s.Network.Interfaces.Count > 0)
            {
                node.Interfaces = s.Network.Interfaces.ToArray();
                double Rate(bool tx) => !hadSample || seconds <= 0 ? 0 : node.Interfaces.Sum(i =>
                {
                    if (!previousInterfaces.TryGetValue(i.Name, out var prev)) return 0.0;
                    ulong current = tx ? i.TxBytes : i.RxBytes, old = tx ? prev.TxBytes : prev.RxBytes;
                    return current >= old ? (current - old) / seconds : 0.0;
                });
                node.TxBytesPerSecond = Rate(true);
                node.RxBytesPerSecond = Rate(false);
                node.SparkNetUp = node.SparkNetUp.Append(node.TxBytesPerSecond / 1024).TakeLast(60).ToArray();
                node.SparkNetDown = node.SparkNetDown.Append(node.RxBytesPerSecond / 1024).TakeLast(60).ToArray();
            }

            // Keep the prior disk counters for rates, then replace the numeric snapshot.
            // Omitted/disabled metrics must not become fictitious cached measurements.
            node.LatestMetricValues.TryGetValue("disk.io.read_bytes", out var previousDiskRead);
            node.LatestMetricValues.TryGetValue("disk.io.write_bytes", out var previousDiskWrite);
            node.LatestMetricValues.Clear();

            // Populate all raw/computed metric keys for live graph streaming
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
            if (s.Memory != null)
            {
                node.LatestMetricValues["memory.total"] = s.Memory.MemTotalBytes;
                node.LatestMetricValues["memory.available"] = s.Memory.MemAvailableBytes;
                node.LatestMetricValues["memory.used"] = (double)(s.Memory.MemTotalBytes - Math.Min(s.Memory.MemTotalBytes, s.Memory.MemAvailableBytes));
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
            node.Disks = Array.Empty<DiskIoDevice>();
            node.DiskReadBytesPerSecond = 0;
            node.DiskWriteBytesPerSecond = 0;
            if (s.DiskIo != null)
            {
                if (s.DiskIo.Devices.Count > 0)
                {
                    node.Disks = s.DiskIo.Devices.ToArray();
                }

                if (hadSample && seconds > 0)
                {
                    ulong prevRead = 0, prevWrite = 0;
                    prevRead = (ulong)previousDiskRead;
                    prevWrite = (ulong)previousDiskWrite;
                    if (s.DiskIo.ReadBytes >= prevRead) node.DiskReadBytesPerSecond = (s.DiskIo.ReadBytes - prevRead) / seconds;
                    if (s.DiskIo.WriteBytes >= prevWrite) node.DiskWriteBytesPerSecond = (s.DiskIo.WriteBytes - prevWrite) / seconds;
                }

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

            if (s.Processes != null && s.Processes.Count > 0)
            {
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

            HistoryCache.Record(node.CollectorEndpoint, node.Id, s.TimestampUnixNano, node.LatestMetricValues);
            NodeTelemetryUpdated?.Invoke(this, node);
        });
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

    public async Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var node = GetNode(hostId);
        var client = node == null ? null : _collectorManager.GetClientForNode(node);
        if (node == null) return Array.Empty<LODPoint>();
        var cfg = await GetNodeConfigAsync(hostId, ct);
        bool localOnly = client == null || (cfg != null && TelemetryOptInResolver.GetMetricOptInMode(cfg, metric) != TelemetryOptInMode.OptInMonitorAndStore);
        return await HistoryCache.QueryAsync(node.CollectorEndpoint, hostId, metric, start, end, localOnly,
            (from, to, token) => client!.QueryRangeAsync(hostId, metric, from, to, ct: token), ct);
    }

    public async Task<NodeConfig?> GetNodeConfigAsync(string hostId, CancellationToken ct = default)
    {
        var node = GetNode(hostId);
        var client = node == null ? null : _collectorManager.GetClientForNode(node);
        if (client == null || node == null) return null;
        var key = (node.CollectorEndpoint, hostId);
        await _configGate.WaitAsync(ct);
        try
        {
            if (_configs.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow) return entry.Config.Clone();
            var config = await client.GetNodeConfigAsync(hostId, ct);
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
        HistoryCache.Clear();
        _configs.Clear();

    }
}
