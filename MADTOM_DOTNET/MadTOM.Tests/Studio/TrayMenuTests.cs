using System;
using System.Collections.Generic;
using System.Linq;
using MADTOM.PluginContracts;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using Xunit;

namespace MadTOM.Tests;

public class TrayMenuTests
{
    private sealed class DummySection : ITrayMenuSection
    {
        public string SectionId { get; }
        public int OrderWeight { get; }
        private readonly List<TrayMenuItemDescriptor> _items = new();

        public event EventHandler? ItemsChanged;

        public DummySection(string id, int weight, IEnumerable<TrayMenuItemDescriptor>? items = null)
        {
            SectionId = id;
            OrderWeight = weight;
            if (items != null)
            {
                _items.AddRange(items);
            }
        }

        public IReadOnlyList<TrayMenuItemDescriptor> GetItems() => _items;

        public void TriggerChange() => ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void TrayMenuService_OrdersSectionsByWeight()
    {
        var service = new TrayMenuService();
        var s1 = new DummySection("sec1", 50);
        var s2 = new DummySection("sec2", 10);
        var s3 = new DummySection("sec3", 25);

        using var r1 = service.RegisterSection(s1);
        using var r2 = service.RegisterSection(s2);
        using var r3 = service.RegisterSection(s3);

        var sections = service.GetSections();
        Assert.Equal(3, sections.Count);
        Assert.Equal("sec2", sections[0].SectionId);
        Assert.Equal("sec3", sections[1].SectionId);
        Assert.Equal("sec1", sections[2].SectionId);
    }

    [Fact]
    public void TrayMenuService_UnregisterRemovesSection()
    {
        var service = new TrayMenuService();
        var s1 = new DummySection("sec1", 10);
        var s2 = new DummySection("sec2", 20);

        var reg1 = service.RegisterSection(s1);
        using var reg2 = service.RegisterSection(s2);

        Assert.Equal(2, service.GetSections().Count);

        reg1.Dispose();

        var remaining = service.GetSections();
        Assert.Single(remaining);
        Assert.Equal("sec2", remaining[0].SectionId);
    }

    [Fact]
    public void TrayMenuService_ItemChangeFiresSectionsChanged()
    {
        var service = new TrayMenuService();
        var s1 = new DummySection("sec1", 10);
        using var reg = service.RegisterSection(s1);

        bool eventFired = false;
        service.SectionsChanged += (_, _) => eventFired = true;

        s1.TriggerChange();
        Assert.True(eventFired);
    }

    [Fact]
    public void TelemetryTrayMenuSection_FormatsOverallStatistics()
    {
        var mockProvider = new MockTelemetryDataProvider();
        using var section = new TelemetryTrayMenuSection(mockProvider);

        Assert.Equal("MADTOM Telemetry", section.PluginName);

        var items = section.GetItems();
        Assert.True(items.Count >= 2);

        Assert.Contains("Online", items[0].Text);
        Assert.False(items[0].IsEnabled);

        Assert.Contains("CPU", items[1].Text);
        Assert.Contains("RAM", items[1].Text);
        Assert.False(items[1].IsEnabled);
    }

    [Fact]
    public void TelemetryTrayMenuSection_OnlyShowsTwampWhenAvailable()
    {
        var mockProvider = new MockTelemetryDataProvider();
        var nodes = mockProvider.GetFleetNodes().ToList();

        // 1. All TWAMP disabled
        foreach (var n in nodes)
        {
            n.Twamp.Available = false;
        }

        using (var sectionNoTwamp = new TelemetryTrayMenuSection(mockProvider))
        {
            var itemsNoTwamp = sectionNoTwamp.GetItems();
            Assert.DoesNotContain(itemsNoTwamp, i => i.Text.Contains("TWAMP"));
        }

        // 2. Enable TWAMP on one node
        nodes[0].Twamp.Available = true;
        nodes[0].Twamp.RttMs = 12.34;

        using (var sectionWithTwamp = new TelemetryTrayMenuSection(mockProvider))
        {
            var itemsWithTwamp = sectionWithTwamp.GetItems();
            var twampItem = itemsWithTwamp.FirstOrDefault(i => i.Text.Contains("TWAMP"));
            Assert.NotNull(twampItem);
            Assert.Contains("12.3", twampItem.Text);
        }
    }

    [Fact]
    public void SidebarViewModel_IsTwampAvailable_ReflectsNodeTwampStatus()
    {
        var mockProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
        var nodes = mockProvider.GetFleetNodes().ToList();
        foreach (var n in nodes)
        {
            n.Twamp.Available = false;
        }

        var vm = new SidebarViewModel(mockProvider);
        Assert.False(vm.IsTwampAvailable);
        Assert.Equal("Unavailable", vm.TwampStatus);

        // Update with an active TWAMP node
        var activeNode = new FleetNodeModel
        {
            Id = "test-node",
            Status = "healthy",
            Twamp = new TwampTelemetryModel { Available = true, RttMs = 8.5 }
        };

        mockProvider.RaiseNodeTelemetryUpdated(activeNode);

        Assert.True(vm.IsTwampAvailable);
        Assert.Equal("ONLINE", vm.TwampStatus);
    }
}
