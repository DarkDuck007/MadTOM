using System;
using System.Linq;
using MadTOM.Controls.Charts;
using MadTOM.Services;
using MadTOM.ViewModels;
using Xunit;

namespace MadTOM.Tests;

public class RadarAndChartGestureTests
{
    [Fact]
    public void CursorAnchoredZoom_LeftEdge_AnchorsAtZeroPan()
    {
        double oldZoom = 1.0;
        double oldPan = 0.0;
        double deltaY = 1.0; // zoom in
        double leftPad = 46.0;
        double plotW = 500.0;
        double cursorX = leftPad; // left edge

        var (newZoom, newPan) = TwampTimeSeriesChartControl.ComputeCursorAnchoredZoom(
            oldZoom, oldPan, deltaY, cursorX, leftPad, plotW);

        Assert.Equal(1.25, newZoom);
        Assert.Equal(0.0, newPan);
    }

    [Fact]
    public void CursorAnchoredZoom_RightEdge_AnchorsAtMaxPan()
    {
        double oldZoom = 1.0;
        double oldPan = 0.0;
        double deltaY = 1.0;
        double leftPad = 46.0;
        double plotW = 500.0;
        double cursorX = leftPad + plotW; // right edge

        var (newZoom, newPan) = TwampTimeSeriesChartControl.ComputeCursorAnchoredZoom(
            oldZoom, oldPan, deltaY, cursorX, leftPad, plotW);

        Assert.Equal(1.25, newZoom);
        double expectedMaxPan = (1.25 - 1.0) * plotW;
        Assert.Equal(expectedMaxPan, newPan, precision: 3);
    }

    [Fact]
    public void CursorAnchoredZoom_Center_PreservesCenterRatio()
    {
        double oldZoom = 2.0;
        double leftPad = 46.0;
        double plotW = 500.0;
        double oldMaxPan = (oldZoom - 1.0) * plotW; // 500.0
        double oldPan = oldMaxPan / 2.0; // 250.0
        double cursorX = leftPad + (plotW / 2.0); // Center

        double deltaY = 2.0; // zoom to 2.5
        var (newZoom, newPan) = TwampTimeSeriesChartControl.ComputeCursorAnchoredZoom(
            oldZoom, oldPan, deltaY, cursorX, leftPad, plotW);

        Assert.Equal(2.5, newZoom);
        // Data fraction before: (250 + 250) / (500 * 2.0) = 500 / 1000 = 0.5
        // After zoom: (0.5 * 500 * 2.5) - 250 = 625 - 250 = 375
        Assert.Equal(375.0, newPan, precision: 3);
    }

    [Fact]
    public void CursorAnchoredZoom_ClampsBetweenMinAndMaxZoom()
    {
        double leftPad = 46.0;
        double plotW = 500.0;

        // Zoom out beyond 1.0
        var (minZoom, minPan) = TwampTimeSeriesChartControl.ComputeCursorAnchoredZoom(
            1.0, 0.0, -10.0, leftPad + 100, leftPad, plotW, minZoom: 1.0, maxZoom: 8.0);
        Assert.Equal(1.0, minZoom);
        Assert.Equal(0.0, minPan);

        // Zoom in beyond 8.0
        var (maxZoom, _) = TwampTimeSeriesChartControl.ComputeCursorAnchoredZoom(
            7.5, 0.0, 10.0, leftPad + 100, leftPad, plotW, minZoom: 1.0, maxZoom: 8.0);
        Assert.Equal(8.0, maxZoom);
    }

    [Fact]
    public void GlobalRadarViewModel_ScopedNodeSelection_UpdatesMachineSpecificOrigins()
    {
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new GlobalRadarViewModel(provider);

        Assert.Equal(provider.GetFleetNodes().Count, vm.Topology.Count);
        Assert.Empty(vm.TopOrigins);
        var node = provider.GetFleetNodes()[0];
        node.TxBytesPerSecond = 125000;
        vm.ScopedNodeId = node.Id;
        Assert.Single(vm.Topology);
        Assert.Contains(node.Id, vm.Topology[0]);
        Assert.Equal("1.000 Mbps", vm.EgressRate);
        vm.ScopedNodeId = "missing-node";
        Assert.Empty(vm.Topology);
        Assert.Empty(vm.Interfaces);
    }

    [Fact]
    public void GlobalRadarViewModel_HoverCountry_MutesNonHoveredOrigins()
    {
        using var provider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var vm = new GlobalRadarViewModel(provider);

        vm.TopOrigins.Add(new OriginShareItem { CountryName = "Japan" });
        vm.TopOrigins.Add(new OriginShareItem { CountryName = "Germany" });
        // Hover over Japan
        vm.HoveredCountry = "Japan";

        var japan = vm.TopOrigins.First(o => o.CountryName == "Japan");
        Assert.True(japan.IsHovered);
        Assert.False(japan.IsMuted);

        var others = vm.TopOrigins.Where(o => o.CountryName != "Japan");
        foreach (var other in others)
        {
            Assert.False(other.IsHovered);
            Assert.True(other.IsMuted);
        }

        // Clear hover
        vm.HoveredCountry = string.Empty;
        foreach (var origin in vm.TopOrigins)
        {
            Assert.False(origin.IsHovered);
            Assert.False(origin.IsMuted);
        }
    }
}
