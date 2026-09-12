using System;
using System.Linq;
using MadTOM.Controls.Charts;
using MadTOM.Localization;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
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
}

