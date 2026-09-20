using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Common;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels;

public partial class MetricOptInItemViewModel : ObservableObject
{
    private readonly Action? _onChanged;
    public string Key { get; }
    public string DisplayName { get; }
    public string Subtitle { get; }
    public string GroupName { get; }

    [ObservableProperty]
    private TelemetryOptInMode _mode;

    public bool IsOff
    {
        get => Mode == TelemetryOptInMode.OptInOff;
        set
        {
            if (value && Mode != TelemetryOptInMode.OptInOff)
            {
                Mode = TelemetryOptInMode.OptInOff;
                OnPropertyChanged(nameof(IsMonitorOnly));
                OnPropertyChanged(nameof(IsMonitorAndStore));
                _onChanged?.Invoke();
            }
        }
    }

    public bool IsMonitorOnly
    {
        get => Mode == TelemetryOptInMode.OptInMonitorOnly;
        set
        {
            if (value && Mode != TelemetryOptInMode.OptInMonitorOnly)
            {
                Mode = TelemetryOptInMode.OptInMonitorOnly;
                OnPropertyChanged(nameof(IsOff));
                OnPropertyChanged(nameof(IsMonitorAndStore));
                _onChanged?.Invoke();
            }
        }
    }

    public bool IsMonitorAndStore
    {
        get => Mode == TelemetryOptInMode.OptInMonitorAndStore;
        set
        {
            if (value && Mode != TelemetryOptInMode.OptInMonitorAndStore)
            {
                Mode = TelemetryOptInMode.OptInMonitorAndStore;
                OnPropertyChanged(nameof(IsOff));
                OnPropertyChanged(nameof(IsMonitorOnly));
                _onChanged?.Invoke();
            }
        }
    }

    public MetricOptInItemViewModel(string key, string displayName, string subtitle, TelemetryOptInMode mode, Action? onChanged = null)
    {
        Key = key;
        DisplayName = displayName;
        Subtitle = subtitle;
        GroupName = "OptIn_" + Guid.NewGuid().ToString("N");
        _mode = mode;
        _onChanged = onChanged;
    }

    public void UpdateModeSilently(TelemetryOptInMode mode)
    {
        Mode = mode;
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(IsMonitorOnly));
        OnPropertyChanged(nameof(IsMonitorAndStore));
    }
}

