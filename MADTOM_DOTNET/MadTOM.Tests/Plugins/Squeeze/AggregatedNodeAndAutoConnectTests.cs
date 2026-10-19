using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQUEEZE.Models;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using Xunit;

namespace SQUEEZE.Tests;

public class TestDiscoveryService : IServerDiscoveryService
{
    private readonly List<DiscoveredServer> _servers = new();

    public event EventHandler<DiscoveredServer>? ServerDiscovered;
    public IReadOnlyList<DiscoveredServer> DiscoveredServers => _servers;

    public Task StartDiscoveryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopDiscoveryAsync() => Task.CompletedTask;
    public Task ScanNetworkAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void SimulateServerDiscovered(DiscoveredServer server)
    {
        _servers.Add(server);
        ServerDiscovered?.Invoke(this, server);
    }
}

public class ControllableMockBackendService : ITranscoderBackendService
{
    public string BaseUrl { get; private set; } = "http://127.0.0.1:8080";
    public bool IsConnected { get; private set; } = false;
    public HashSet<string> ReachableUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

#pragma warning disable CS0067
    public event EventHandler<TranscodeJob>? JobUpdated;
    public event EventHandler<TelemetryMetrics>? TelemetryUpdated;
    public event EventHandler<string>? StdoutLineReceived;
#pragma warning restore CS0067
    public event EventHandler<bool>? ConnectionStatusChanged;

    public Task<bool> ConnectAsync(string baseUrl, string? token = null)
    {
        if (ReachableUrls.Count > 0 && !ReachableUrls.Contains(baseUrl))
        {
            return Task.FromResult(false);
        }
        BaseUrl = baseUrl;
        IsConnected = true;
        ConnectionStatusChanged?.Invoke(this, true);
        return Task.FromResult(true);
    }

