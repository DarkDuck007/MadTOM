using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MadTOM.Models;

namespace MadTOM.Services;

public sealed class MockTelemetryDataProvider : ITelemetryDataProvider
{
    private readonly Random _random = new(42);
    private readonly List<FleetNodeModel> _nodes = new();
    private readonly List<ProcessInfoModel> _mockProcesses = new();
    private readonly List<DropRuleModel> _mockDrops = new();
    private readonly Timer? _streamTimer;
    private bool _logsPaused;

    public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
    public event EventHandler<LogEntryModel>? LogReceived;

    public MockTelemetryDataProvider(bool startBackgroundTimer = true)
    {
        InitializeFleetNodes();
        InitializeProcesses();
        InitializeDrops();

        if (startBackgroundTimer)
        {
            _streamTimer = new Timer(OnStreamTick, null, 1000, 1000);
        }
    }

    private void InitializeFleetNodes()
    {
        _nodes.Add(CreateNode("gander-epyc-01", "baremetal", "10.0.10.2", "Dual AMD EPYC 9996", 1024, "512 GB", 28.9, 41.2, "healthy",
            1.84, 3.12, 0.12, 0.28,
            [1.8, 1.9, 1.7, 2.1, 1.8, 1.84], [3.1, 3.0, 3.4, 3.2, 3.3, 3.12],
            [38, 42, 40, 45, 41, 41], [28, 28, 29, 29, 28, 29]));

        _nodes.Add(CreateNode("gander-storage-02", "baremetal", "10.0.10.8", "AMD EPYC 9654", 256, "256 GB", 68.2, 74.5, "warning",
            4.20, 11.80, 0.45, 2.40,
            [3.8, 4.0, 4.1, 4.4, 4.1, 4.2], [8.5, 9.2, 10.4, 11.2, 12.0, 11.8],
            [65, 70, 72, 78, 76, 75], [64, 65, 66, 67, 68, 68]));

        _nodes.Add(CreateNode("gosling-edge-01", "vm", "10.0.40.11", "Virtual EPYC vCPU", 64, "64 GB", 44.0, 22.4, "healthy",
            8.20, 8.45, 0.32, 0.35,
            [8.1, 8.2, 8.3, 8.1, 8.2, 8.2], [8.4, 8.3, 8.5, 8.6, 8.4, 8.45],
            [20, 22, 25, 23, 22, 22], [43, 44, 44, 44, 44, 44]));

        _nodes.Add(CreateNode("gosling-cache-04", "vm", "10.0.40.15", "Virtual Xeon Gold", 32, "32 GB", 82.5, 56.1, "healthy",
            2.10, 2.25, 0.08, 0.11,
            [2.0, 2.1, 2.1, 2.2, 2.0, 2.1], [2.2, 2.3, 2.2, 2.4, 2.3, 2.25],
            [52, 54, 58, 55, 57, 56], [80, 81, 81, 82, 82, 83]));

        _nodes.Add(CreateNode("gander-transatlantic-01", "baremetal", "198.51.100.22", "Dual AMD EPYC 9754", 512, "384 GB", 35.1, 48.0, "healthy",
            38.4, 74.2, 1.10, 4.80,
            [37.8, 38.0, 38.5, 38.2, 38.9, 38.4], [70.5, 72.1, 75.8, 73.4, 76.0, 74.2],
            [44, 46, 50, 48, 47, 48], [34, 35, 35, 35, 35, 35]));

        _nodes.Add(CreateNode("gosling-ingress-gw", "vm", "10.0.40.88", "Virtual EPYC vCPU", 16, "16 GB", 91.0, 89.4, "critical",
            14.8, 46.2, 3.40, 14.80,
            [12.0, 13.5, 14.2, 14.0, 15.1, 14.8], [32.0, 38.4, 44.0, 48.2, 45.0, 46.2],
            [78, 82, 86, 91, 88, 89], [88, 89, 90, 91, 91, 91]));
    }

