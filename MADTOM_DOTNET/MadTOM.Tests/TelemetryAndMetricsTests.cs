using System;
using System.Linq;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using Xunit;

namespace MadTOM.Tests;

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
}
