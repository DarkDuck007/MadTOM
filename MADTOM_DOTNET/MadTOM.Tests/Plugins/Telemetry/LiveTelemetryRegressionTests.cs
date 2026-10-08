using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using MadTOM.Localization;
using Xunit;
namespace MadTOM.Tests;

[Collection("GlobalSingletons")]
public sealed class LiveTelemetryRegressionTests
{
    [Fact]
    public async Task HistoryUsesSelectedHostMetricAndTimeWindow()
    {
        using var provider = new RecordingProvider();
        var vm = new HostMetricsTabViewModel(provider);
        vm.UpdateForNode("two-thread-node", provider.Node, provider.GetFleetNodes());
        vm.SetScope("1m");
        await vm.RefreshHistoryAsync();
        Assert.All(provider.Queries.TakeLast(2), q => { Assert.Equal("two-thread-node", q.Host); Assert.Equal(TimeSpan.FromMinutes(1), q.End - q.Start); });
        Assert.All(vm.Graphs, g => Assert.Equal(new[] { 42.0 }, g.Values));
        vm.SelectedMetric = "power.rate_watts";
        vm.AddGraph();
        await vm.RefreshHistoryAsync();
        Assert.Contains(provider.Queries, q => q.Metric == "power.rate_watts");
        var graph = vm.Graphs.Last();
        vm.RemoveGraph(graph);
        Assert.DoesNotContain(graph, vm.Graphs);
    }
    [Fact]
    public void SelectedNodeRefreshesHardwareProcessesAndTabIndicator()
    {
        using var provider = new RecordingProvider();
        var metrics = new HostMetricsTabViewModel(provider);
        var processes = new HostProcessesTabViewModel(provider);
        var detail = new HostDetailViewModel(provider, LexiconService.Instance, metrics, processes, new(provider), new(provider));
        detail.SelectHost(provider.Node.Id);
        Assert.Equal("2 Logical Threads", detail.ThreadsSpec);
        Assert.Equal(2, metrics.ThreadCount);
        provider.Node.Cores = 4;
        provider.Node.Processes = new[] { new ProcessInfoModel { Pid = 10, Name = "actual-process" } };
        provider.Publish();
        Assert.Equal("4 Logical Threads", detail.ThreadsSpec);
        Assert.Single(processes.Processes);
        detail.SwitchTab("processes");
        Assert.True(detail.IsProcessesTab);
        Assert.False(detail.IsMetricsTab);
        detail.SelectHost("missing-node");
        Assert.Empty(processes.Processes);
        Assert.Equal(0, metrics.ThreadCount);
        Assert.Equal("missing-node", detail.HostTitle);
    }
    [Fact]
    public void CustomWindowRejectsReversedDates()
    {
        var vm = new HostMetricsTabViewModel();
        vm.SetScope("custom");
        vm.CustomStartDate = DateTimeOffset.Now.AddDays(1);
        vm.CustomEndDate = DateTimeOffset.Now;
        vm.ApplyCustomScope();
        Assert.True(vm.IsCustomScopeModalOpen);
        Assert.NotEmpty(vm.ScopeError);
    }
    [Fact]
    public void GraphLayoutPersistsAdditionsAndRemovals()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);
            var vm = new HostMetricsTabViewModel(layoutStore: store);
            vm.SelectedMetric = "twamp.rtt"; vm.AddGraph(); vm.RemoveGraph(vm.Graphs[0]);
            var restored = new HostMetricsTabViewModel(layoutStore: store);
            Assert.Equal(vm.Graphs.Select(g => g.Metric), restored.Graphs.Select(g => g.Metric));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void MultiSeriesMergingAndSplittingPersists()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);
            var vm = new HostMetricsTabViewModel(layoutStore: store);
            Assert.Equal(2, vm.Graphs.Count);
            // Merge graph 0 and graph 1
            vm.MergeWithNext(vm.Graphs[0]);
            Assert.Single(vm.Graphs);
            Assert.True(vm.Graphs[0].IsMerged);
            Assert.Equal(2, vm.Graphs[0].Series.Count);

            // Change color of series 1
            vm.Graphs[0].Series[1].ChangeColor("#EF4444");
            Assert.Equal("#EF4444", vm.Graphs[0].Series[1].ColorHex);

            // Verify persistence
            var restored = new HostMetricsTabViewModel(layoutStore: store);
            Assert.Single(restored.Graphs);
            Assert.True(restored.Graphs[0].IsMerged);
            Assert.Equal(2, restored.Graphs[0].Series.Count);
            Assert.Equal("#EF4444", restored.Graphs[0].Series[1].ColorHex);

            // Split graph back
            restored.SplitGraph(restored.Graphs[0]);
            Assert.Equal(2, restored.Graphs.Count);
            Assert.False(restored.Graphs[0].IsMerged);
            Assert.False(restored.Graphs[1].IsMerged);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void QuickMergePresetsCreateMultiSeriesCharts()
    {
        var vm = new HostMetricsTabViewModel();
        vm.QuickMergeCpu();
        var cpuGraph = vm.Graphs[0];
        Assert.Equal("CPU Breakdown", cpuGraph.Title);
        Assert.Equal(4, cpuGraph.Series.Count);
        Assert.True(cpuGraph.IsMerged);

        vm.QuickMergeMemory();
        var memGraph = vm.Graphs.First(g => g.Title == "Memory Breakdown");
        Assert.Equal(2, memGraph.Series.Count);
    }
    [Fact]
    public void LegacyLayoutStoreDeserializesStringArraysGracefully()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "[\"cpu.total\", \"power.battery_pct\"]");
            var store = new GraphLayoutStore(path);
            var configs = store.LoadConfigs();
            Assert.NotNull(configs);
            Assert.Equal(2, configs.Count);
            Assert.Equal("cpu.total", configs[0].Title);
            Assert.Equal("power.battery_pct", configs[1].Title);
            Assert.Equal("#EC4899", configs[1].Series[0].ColorHex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ScopeChange_DoesNotProduceSpikeOnRateMetrics()
    {
        using var provider = new RecordingProvider();
        provider.Node.Interfaces = new[]
        {
            new MADTOM.Plugins.Telemetry.Proto.V1.NicMetric { Name = "eth0", RxBytes = 9_000_000_000, TxBytes = 1_000_000_000 }
        };
        provider.Node.Disks = new[]
        {
            new MADTOM.Plugins.Telemetry.Proto.V1.DiskIoDevice { Name = "nvme0n1", ReadBytes = 44_000_000_000, WriteBytes = 2_000_000_000 }
        };
        provider.Node.TimestampUnixNano = 1_700_000_000_000_000_000L;

        var vm = new HostMetricsTabViewModel(provider);
        vm.UpdateForNode(provider.Node.Id, provider.Node, provider.GetFleetNodes());

        // Add rate graphs for network and disk
        vm.SelectedMetric = "nic.eth0.rx_bytes";
        vm.AddGraph();
        vm.SelectedMetric = "disk.io.nvme0n1.read_bytes";
        vm.AddGraph();

        await vm.RefreshHistoryAsync();

        // Change scope to 1h
        vm.SetScope("1h");
        await vm.RefreshHistoryAsync();

        // First live update arrives shortly after (0.5s later)
        long t1 = provider.Node.TimestampUnixNano + 500_000_000L;
        provider.Node.TimestampUnixNano = t1;
        provider.Node.Interfaces[0].RxBytes = 9_000_050_000; // 50 KB increment
        provider.Node.Disks[0].ReadBytes = 44_000_100_000;   // 100 KB increment
        vm.UpdateForNode(provider.Node.Id, provider.Node, provider.GetFleetNodes());

        // Second live update arrives 1.0s later
        long t2 = t1 + 1_000_000_000L;
        provider.Node.TimestampUnixNano = t2;
        provider.Node.Interfaces[0].RxBytes = 9_000_150_000; // 100 KB increment
        provider.Node.Disks[0].ReadBytes = 44_000_300_000;   // 200 KB increment
        vm.UpdateForNode(provider.Node.Id, provider.Node, provider.GetFleetNodes());

        var nicGraph = Assert.Single(vm.Graphs, g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes"));
        var diskGraph = Assert.Single(vm.Graphs, g => g.Series.Any(s => s.Metric == "disk.io.nvme0n1.read_bytes"));

        // Neither graph should have a point anywhere near 9G or 44G!
        Assert.All(nicGraph.Values, v => Assert.True(v < 100_000_000, $"NIC rate was unexpectedly large: {v}"));
        Assert.All(diskGraph.Values, v => Assert.True(v < 100_000_000, $"Disk rate was unexpectedly large: {v}"));
    }
    private sealed class RecordingProvider : ITelemetryDataProvider
    {
        public FleetNodeModel Node { get; } = new() { Id = "two-thread-node", Cores = 2, CpuModel = "Real CPU", RamTotal = "8 GB" };
        public List<(string Host, string Metric, DateTime Start, DateTime End)> Queries { get; } = new();
        public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
        public event EventHandler<LogEntryModel>? LogReceived { add { } remove { } }
        public void Publish() => NodeTelemetryUpdated?.Invoke(this, Node);
        public IReadOnlyList<FleetNodeModel> GetFleetNodes() => new[] { Node };
        public FleetNodeModel? GetNode(string id) => id == Node.Id ? Node : null;
        public ClusterTelemetrySummary GetClusterSummary() => new();
        public IReadOnlyList<ProcessInfoModel> GetProcesses(string id) => id == Node.Id || id == "all" ? Node.Processes : Array.Empty<ProcessInfoModel>();
        public IReadOnlyList<DropRuleModel> GetDropRules(string id) => Array.Empty<DropRuleModel>();
        public IReadOnlyList<RegionTrafficModel> GetRegions(string id) => Array.Empty<RegionTrafficModel>();
        public Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, CancellationToken ct = default)
        {
            Queries.Add((hostId, metric, start, end));
            return Task.FromResult<IReadOnlyList<LODPoint>>(new[] { new LODPoint(new DateTimeOffset(end).ToUnixTimeMilliseconds() * 1000000, 42, 42, 42) });
        }
        public void SendSignal(string hostId, int pid, int signal) { }
        public void PauseLogs(bool paused) { }
        public void ClearLogs() { }
        public void Dispose() { }
    }
}
