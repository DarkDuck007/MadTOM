using System;
using System.Linq;
using MadTOM.Controls.Charts;
using MadTOM.Localization;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using MADTOM.PluginContracts;
using MadTOM.Common;
using MADTOM.Plugins.Telemetry.Proto.V1;
using Xunit;

namespace MadTOM.Tests;

[Collection("GlobalSingletons")]
public class TelemetryAndMetricsTests
{
    [Fact]
    public void MockTelemetryDataProvider_Initializes60SampleSparklines()
    {
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var nodes = provider.GetFleetNodes();

        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
        {
            Assert.Equal(60, node.SparkNetUp.Length);
            Assert.Equal(60, node.SparkNetDown.Length);
            Assert.Equal(60, node.SparkCpu.Length);
            Assert.Equal(60, node.SparkRam.Length);
            Assert.Equal(node.Cores, node.CoreLoads.Length);
        }
    }

    [Fact]
    public void TwampTelemetryModel_CalculatesAndNotifiesAsymmetry()
    {
        var model = new TwampTelemetryModel
        {
            ForwardMs = 2.0,
            ReverseMs = 5.5
        };

        Assert.Equal(3.5, model.AsymmetryMs);

        bool asymmetryFired = false;
        model.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(model.AsymmetryMs)) asymmetryFired = true;
        };

        model.ForwardMs = 6.0;
        Assert.True(asymmetryFired);
        Assert.Equal(0.5, model.AsymmetryMs);
    }

    [Fact]
    public void HostMetricsTabViewModel_StartsWithoutFabricatedHistory()
    {
        var vm = new HostMetricsTabViewModel();

        Assert.Empty(vm.ForwardSeries);
        Assert.Empty(vm.ReverseSeries);
        Assert.Empty(vm.AsymmetrySeries);
        Assert.Empty(vm.TimeLabels);
        Assert.All(vm.Graphs, graph => Assert.Empty(graph.Values));
    }

    [Fact]
    public void HostMetricsTabViewModel_PushLiveSample_Maintains1200PointsAndRollsBuffer()
    {
        var vm = new HostMetricsTabViewModel();
        for (int i = 0; i < 1200; i++) vm.PushLiveSample(i, i + 1);
        double oldFirst = vm.ForwardSeries[1];

        vm.PushLiveSample(12.34, 18.76);

        Assert.Equal(1200, vm.ForwardSeries.Length);
        Assert.Equal(1200, vm.ReverseSeries.Length);
        Assert.Equal(1200, vm.AsymmetrySeries.Length);
        Assert.Equal(12.34, vm.ForwardSeries[^1]);
        Assert.Equal(18.76, vm.ReverseSeries[^1]);
        Assert.Equal(6.42, vm.AsymmetrySeries[^1], 2);
        Assert.Equal(oldFirst, vm.ForwardSeries[0]);
    }

    [Fact]
    public void HostMetricsTabViewModel_CustomScopeModal_ControlsVisibilityAndApply()
    {
        var vm = new HostMetricsTabViewModel();

        Assert.False(vm.IsCustomScopeModalOpen);
        Assert.False(vm.IsScopeCustom);

        vm.SetScope("custom");
        Assert.True(vm.IsCustomScopeModalOpen);
        Assert.True(vm.IsScopeCustom);

        vm.CustomStartDate = DateTimeOffset.Now.AddDays(-1);
        vm.CustomEndDate = DateTimeOffset.Now;
        vm.ApplyCustomScope();

        Assert.False(vm.IsCustomScopeModalOpen);
        Assert.Empty(vm.ForwardSeries);
    }

    [Fact]
    public void HostMetricsTabViewModel_CustomScopeModal_CancelRevertsScope()
    {
        var vm = new HostMetricsTabViewModel();
        vm.SetScope("custom");
        Assert.True(vm.IsCustomScopeModalOpen);

        vm.CancelCustomScope();
        Assert.False(vm.IsCustomScopeModalOpen);
        Assert.Equal("5m", vm.SelectedScope);
    }

    [Fact]
    public void SidebarViewModel_DoesNotReplaceNodesOnTelemetryUpdate()
    {
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new SidebarViewModel(provider);

        Assert.Equal(6, vm.Nodes.Count);

        bool collectionChanged = false;
        vm.Nodes.CollectionChanged += (s, e) => collectionChanged = true;

        var existingNode = provider.GetFleetNodes()[0];
        existingNode.Twamp.ForwardMs = 99.9;

        // Collection should not emit Replace events for existing nodes
        Assert.False(collectionChanged);
        Assert.Equal(6, vm.Nodes.Count);
    }

    [Fact]
    public void DualSparklineControl_RenderCalculations_DoNotThrowOnNarrowWidth()
    {
        // Test that clamp bounds do not invert when width is narrow or fractional
        const double yAxisWidth = 26.0;
        double w = 25.573486328125; // Exact value from crash report
        double tipW = 75.0;

        double minMouseX = yAxisWidth;
        double maxMouseX = Math.Max(minMouseX, w);
        double mouseX = Math.Clamp(10.0, minMouseX, maxMouseX);
        Assert.True(mouseX >= minMouseX);

        double minTipX = yAxisWidth + 2;
        double maxTipX = Math.Max(minTipX, w - tipW - 2);
        double targetX = yAxisWidth + 5;
        double tipX = Math.Clamp(targetX - tipW / 2.0, minTipX, maxTipX);
        Assert.True(tipX >= minTipX);
    }

    [Fact]
    public void FleetNodeModel_GetMetricValue_ResolvesLiveMetricsAndFallbacks()
    {
        var node = new FleetNodeModel
        {
            Id = "node-1",
            CpuAvgPct = 42.5,
            MemoryTotalBytes = 16_000_000_000,
            RamUsedPct = 50.0,
            HasBattery = true,
            BatteryPct = 85.0
        };
        node.Twamp.Available = true;
        node.Twamp.RttMs = 3.5;

        // Fallback checks
        Assert.Equal(42.5, node.GetMetricValue("cpu.total"));
        Assert.Equal(8_000_000_000.0, node.GetMetricValue("memory.used"));
        Assert.Equal(16_000_000_000.0, node.GetMetricValue("memory.total"));
        Assert.Equal(3.5, node.GetMetricValue("twamp.rtt"));
        Assert.Equal(85.0, node.GetMetricValue("power.battery_pct"));
        Assert.Null(node.GetMetricValue("unknown.metric"));

        // Explicit LatestMetricValues overrides
        node.LatestMetricValues["cpu.total"] = 99.1;
        node.LatestMetricValues["cpu.user"] = 65.4;
        Assert.Equal(99.1, node.GetMetricValue("cpu.total"));
        Assert.Equal(65.4, node.GetMetricValue("cpu.user"));
    }

    [Fact]
    public void HostMetricsTabViewModel_UpdateForNode_AppendsLiveSamplesToGraphs()
    {
        var vm = new HostMetricsTabViewModel();
        vm.SetScope("1m");

        var node = new FleetNodeModel
        {
            Id = "test-host",
            CpuAvgPct = 25.0,
            TimestampUnixNano = 1_700_000_000_000_000_000L
        };
        node.LatestMetricValues["cpu.total"] = 25.0;

        // Initial setup for host
        vm.UpdateForNode("test-host", node, new[] { node });

        // Push subsequent 1Hz live samples
        long t1 = 1_700_000_001_000_000_000L;
        node.TimestampUnixNano = t1;
        node.LatestMetricValues["cpu.total"] = 30.0;
        vm.UpdateForNode("test-host", node, new[] { node });

        long t2 = 1_700_000_002_000_000_000L;
        node.TimestampUnixNano = t2;
        node.LatestMetricValues["cpu.total"] = 35.0;
        vm.UpdateForNode("test-host", node, new[] { node });

        var cpuGraph = vm.Graphs.FirstOrDefault(g => g.Series.Any(s => s.Metric == "cpu.total"));
        Assert.NotNull(cpuGraph);
        var series = cpuGraph.Series.First(s => s.Metric == "cpu.total");

        Assert.Contains(35.0, series.Values);
        Assert.Equal(35.0, series.LatestValue);
        Assert.Equal(t2, cpuGraph.WindowEnd);
        Assert.Equal(t2 - (60L * 1_000_000_000L), cpuGraph.WindowStart);
    }

    [Fact]
    public void GraphLayoutStore_PerNodePersistenceAndLegacyMigration()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);

            // 1. Save configs for node "oc1" with a rate-of-change series
            var oc1Configs = new List<GraphItemConfig>
            {
                new()
                {
                    Title = "eth0 rx",
                    Series = new List<GraphSeriesConfig>
                    {
                        new() { Metric = "nic.eth0.rx_bytes", Label = "eth0 rx", ColorHex = "#06B6D4", IsRateOfChange = true }
                    }
                }
            };
            store.SaveConfigs("oc1", oc1Configs);

            // 2. Save configs for "aggregated"
            var aggConfigs = new List<GraphItemConfig>
            {
                new()
                {
                    Title = "Cluster CPU",
                    Series = new List<GraphSeriesConfig>
                    {
                        new() { Metric = "cpu.total", Label = "CPU Total", ColorHex = "#3B82F6", IsRateOfChange = false }
                    }
                }
            };
            store.SaveConfigs("aggregated", aggConfigs);

            // Verify isolated loading
            var loadedOc1 = store.LoadConfigs("oc1");
            Assert.NotNull(loadedOc1);
            Assert.Single(loadedOc1);
            Assert.Equal("eth0 rx", loadedOc1[0].Title);
            Assert.True(loadedOc1[0].Series[0].IsRateOfChange);

            var loadedAgg = store.LoadConfigs("aggregated");
            Assert.NotNull(loadedAgg);
            Assert.Single(loadedAgg);
            Assert.Equal("Cluster CPU", loadedAgg[0].Title);
            Assert.False(loadedAgg[0].Series[0].IsRateOfChange);

            // Node with no configuration returns null (will fallback to default graphs)
            Assert.Null(store.LoadConfigs("node-never-configured"));

            // 3. Test legacy flat list JSON migration to "aggregated"
            string legacyListJson = "[{\"Title\": \"Legacy Mem\", \"Series\": [{\"Metric\": \"memory.used\", \"Label\": \"RAM\", \"ColorHex\": \"#10B981\", \"IsRateOfChange\": false}]}]";
            System.IO.File.WriteAllText(path, legacyListJson);

            var legacyStore = new GraphLayoutStore(path);
            var migrated = legacyStore.LoadConfigs("aggregated");
            Assert.NotNull(migrated);
            Assert.Single(migrated);
            Assert.Equal("Legacy Mem", migrated[0].Title);
            Assert.Null(legacyStore.LoadConfigs("oc1"));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void MetricHistoryChartControl_FormatMetricValue_FormatsRatesCorrectly()
    {
        // CPU percentage rate
        Assert.Equal("+5.2%/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("cpu.total", 5.2, isRate: true));
        Assert.Equal("-3.1%/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("cpu.total", -3.1, isRate: true));
        Assert.Equal("5.2%", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("cpu.total", 5.2, isRate: false));

        // Network bytes rate
        Assert.Equal("+10.0 MB/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("nic.eth0.rx_bytes", 10_485_760, isRate: true));
        Assert.Equal("-500 KB/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("nic.eth0.rx_bytes", -512_000, isRate: true));
        Assert.Equal("+1.50 GB/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("nic.eth0.tx_bytes", 1.5 * 1_073_741_824, isRate: true));
        Assert.Equal("10.0 MB", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("nic.eth0.rx_bytes", 10_485_760, isRate: false));

        // TWAMP latency rate
        Assert.Equal("2.50 ms/s", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("twamp.rtt", 2.5, isRate: true));
        Assert.Equal("2.50 ms", MadTOM.Controls.Charts.MetricHistoryChartControl.FormatMetricValue("twamp.rtt", 2.5, isRate: false));
    }

    [Fact]
    public void HostMetricsTabViewModel_PerNodeLayoutAndInterfaceSeparation()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);
            var vm = new HostMetricsTabViewModel(layoutStore: store);

            var node1 = new FleetNodeModel
            {
                Id = "node1",
                Interfaces = new[] { new MADTOM.Plugins.Telemetry.Proto.V1.NicMetric { Name = "eth0" } }
            };
            var node2 = new FleetNodeModel
            {
                Id = "node2",
                Interfaces = new[] { new MADTOM.Plugins.Telemetry.Proto.V1.NicMetric { Name = "wlan0" } }
            };
            var allNodes = new[] { node1, node2 };

            // Switch to node1
            vm.UpdateForNode("node1", node1, allNodes);
            Assert.Contains("nic.eth0.rx_bytes", vm.AvailableMetrics);
            Assert.DoesNotContain("nic.wlan0.rx_bytes", vm.AvailableMetrics);

            // Add rate of change graph for eth0 on node1
            vm.SelectedMetric = "nic.eth0.rx_bytes";
            vm.IsAddMetricRateOfChange = true;
            vm.AddGraph();

            Assert.Contains(vm.Graphs, g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes" && s.IsRateOfChange));

            // Switch to node2
            vm.UpdateForNode("node2", node2, allNodes);

            // node2 should have its own default graphs and NOT node1's eth0 graph
            Assert.DoesNotContain(vm.Graphs, g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes"));
            Assert.Contains("nic.wlan0.rx_bytes", vm.AvailableMetrics);
            Assert.DoesNotContain("nic.eth0.rx_bytes", vm.AvailableMetrics);

            // Switch to aggregated mode
            vm.UpdateForNode("aggregated", null, allNodes);

            // Aggregated mode combines interfaces from all cluster nodes
            Assert.Contains("nic.eth0.rx_bytes", vm.AvailableMetrics);
            Assert.Contains("nic.wlan0.rx_bytes", vm.AvailableMetrics);
            // Aggregated mode also does not inherit node1's custom eth0 graph
            Assert.DoesNotContain(vm.Graphs, g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes"));

            // Switch back to node1: layout should be preserved from disk
            vm.UpdateForNode("node1", node1, allNodes);
            Assert.Contains(vm.Graphs, g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes" && s.IsRateOfChange));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void HostMetricsTabViewModel_LiveSampleRateOfChange_ComputesDerivativeAndHandlesReset()
    {
        var vm = new HostMetricsTabViewModel();
        vm.SetScope("1m");

        var node = new FleetNodeModel { Id = "test-node" };

        vm.UpdateForNode("test-node", node, new[] { node });

        // Add a rate-of-change graph
        vm.SelectedMetric = "nic.eth0.rx_bytes";
        vm.IsAddMetricRateOfChange = true;
        vm.AddGraph();

        var graph = vm.Graphs.First(g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes"));
        var series = graph.Series.First(s => s.Metric == "nic.eth0.rx_bytes");
        Assert.True(series.IsRateOfChange);

        // Sample 1: Baseline sample (counter = 1,000,000)
        long t0 = 1_700_000_000_000_000_000L;
        node.TimestampUnixNano = t0;
        node.LatestMetricValues["nic.eth0.rx_bytes"] = 1_000_000.0;
        vm.UpdateForNode("test-node", node, new[] { node });

        // No rate value should be computed on very first baseline sample
        Assert.Empty(series.Values);

        // Sample 2: 1 second later, counter = 2,500,000 (+1,500,000 bytes over 1.0s)
        long t1 = t0 + 1_000_000_000L;
        node.TimestampUnixNano = t1;
        node.LatestMetricValues["nic.eth0.rx_bytes"] = 2_500_000.0;
        vm.UpdateForNode("test-node", node, new[] { node });

        Assert.Single(series.Values);
        Assert.Equal(1_500_000.0, series.LatestValue, 1);

        // Duplicate and late notifications must not overwrite the rate or its baseline.
        vm.UpdateForNode("test-node", node, new[] { node });
        Assert.Equal(1_500_000.0, Assert.Single(series.Values));
        node.TimestampUnixNano = t0;
        node.LatestMetricValues["nic.eth0.rx_bytes"] = 99;
        vm.UpdateForNode("test-node", node, new[] { node });
        Assert.Equal(1_500_000.0, Assert.Single(series.Values));

        // Sample 3: 1 second later, counter reset / reboot (counter dropped to 500,000)
        long t2 = t1 + 1_000_000_000L;
        node.TimestampUnixNano = t2;
        node.LatestMetricValues["nic.eth0.rx_bytes"] = 500_000.0;
        vm.UpdateForNode("test-node", node, new[] { node });

        Assert.Equal(2, series.Values.Length);
        // Reset should clamp to 0 rather than reporting negative 2,000,000 B/s
        Assert.Equal(0.0, series.LatestValue);
    }

    [Fact]
    public async Task HostMetricsTabViewModel_RefreshHistoryAsync_ComputesRateOfChangeDerivative()
    {
        var mockProvider = new RateTestHistoryProvider();
        var vm = new HostMetricsTabViewModel(mockProvider);
        vm.SetScope("1m");

        var node = new FleetNodeModel { Id = "test-node" };
        vm.UpdateForNode("test-node", node, new[] { node });

        // Add rate-of-change graph
        vm.SelectedMetric = "nic.eth0.rx_bytes";
        vm.IsAddMetricRateOfChange = true;
        vm.AddGraph();

        await vm.RefreshHistoryAsync();

        var graph = vm.Graphs.First(g => g.Series.Any(s => s.Metric == "nic.eth0.rx_bytes"));
        var series = graph.Series.First(s => s.Metric == "nic.eth0.rx_bytes");

        // mockProvider provides 3 raw points:
        // t0: 1000
        // t0 + 1s: 3000 (delta = 2000, dt = 1.0s => rate = 2000)
        // t0 + 3s: 7000 (delta = 4000, dt = 2.0s => rate = 2000)
        Assert.Equal(2, series.Values.Length);
        Assert.Equal(2000.0, series.Values[0], 1);
        Assert.Equal(2000.0, series.Values[1], 1);
    }

    private sealed class RateTestHistoryProvider : ITelemetryDataProvider
    {
        public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated { add { } remove { } }
        public event EventHandler<LogEntryModel>? LogReceived { add { } remove { } }

        public IReadOnlyList<FleetNodeModel> GetFleetNodes() => Array.Empty<FleetNodeModel>();
        public FleetNodeModel? GetNode(string id) => null;
        public ClusterTelemetrySummary GetClusterSummary() => new();
        public IReadOnlyList<ProcessInfoModel> GetProcesses(string id) => Array.Empty<ProcessInfoModel>();
        public IReadOnlyList<DropRuleModel> GetDropRules(string id) => Array.Empty<DropRuleModel>();
        public IReadOnlyList<RegionTrafficModel> GetRegions(string id) => Array.Empty<RegionTrafficModel>();

        public Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, CancellationToken ct = default)
        {
            long baseNano = 1_700_000_000_000_000_000L;
            var points = new List<LODPoint>
            {
                new(baseNano, 1000, 1000, 1000),
                new(baseNano + 1_000_000_000L, 3000, 3000, 3000),
                new(baseNano + 3_000_000_000L, 7000, 7000, 7000)
            };
            return Task.FromResult<IReadOnlyList<LODPoint>>(points);
        }

        public void SendSignal(string hostId, int pid, int signal) { }
        public void PauseLogs(bool paused) { }
        public void ClearLogs() { }
        public void Dispose() { }
    }

    [Fact]
    public void GraphLayoutStore_GroupConfigs_SaveAndLoadRoundtrip()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);
            var groups = new List<GraphGroupConfig>
            {
                new("Row 1: System", new[]
                {
                    new GraphItemConfig
                    {
                        Title = "CPU Breakdown",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "cpu.total", Label = "Total", ColorHex = "#06B6D4" },
                            new() { Metric = "cpu.user", Label = "User", ColorHex = "#3B82F6" }
                        }
                    },
                    new GraphItemConfig
                    {
                        Title = "Memory Breakdown",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "memory.used", Label = "Used", ColorHex = "#10B981" }
                        }
                    }
                }),
                new("Row 2: Network", new[]
                {
                    new GraphItemConfig
                    {
                        Title = "eth0 Throughput",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "nic.eth0.rx_bytes", Label = "RX/s", ColorHex = "#8B5CF6", IsRateOfChange = true }
                        }
                    }
                })
            };

            store.SaveGroupConfigs("node42", groups);

            var loaded = store.LoadGroupConfigs("node42");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Count);

            // Group 0 has 2 side-by-side graphs
            Assert.Equal("Row 1: System", loaded[0].Title);
            Assert.Equal(2, loaded[0].Graphs.Count);
            Assert.Equal("CPU Breakdown", loaded[0].Graphs[0].Title);
            Assert.Equal(2, loaded[0].Graphs[0].Series.Count);
            Assert.Equal("Memory Breakdown", loaded[0].Graphs[1].Title);

            // Group 1 has 1 graph
            Assert.Equal("Row 2: Network", loaded[1].Title);
            Assert.Single(loaded[1].Graphs);
            Assert.True(loaded[1].Graphs[0].Series[0].IsRateOfChange);

            // Backward compatible LoadConfigs should flatten all graphs
            var flat = store.LoadConfigs("node42");
            Assert.NotNull(flat);
            Assert.Equal(3, flat.Count);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void GraphLayoutStore_GroupConfigs_LegacyFallbacks()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphLayoutStore(path);

            // 1. Fallback from legacy string array
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { "cpu.total", "memory.used" }));
            var groupsFromStringArray = store.LoadGroupConfigs("aggregated");
            Assert.NotNull(groupsFromStringArray);
            Assert.Equal(2, groupsFromStringArray.Count);
            Assert.Equal("cpu.total", groupsFromStringArray[0].Graphs[0].Title);
            Assert.Equal("memory.used", groupsFromStringArray[1].Graphs[0].Title);

            // 2. Fallback from flat List<GraphItemConfig>
            var flatList = new List<GraphItemConfig>
            {
                new() { Title = "twamp.rtt", Series = new List<GraphSeriesConfig> { new() { Metric = "twamp.rtt" } } }
            };
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(flatList));
            var groupsFromFlatList = store.LoadGroupConfigs("aggregated");
            Assert.NotNull(groupsFromFlatList);
            Assert.Single(groupsFromFlatList);
            Assert.Equal("twamp.rtt", groupsFromFlatList[0].Graphs[0].Title);

            // 3. Fallback from Dictionary<string, List<GraphItemConfig>>
            var dict = new Dictionary<string, List<GraphItemConfig>>
            {
                ["node-a"] = new List<GraphItemConfig>
                {
                    new() { Title = "cpu.user", Series = new List<GraphSeriesConfig> { new() { Metric = "cpu.user" } } }
                }
            };
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dict));
            var groupsFromDict = store.LoadGroupConfigs("node-a");
            Assert.NotNull(groupsFromDict);
            Assert.Single(groupsFromDict);
            Assert.Equal("cpu.user", groupsFromDict[0].Graphs[0].Title);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void GraphPresetStore_BuiltInAndCustomPresets()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GraphPresetStore(path);
            var presets = store.LoadAllPresets();
            Assert.True(presets.Count >= 4);
            Assert.Contains(presets, p => p.Name == "Standard Stack" && p.IsBuiltIn);
            Assert.Contains(presets, p => p.Name == "Dual Side-by-Side" && p.IsBuiltIn);
            Assert.Contains(presets, p => p.Name == "Quad Horizontal Grid" && p.IsBuiltIn);
            Assert.Contains(presets, p => p.Name == "Network & Latency Focus" && p.IsBuiltIn);

            // Save custom preset
            var customGroups = new List<GraphGroupConfig>
            {
                new(new[]
                {
                    new GraphItemConfig { Title = "Test G1", Series = new List<GraphSeriesConfig> { new() { Metric = "cpu.total" } } },
                    new GraphItemConfig { Title = "Test G2", Series = new List<GraphSeriesConfig> { new() { Metric = "memory.used" } } }
                })
            };
            store.SaveUserPreset("My Custom View", customGroups, "A 2-column view");

            var updated = store.LoadAllPresets();
            var custom = updated.FirstOrDefault(p => p.Name == "My Custom View");
            Assert.NotNull(custom);
            Assert.False(custom.IsBuiltIn);
            Assert.Equal("A 2-column view", custom.Description);
            Assert.Single(custom.Groups);
            Assert.Equal(2, custom.Groups[0].Graphs.Count);

            // Delete custom preset
            bool deleted = store.DeleteUserPreset(custom.Id);
            Assert.True(deleted);

            var afterDelete = store.LoadAllPresets();
            Assert.DoesNotContain(afterDelete, p => p.Name == "My Custom View");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void HostMetricsTabViewModel_Groups_ReorderingAndRowSeparation()
    {
        string layoutPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        string presetPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var layoutStore = new GraphLayoutStore(layoutPath);
            var presetStore = new GraphPresetStore(presetPath);
            var vm = new HostMetricsTabViewModel(layoutStore: layoutStore, presetStore: presetStore);

            // Initial defaults: 2 groups, each with 1 graph (cpu.total and memory.used)
            Assert.Equal(2, vm.Groups.Count);
            Assert.Equal(2, vm.Graphs.Count);

            // Combine group 0 with group 1 so both are in the same row side-by-side
            vm.CombineGroupWithNext(vm.Groups[0]);
            Assert.Single(vm.Groups);
            Assert.Equal(2, vm.Groups[0].Graphs.Count);
            Assert.Equal(2, vm.Graphs.Count);
            Assert.Equal("cpu.total", vm.Groups[0].Graphs[0].Title);
            Assert.Equal("memory.used", vm.Groups[0].Graphs[1].Title);

            // Move Right on first graph: swaps position with memory.used
            var cpuGraph = vm.Groups[0].Graphs[0];
            vm.MoveGraphRight(cpuGraph);
            Assert.Equal("memory.used", vm.Groups[0].Graphs[0].Title);
            Assert.Equal("cpu.total", vm.Groups[0].Graphs[1].Title);
            Assert.Equal("memory.used", vm.Graphs[0].Title);
            Assert.Equal("cpu.total", vm.Graphs[1].Title);

            // Separate cpuGraph back into its own new row
            vm.SeparateGraphToNewRow(cpuGraph);
            Assert.Equal(2, vm.Groups.Count);
            Assert.Single(vm.Groups[0].Graphs);
            Assert.Equal("memory.used", vm.Groups[0].Graphs[0].Title);
            Assert.Single(vm.Groups[1].Graphs);
            Assert.Equal("cpu.total", vm.Groups[1].Graphs[0].Title);

            // Move Group 1 (cpu) up
            vm.MoveGroupUp(vm.Groups[1]);
            Assert.Equal("cpu.total", vm.Groups[0].Graphs[0].Title);
            Assert.Equal("memory.used", vm.Groups[1].Graphs[0].Title);
        }
        finally
        {
            if (System.IO.File.Exists(layoutPath)) System.IO.File.Delete(layoutPath);
            if (System.IO.File.Exists(presetPath)) System.IO.File.Delete(presetPath);
        }
    }

    [Fact]
    public void HostMetricsTabViewModel_Presets_ApplyAndSave()
    {
        string layoutPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        string presetPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var layoutStore = new GraphLayoutStore(layoutPath);
            var presetStore = new GraphPresetStore(presetPath);
            var vm = new HostMetricsTabViewModel(layoutStore: layoutStore, presetStore: presetStore);

            // Apply Dual Side-by-Side preset
            var dualPreset = vm.AvailablePresets.First(p => p.Name == "Dual Side-by-Side");
            vm.ApplyPreset(dualPreset);

            Assert.Equal(2, vm.Groups.Count);
            Assert.Equal(2, vm.Groups[0].Graphs.Count); // Row 1: CPU & Memory
            Assert.Equal(2, vm.Groups[1].Graphs.Count); // Row 2: TWAMP & Network
            Assert.Equal(4, vm.Graphs.Count);

            // Save as custom preset
            vm.OpenSavePresetModal();
            Assert.True(vm.IsSavePresetModalOpen);
            vm.NewPresetName = "My Production Dual";
            vm.NewPresetDescription = "Custom dual view";
            vm.ConfirmSavePreset();
            Assert.False(vm.IsSavePresetModalOpen);

            // Verify it was registered in AvailablePresets
            Assert.Contains(vm.AvailablePresets, p => p.Name == "My Production Dual");
            var myPreset = vm.AvailablePresets.First(p => p.Name == "My Production Dual");
            Assert.False(myPreset.IsBuiltIn);
            Assert.Equal("Custom dual view", myPreset.Description);
        }
        finally
        {
            if (System.IO.File.Exists(layoutPath)) System.IO.File.Delete(layoutPath);
            if (System.IO.File.Exists(presetPath)) System.IO.File.Delete(presetPath);
        }
    }

    [Fact]
    public void NodeGroupStore_GetSetAndPersist_WorksCorrectly()
    {
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store1 = new NodeGroupStore(tempPath);
            Assert.Equal(string.Empty, store1.GetGroup("unknown-node"));
            Assert.DoesNotContain("Default", store1.GetAllGroups());

            store1.SetGroup("node-1", "Compute");
            store1.SetGroup("node-2", "Storage");

            Assert.Equal("Compute", store1.GetGroup("node-1"));
            Assert.Equal("Storage", store1.GetGroup("node-2"));

            var groups = store1.GetAllGroups();
            Assert.Contains("Compute", groups);
            Assert.Contains("Storage", groups);
            Assert.DoesNotContain("Default", groups);

            // Setting to None or empty clears assignment
            store1.SetGroup("node-2", "None");
            Assert.Equal(string.Empty, store1.GetGroup("node-2"));
            Assert.DoesNotContain("Storage", store1.GetAllGroups());

            // Re-load from disk in another instance
            var store2 = new NodeGroupStore(tempPath);
            Assert.Equal("Compute", store2.GetGroup("node-1"));
            Assert.Equal(string.Empty, store2.GetGroup("node-2"));
        }
        finally
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public void FleetViewModel_CustomGroupFiltering_UpdatesCardsAndPills()
    {
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
            var store = new NodeGroupStore(tempPath);
            var nodes = provider.GetFleetNodes();
            Assert.NotEmpty(nodes);

            string targetNodeId = nodes[0].Id;
            store.SetGroup(targetNodeId, "Edge");

            var vm = new FleetViewModel(provider, store);
            Assert.Contains("Edge", vm.AvailableGroups);
            Assert.Contains("All", vm.AvailableGroups);

            // Filter by "Edge"
            vm.SelectedGroup = "Edge";
            Assert.All(vm.Cards, c => Assert.Equal("Edge", c.Node.GroupName));
            Assert.Contains(vm.Cards, c => c.Node.Id == targetNodeId);

            // Filter by "All"
            vm.SelectedGroup = "All";
            Assert.Equal(nodes.Count, vm.Cards.Count);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task CollectorSettingsViewModel_CollectorEditMode_Lifecycle()
    {
        var manager = new MultiCollectorManager(":memory:");
        manager.AddCollector("Secondary", "10.0.0.2:50051");

        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new CollectorSettingsViewModel(manager, provider);

        Assert.False(vm.IsEditingCollector);
        Assert.Equal("+ Add Collector", vm.SaveCollectorButtonText);

        var endpoint = vm.Endpoints.First(e => e.Address == "10.0.0.2:50051");
        vm.SelectCollectorForEdit(endpoint);

        Assert.True(vm.IsEditingCollector);
        Assert.Equal("Secondary", vm.NewName);
        Assert.Equal("10.0.0.2:50051", vm.NewAddress);
        Assert.Equal("Save Changes", vm.SaveCollectorButtonText);

        // Cancel edit
        vm.CancelEditCollector();
        Assert.False(vm.IsEditingCollector);
        Assert.Equal("+ Add Collector", vm.SaveCollectorButtonText);

        // Edit and Save Changes
        vm.SelectCollectorForEdit(endpoint);
        vm.NewName = "Renamed Secondary";
        vm.NewAddress = "10.0.0.2:50055";
        await vm.SaveCollectorAsync();

        Assert.False(vm.IsEditingCollector);
        Assert.DoesNotContain(vm.Endpoints, e => e.Address == "10.0.0.2:50051");
        Assert.Contains(vm.Endpoints, e => e.Address == "10.0.0.2:50055" && e.Name == "Renamed Secondary");
    }

    [Fact]
    public void HostMetricsTabViewModel_6hAnd12hScopes_Supported()
    {
        var vm = new HostMetricsTabViewModel();

        vm.SetScope("6h");
        Assert.Equal("6h", vm.SelectedScope);
        Assert.True(vm.IsScope6h);
        Assert.False(vm.IsScope12h);

        vm.SetScope("12h");
        Assert.Equal("12h", vm.SelectedScope);
        Assert.True(vm.IsScope12h);
        Assert.False(vm.IsScope6h);
    }

    [Fact]
    public void MetricHistoryChartControl_FormatMetricValue_HandlesNaNAndInfinity()
    {
        Assert.Equal("N/A", MetricHistoryChartControl.FormatMetricValue("cpu.total", double.NaN));
        Assert.Equal("N/A", MetricHistoryChartControl.FormatMetricValue("disk.io.read_bytes", double.PositiveInfinity));
        Assert.Equal("N/A", MetricHistoryChartControl.FormatMetricValue("twamp.forward", double.NegativeInfinity));

        Assert.Equal("1.0 MB", MetricHistoryChartControl.FormatMetricValue("disk.io.read_bytes", 1048576));
        Assert.Equal("+1.0 MB/s", MetricHistoryChartControl.FormatMetricValue("disk.io.read_bytes", 1048576, isRate: true));
        Assert.Equal("10.5%", MetricHistoryChartControl.FormatMetricValue("cpu.total", 10.5));
    }

    [Fact]
    public void CollectorSettingsViewModel_GlobalMetrics_CalculatesAggregatesAndModifiers()
    {
        var manager = new MultiCollectorManager();
        var dataProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var store = new NodeGroupStore();
        var vm = new CollectorSettingsViewModel(manager, dataProvider, store);

        Assert.NotEmpty(vm.GlobalMetrics);
        var ingressMetric = vm.GlobalMetrics.FirstOrDefault(m => m.Key == "network.ingress");
        Assert.NotNull(ingressMetric);

        // Verify Sum modifier (default)
        Assert.True(vm.IsModifierSum);
        Assert.Equal("Sum", ingressMetric.ModifierLabel);
        Assert.NotEmpty(ingressMetric.FormattedValue);

        // Switch to Avg
        vm.SetModifier("Avg");
        Assert.True(vm.IsModifierAvg);
        Assert.False(vm.IsModifierSum);
        Assert.Equal("Avg", ingressMetric.ModifierLabel);

        // Switch to Rate of Change
        vm.SetModifier("Rate of Change");
        Assert.True(vm.IsModifierRate);
        Assert.False(vm.IsModifierAvg);
        Assert.Equal("Rate of Change", ingressMetric.ModifierLabel);
    }

    [Fact]
    public void NodeGroups_DecoupledFromTelemetryTick_RetainsUnassignedNone()
    {
        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new NodeGroupStore(tempPath);
            var dataProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
            var fleetVm = new FleetViewModel(dataProvider, store);

            // Assign "Compute" to first node
            var firstNode = fleetVm.Cards[0].Node;
            store.SetGroup(firstNode.Id, "Compute");

            Assert.Contains("Compute", fleetVm.AvailableGroups);

            // Now assign "None" to clear it
            store.SetGroup(firstNode.Id, "None");

            Assert.DoesNotContain("Compute", fleetVm.AvailableGroups);
            Assert.Equal(new[] { "All" }, fleetVm.AvailableGroups);

            // Simulate multiple 1Hz telemetry updates
            for (int i = 0; i < 10; i++)
            {
                firstNode.CpuAvgPct = 50.0 + i;
                // Emulate NodeTelemetryUpdated tick with a node whose GroupName might be stale/empty
                fleetVm.ApplyFilter();
            }

            // "Compute" must NEVER reappear! AvailableGroups must remain strictly ["All"]
            Assert.DoesNotContain("Compute", fleetVm.AvailableGroups);
            Assert.Equal(new[] { "All" }, fleetVm.AvailableGroups);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public void GlobalMetricsStore_PinAndUnpin_UpdatesHeaderCollection()
    {
        string tempMetricsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new GlobalMetricsStore(tempMetricsPath);
            var dataProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
            var lexiconService = LexiconService.Instance;
            var headerVm = new HeaderViewModel(lexiconService, dataProvider, store);

            // Default pinned items: Ingress and Egress
            Assert.Equal(2, headerVm.PinnedMetrics.Count);
            Assert.Contains(headerVm.PinnedMetrics, m => m.Key == "network.ingress");
            Assert.Contains(headerVm.PinnedMetrics, m => m.Key == "network.egress");

            // Pin CPU (Avg)
            store.PinMetric("cpu.load", "Avg");
            Assert.Equal(3, headerVm.PinnedMetrics.Count);
            var cpuMetric = headerVm.PinnedMetrics.FirstOrDefault(m => m.Key == "cpu.load");
            Assert.NotNull(cpuMetric);
            Assert.Equal("Avg", cpuMetric.ModifierLabel);
            Assert.Equal("AVG", cpuMetric.ModifierTag);

            // Unpin Ingress
            store.UnpinMetric("network.ingress", "Sum");
            Assert.Equal(2, headerVm.PinnedMetrics.Count);
            Assert.DoesNotContain(headerVm.PinnedMetrics, m => m.Key == "network.ingress");
        }
        finally
        {
            if (System.IO.File.Exists(tempMetricsPath))
                System.IO.File.Delete(tempMetricsPath);
        }
    }

    [Fact]
    public void CollectorSettingsViewModel_OptInGlobalMetrics_CommandsWork()
    {
        string tempMetricsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var metricsStore = new GlobalMetricsStore(tempMetricsPath);
            var manager = new MultiCollectorManager();
            var dataProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
            var nodeStore = new NodeGroupStore();
            var vm = new CollectorSettingsViewModel(manager, dataProvider, nodeStore, metricsStore);

            // Select CPU and Rate
            vm.SelectedMetricOption = vm.AvailableMetricOptions.First(m => m.Key == "cpu.load");
            vm.SetAddModifier("Rate of Change");
            Assert.True(vm.IsAddModifierRate);

            // Pin selection
            vm.PinCurrentSelectionCommand.Execute(null);
            Assert.True(metricsStore.IsPinned("cpu.load", "Rate of Change"));

            // Toggle pin on a catalog item
            var ramItem = vm.GlobalMetrics.First(m => m.Key == "memory.bytes");
            vm.TogglePinForMetricCommand.Execute(ramItem);
            Assert.True(ramItem.IsPinned);

            vm.TogglePinForMetricCommand.Execute(ramItem);
            Assert.False(ramItem.IsPinned);
        }
        finally
        {
            if (System.IO.File.Exists(tempMetricsPath))
                System.IO.File.Delete(tempMetricsPath);
        }
    }

    [Fact]
    public void MetricHistoryChartControl_FormatMetricValue_NormalizesMemoryUnits()
    {
        // 657,027,072 bytes -> ~626.6 MB
        string formattedMb = MetricHistoryChartControl.FormatMetricValue("memory.used", 657027072);
        Assert.Equal("626.6 MB", formattedMb);

        // 17,179,869,184 bytes -> 16.00 GB
        string formattedGb = MetricHistoryChartControl.FormatMetricValue("memory.total", 17179869184);
        Assert.Equal("16.00 GB", formattedGb);

        // Substring match "mem"
        string formattedMem = MetricHistoryChartControl.FormatMetricValue("system_mem_free", 1048576);
        Assert.Equal("1.0 MB", formattedMem);

        // Bytes
        string formattedBytes = MetricHistoryChartControl.FormatMetricValue("memory.buffers", 512);
        Assert.Equal("512 B", formattedBytes);

        // Invalid / NaN
        Assert.Equal("N/A", MetricHistoryChartControl.FormatMetricValue("memory.used", double.NaN));
    }

    [Fact]
    public void ChartSeriesModel_ColorProperty_ConvertsHexAndNotifies()
    {
        var series = new ChartSeriesModel("cpu.load", "CPU Load", "#06B6D4");
        bool configFired = false;
        series.ConfigurationChanged += () => configFired = true;

        // Set Avalonia Color
        series.Color = Avalonia.Media.Color.FromRgb(239, 68, 68); // #EF4444
        Assert.True(configFired);
        Assert.Equal("#EF4444", series.ColorHex);
        Assert.Equal((byte)239, series.Color.R);
        Assert.Equal((byte)68, series.Color.G);
        Assert.Equal((byte)68, series.Color.B);

        // Direct hex change
        configFired = false;
        series.ChangeColor("#10B981");
        Assert.True(configFired);
        Assert.Equal("#10B981", series.ColorHex);
        Assert.Equal((byte)16, series.Color.R);
        Assert.Equal((byte)185, series.Color.G);
        Assert.Equal((byte)129, series.Color.B);
    }

    [Fact]
    public void AppSettingsStore_UiScalePercent_DefaultsTo100()
    {
        var settings = new AppSettings();
        Assert.Equal(100, settings.UiScalePercent);
    }

    [Fact]
    public void MultiDisk_PopulateAvailableMetrics_IncludesAllDisksAndNics()
    {
        var vm = new HostMetricsTabViewModel();
        var node = new FleetNodeModel
        {
            Id = "node-1",
            Disks = new[]
            {
                new MADTOM.Plugins.Telemetry.Proto.V1.DiskIoDevice { Name = "sda" },
                new MADTOM.Plugins.Telemetry.Proto.V1.DiskIoDevice { Name = "sda1" },
                new MADTOM.Plugins.Telemetry.Proto.V1.DiskIoDevice { Name = "sdb" }
            },
            Interfaces = new[]
            {
                new MADTOM.Plugins.Telemetry.Proto.V1.NicMetric { Name = "eth0" },
                new MADTOM.Plugins.Telemetry.Proto.V1.NicMetric { Name = "wg0" }
            }
        };

        vm.PopulateAvailableMetrics(node, new[] { node });

        Assert.Contains("disk.io.sda.read_bytes", vm.AvailableMetrics);
        Assert.Contains("disk.io.sda.write_bytes", vm.AvailableMetrics);
        Assert.Contains("disk.io.sda.read_ops", vm.AvailableMetrics);
        Assert.Contains("disk.io.sda.write_ops", vm.AvailableMetrics);
        Assert.Contains("disk.io.sda1.read_bytes", vm.AvailableMetrics);
        Assert.Contains("disk.io.sdb.write_ops", vm.AvailableMetrics);
        Assert.Contains("nic.eth0.rx_bytes", vm.AvailableMetrics);
        Assert.Contains("nic.wg0.tx_bytes", vm.AvailableMetrics);
    }

    [Fact]
    public void GlobalMetricsStore_GetMetricDefinition_ParsesDisksAndNics()
    {
        var readDef = GlobalMetricsStore.GetMetricDefinition("disk.io.sda.read_bytes");
        Assert.Equal("Disk Read (sda)", readDef.Name);
        Assert.Equal("sda R", readDef.ShortName);
        Assert.Equal("B/s", readDef.Unit);
        Assert.Equal("📖", readDef.Icon);

        var opsDef = GlobalMetricsStore.GetMetricDefinition("disk.io.nvme0n1.write_ops");
        Assert.Equal("Disk Write IOPS (nvme0n1)", opsDef.Name);
        Assert.Equal("nvme0n1 W-IOPS", opsDef.ShortName);
        Assert.Equal("IOPS", opsDef.Unit);
        Assert.Equal("⚡", opsDef.Icon);

        var nicDef = GlobalMetricsStore.GetMetricDefinition("nic.eth0.rx_bytes");
        Assert.Equal("Network Ingress (eth0)", nicDef.Name);
        Assert.Equal("eth0 In", nicDef.ShortName);
        Assert.Equal("bps", nicDef.Unit);
        Assert.Equal("⬇", nicDef.Icon);

        var catalogDef = GlobalMetricsStore.GetMetricDefinition("network.ingress");
        Assert.Equal("Network Ingress", catalogDef.Name);
    }

    [Fact]
    public void HostMetricsTabViewModel_QuickMergeDiskIo_CreatesMergedChart()
    {
        var vm = new HostMetricsTabViewModel();
        int initialCount = vm.Graphs.Count;

        vm.QuickMergeDiskIoCommand.Execute(null);

        Assert.Equal(initialCount + 1, vm.Graphs.Count);
        var merged = vm.Graphs.Last();
        Assert.Equal("Disk I/O Throughput", merged.Title);
        Assert.Equal(2, merged.Series.Count);
        Assert.Equal("disk.io.read_bytes", merged.Series[0].Metric);
        Assert.True(merged.Series[0].IsRateOfChange);
        Assert.Equal("disk.io.write_bytes", merged.Series[1].Metric);
        Assert.True(merged.Series[1].IsRateOfChange);
    }

    [Fact]
    public void GlobalMetricItemViewModel_FormatsDiskAndNicMetrics()
    {
        var diskItem = new GlobalMetricItemViewModel("disk.io.sda.read_bytes", "Disk Read (sda)", "📖", "B/s", "sda R");
        diskItem.Update(sum: 104857600, avg: 52428800, rate: 5242880, activeNodes: 2, modifier: "Rate of Change");
        Assert.Equal("+5.0 MB/s", diskItem.FormattedValue);

        diskItem.ApplyModifier("Sum");
        Assert.Equal("100.0 MB", diskItem.FormattedValue);

        var nicItem = new GlobalMetricItemViewModel("nic.eth0.rx_bytes", "Network Ingress (eth0)", "⬇", "bps", "eth0 In");
        nicItem.Update(sum: 12500000, avg: 6250000, rate: 1250000, activeNodes: 2, modifier: "Rate of Change");
        Assert.Equal("+1.25 Mbps/s", nicItem.FormattedValue);
    }

    [Theory]
    [InlineData("SizeWestEast")]
    [InlineData("SizeNorthSouth")]
    [InlineData("TopLeftCorner")]
    [InlineData("TopRightCorner")]
    [InlineData("BottomLeftCorner")]
    [InlineData("BottomRightCorner")]
    public void ModalResizeCursors_AreValidAvaloniaStandardCursorTypes(string cursorName)
    {
        Assert.True(Enum.TryParse<Avalonia.Input.StandardCursorType>(cursorName, out _));
    }

    [Theory]
    [InlineData("chrome.bin", "chrome.bin")]
    [InlineData("kworker/u16:0", "kworker_u16_0")]
    [InlineData("  /usr/bin/python3.11  ", "usr_bin_python3.11")]
    [InlineData("", "unknown")]
    [InlineData("!!!", "unknown")]
    public void ProcessTelemetry_SanitizeMetricName_CleansSpecialChars(string input, string expected)
    {
        Assert.Equal(expected, FleetNodeModel.SanitizeMetricName(input));
    }

    [Fact]
    public void ProcessTelemetry_FleetNodeModel_GetMetricValue_ResolvesProcessesAndOther()
    {
        var node = new FleetNodeModel
        {
            Id = "node-proc",
            CpuAvgPct = 75.0,
            ProcessesAvailable = true,
            Processes = new[]
            {
                new ProcessInfoModel { Pid = 100, Name = "postgres", Cpu = 40.0 },
                new ProcessInfoModel { Pid = 101, Name = "nginx", Cpu = 15.0 },
                new ProcessInfoModel { Pid = 102, Name = "redis-server", Cpu = 5.0 }
            }
        };

        // Fallback computation directly from Processes
        Assert.Equal(40.0, node.GetMetricValue("proc.cpu.postgres"));
        Assert.Equal(15.0, node.GetMetricValue("proc.cpu.nginx"));
        Assert.Equal(5.0, node.GetMetricValue("proc.cpu.redis-server"));
        // Remainder: 75.0 - (40 + 15 + 5) = 15.0
        Assert.Equal(15.0, node.GetMetricValue("proc.cpu.other"));

        // Explicit override in LatestMetricValues takes precedence
        node.LatestMetricValues["proc.cpu.postgres"] = 42.0;
        Assert.Equal(42.0, node.GetMetricValue("proc.cpu.postgres"));
    }

    [Fact]
    public void NodeSettingsViewModel_ProcessTelemetry_ModesAndSlider()
    {
        var node = new FleetNodeModel { Id = "node-test" };
        var vm = new NodeSettingsViewModel(node, null);

        // Default mode is LiveOnly
        Assert.Equal(MADTOM.Plugins.Telemetry.Proto.V1.ProcessTelemetryMode.ProcessModeLiveOnly, vm.ProcessMode);
        Assert.False(vm.IsProcessDisabled);
        Assert.True(vm.IsProcessLiveOnly);
        Assert.False(vm.IsProcessStored);

        // Switch to Disabled
        vm.IsProcessDisabled = true;
        Assert.Equal(MADTOM.Plugins.Telemetry.Proto.V1.ProcessTelemetryMode.ProcessModeDisabled, vm.ProcessMode);
        Assert.True(vm.IsProcessDisabled);
        Assert.False(vm.IsProcessLiveOnly);
        Assert.False(vm.IsProcessStored);

        // Switch to Probed & Stored
        vm.IsProcessStored = true;
        Assert.Equal(MADTOM.Plugins.Telemetry.Proto.V1.ProcessTelemetryMode.ProcessModeProbedAndStored, vm.ProcessMode);
        Assert.False(vm.IsProcessDisabled);
        Assert.False(vm.IsProcessLiveOnly);
        Assert.True(vm.IsProcessStored);

        // Slider for TopNProcesses (default 5, clamps between 1 and 10)
        Assert.Equal(5, vm.TopNProcesses);
        vm.TopNProcesses = 3;
        Assert.Equal(3, vm.TopNProcesses);
    }

    [Fact]
    public void HostMetricsTabViewModel_QuickMergeProcessCpu_CreatesBreakdownGraph()
    {
        var vm = new HostMetricsTabViewModel();
        var node = new FleetNodeModel
        {
            Id = "node-quick",
            Processes = new[]
            {
                new ProcessInfoModel { Name = "dotnet", Cpu = 25.0 },
                new ProcessInfoModel { Name = "mysqld", Cpu = 15.0 }
            }
        };

        vm.UpdateForNode("node-quick", node, new[] { node });
        Assert.Contains("process.breakdown", vm.AvailableMetrics);
        Assert.DoesNotContain("proc.cpu.dotnet", vm.AvailableMetrics);
        Assert.DoesNotContain("proc.cpu.mysqld", vm.AvailableMetrics);
        Assert.DoesNotContain("proc.cpu.other", vm.AvailableMetrics);

        // Execute QuickMergeProcessCpu
        vm.QuickMergeProcessCpu();

        var breakdownGroup = vm.Groups.FirstOrDefault(g => g.Title == "Process Breakdown" || g.Title == "Process CPU Breakdown");
        Assert.NotNull(breakdownGroup);
        var graph = breakdownGroup.Graphs.First();
        Assert.Equal("Process Breakdown", graph.Title);
        Assert.Contains(graph.Series, s => s.Metric == "cpu.total");
        Assert.Contains(graph.Series, s => s.Metric == "proc.cpu.dotnet");
        Assert.Contains(graph.Series, s => s.Metric == "proc.cpu.mysqld");
        Assert.Contains(graph.Series, s => s.Metric == "proc.cpu.other");
    }

    [Fact]
    public void HostProcessesTabViewModel_DisablesWhenNodeHasNoProcessesAvailable()
    {
        var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new HostProcessesTabViewModel(provider);

        var firstNode = provider.GetFleetNodes()[0];
        string targetId = firstNode.Id;

        // Target host has processes available
        vm.SetTargetHost(targetId);
        Assert.False(vm.IsProcessCollectionDisabled);

        // Set node ProcessesAvailable = false
        firstNode.ProcessesAvailable = false;

        vm.SetTargetHost(targetId);
        Assert.True(vm.IsProcessCollectionDisabled);
    }

    [Fact]
    public void NodeSettingsViewModel_DetectsUnappliedChanges_AndReverts()
    {
        var node = new FleetNodeModel { Id = "test-node" };
        var vm = new NodeSettingsViewModel(node, null);

        Assert.False(vm.HasUnappliedChanges);

        // Modify a boolean toggle
        vm.CollectCpuPerCore = !vm.CollectCpuPerCore;
        Assert.True(vm.HasUnappliedChanges);

        // Modify it back
        vm.CollectCpuPerCore = !vm.CollectCpuPerCore;
        Assert.False(vm.HasUnappliedChanges);

        // Modify multiple settings
        vm.CollectMemoryBasic = false;
        vm.TopNProcesses = 9;
        vm.ProcessSnapshotLimit = 25;
        vm.TwampTarget = "127.0.0.1:862";
        Assert.True(vm.HasUnappliedChanges);

        // Revert changes
        vm.RevertChanges();
        Assert.False(vm.HasUnappliedChanges);
        Assert.True(vm.CollectMemoryBasic);
        Assert.Equal(5, vm.TopNProcesses);
        Assert.Equal(1000, vm.ProcessSnapshotLimit);
        Assert.Equal(string.Empty, vm.TwampTarget);
    }

    [Fact]
    public void CollectorSettingsViewModel_HandlesUnappliedChangesAndDefaultSelection()
    {
        var manager = new MultiCollectorManager();
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new CollectorSettingsViewModel(manager, provider);

        // 1. Initial state: SelectedNode should be null (None selected)
        Assert.Null(vm.SelectedNode);
        Assert.Null(vm.SelectedNodeSettings);
        Assert.False(vm.IsUnsavedPromptOpen);

        // 2. Select a node
        var node = new FleetNodeModel { Id = "node-alpha", CollectorName = "Local" };
        vm.SelectedNode = node;
        Assert.NotNull(vm.SelectedNodeSettings);
        Assert.False(vm.SelectedNodeSettings.HasUnappliedChanges);

        // 3. Close with clean settings -> Closes immediately
        bool closed = false;
        vm.CloseRequested += () => closed = true;
        vm.Close();
        Assert.True(closed);
        Assert.False(vm.IsUnsavedPromptOpen);

        // 4. Modify settings -> HasUnappliedChanges becomes true
        closed = false;
        vm.SelectedNodeSettings.CollectCpuOverall = false;
        Assert.True(vm.SelectedNodeSettings.HasUnappliedChanges);

        // 5. Close with unapplied changes -> Opens prompt, does NOT close
        vm.Close();
        Assert.False(closed);
        Assert.True(vm.IsUnsavedPromptOpen);

        // 6. Cancel close prompt -> Keeps dirty settings, prompt closes, modal stays open
        vm.CancelClosePrompt();
        Assert.False(vm.IsUnsavedPromptOpen);
        Assert.False(closed);
        Assert.True(vm.SelectedNodeSettings.HasUnappliedChanges);

        // 7. Discard and close -> Reverts changes, closes modal
        vm.IsUnsavedPromptOpen = true;
        vm.DiscardAndClose();
        Assert.False(vm.IsUnsavedPromptOpen);
        Assert.True(closed);
        Assert.False(vm.SelectedNodeSettings.HasUnappliedChanges);
        Assert.True(vm.SelectedNodeSettings.CollectCpuOverall);

        // 8. ClearSelectedNodeCommand resets to null
        vm.ClearSelectedNode();
        Assert.Null(vm.SelectedNode);
        Assert.Null(vm.SelectedNodeSettings);
    }

    [Fact]
    public void CustomProcess_Selector_OpensFiltersAndAddsGraph()
    {
        var vm = new HostMetricsTabViewModel();
        var node = new FleetNodeModel
        {
            Id = "node-1",
            Processes = new List<ProcessInfoModel>
            {
                new() { Pid = 101, Name = "dotnet", User = "root", Cpu = 25.5 },
                new() { Pid = 202, Name = "madtom", User = "danial", Cpu = 15.0 },
                new() { Pid = 303, Name = "caddy", User = "www-data", Cpu = 5.2 }
            }
        };

        vm.UpdateForNode("node-1", node, new[] { node });
        Assert.Contains("custom.process", vm.AvailableMetrics);

        // Selecting custom.process and AddGraph opens the modal
        vm.SelectedMetric = "custom.process";
        vm.AddGraph();
        Assert.True(vm.IsCustomProcessModalOpen);
        Assert.Equal(3, vm.SelectableProcesses.Count);
        Assert.Equal("dotnet", vm.SelectedCustomProcess?.Name);

        // Filtering by name
        vm.CustomProcessSearchFilter = "caddy";
        Assert.Single(vm.FilteredSelectableProcesses);
        Assert.Equal("caddy", vm.SelectedCustomProcess?.Name);

        // Confirm adds graph
        vm.ConfirmAddCustomProcess();
        Assert.False(vm.IsCustomProcessModalOpen);
        Assert.Contains(vm.Graphs, g => g.Title == "Process: caddy");

        // Cancel modal closes without adding
        vm.SelectedMetric = "custom.process";
        vm.AddGraph();
        Assert.True(vm.IsCustomProcessModalOpen);
        vm.CancelCustomProcess();
        Assert.False(vm.IsCustomProcessModalOpen);
    }

    private sealed class TestTelemetryProvider : ITelemetryDataProvider
    {
        public List<FleetNodeModel> Nodes { get; } = new();
        public List<ProcessInfoModel> Processes { get; set; } = new();
        public Dictionary<string, NodeConfig> Configs { get; } = new();
        public List<(string HostId, NodeConfig Config)> UpdatedConfigs { get; } = new();

        public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated;
        public event EventHandler<LogEntryModel>? LogReceived;

        public IReadOnlyList<FleetNodeModel> GetFleetNodes() => Nodes;
        public FleetNodeModel? GetNode(string hostId) => Nodes.FirstOrDefault(n => n.Id == hostId);
        public ClusterTelemetrySummary GetClusterSummary() => new();
        public IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId) => Processes;
        public IReadOnlyList<DropRuleModel> GetDropRules(string hostId) => Array.Empty<DropRuleModel>();
        public IReadOnlyList<RegionTrafficModel> GetRegions(string hostId) => Array.Empty<RegionTrafficModel>();
        public void SendSignal(string hostId, int pid, int signal) { }
        public void PauseLogs(bool pause) { }
        public void ClearLogs() { }
        public void Dispose() { }

        public void TriggerTelemetryUpdated(FleetNodeModel node) => NodeTelemetryUpdated?.Invoke(this, node);

        public Task<NodeConfig?> GetNodeConfigAsync(string hostId, CancellationToken ct = default)
        {
            Configs.TryGetValue(hostId, out var cfg);
            return Task.FromResult<NodeConfig?>(cfg);
        }

        public Task<bool> UpdateNodeConfigAsync(string hostId, NodeConfig config, CancellationToken ct = default)
        {
            Configs[hostId] = config;
            UpdatedConfigs.Add((hostId, config));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void HostProcessesTabViewModel_TrueTopN_UpdatesRanksAndPointLabels()
    {
        var mockProvider = new TestTelemetryProvider();
        var node = new FleetNodeModel
        {
            Id = "host-1",
            ProcessesAvailable = true
        };
        mockProvider.Nodes.Add(node);
        mockProvider.Processes = new List<ProcessInfoModel>
        {
            new() { Pid = 1, Name = "procA", Cpu = 50.0 },
            new() { Pid = 2, Name = "procB", Cpu = 30.0 },
            new() { Pid = 3, Name = "procC", Cpu = 10.0 }
        };

        var vm = new HostProcessesTabViewModel(mockProvider);
        vm.SetTargetHost("host-1");

        // Default state
        Assert.True(vm.IsProcessListTabSelected);
        Assert.False(vm.IsProcessOverviewTabSelected);
        Assert.Equal(5, vm.OverviewTopN);
        Assert.Equal(5, vm.OverviewSeries.Count);

        // Switch to overview tab
        vm.SelectProcessOverviewTabCommand.Execute(null);
        Assert.False(vm.IsProcessListTabSelected);
        Assert.True(vm.IsProcessOverviewTabSelected);

        // Verify Rank 1 and Rank 2 point labels and values
        var rank1 = vm.OverviewSeries[0];
        var rank2 = vm.OverviewSeries[1];
        // 2 samples recorded initially (seed point at T-1s + sample 1) so lines render immediately
        Assert.Equal(2, rank1.Values.Length);
        Assert.Equal(50.0, rank1.Values[0]);
        Assert.Equal(50.0, rank1.Values[1]);
        Assert.NotNull(rank1.PointLabels);
        Assert.Equal("procA (#1)", rank1.PointLabels[0]);
        Assert.Equal("procA (#1)", rank1.PointLabels[1]);

        Assert.Equal(2, rank2.Values.Length);
        Assert.Equal(30.0, rank2.Values[0]);
        Assert.Equal(30.0, rank2.Values[1]);
        Assert.NotNull(rank2.PointLabels);
        Assert.Equal("procB (#2)", rank2.PointLabels[0]);
        Assert.Equal("procB (#2)", rank2.PointLabels[1]);

        // Verify leader summary
        Assert.Equal("procA", vm.CurrentRankLeaders[0].ProcessName);
        Assert.Equal(50.0, vm.CurrentRankLeaders[0].Cpu);

        // Change processes where procB takes over rank 1
        mockProvider.Processes = new List<ProcessInfoModel>
        {
            new() { Pid = 2, Name = "procB", Cpu = 80.0 },
            new() { Pid = 1, Name = "procA", Cpu = 20.0 }
        };
        vm.Refresh();

        // 3 samples recorded (seed + sample 1 + sample 2)
        Assert.Equal(3, rank1.Values.Length);
        Assert.Equal(50.0, rank1.Values[0]);
        Assert.Equal(50.0, rank1.Values[1]);
        Assert.Equal(80.0, rank1.Values[2]);
        Assert.Equal("procA (#1)", rank1.PointLabels[0]);
        Assert.Equal("procA (#1)", rank1.PointLabels[1]);
        Assert.Equal("procB (#1)", rank1.PointLabels[2]);

        // Leader updated to procB
        Assert.Equal("procB", vm.CurrentRankLeaders[0].ProcessName);
        Assert.Equal(80.0, vm.CurrentRankLeaders[0].Cpu);

        // Configurable Top N
        vm.OverviewTopN = 3;
        Assert.Equal(3, vm.OverviewSeries.Count);

        // Clamped bounds
        vm.OverviewTopN = 25;
        Assert.Equal(10, vm.OverviewTopN);
        Assert.Equal(10, vm.OverviewSeries.Count);
    }

    [Fact]
    public void HostProcessesTabViewModel_ScopeSelection_UpdatesWindowAndFlags()
    {
        var mockProvider = new TestTelemetryProvider();
        mockProvider.Nodes.Add(new FleetNodeModel { Id = "host-1", ProcessesAvailable = true });
        mockProvider.Processes = new List<ProcessInfoModel>
        {
            new() { Pid = 1, Name = "proc1", Cpu = 45.0 }
        };

        var vm = new HostProcessesTabViewModel(mockProvider);
        vm.SetTargetHost("host-1");
        vm.SelectProcessOverviewTabCommand.Execute(null);

        // Default scope is 5m
        Assert.Equal("5m", vm.SelectedScope);
        Assert.True(vm.IsScope5m);
        Assert.False(vm.IsScope1m);

        // Switch to 1m
        vm.SetScope("1m");
        Assert.Equal("1m", vm.SelectedScope);
        Assert.True(vm.IsScope1m);
        Assert.False(vm.IsScope5m);

        // Verify window bounds: WindowEnd - WindowStart == spanNano
        long spanNano1m = 60L * 1_000_000_000L;
        Assert.Equal(spanNano1m, vm.OverviewWindowEnd - vm.OverviewWindowStart);

        // Switch to 30m
        vm.SetScope("30m");
        Assert.Equal("30m", vm.SelectedScope);
        Assert.True(vm.IsScope30m);
        long spanNano30m = 30L * 60L * 1_000_000_000L;
        Assert.Equal(spanNano30m, vm.OverviewWindowEnd - vm.OverviewWindowStart);

        // Verify window is not clamped to sample 0
        Assert.True(vm.OverviewWindowStart < vm.OverviewTimestamps[0]);
    }

    [Fact]
    public void ColorPicker_InstantiatesWithoutMissingMethodException()
    {
        var picker = new Avalonia.Controls.ColorPicker
        {
            IsAlphaEnabled = false,
            Color = Avalonia.Media.Color.FromRgb(255, 0, 0)
        };
        Assert.NotNull(picker);
        Assert.False(picker.IsAlphaEnabled);
        Assert.Equal((byte)255, picker.Color.R);
    }

    [Fact]
    public void HostDetailViewModel_FullCpuTooltip_FormatsModelAndThreads()
    {
        var provider = new TestTelemetryProvider();
        var lexicon = LexiconService.Instance;
        var metricsTab = new HostMetricsTabViewModel(provider);
        var processesTab = new HostProcessesTabViewModel(provider);
        var logsTab = new HostLogsTabViewModel(provider);
        var flightTab = new HostFlightTabViewModel(provider);

        var vm = new HostDetailViewModel(provider, lexicon, metricsTab, processesTab, logsTab, flightTab);
        vm.CpuSpec = "AMD EPYC 7763";
        vm.ThreadsSpec = "64 Logical Threads";

        Assert.Equal("AMD EPYC 7763 • 64 Logical Threads", vm.FullCpuTooltip);

        vm.ThreadsSpec = "";
        Assert.Equal("AMD EPYC 7763", vm.FullCpuTooltip);

        vm.CpuSpec = "";
        Assert.Equal("", vm.FullCpuTooltip);
    }

    [Fact]
    public void HeaderViewModel_NavigateBackCommand_FiresNavigateBackRequested()
    {
        var provider = new TestTelemetryProvider();
        var header = new HeaderViewModel(LexiconService.Instance, provider);

        bool backFired = false;
        header.NavigateBackRequested += () => backFired = true;

        header.IsDetailPage = true;
        header.CurrentPageTitle = "TestNode";
        header.CurrentPageSubtitle = "CPU Specs";

        header.NavigateBackCommand.Execute(null);

        Assert.True(backFired);
    }

    [Fact]
    public void MainViewModel_Navigation_SynchronizesHeaderNavigationState()
    {
        var provider = new TestTelemetryProvider();
        var node1 = new FleetNodeModel { Id = "host-alpha", Status = "online", Role = "agent", CpuModel = "Intel Core i9", Cores = 16 };
        var node2 = new FleetNodeModel { Id = "host-beta", Status = "online", Role = "gateway", CpuModel = "AMD Ryzen 9", Cores = 24 };
        provider.Nodes.Add(node1);
        provider.Nodes.Add(node2);

        var mainVm = new MainViewModel(provider, LexiconService.Instance, NotificationService.Instance);

        // Initially in FleetView
        Assert.False(mainVm.Header.IsDetailPage);
        Assert.Equal("", mainVm.Header.CurrentPageTitle);

        // Navigate to host detail
        mainVm.OpenHostDetail("host-alpha");
        Assert.True(mainVm.Header.IsDetailPage);
        Assert.Equal("host-alpha", mainVm.Header.CurrentPageTitle);
        Assert.Contains("Intel Core i9", mainVm.Header.CurrentPageSubtitle);

        // Switch to host-beta
        mainVm.HostDetailView.SelectHost("host-beta");
        Assert.Equal("host-beta", mainVm.Header.CurrentPageTitle);
        Assert.Contains("AMD Ryzen 9", mainVm.Header.CurrentPageSubtitle);

        // Press back from header
        mainVm.Header.NavigateBack();
        Assert.False(mainVm.Header.IsDetailPage);
        Assert.Equal("", mainVm.Header.CurrentPageTitle);
        Assert.Equal(mainVm.FleetView, mainVm.CurrentView);
    }

    [Fact]
    public void HostProcessesTabViewModel_ConsecutiveLiveUpdates_AccumulateAndSlideWindowWithoutFlickerOrWiping()
    {
        var provider = new TestTelemetryProvider();
        var node = new FleetNodeModel { Id = "host-alpha", ProcessesAvailable = true };
        provider.Nodes.Add(node);
        provider.Processes = new List<ProcessInfoModel>
        {
            new() { Pid = 100, Name = "daemon", Cpu = 25.0 }
        };

        var vm = new HostProcessesTabViewModel(provider);
        vm.SetTargetHost("host-alpha");
        vm.SelectProcessOverviewTab();

        // Initial state: seeded baseline + sample 1 = 2 points
        Assert.Equal(2, vm.OverviewSeries[0].Values.Length);
        long initialWindowEnd = vm.OverviewWindowEnd;

        // Simulate 5 consecutive ticks arriving from HostDetailViewModel (which calls SetTargetHost("host-alpha"))
        for (int tick = 1; tick <= 5; tick++)
        {
            provider.Processes = new List<ProcessInfoModel>
            {
                new() { Pid = 100, Name = "daemon", Cpu = 25.0 + tick }
            };

            // Mimic HostDetailViewModel invoking SetTargetHost on telemetry tick
            vm.SetTargetHost("host-alpha");

            // Verify sample count increases continuously and never wipes back to 1 or 0
            Assert.Equal(2 + tick, vm.OverviewSeries[0].Values.Length);
            Assert.True(vm.OverviewWindowEnd > initialWindowEnd);
            Assert.Equal(25.0 + tick, vm.OverviewSeries[0].LatestValue);
            Assert.Equal(25.0 + tick, vm.CurrentRankLeaders[0].Cpu);
        }
    }

    [Fact]
    public void TelemetryOptInResolver_ResolvesAndSetsModesCorrectly()
    {
        // 1. Backward compatibility resolution
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.ResolveMode(TelemetryOptInMode.OptInOff, legacyBool: true));
        Assert.Equal(TelemetryOptInMode.OptInOff, TelemetryOptInResolver.ResolveMode(TelemetryOptInMode.OptInOff, legacyBool: false));
        Assert.Equal(TelemetryOptInMode.OptInMonitorOnly, TelemetryOptInResolver.ResolveMode(TelemetryOptInMode.OptInMonitorOnly, legacyBool: false));
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.ResolveMode(TelemetryOptInMode.OptInMonitorAndStore, legacyBool: false));

        // 2. Querying modes from TelemetryNodeConfig with inheritance & overrides
        var config = new NodeConfig
        {
            CollectCpuOverall = true,
            CpuOverallMode = TelemetryOptInMode.OptInMonitorAndStore,
            CpuPerCoreMode = TelemetryOptInMode.OptInMonitorOnly,
            NetworkMode = TelemetryOptInMode.OptInMonitorAndStore,
            MemoryBasicMode = TelemetryOptInMode.OptInMonitorAndStore,
            MemorySwapMode = TelemetryOptInMode.OptInMonitorOnly,
            ZramMode = TelemetryOptInMode.OptInOff
        };
        // Per-core override: core 0 is Off, core 1 is not in dictionary (inherits CpuPerCoreMode)
        config.CoreModes["0"] = TelemetryOptInMode.OptInOff;
        // Per-NIC override: eth0 inherits NetworkMode, docker0 is explicitly Off
        config.NicModes["docker0"] = TelemetryOptInMode.OptInOff;
        // Per-swap override: /dev/nvme0n1p2 is Store, zram0 is not in dictionary (inherits MemorySwapMode)
        config.SwapDeviceModes["/dev/nvme0n1p2"] = TelemetryOptInMode.OptInMonitorAndStore;

        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.GetMetricOptInMode(config, "cpu.total"));
        Assert.Equal(TelemetryOptInMode.OptInOff, TelemetryOptInResolver.GetMetricOptInMode(config, "cpu.core.0"));
        Assert.Equal(TelemetryOptInMode.OptInMonitorOnly, TelemetryOptInResolver.GetMetricOptInMode(config, "cpu.core.1"));
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.GetMetricOptInMode(config, "nic.eth0.rx_bytes"));
        Assert.Equal(TelemetryOptInMode.OptInOff, TelemetryOptInResolver.GetMetricOptInMode(config, "nic.docker0.rx_bytes"));
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.GetMetricOptInMode(config, "swap./dev/nvme0n1p2.used_bytes"));
        Assert.Equal(TelemetryOptInMode.OptInMonitorOnly, TelemetryOptInResolver.GetMetricOptInMode(config, "swap.zram0.used_bytes"));
        Assert.Equal(TelemetryOptInMode.OptInOff, TelemetryOptInResolver.GetMetricOptInMode(config, "zram.zram0.disksize_bytes"));

        // 3. Setting modes via SetMetricOptInMode
        TelemetryOptInResolver.SetMetricOptInMode(config, "cpu.core.0", TelemetryOptInMode.OptInMonitorAndStore);
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, config.CoreModes["0"]);
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, TelemetryOptInResolver.GetMetricOptInMode(config, "cpu.core.0"));

        TelemetryOptInResolver.SetMetricOptInMode(config, "zram.zram0.disksize_bytes", TelemetryOptInMode.OptInMonitorOnly);
        Assert.Equal(TelemetryOptInMode.OptInMonitorOnly, config.ZramDeviceModes["zram0"]);
        Assert.Equal(TelemetryOptInMode.OptInMonitorOnly, config.ZramMode);

        TelemetryOptInResolver.SetMetricOptInMode(config, "power.battery_pct", TelemetryOptInMode.OptInMonitorAndStore);
        Assert.Equal(TelemetryOptInMode.OptInMonitorAndStore, config.PowerMode);
        Assert.True(config.CollectPowerBattery);
    }

    [Fact]
    public void PopulateAvailableMetrics_IncludesPerCoreAndSwapZramDevices()
    {
        var vm = new HostMetricsTabViewModel();
        var node = new FleetNodeModel
        {
            Id = "node-alpha",
            Cores = 4,
            SwapDevices = new[]
            {
                new MADTOM.Plugins.Telemetry.Proto.V1.SwapDevice { Name = "/dev/dm-0", TotalBytes = 8589934592, UsedBytes = 1073741824 }
            },
            ZramDevices = new[]
            {
                new MADTOM.Plugins.Telemetry.Proto.V1.ZramDevice { Name = "zram0", DisksizeBytes = 4294967296, MemUsedBytes = 536870912 }
            }
        };

        vm.UpdateForNode("node-alpha", node, new[] { node });

        // Check per-core metrics
        Assert.Contains("cpu.core.0", vm.AvailableMetrics);
        Assert.Contains("cpu.core.1", vm.AvailableMetrics);
        Assert.Contains("cpu.core.2", vm.AvailableMetrics);
        Assert.Contains("cpu.core.3", vm.AvailableMetrics);
        Assert.DoesNotContain("cpu.core.4", vm.AvailableMetrics);

        // Check swap and zram metrics
        Assert.Contains("memory.swap_used", vm.AvailableMetrics);
        Assert.Contains("memory.swap_total", vm.AvailableMetrics);
        Assert.DoesNotContain("memory.swap_free", vm.AvailableMetrics);
        Assert.DoesNotContain("memory.zram_ratio", vm.AvailableMetrics);

        Assert.Contains("swap./dev/dm-0.total_bytes", vm.AvailableMetrics);
        Assert.Contains("swap./dev/dm-0.used_bytes", vm.AvailableMetrics);

        Assert.Contains("zram.zram0.disksize_bytes", vm.AvailableMetrics);
        Assert.Contains("zram.zram0.mem_used_bytes", vm.AvailableMetrics);
        Assert.Contains("zram.zram0.orig_data_bytes", vm.AvailableMetrics);
        Assert.Contains("zram.zram0.compr_data_bytes", vm.AvailableMetrics);
    }

    [Fact]
    public async Task HostMetricsTabViewModel_AddGraph_TriggersOptInPromptWhenOff()
    {
        var provider = new TestTelemetryProvider();
        var node = new FleetNodeModel { Id = "host-gamma", Cores = 4 };
        provider.Nodes.Add(node);

        var config = new NodeConfig
        {
            CollectCpuOverall = true,
            CpuOverallMode = TelemetryOptInMode.OptInMonitorAndStore,
            CpuPerCoreMode = TelemetryOptInMode.OptInOff
        };
        provider.Configs["host-gamma"] = config;

        var vm = new HostMetricsTabViewModel(provider);
        vm.UpdateForNode("host-gamma", node, new[] { node });

        // Select a metric that is currently OFF
        vm.SelectedMetric = "cpu.core.0";
        await vm.AddGraphAsync();

        // Should trigger interactive opt-in prompt
        Assert.True(vm.IsOptInPromptOpen);
        Assert.Equal("cpu.core.0", vm.OptInPromptMetric);
        Assert.Equal("host-gamma", vm.OptInPromptHostId);

        // Confirm "store" mode
        await vm.OptInAndAddGraphAsync("store");

        // Prompt closes, node config is updated, graph is created
        Assert.False(vm.IsOptInPromptOpen);
        Assert.Contains(provider.UpdatedConfigs, u => u.HostId == "host-gamma" && u.Config.CoreModes["0"] == TelemetryOptInMode.OptInMonitorAndStore);
        Assert.Contains(vm.Graphs, g => g.Series.Any(s => s.Metric == "cpu.core.0"));
    }
}