    private FleetNodeModel CreateNode(string id, string role, string ip, string cpuModel, int cores,
        string ramTotal, double ramUsedPct, double cpuAvgPct, string status,
        double fwd, double rev, double jitUp, double jitDown,
        double[] netUp, double[] netDown, double[] cpu, double[] ram)
    {
        var node = new FleetNodeModel
        {
            Id = id,
            Role = role,
            Ip = ip,
            CpuModel = cpuModel,
            Cores = cores,
            RamTotal = ramTotal,
            RamUsedPct = ramUsedPct,
            CpuAvgPct = cpuAvgPct,
            Status = status,
            Twamp = new TwampTelemetryModel
            {
                ForwardMs = fwd,
                ReverseMs = rev,
                JitterUp = jitUp,
                JitterDown = jitDown
            },
            SparkNetUp = netUp,
            SparkNetDown = netDown,
            SparkCpu = cpu,
            SparkRam = ram,
            CoreLoads = new float[cores]
        };

        for (int i = 0; i < cores; i++)
        {
            node.CoreLoads[i] = (float)Math.Clamp(_random.NextDouble(), 0.05, 0.98);
        }

        return node;
    }

    private void InitializeProcesses()
    {
        _mockProcesses.Add(new ProcessInfoModel { Pid = 1024, Name = "madtomd (goosed)", User = "root", Threads = 16, Cpu = 1.8, Mem = "24.5 MB" });
        _mockProcesses.Add(new ProcessInfoModel { Pid = 482, Name = "systemd-journald", User = "systemd", Threads = 4, Cpu = 0.4, Mem = "48.2 MB" });
        _mockProcesses.Add(new ProcessInfoModel { Pid = 1849, Name = "twamp_reflector", User = "madtom", Threads = 8, Cpu = 12.4, Mem = "64.0 MB" });
        _mockProcesses.Add(new ProcessInfoModel { Pid = 9284, Name = "zstd_compressor", User = "madtom", Threads = 32, Cpu = 18.2, Mem = "128.4 MB" });
        _mockProcesses.Add(new ProcessInfoModel { Pid = 14092, Name = "nginx: worker", User = "www-data", Threads = 2, Cpu = 3.1, Mem = "34.1 MB" });
        _mockProcesses.Add(new ProcessInfoModel { Pid = 28410, Name = "postgres: writer", User = "postgres", Threads = 6, Cpu = 8.5, Mem = "512.8 MB" });
    }

    private void InitializeDrops()
    {
        _mockDrops.Add(new DropRuleModel { Rule = "DROP_PORT_SCAN", Ip = "198.51.100.82", Proto = "TCP/23" });
        _mockDrops.Add(new DropRuleModel { Rule = "BOGON_SOURCE", Ip = "10.240.12.99", Proto = "UDP/53" });
        _mockDrops.Add(new DropRuleModel { Rule = "SYN_FLOOD_LIMIT", Ip = "203.0.113.41", Proto = "TCP/443" });
        _mockDrops.Add(new DropRuleModel { Rule = "INVALID_CONNT", Ip = "192.0.2.18", Proto = "TCP/80" });
        _mockDrops.Add(new DropRuleModel { Rule = "RATE_LIMIT_DROP", Ip = "198.51.100.12", Proto = "ICMP" });
    }

    public IReadOnlyList<FleetNodeModel> GetFleetNodes() => _nodes;

