using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MadTOM.Controls.Charts;
using MadTOM.Localization;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using MADTOM.Plugins.Telemetry.Proto.V1;
using Xunit;

namespace MadTOM.Tests;

public class DeepDivePageTransitionLoadingTests
{
    private sealed class MockTelemetryProvider : ITelemetryDataProvider
    {
        public readonly List<FleetNodeModel> Nodes = new();
        public readonly Dictionary<string, List<LODPoint>> HistoryData = new();
        public int QueryCount { get; private set; }

        public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated { add { } remove { } }
        public event EventHandler<LogEntryModel>? LogReceived { add { } remove { } }

        public IReadOnlyList<FleetNodeModel> GetFleetNodes() => Nodes;
        public FleetNodeModel? GetNode(string hostId) => Nodes.FirstOrDefault(n => n.Id == hostId);
        public IReadOnlyList<ProcessInfoModel> GetProcesses(string hostId) => Array.Empty<ProcessInfoModel>();
        public void SendSignal(string hostId, int pid, int signal) { }
        public Task<NodeConfig?> GetNodeConfigAsync(string hostId, CancellationToken ct = default) => Task.FromResult<NodeConfig?>(new NodeConfig());
        public Task<bool> UpdateNodeConfigAsync(string hostId, NodeConfig config, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string hostId, string metric, DateTime start, DateTime end, CancellationToken ct = default)
        {
            QueryCount++;
            if (HistoryData.TryGetValue(metric, out var list))
                return Task.FromResult<IReadOnlyList<LODPoint>>(list);
            return Task.FromResult<IReadOnlyList<LODPoint>>(Array.Empty<LODPoint>());
        }
        public ClusterTelemetrySummary GetClusterSummary() => new();
        public IReadOnlyList<DropRuleModel> GetDropRules(string hostId) => Array.Empty<DropRuleModel>();
        public IReadOnlyList<RegionTrafficModel> GetRegions(string hostId) => Array.Empty<RegionTrafficModel>();
        public void PauseLogs(bool paused) { }
        public void ClearLogs() { }
        public void Dispose() { }
    }

    [Fact]
    public void MetricGraphViewModel_InitializesWithLoadingState()
    {
        var vm = new MetricGraphViewModel("cpu.total")
        {
            IsLoading = true,
            Status = "Loading…"
        };

        Assert.True(vm.IsLoading);
        Assert.Equal("Loading…", vm.Status);

        vm.IsLoading = false;
        vm.Status = "120 points · 10:00:00 – 10:05:00";
        Assert.False(vm.IsLoading);
        Assert.Equal("120 points · 10:00:00 – 10:05:00", vm.Status);
    }

    [Fact]
    public void MetricHistoryChartControl_HasIsLoadingProperty()
    {
        var chart = new MetricHistoryChartControl();
        Assert.False(chart.IsLoading);

        chart.IsLoading = true;
        Assert.True(chart.IsLoading);
    }

    [Fact]
    public void HostMetricsTabViewModel_LoadGraphsForNode_SetsLoadingStateWhenProviderAttached()
    {
        var provider = new MockTelemetryProvider();
        provider.Nodes.Add(new FleetNodeModel { Id = "alpha", Cores = 4, Status = "online" });
        var vm = new HostMetricsTabViewModel(provider);

        vm.LoadGraphsForNode("alpha");

        Assert.NotEmpty(vm.Graphs);
        foreach (var graph in vm.Graphs)
        {
            Assert.True(graph.IsLoading);
            Assert.Equal("Loading…", graph.Status);
        }
    }

    [Fact]
    public void HostMetricsTabViewModel_SetScope_ImmediatelySetsAllGraphsToLoadingState()
    {
        var provider = new MockTelemetryProvider();
        provider.Nodes.Add(new FleetNodeModel { Id = "alpha", Cores = 4, Status = "online" });
        var vm = new HostMetricsTabViewModel(provider);
        vm.LoadGraphsForNode("alpha");

        // Simulate graphs having completed an initial load
        foreach (var graph in vm.Graphs)
        {
            graph.IsLoading = false;
            graph.Status = "100 points";
        }

        // Change scope
        vm.SetScope("30m");

        // Immediately upon setting scope, graphs should be in Loading state
        foreach (var graph in vm.Graphs)
        {
            Assert.True(graph.IsLoading);
            Assert.Equal("Loading…", graph.Status);
        }
    }

    [Fact]
    public void PushLiveSampleToGraphs_DoesNotOverwriteLoadingStatusBeforeHistoryCompletes()
    {
        var provider = new MockTelemetryProvider();
        var node = new FleetNodeModel
        {
            Id = "alpha",
            Cores = 4,
            Status = "online",
            TimestampUnixNano = 1_700_000_000_000_000_000L
        };
        node.LatestMetricValues["cpu.total"] = 45.0;
        provider.Nodes.Add(node);

        var vm = new HostMetricsTabViewModel(provider);
        vm.LoadGraphsForNode("alpha");

        var cpuGraph = vm.Graphs.First(g => g.Series.Any(s => s.Metric == "cpu.total"));
        Assert.True(cpuGraph.IsLoading);
        Assert.Equal("Loading…", cpuGraph.Status);

        // UpdateForNode sets _hasLoadedHistory = false and kicks off async history refresh
        vm.UpdateForNode("alpha", node, new[] { node });

        // While history has not finished loading, graph must remain in loading state
        Assert.True(cpuGraph.IsLoading);
        Assert.Equal("Loading…", cpuGraph.Status);
    }

    [Fact]
    public void MainViewModel_OpenHostDetail_SwitchesCurrentViewImmediately()
    {
        var provider = new MockTelemetryProvider();
        var node = new FleetNodeModel { Id = "alpha", Cores = 4, Status = "online" };
        provider.Nodes.Add(node);

        var mainVm = new MainViewModel(provider, LexiconService.Instance, NotificationService.Instance);
        Assert.Same(mainVm.FleetView, mainVm.CurrentView);

        mainVm.OpenHostDetail("alpha");

        // View must immediately be HostDetailView
        Assert.Same(mainVm.HostDetailView, mainVm.CurrentView);
        Assert.Equal("detail", mainVm.Sidebar.ActiveView);
        Assert.Equal("alpha", mainVm.HostDetailView.SelectedHostId);
    }

    [Fact]
    public void HostProcessesTabViewModel_LazyHistoryQuery_DoesNotQueryWhenOverviewTabNotSelected()
    {
        var provider = new MockTelemetryProvider();
        var node = new FleetNodeModel { Id = "alpha", ProcessesAvailable = true };
        provider.Nodes.Add(node);

        var procVm = new HostProcessesTabViewModel(provider);
        Assert.True(procVm.IsProcessListTabSelected);
        Assert.False(procVm.IsProcessOverviewTabSelected);

        // Setting target host while on Process List tab (default) should not query history
        int initialQueries = provider.QueryCount;
        procVm.SetTargetHost("alpha");
        Assert.Equal(initialQueries, provider.QueryCount);

        // Switching to Overview tab triggers history query
        procVm.SelectProcessOverviewTab();
        Assert.True(procVm.IsProcessOverviewTabSelected);
    }
}