    public Task<bool> TestConnectionAsync(string baseUrl, string? token = null)
    {
        if (ReachableUrls.Count > 0 && !ReachableUrls.Contains(baseUrl))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public Task<ServerNodeInfo> GetNodeInfoAsync()
    {
        return Task.FromResult(new ServerNodeInfo
        {
            IsOnline = IsConnected,
            NodeName = "TEST_NODE",
            LanAddress = BaseUrl
        });
    }

    public Task<IReadOnlyList<TranscodePreset>> GetPresetsAsync() => Task.FromResult<IReadOnlyList<TranscodePreset>>(new List<TranscodePreset>());
    public Task<IReadOnlyList<TranscodeJob>> GetJobsAsync() => Task.FromResult<IReadOnlyList<TranscodeJob>>(new List<TranscodeJob>());
    public Task<TranscodeJob> AddJobAsync(string name, string meta, string presetKey, string? filePath = null, TranscodePreset? options = null) =>
        Task.FromResult(new TranscodeJob());
    public Task<bool> StartJobAsync(Guid jobId) => Task.FromResult(true);
    public Task<bool> PauseJobAsync(Guid jobId) => Task.FromResult(true);
    public Task<bool> ResumeJobAsync(Guid jobId) => Task.FromResult(true);
    public Task<bool> CancelJobAsync(Guid jobId) => Task.FromResult(true);
    public Task ClearCompletedJobsAsync() => Task.CompletedTask;
    public Task<string> GetDownloadUrlAsync(Guid jobId) => Task.FromResult(string.Empty);
    public Task<ServerNodeInfo> RescanHardwareAsync() => GetNodeInfoAsync();
}

public class AggregatedNodeAndAutoConnectTests
{
    [Fact]
    public void DiscoveredNodes_MultipleInterfacesForSameNodeId_AreAggregatedIntoOneItem()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "192.168.1.100",
            Port = 8080,
            HardwareGpu = "hevc_vaapi"
        });

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "10.0.0.50",
            Port = 8080,
            HardwareGpu = "hevc_vaapi"
        });

        Assert.Single(vm.DiscoveredNodes);
        var aggregated = vm.DiscoveredNodes[0];
        Assert.Equal("RIG_ALPHA_01", aggregated.NodeId);
        Assert.Equal(2, aggregated.Interfaces.Count);
        Assert.True(aggregated.HasMultipleInterfaces);
        Assert.Contains(aggregated.Interfaces, i => i.Host == "192.168.1.100");
        Assert.Contains(aggregated.Interfaces, i => i.Host == "10.0.0.50");
    }

    [Fact]
    public void DiscoveredNode_DefaultSelection_SelectsFirstDiscoveredInterface()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "192.168.1.100",
            Port = 8080
        });

        var node = vm.DiscoveredNodes[0];
        Assert.NotNull(node.SelectedInterface);
        Assert.Equal("192.168.1.100", node.SelectedInterface.Host);

        // Later discovery of second interface does not clobber default selection
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "10.0.0.50",
            Port = 8080
        });

        Assert.Equal(2, node.Interfaces.Count);
        Assert.Equal("192.168.1.100", node.SelectedInterface.Host);
    }

    [Fact]
    public void DiscoveredNode_OutOfOrderLatency_MatchesConfiguredPriorityInterfaceWhenArrivedLater()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        // Setup configured priority interface for RIG_ALPHA_01 as 10.0.0.50:8080
        vm.SetNodePriorityInterface("RIG_ALPHA_01", "http://10.0.0.50:8080");

        // Interface 1 (192.168.1.100) arrives first
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "192.168.1.100",
            Port = 8080
        });

        var node = vm.DiscoveredNodes[0];
        Assert.Equal("192.168.1.100", node.SelectedInterface?.Host);

        // Another device arrives in between
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_BETA_02",
            Host = "192.168.1.200",
            Port = 8080
        });

        // Interface 2 (10.0.0.50) arrives later due to latency
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "10.0.0.50",
            Port = 8080
        });

        // Node should have automatically resolved to its configured priority interface!
        Assert.Equal("10.0.0.50", node.SelectedInterface?.Host);
    }

    [Fact]
    public void DiscoveredNode_UserExplicitSelection_PreservedOnSubsequentDiscovery()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "192.168.1.100",
            Port = 8080
        });

        var node = vm.DiscoveredNodes[0];
        var manualIface = new DiscoveredNodeInterface { Host = "192.168.1.100", Port = 8080 };
        node.SetUserSelectedInterface(manualIface);

        // Third interface arrives
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "172.16.0.5",
            Port = 8080
        });

        Assert.Equal("192.168.1.100", node.SelectedInterface?.Host);
        Assert.True(node.IsUserSelected);
    }

    [Fact]
    public void UserSelectsInterface_UpdatesPriorityListDefaultInterface()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "192.168.1.100",
            Port = 8080
        });
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_ALPHA_01",
            Host = "10.0.0.50",
            Port = 8080
        });

        var node = vm.DiscoveredNodes[0];
        var targetIface = node.Interfaces.First(i => i.Host == "10.0.0.50");

        // User chooses which interface to use
        node.SetUserSelectedInterface(targetIface);

        // Priority list should contain RIG_ALPHA_01 with http://10.0.0.50:8080 as priority interface
        var priorityItem = vm.AutoConnectPriorityList.FirstOrDefault(p => p.NodeId == "RIG_ALPHA_01");
        Assert.NotNull(priorityItem);
        Assert.Equal("http://10.0.0.50:8080", priorityItem.PriorityInterface);
    }

    [Fact]
    public void PriorityList_ReorderingAndRemoval_UpdatesRanks()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        vm.AutoConnectPriorityList.Clear();
        vm.SetNodePriorityInterface("NODE_A", "http://10.0.0.1:8080");
        vm.SetNodePriorityInterface("NODE_B", "http://10.0.0.2:8080");
        vm.SetNodePriorityInterface("NODE_C", "http://10.0.0.3:8080");

        Assert.Equal(3, vm.AutoConnectPriorityList.Count);
        Assert.Equal("NODE_A", vm.AutoConnectPriorityList[0].NodeId);
        Assert.Equal(1, vm.AutoConnectPriorityList[0].PriorityRank);
        Assert.Equal("NODE_B", vm.AutoConnectPriorityList[1].NodeId);
        Assert.Equal(2, vm.AutoConnectPriorityList[1].PriorityRank);
        Assert.Equal("NODE_C", vm.AutoConnectPriorityList[2].NodeId);
        Assert.Equal(3, vm.AutoConnectPriorityList[2].PriorityRank);

        // Move NODE_B up
        vm.MovePriorityUp(vm.AutoConnectPriorityList[1]);
        Assert.Equal("NODE_B", vm.AutoConnectPriorityList[0].NodeId);
        Assert.Equal(1, vm.AutoConnectPriorityList[0].PriorityRank);
        Assert.Equal("NODE_A", vm.AutoConnectPriorityList[1].NodeId);
        Assert.Equal(2, vm.AutoConnectPriorityList[1].PriorityRank);

        // Move NODE_B down
        vm.MovePriorityDown(vm.AutoConnectPriorityList[0]);
        Assert.Equal("NODE_A", vm.AutoConnectPriorityList[0].NodeId);
        Assert.Equal("NODE_B", vm.AutoConnectPriorityList[1].NodeId);

        // Remove NODE_B
        vm.RemovePriorityItem(vm.AutoConnectPriorityList[1]);
        Assert.Equal(2, vm.AutoConnectPriorityList.Count);
        Assert.Equal("NODE_A", vm.AutoConnectPriorityList[0].NodeId);
        Assert.Equal(1, vm.AutoConnectPriorityList[0].PriorityRank);
        Assert.Equal("NODE_C", vm.AutoConnectPriorityList[1].NodeId);
        Assert.Equal(2, vm.AutoConnectPriorityList[1].PriorityRank);
    }

    [Fact]
    public async Task AutoConnect_ConnectsToAnyAvailablePreviouslyConnectedDevice_InPriorityOrder()
    {
        var backend = new ControllableMockBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        // Priority 1: RIG_HOME (offline on current network)
        // Priority 2: RIG_OFFICE (online on current network)
        vm.AutoConnectPriorityList.Clear();
        vm.SetNodePriorityInterface("RIG_HOME", "http://192.168.1.50:8080");
        vm.SetNodePriorityInterface("RIG_OFFICE", "http://10.200.1.20:8080");

        // RIG_OFFICE is reachable
        backend.ReachableUrls.Add("http://10.200.1.20:8080");

        // Simulate discovery of RIG_OFFICE
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_OFFICE",
            Host = "10.200.1.20",
            Port = 8080
        });

        // Trigger auto connect
        bool connected = await vm.TryAutoConnectAsync();

        Assert.True(connected);
        Assert.Equal("http://10.200.1.20:8080", backend.BaseUrl);
        Assert.True(backend.IsConnected);
    }

    [Fact]
    public async Task AutoConnect_UsesFallbackInterface_WhenPriorityInterfaceNotDiscovered()
    {
        var backend = new ControllableMockBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        // Configured priority interface was 192.168.1.50:8080 (e.g. Ethernet)
        vm.AutoConnectPriorityList.Clear();
        vm.SetNodePriorityInterface("RIG_DUAL", "http://192.168.1.50:8080");

        // Only WiFi interface is reachable and discovered
        backend.ReachableUrls.Add("http://10.0.0.77:8080");

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_DUAL",
            Host = "10.0.0.77",
            Port = 8080
        });

        bool connected = await vm.TryAutoConnectAsync();

        Assert.True(connected);
        Assert.Equal("http://10.0.0.77:8080", backend.BaseUrl);
        Assert.True(backend.IsConnected);
    }

    [Fact]
    public async Task SelectAndConnectNode_ConnectsToChosenInterface_AndUpdatesSettings()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new TestDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_STUDIO",
            Host = "192.168.1.10",
            Port = 8080
        });
        discovery.SimulateServerDiscovered(new DiscoveredServer
        {
            NodeId = "RIG_STUDIO",
            Host = "10.0.0.99",
            Port = 8080
        });

        var node = vm.DiscoveredNodes[0];
        // Select the second interface
        node.SetUserSelectedInterface(node.Interfaces.First(i => i.Host == "10.0.0.99"));

        await vm.SelectAndConnectNode(node);

        Assert.Equal("http://10.0.0.99:8080", backend.BaseUrl);
        Assert.True(backend.IsConnected);

        var pref = vm.AutoConnectPriorityList.FirstOrDefault(p => p.NodeId == "RIG_STUDIO");
        Assert.NotNull(pref);
        Assert.Equal("http://10.0.0.99:8080", pref.PriorityInterface);
    }
}