public partial class MetricOptInGroupViewModel : ObservableObject
{
    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<MetricOptInItemViewModel> Items { get; } = new();
    public ObservableCollection<MetricOptInItemViewModel> SubItems { get; } = new();
    public bool HasSubItems => SubItems.Count > 0;
    public string SubItemsHeader { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public MetricOptInGroupViewModel(string title, string description, string subItemsHeader = "Granular Items")
    {
        Title = title;
        Description = description;
        SubItemsHeader = subItemsHeader;
    }

    [RelayCommand]
    public void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public partial class NodeSettingsViewModel : ViewModelBase
{
    private readonly CollectorClientService? _client;
    private readonly FleetNodeModel _node;
    private NodeConfig? _loadedConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private string _twampTarget = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _twampClocksSynchronized;

    [ObservableProperty]
    private bool _isLoadingConfig;

    public string NodeId => _node.Id;
    public string CollectorName => _node.CollectorName;

    public ObservableCollection<MetricOptInGroupViewModel> MetricGroups { get; } = new();

    // --- Legacy / Global Switches (Kept for backward compatibility) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectCpuOverall = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectCpuPerCore = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectMemoryBasic = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectMemorySwapZram = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectPowerBattery = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectNetworkInterfaces = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _collectNetworkConnections = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    [NotifyPropertyChangedFor(nameof(IsProcessDisabled))]
    [NotifyPropertyChangedFor(nameof(IsProcessLiveOnly))]
    [NotifyPropertyChangedFor(nameof(IsProcessStored))]
    private ProcessTelemetryMode _processMode = ProcessTelemetryMode.ProcessModeLiveOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private int _topNProcesses = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private int _processSnapshotLimit = 1000;

    public bool IsProcessDisabled
    {
        get => ProcessMode == ProcessTelemetryMode.ProcessModeDisabled;
        set { if (value) ProcessMode = ProcessTelemetryMode.ProcessModeDisabled; }
    }

    public bool IsProcessLiveOnly
    {
        get => ProcessMode == ProcessTelemetryMode.ProcessModeLiveOnly;
        set { if (value) ProcessMode = ProcessTelemetryMode.ProcessModeLiveOnly; }
    }

    public bool IsProcessStored
    {
        get => ProcessMode == ProcessTelemetryMode.ProcessModeProbedAndStored;
        set { if (value) ProcessMode = ProcessTelemetryMode.ProcessModeProbedAndStored; }
    }

    // --- Tier 2: Viewing Opt-in (Client UI Level) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _isViewingEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _viewCpuMatrix = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _viewSwapZram = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _viewPowerBattery = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnappliedChanges))]
    private bool _viewNetworkCounters = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public record struct ConfigSnapshot(
        bool CollectCpuOverall,
        bool CollectCpuPerCore,
        bool CollectMemoryBasic,
        bool CollectMemorySwapZram,
        bool CollectPowerBattery,
        bool CollectNetworkInterfaces,
        bool CollectNetworkConnections,
        string TwampTarget,
        bool TwampClocksSynchronized,
        ProcessTelemetryMode ProcessMode,
        int TopNProcesses,
        int ProcessSnapshotLimit,
        bool IsViewingEnabled,
        bool ViewCpuMatrix,
        bool ViewSwapZram,
        bool ViewPowerBattery,
        bool ViewNetworkCounters,
        string MetricModesHash
    );

    private ConfigSnapshot? _baseline;

    public bool HasUnappliedChanges
    {
        get
        {
            if (_baseline == null) return false;
            return CurrentSnapshot != _baseline.Value;
        }
    }

    public MetricOptInItemViewModel? FindMetricItem(string key)
    {
        foreach (var g in MetricGroups)
        {
            foreach (var item in g.Items)
                if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return item;
            foreach (var sub in g.SubItems)
                if (sub.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return sub;
        }
        return null;
    }

    partial void OnCollectCpuOverallChanged(bool value)
    {
        FindMetricItem("cpu.total")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    partial void OnCollectCpuPerCoreChanged(bool value)
    {
        FindMetricItem("cpu.per_core")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    partial void OnCollectMemoryBasicChanged(bool value)
    {
        FindMetricItem("memory.total")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    partial void OnCollectMemorySwapZramChanged(bool value)
    {
        FindMetricItem("memory.swap_used")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    partial void OnCollectPowerBatteryChanged(bool value)
    {
        FindMetricItem("power.default")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    partial void OnCollectNetworkInterfacesChanged(bool value)
    {
        FindMetricItem("network.default")?.UpdateModeSilently(value ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff);
    }

    private string ComputeModesHash()
    {
        var parts = new List<string>();
        foreach (var g in MetricGroups)
        {
            foreach (var item in g.Items)
                parts.Add($"{item.Key}:{(int)item.Mode}");
            foreach (var sub in g.SubItems)
                parts.Add($"{sub.Key}:{(int)sub.Mode}");
        }
        return string.Join(";", parts);
    }

    private ConfigSnapshot CurrentSnapshot => new(
        CollectCpuOverall,
        CollectCpuPerCore,
        CollectMemoryBasic,
        CollectMemorySwapZram,
        CollectPowerBattery,
        CollectNetworkInterfaces,
        CollectNetworkConnections,
        TwampTarget,
        TwampClocksSynchronized,
        ProcessMode,
        TopNProcesses,
        ProcessSnapshotLimit,
        IsViewingEnabled,
        ViewCpuMatrix,
        ViewSwapZram,
        ViewPowerBattery,
        ViewNetworkCounters,
        ComputeModesHash()
    );

    public void CaptureBaseline()
    {
        _baseline = CurrentSnapshot;
        OnPropertyChanged(nameof(HasUnappliedChanges));
    }

    public void RevertChanges()
    {
        if (_baseline != null)
        {
            var b = _baseline.Value;
            CollectCpuOverall = b.CollectCpuOverall;
            CollectCpuPerCore = b.CollectCpuPerCore;
            CollectMemoryBasic = b.CollectMemoryBasic;
            CollectMemorySwapZram = b.CollectMemorySwapZram;
            CollectPowerBattery = b.CollectPowerBattery;
            CollectNetworkInterfaces = b.CollectNetworkInterfaces;
            CollectNetworkConnections = b.CollectNetworkConnections;
            TwampTarget = b.TwampTarget;
            TwampClocksSynchronized = b.TwampClocksSynchronized;
            ProcessMode = b.ProcessMode;
            TopNProcesses = b.TopNProcesses;
            ProcessSnapshotLimit = b.ProcessSnapshotLimit;
            IsViewingEnabled = b.IsViewingEnabled;
            ViewCpuMatrix = b.ViewCpuMatrix;
            ViewSwapZram = b.ViewSwapZram;
            ViewPowerBattery = b.ViewPowerBattery;
            ViewNetworkCounters = b.ViewNetworkCounters;

            // Revert metric item modes from hash
            var modeDict = b.MetricModesHash.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':'))
                .Where(p => p.Length == 2 && int.TryParse(p[1], out _))
                .ToDictionary(p => p[0], p => (TelemetryOptInMode)int.Parse(p[1]), StringComparer.OrdinalIgnoreCase);

            foreach (var g in MetricGroups)
            {
                foreach (var item in g.Items)
                {
                    if (modeDict.TryGetValue(item.Key, out var m))
                        item.UpdateModeSilently(m);
                }
                foreach (var sub in g.SubItems)
                {
                    if (modeDict.TryGetValue(sub.Key, out var m))
                        sub.UpdateModeSilently(m);
                }
            }

            OnPropertyChanged(nameof(HasUnappliedChanges));
        }
    }

    public NodeSettingsViewModel(FleetNodeModel node, CollectorClientService? client)
    {
        _node = node;
        _client = client;
        _isViewingEnabled = node.IsViewingOptedIn;
        ViewCpuMatrix = node.ViewCpuMatrix;
        ViewSwapZram = node.ViewSwapZram;
        ViewPowerBattery = node.ViewPowerBattery;
        ViewNetworkCounters = node.ViewNetworkCounters;

        BuildMetricGroups();
        CaptureBaseline();
    }

    private void OnOptInItemChanged()
    {
        OnPropertyChanged(nameof(HasUnappliedChanges));
    }

    private void BuildMetricGroups()
    {
        MetricGroups.Clear();

        // 1. CPU
        var cpuGroup = new MetricOptInGroupViewModel("CPU Compute & Cores", "Configure overall utilization and individual core opt-in policy.", "Granular CPU Cores");
        var cpuOverallMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "cpu.total");
        var cpuPerCoreMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "cpu.core.0");

        cpuGroup.Items.Add(new MetricOptInItemViewModel("cpu.total", "Overall CPU Usage", "Total, User, System, IOWait load metrics", cpuOverallMode, OnOptInItemChanged));
        cpuGroup.Items.Add(new MetricOptInItemViewModel("cpu.per_core", "Per-Core Matrix Default", "Default opt-in policy applied to all CPU cores", cpuPerCoreMode, OnOptInItemChanged));

        int coreCount = Math.Max(_node.Cores, _loadedConfig?.CoreModes.Count ?? 0);
        if (coreCount <= 0 && _node.SparkCpu != null && _node.SparkCpu.Length > 0) coreCount = 4;
        for (int i = 0; i < coreCount; i++)
        {
            string key = $"cpu.core.{i}";
            var mode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, key);
            cpuGroup.SubItems.Add(new MetricOptInItemViewModel(key, $"Core {i}", $"Hardware Core #{i} metric stream", mode, OnOptInItemChanged));
        }
        MetricGroups.Add(cpuGroup);

        // 2. Memory, Swap & ZRAM
        var memGroup = new MetricOptInGroupViewModel("Memory, Swap & ZRAM", "Configure basic memory, swap files/partitions and compressed ZRAM devices.", "Partitions & Devices");
        var memBasicMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "memory.total");
        var swapMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "memory.swap_used");
        var zramMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "zram.zram0");

        memGroup.Items.Add(new MetricOptInItemViewModel("memory.total", "Basic RAM Memory", "MemTotal, MemAvailable, MemUsed metrics", memBasicMode, OnOptInItemChanged));
        memGroup.Items.Add(new MetricOptInItemViewModel("memory.swap_used", "Swap Partitions Default", "Default opt-in policy for swap devices", swapMode, OnOptInItemChanged));
        memGroup.Items.Add(new MetricOptInItemViewModel("zram.mode", "ZRAM Devices Default", "Default opt-in policy for compressed RAM disks", zramMode, OnOptInItemChanged));

        var swapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _node.SwapDevices)
        {
            if (!string.IsNullOrEmpty(s.Name)) swapNames.Add(s.Name);
        }
        if (_loadedConfig != null)
        {
            foreach (var k in _loadedConfig.SwapDeviceModes.Keys)
                swapNames.Add(k);
        }
        foreach (var name in swapNames)
        {
            string key = $"swap.{name}";
            var mode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, key);
            memGroup.SubItems.Add(new MetricOptInItemViewModel(key, $"Swap Partition: {name}", $"Device partition or swapfile", mode, OnOptInItemChanged));
        }

        var zramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in _node.ZramDevices)
        {
            if (!string.IsNullOrEmpty(z.Name)) zramNames.Add(z.Name);
        }
        if (_loadedConfig != null)
        {
            foreach (var k in _loadedConfig.ZramDeviceModes.Keys)
                zramNames.Add(k);
        }
        if (zramNames.Count == 0 && (_node.ZramRatio > 0 || _node.SwapDevices.Any(s => s.Name.StartsWith("zram"))))
        {
            zramNames.Add("zram0");
        }
        foreach (var name in zramNames)
        {
            string key = $"zram.{name}";
            var mode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, key);
            memGroup.SubItems.Add(new MetricOptInItemViewModel(key, $"ZRAM Device: {name}", $"Compressed memory swap disk", mode, OnOptInItemChanged));
        }
        MetricGroups.Add(memGroup);

        // 3. Network Interfaces
        var netGroup = new MetricOptInGroupViewModel("Network Interfaces", "Configure default and per-NIC metrics (e.g. eth0 vs virtual docker0 interfaces).", "Network Interfaces (NICs)");
        var netMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "nic.default");
        netGroup.Items.Add(new MetricOptInItemViewModel("network.default", "Network Interfaces Default", "Default opt-in policy for all network interfaces", netMode, OnOptInItemChanged));

        var nicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in _node.Interfaces)
        {
            if (!string.IsNullOrEmpty(nic.Name)) nicNames.Add(nic.Name);
        }
        if (_loadedConfig != null)
        {
            foreach (var k in _loadedConfig.NicModes.Keys)
                nicNames.Add(k);
        }
        foreach (var name in nicNames)
        {
            string key = $"nic.{name}";
            var nic = _node.Interfaces.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string subtitle = nic != null 
                ? $"{(nic.LinkSpeedMbps > 0 ? $"{nic.LinkSpeedMbps} Mbps, " : "")}Carrier: {(nic.CarrierUp ? "Up" : "Down")}" 
                : "Interface";
            var mode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, key);
            netGroup.SubItems.Add(new MetricOptInItemViewModel(key, $"NIC: {name}", subtitle, mode, OnOptInItemChanged));
        }
        MetricGroups.Add(netGroup);

        // 4. Disks & Storage
        var diskGroup = new MetricOptInGroupViewModel("Disks & Block Devices", "Configure storage throughput, I/O rates and individual disk drives.", "Individual Block Devices");
        var diskMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "disk.io.default");
        diskGroup.Items.Add(new MetricOptInItemViewModel("disk.io.default", "Disk I/O Default", "Default opt-in policy for block devices", diskMode, OnOptInItemChanged));

        var diskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _node.Disks)
        {
            if (!string.IsNullOrEmpty(d.Name)) diskNames.Add(d.Name);
        }
        if (_loadedConfig != null)
        {
            foreach (var k in _loadedConfig.DiskDeviceModes.Keys)
                diskNames.Add(k);
        }
        foreach (var name in diskNames)
        {
            string key = $"disk.io.{name}.read_bytes";
            var mode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, key);
            diskGroup.SubItems.Add(new MetricOptInItemViewModel(key, $"Disk: {name}", "Storage block device", mode, OnOptInItemChanged));
        }
        MetricGroups.Add(diskGroup);

        // 5. Power & Battery
        var pwrGroup = new MetricOptInGroupViewModel("Power & Battery", "Configure line AC power state and battery monitoring.", "Power Metrics");
        var pwrMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "power.battery_pct");
        pwrGroup.Items.Add(new MetricOptInItemViewModel("power.default", "Power & Battery Default", "Line power and battery state monitoring", pwrMode, OnOptInItemChanged));
        pwrGroup.SubItems.Add(new MetricOptInItemViewModel("power.battery_pct", "Battery Percentage", "Remaining charge percentage (0-100%)", TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "power.battery_pct"), OnOptInItemChanged));
        pwrGroup.SubItems.Add(new MetricOptInItemViewModel("power.rate_watts", "Power Rate Draw", "Instantaneous power draw in Watts", TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "power.rate_watts"), OnOptInItemChanged));
        MetricGroups.Add(pwrGroup);

        // 6. TWAMP
        var twampGroup = new MetricOptInGroupViewModel("TWAMP Latency Prober", "Configure proactive Two-Way Active Measurement Protocol network probe.", "");
        var twampMode = TelemetryOptInResolver.GetMetricOptInMode(_loadedConfig, "twamp.rtt");
        twampGroup.Items.Add(new MetricOptInItemViewModel("twamp.default", "TWAMP Prober Mode", "Proactive round-trip and one-way delay prober", twampMode, OnOptInItemChanged));
        MetricGroups.Add(twampGroup);
    }

    public async Task LoadConfigAsync()
    {
        IsLoadingConfig = true;
        try
        {
            if (_client != null)
            {
                var config = await _client.GetNodeConfigAsync(_node.Id);
                if (config != null)
                {
                    _loadedConfig = config;
                    TwampTarget = config.TwampTarget ?? "";
                    TwampClocksSynchronized = config.TwampClocksSynchronized;
                    CollectCpuOverall = config.CollectCpuOverall;
                    CollectCpuPerCore = config.CollectCpuPerCore;
                    CollectMemoryBasic = config.CollectMemoryBasic;
                    CollectMemorySwapZram = config.CollectMemorySwapZram;
                    CollectPowerBattery = config.CollectPowerBattery;
                    CollectNetworkInterfaces = config.CollectNetworkInterfaces;
                    CollectNetworkConnections = config.CollectNetworkConnections;
                    ProcessMode = config.ProcessMode;
                    TopNProcesses = (int)(config.TopNProcesses > 0 ? config.TopNProcesses : 5);
                    ProcessSnapshotLimit = (int)(config.ProcessSnapshotLimit == 0 ? 1000 : Math.Clamp(config.ProcessSnapshotLimit, 1u, 1000u));

                    BuildMetricGroups();
                }
            }
            CaptureBaseline();
        }
        finally
        {
            IsLoadingConfig = false;
        }
    }

    [RelayCommand]
    public async Task SaveConfigAsync()
    {
        // 1. Update UI Viewing opt-in
        _node.IsViewingOptedIn = IsViewingEnabled;
        _node.ViewCpuMatrix = ViewCpuMatrix;
        _node.ViewSwapZram = ViewSwapZram;
        _node.ViewPowerBattery = ViewPowerBattery;
        _node.ViewNetworkCounters = ViewNetworkCounters;

        // 2. Dispatch Collection opt-in upstream to Collector & Daemon
        if (_client != null)
        {
            var cfg = _loadedConfig?.Clone() ?? new NodeConfig
            {
                FastPollIntervalMs = 1000,
                NormalPollIntervalMs = 10000,
                SlowPollIntervalMs = 30000,
                MaxSpoolBytes = 1073741824
            };
            cfg.NodeId = _node.Id;

            // Apply each group item and subitem to cfg
            foreach (var g in MetricGroups)
            {
                foreach (var item in g.Items)
                {
                    TelemetryOptInResolver.SetMetricOptInMode(cfg, item.Key, item.Mode);
                }
                foreach (var sub in g.SubItems)
                {
                    TelemetryOptInResolver.SetMetricOptInMode(cfg, sub.Key, sub.Mode);
                }
            }

            // Sync legacy bools
            cfg.CollectCpuOverall = cfg.CpuOverallMode != TelemetryOptInMode.OptInOff;
            cfg.CollectCpuPerCore = cfg.CpuPerCoreMode != TelemetryOptInMode.OptInOff;
            cfg.CollectMemoryBasic = cfg.MemoryBasicMode != TelemetryOptInMode.OptInOff;
            cfg.CollectMemorySwapZram = cfg.MemorySwapMode != TelemetryOptInMode.OptInOff || cfg.ZramMode != TelemetryOptInMode.OptInOff;
            cfg.CollectPowerBattery = cfg.PowerMode != TelemetryOptInMode.OptInOff;
            cfg.CollectNetworkInterfaces = cfg.NetworkMode != TelemetryOptInMode.OptInOff;

            cfg.ProcessMode = ProcessMode;
            cfg.TopNProcesses = (uint)Math.Clamp(TopNProcesses, 1, 10);
            cfg.ProcessSnapshotLimit = (uint)Math.Clamp(ProcessSnapshotLimit, 1, 1000);
            cfg.TwampTarget = TwampTarget.Trim();
            cfg.TwampClocksSynchronized = TwampClocksSynchronized;

            bool success = await _client.UpdateNodeConfigAsync(_node.Id, cfg);
            if (success)
            {
                _loadedConfig = cfg;
                CaptureBaseline();
                StatusMessage = "Configuration saved; delivered on the next daemon exchange.";
            }
            else
            {
                StatusMessage = "Failed to reach Collector.";
            }
        }
        else
        {
            CaptureBaseline();
            StatusMessage = "Local settings updated.";
        }
    }
}
