using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MADTOM.PluginContracts;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.Services;

/// <summary>
/// Telemetry plugin tray section contributor.
/// Supplies overall fleet statistics and conditional TWAMP latency indicators to MADTOM Console.
/// </summary>
public sealed class TelemetryTrayMenuSection : ITrayMenuSection, IDisposable
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly Timer _refreshTimer;
    private int _isDisposed;

    private string _lastFingerprint = string.Empty;

    public string SectionId => "telemetry";
    public string PluginName => "MADTOM Telemetry";
    public int OrderWeight => 10;

    public event EventHandler? ItemsChanged;

    public TelemetryTrayMenuSection(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider ?? throw new ArgumentNullException(nameof(telemetryProvider));

        _telemetryProvider.NodeTelemetryUpdated += OnNodeTelemetryUpdated;

        // Throttle checks every 3 seconds
        _refreshTimer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void OnNodeTelemetryUpdated(object? sender, FleetNodeModel node)
    {
        // Handled via periodic timer to throttle rapid telemetry streams
    }

    private void OnTimerTick(object? state)
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            return;

        var items = GetItems();
        var fp = string.Join("|", items.Select(i => i.Text));
        if (fp == _lastFingerprint)
            return; // No change in numbers or formatting, avoid firing event

        _lastFingerprint = fp;
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<TrayMenuItemDescriptor> GetItems()
    {
        var nodes = _telemetryProvider.GetFleetNodes()?.ToList() ?? new List<FleetNodeModel>();
        int total = nodes.Count;
        int online = nodes.Count(n => n.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase) ||
                                      n.Status.Equals("online", StringComparison.OrdinalIgnoreCase));

        double avgCpu = nodes.Count > 0 ? nodes.Average(n => n.CpuAvgPct) : 0;
        double avgRamPct = nodes.Count > 0 ? nodes.Average(n => n.RamUsedPct) : 0;

        var twampNodes = nodes.Where(n => n.Twamp != null && n.Twamp.Available).ToList();
        bool hasTwamp = twampNodes.Count > 0;
        double avgTwamp = hasTwamp ? twampNodes.Average(n => n.Twamp.RttMs) : 0;

        var items = new List<TrayMenuItemDescriptor>
        {
            TrayMenuItemDescriptor.TextItem($"● {online}/{total} Online", enabled: false),
            TrayMenuItemDescriptor.TextItem($"⚡ CPU: {avgCpu:F1}%  |  RAM: {avgRamPct:F0}%", enabled: false)
        };

        if (hasTwamp)
        {
            items.Add(TrayMenuItemDescriptor.TextItem($"🌐 TWAMP: {avgTwamp:F1} ms", enabled: false));
        }

        return items;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _refreshTimer.Dispose();
            _telemetryProvider.NodeTelemetryUpdated -= OnNodeTelemetryUpdated;
        }
    }
}