    public FleetNodeModel? GetNode(string hostId) => _nodes.FirstOrDefault(n => n.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));

    public ClusterTelemetrySummary GetClusterSummary() => new()
    {
        HostCount = _nodes.Count,
        GanderCount = _nodes.Count(n => n.Role == "baremetal"),
        GoslingCount = _nodes.Count(n => n.Role == "vm"),
        P95ForwardMs = 1.8,
        P95ReverseMs = 2.4,
        GlobalEgressGbps = 148.2
    };

    public IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId) => _mockProcesses;

    public IReadOnlyList<DropRuleModel> GetDropRules(string hostId) => _mockDrops;

    public IReadOnlyList<RegionTrafficModel> GetRegions(string hostId)
    {
        return new List<RegionTrafficModel>
        {
            new() { RegionId = "reg_na", RegionName = "North America (US/CA)", TrafficRate = "44.8 Gbps", Rtt = "78ms", ColorHex = "#ea580c" },
            new() { RegionId = "reg_ca", RegionName = "Central America", TrafficRate = "8.2 Gbps", Rtt = "92ms", ColorHex = "#f59e0b" },
            new() { RegionId = "reg_sa", RegionName = "South America", TrafficRate = "16.4 Gbps", Rtt = "124ms", ColorHex = "#f59e0b" },
            new() { RegionId = "reg_eu", RegionName = "Western Europe", TrafficRate = "52.1 Gbps", Rtt = "18ms", ColorHex = "#ea580c" },
            new() { RegionId = "reg_ee", RegionName = "Eastern Europe & North Asia", TrafficRate = "11.8 Gbps", Rtt = "118ms", ColorHex = "#334155" },
            new() { RegionId = "reg_me", RegionName = "Middle East", TrafficRate = "14.2 Gbps", Rtt = "135ms", ColorHex = "#d97706" },
            new() { RegionId = "reg_af", RegionName = "Africa", TrafficRate = "5.2 Gbps", Rtt = "198ms", ColorHex = "#1e293b" },
            new() { RegionId = "reg_ea", RegionName = "East Asia (Japan/KR)", TrafficRate = "64.5 Gbps", Rtt = "1.8ms", ColorHex = "#ea580c" },
            new() { RegionId = "reg_se", RegionName = "Southeast Asia", TrafficRate = "28.1 Gbps", Rtt = "62ms", ColorHex = "#f59e0b" },
            new() { RegionId = "reg_oc", RegionName = "Oceania (Australia/NZ)", TrafficRate = "9.4 Gbps", Rtt = "148ms", ColorHex = "#475569" }
        };
    }

    private void OnStreamTick(object? state)
    {
        // Random slight fluctuation to simulated TWAMP metrics and core loads
        foreach (var node in _nodes)
        {
            node.Twamp.ForwardMs = Math.Max(0.5, Math.Round(node.Twamp.ForwardMs + (_random.NextDouble() - 0.5) * 0.2, 2));
            node.Twamp.ReverseMs = Math.Max(0.8, Math.Round(node.Twamp.ReverseMs + (_random.NextDouble() - 0.5) * 0.3, 2));

            // Shift a few core loads
            int countToMutate = Math.Min(16, node.Cores);
            for (int i = 0; i < countToMutate; i++)
            {
                int coreIdx = _random.Next(node.Cores);
                node.CoreLoads[coreIdx] = (float)Math.Clamp(node.CoreLoads[coreIdx] + (_random.NextDouble() - 0.5) * 0.15, 0.05, 0.98);
            }

            NodeTelemetryUpdated?.Invoke(this, node);
        }

        // Live journal log stream
        if (!_logsPaused && LogReceived != null)
        {
            string[] templates = [
                $"[TWAMP] Sent probe frame seq={_random.Next(1000, 9999)} dscp=46 rtt={(_random.NextDouble() * 2 + 1.5):F2}ms",
                $"kernel: [NFLOG] DROP_PREDATOR: IN=eth0 SRC=198.51.100.{_random.Next(10, 240)} PROTO=TCP SPT={_random.Next(10000, 60000)} DPT=23",
                $"madtomd[1024]: [HONK] Sub-space broadcast acknowledged by 5 peer ganders",
                $"madtomd[1024]: [ZSTD] Ring-buffer commit chunk #{_random.Next(300, 600)} - compressed 256KB to {(_random.NextDouble() * 20 + 25):F1}KB (6.8x)"
            ];

            string msg = templates[_random.Next(templates.Length)];
            LogReceived.Invoke(this, new LogEntryModel
            {
                Timestamp = DateTime.Now,
                Message = msg,
                Source = "madtomd",
                Level = msg.Contains("DROP") ? "WARN" : "INFO"
            });
        }
    }

    public void SendSignal(string hostId, int pid, int signal)
    {
        NotificationService.Instance.ShowToast($"Signal {signal} dispatched to PID {pid} on {hostId}");
    }

    public void PauseLogs(bool paused) => _logsPaused = paused;

    public void ClearLogs() { }

    public void Dispose()
    {
        _streamTimer?.Dispose();
    }
}

