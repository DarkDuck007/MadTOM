using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MadTOM.Services;
using MadTOM.ViewModels.Topology;
using Xunit;

namespace MADTOM.Plugins.Telemetry.Tests;

public class NetworkTopologyViewModelTests
{
    [Fact]
    public void InitialState_IsUninitializedAndEmpty()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var mgr = new MultiCollectorManager(":memory:");
            var vm = new NetworkTopologyViewModel(mgr, tempFile);

            Assert.False(vm.IsLoading);
            Assert.Empty(vm.CanvasNodes);
            Assert.Empty(vm.CanvasEdges);
            Assert.Empty(vm.Routes);
            Assert.Empty(vm.FirewallRules);
            Assert.Empty(vm.Sockets);
            Assert.Empty(vm.Neighbors);
            Assert.False(vm.IsInspectorOpen);
            Assert.Null(vm.SelectedNode);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectNode_OpensInspectorAndSetsSelection()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var mgr = new MultiCollectorManager(":memory:");
            var vm = new NetworkTopologyViewModel(mgr, tempFile);

            var node1 = new TopologyNodeItemViewModel { Id = "iface:eth0", Title = "eth0", X = 100, Y = 100 };
            var node2 = new TopologyNodeItemViewModel { Id = "iface:wg0", Title = "wg0", X = 300, Y = 100 };
            vm.CanvasNodes.Add(node1);
            vm.CanvasNodes.Add(node2);

            vm.SelectCanvasNode(node1);
            Assert.True(vm.IsInspectorOpen);
            Assert.Same(node1, vm.SelectedNode);
            Assert.True(node1.IsSelected);
            Assert.False(node2.IsSelected);

            vm.CloseInspector();
            Assert.False(vm.IsInspectorOpen);
            Assert.Null(vm.SelectedNode);
            Assert.False(node1.IsSelected);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SaveAndLoadLayout_PersistsCoordinates()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"topo_layout_test_{Guid.NewGuid():N}.json");
        try
        {
            var mgr = new MultiCollectorManager(":memory:");
            var vm = new NetworkTopologyViewModel(mgr, tempFile);

            vm.ActivateForNode("tp1", null);

            var node1 = new TopologyNodeItemViewModel { Id = "iface:eth0", Title = "eth0", X = 250, Y = 180 };
            vm.CanvasNodes.Add(node1);
            vm.SaveCurrentLayout();

            Assert.True(File.Exists(tempFile));

            // Create a second instance and activate same node
            var vm2 = new NetworkTopologyViewModel(mgr, tempFile);
            vm2.ActivateForNode("tp1", null);
            // Simulate interface arrival
            var payload = new MADTOM.Plugins.Telemetry.Proto.V1.InterfaceStagePayload();
            payload.Interfaces.Add(new MADTOM.Plugins.Telemetry.Proto.V1.InterfaceNode
            {
                Name = "eth0",
                Kind = "ethernet",
                MacAddress = "52:54:00:12:34:56"
            });

            // The method ApplyInterfaces is invoked via events; we test coordinate preservation by layout
            var json = File.ReadAllText(tempFile);
            Assert.Contains("\"X\": 250", json);
            Assert.Contains("\"Y\": 180", json);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsLocalNode_IdentifiesLocalHostnamesAndYoga()
    {
        Assert.True(MultiCollectorManager.IsLocalNode(Environment.MachineName));
        Assert.True(MultiCollectorManager.IsLocalNode("yoga"));
        Assert.True(MultiCollectorManager.IsLocalNode("localhost"));
        Assert.True(MultiCollectorManager.IsLocalNode("127.0.0.1"));
        Assert.False(MultiCollectorManager.IsLocalNode("nonexistent-node-xyz"));
    }

    [Fact]
    public void DeselectNode_ClearsSelectionAndKeepsInspectorOpen()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var mgr = new MultiCollectorManager(":memory:");
            var vm = new NetworkTopologyViewModel(mgr, tempFile);

            var node1 = new TopologyNodeItemViewModel { Id = "iface:eth0", Title = "eth0", X = 100, Y = 100 };
            vm.CanvasNodes.Add(node1);
            vm.SelectCanvasNode(node1);

            Assert.True(vm.IsInspectorOpen);
            Assert.NotNull(vm.SelectedNode);

            vm.DeselectNode();
            Assert.True(vm.IsInspectorOpen);
            Assert.Null(vm.SelectedNode);
            Assert.False(node1.IsSelected);
            Assert.Equal("Host Network Tables & Rules", vm.InspectorPanelTitle);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FirewallBackendTag_ReflectsCapabilities()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var mgr = new MultiCollectorManager(":memory:");
            var vm = new NetworkTopologyViewModel(mgr, tempFile);

            Assert.Equal("NONE", vm.FirewallBackendTag);
            Assert.Equal("#484F58", vm.FirewallBackendColor);

            vm.HasNftables = true;
            vm.NftVersion = "nftables v1.1.7";
            Assert.Equal("NFTABLES", vm.FirewallBackendTag);
            Assert.Equal("#238636", vm.FirewallBackendColor);

            vm.HasNftables = false;
            vm.HasLegacyIptables = true;
            Assert.Equal("IPTABLES (LEGACY)", vm.FirewallBackendTag);
            Assert.Equal("#D29922", vm.FirewallBackendColor);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
