using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public sealed partial class NodeGroupItemViewModel : ObservableObject
{
    private readonly NodeGroupStore _store;
    public FleetNodeModel Node { get; }

    public string NodeId => Node.Id;
    public string Ip => Node.Ip;

    [ObservableProperty]
    private string _groupName;

    public NodeGroupItemViewModel(FleetNodeModel node, NodeGroupStore store)
    {
        Node = node;
        _store = store;
        _groupName = string.IsNullOrWhiteSpace(node.GroupName) ? store.GetGroup(node.Id) : node.GroupName;
    }

    partial void OnGroupNameChanged(string value)
    {
        string clean = string.IsNullOrWhiteSpace(value) ||
                       value.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value.Trim();
        Node.GroupName = clean;
        _store.SetGroup(NodeId, clean);
    }

    [RelayCommand]
    public void SetPresetGroup(string group)
    {
        GroupName = group ?? string.Empty;
    }
}

public partial class CollectorSettingsViewModel : ViewModelBase
{
    private readonly GraphPerformanceSettings _performanceSettings;
    [ObservableProperty] private double _graphPointsPerPixel = 1;
    [ObservableProperty] private double _historyPointsPerPixel = 3;

    [RelayCommand]
    public void ApplyGraphPerformance()
    {
        if (!GraphPerformanceSettings.IsValid(GraphPointsPerPixel) || !GraphPerformanceSettings.IsValidHistory(HistoryPointsPerPixel))
        {
            StatusMessage = "Drawing density must be 0.1–2 and history density must be 1–10 points per pixel.";
            return;
        }
        try
        {
            _performanceSettings.Save(GraphPointsPerPixel, HistoryPointsPerPixel);
            StatusMessage = "Graph performance settings saved. Applies to all client graphs.";
        }
        catch (Exception ex) { StatusMessage = $"Could not save performance settings: {ex.Message}"; }
    }

    private readonly TelemetryCacheSettingsStore _cacheSettingsStore;
    private DateTime _lastCacheEstimate;
    [ObservableProperty] private string _cacheRetentionMinutes = "60";
    [ObservableProperty] private string _cacheMemoryEstimate = "Waiting for telemetry.";
    [ObservableProperty] private int _liveCacheLimitMiB = 64;
    [ObservableProperty] private int _storedCacheLimitMiB = 32;
    [ObservableProperty] private int _storedCacheRetentionSeconds = 30;
    [ObservableProperty] private string _liveCacheUsage = "0 MiB";
    [ObservableProperty] private string _storedCacheUsage = "0 MiB";
    [ObservableProperty] private string _totalCacheUsage = "0 MiB";
    [ObservableProperty] private string _storedCacheEstimate = "Up to 32 MiB";
    [ObservableProperty] private string _totalCacheEstimate = "Up to 96 MiB";
    partial void OnLiveCacheLimitMiBChanged(int value) => RefreshCacheEstimate();
    partial void OnStoredCacheLimitMiBChanged(int value) => RefreshCacheEstimate();

    [RelayCommand] public void RefreshCacheStats() => RefreshCacheEstimate();
    [RelayCommand] public void ClearLiveCache() { _dataProvider.HistoryCache?.ClearLive(); RefreshCacheEstimate(); }
    [RelayCommand] public void ClearStoredCache() { _dataProvider.HistoryCache?.ClearStored(); RefreshCacheEstimate(); }

    public bool IsCacheAvailable => _dataProvider.HistoryCache != null;

    partial void OnCacheRetentionMinutesChanged(string value) => RefreshCacheEstimate();

    private void RefreshCacheEstimate()
    {
        _lastCacheEstimate = DateTime.UtcNow;
        if (!int.TryParse(CacheRetentionMinutes, out int minutes) || minutes is < 1 or > 1440)
        {
            CacheMemoryEstimate = "Enter 1–1440 minutes.";
            return;
        }
        if (_dataProvider?.HistoryCache is not { } cache) return;
        var usage = cache.GetUsage(minutes);
        LiveCacheUsage = $"{usage.LiveBytes / 1048576.0:F2} MiB · {usage.SeriesCount} series / {usage.LivePoints:N0} points";
        StoredCacheUsage = $"{usage.StoredBytes / 1048576.0:F2} MiB · {usage.StoredRanges} ranges / {usage.StoredPoints:N0} points";
        TotalCacheUsage = $"{usage.TotalBytes / 1048576.0:F2} MiB";
        double projected = Math.Min(usage.ProjectedLiveBytes / 1048576, LiveCacheLimitMiB);
        CacheMemoryEstimate = usage.SeriesCount == 0 ? $"Waiting for samples · cap {LiveCacheLimitMiB} MiB"
            : $"~{projected:F1} MiB at {minutes} min · cap {LiveCacheLimitMiB} MiB";
        StoredCacheEstimate = $"Up to {StoredCacheLimitMiB} MiB · depends on queries";
        TotalCacheEstimate = $"Live forecast + stored cap: ~{projected + StoredCacheLimitMiB:F1} MiB · total cap {LiveCacheLimitMiB + StoredCacheLimitMiB} MiB";
    }

    [RelayCommand]
    public void ApplyCacheRetention()
    {
        if (!int.TryParse(CacheRetentionMinutes, out int minutes) || minutes is < 1 or > 1440)
        {
            StatusMessage = "Cache retention must be 1–1440 minutes.";
            return;
        }
        if (LiveCacheLimitMiB is < 1 or > 4096 || StoredCacheLimitMiB is < 1 or > 4096 || StoredCacheRetentionSeconds is < 1 or > 86400)
        {
            StatusMessage = "Cache limits: 1–4096 MiB each; stored retention: 1–86400 seconds.";
            return;
        }
        if (_dataProvider.HistoryCache is not { } cache) return;
        try
        {
            _cacheSettingsStore.Save(new TelemetryCacheSettingsStore.Settings { RetentionMinutes = minutes,
                LiveLimitMiB = LiveCacheLimitMiB, StoredLimitMiB = StoredCacheLimitMiB, StoredRetentionSeconds = StoredCacheRetentionSeconds });
            cache.Configure(minutes, LiveCacheLimitMiB * 1048576L, StoredCacheLimitMiB * 1048576L, StoredCacheRetentionSeconds);
            RefreshCacheEstimate();
            StatusMessage = "Cache limits saved and applied.";
        }
        catch (Exception ex) { StatusMessage = $"Could not save cache settings: {ex.Message}"; }
    }

    [RelayCommand]
    public void ClearTelemetryCache()
    {
        _dataProvider.HistoryCache?.Clear();
        RefreshCacheEstimate();
        StatusMessage = "Client history cache cleared. New telemetry will start filling it again.";
    }

    private readonly MultiCollectorManager _manager;
    private readonly ITelemetryDataProvider _dataProvider;
    private readonly NodeGroupStore _nodeGroupStore;
    private readonly GlobalMetricsStore _metricsStore;
    private readonly System.Collections.Generic.Dictionary<string, (double PrevSum, DateTime PrevTime)> _previousSums = new();

    public ObservableCollection<CollectorEndpointConfig> Endpoints => _manager.ConfiguredEndpoints;
    public ObservableCollection<FleetNodeModel> DetectedNodes { get; } = new();
    public ObservableCollection<NodeGroupItemViewModel> NodeGroupItems { get; } = new();
    public ObservableCollection<GlobalMetricItemViewModel> GlobalMetrics { get; } = new();
    public ObservableCollection<GlobalMetricItemViewModel> PinnedMetricsPreview { get; } = new();

    public ObservableCollection<MetricDefinition> AvailableMetricOptions { get; } = new(GlobalMetricsStore.AvailableCatalog);

    [ObservableProperty]
    private MetricDefinition _selectedMetricOption = GlobalMetricsStore.AvailableCatalog[0];

    [ObservableProperty]
    private string _selectedAddModifier = "Sum";

    [ObservableProperty]
    private bool _isAddModifierSum = true;

    [ObservableProperty]
    private bool _isAddModifierAvg;

    [ObservableProperty]
    private bool _isAddModifierRate;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _selectedModifier = "Sum";

    [ObservableProperty]
    private bool _isModifierSum = true;

    [ObservableProperty]
    private bool _isModifierAvg;

    [ObservableProperty]
    private bool _isModifierRate;

    [ObservableProperty]
    private string _newName = "Local Collector";

    [ObservableProperty]
    private string _newAddress = "127.0.0.1:50051";

    [ObservableProperty]
    private bool _isEditingCollector;

    [ObservableProperty]
    private string _editingOriginalAddress = string.Empty;

    public string SaveCollectorButtonText => IsEditingCollector ? "Save Changes" : "+ Add Collector";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private FleetNodeModel? _selectedNode;

    [ObservableProperty]
    private NodeSettingsViewModel? _selectedNodeSettings;

    [ObservableProperty]
    private bool _isUnsavedPromptOpen;

    public event Action? CloseRequested;

    public CollectorSettingsViewModel(
        MultiCollectorManager manager,
        ITelemetryDataProvider dataProvider,
        NodeGroupStore? nodeGroupStore = null,
        GlobalMetricsStore? metricsStore = null,
        TelemetryCacheSettingsStore? cacheSettingsStore = null,
        GraphPerformanceSettings? performanceSettings = null)
    {
        _performanceSettings = performanceSettings ?? GraphPerformanceSettings.Current;
        GraphPointsPerPixel = _performanceSettings.PointsPerPixel;
        HistoryPointsPerPixel = _performanceSettings.HistoryPointsPerPixel;
        _cacheSettingsStore = cacheSettingsStore ?? new TelemetryCacheSettingsStore();
        _manager = manager;
        _dataProvider = dataProvider;
        _nodeGroupStore = nodeGroupStore ?? new NodeGroupStore();
        _metricsStore = metricsStore ?? new GlobalMetricsStore();

        CacheRetentionMinutes = (_dataProvider.HistoryCache?.RetentionMinutes ?? 60).ToString();
        LiveCacheLimitMiB = (int)((_dataProvider.HistoryCache?.LiveLimitBytes ?? 64L * 1048576) / 1048576);
        StoredCacheLimitMiB = (int)((_dataProvider.HistoryCache?.StoredLimitBytes ?? 32L * 1048576) / 1048576);
        StoredCacheRetentionSeconds = _dataProvider.HistoryCache?.StoredRetentionSeconds ?? 30;
        RefreshCacheEstimate();
        RefreshNodes();
        RefreshPinnedPreview();

        _nodeGroupStore.GroupChanged += (nodeId, newGroup) =>
        {
            var item = NodeGroupItems.FirstOrDefault(i => i.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (item != null && !item.GroupName.Equals(newGroup, StringComparison.OrdinalIgnoreCase))
            {
                item.GroupName = newGroup;
            }
            var detected = DetectedNodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (detected != null)
            {
                detected.GroupName = newGroup;
            }
        };

        _metricsStore.ConfigChanged += () =>
        {
            RefreshPinnedPreview();
            foreach (var m in GlobalMetrics)
            {
                m.IsPinned = _metricsStore.IsPinned(m.Key, SelectedModifier);
            }
        };

        _dataProvider.NodeTelemetryUpdated += (_, _) =>
        {
            RefreshGlobalMetrics();
            if ((DateTime.UtcNow - _lastCacheEstimate).TotalSeconds >= 5) RefreshCacheEstimate();
        };
    }

    [RelayCommand]
    public void SetAddModifier(string modifier)
    {
        SelectedAddModifier = modifier;
        IsAddModifierSum = modifier is "Sum";
        IsAddModifierAvg = modifier is "Avg" or "Average";
        IsAddModifierRate = modifier is "Rate of Change" or "Rate";
    }

    [RelayCommand]
    public void PinCurrentSelection()
    {
        if (SelectedMetricOption != null)
        {
            _metricsStore.PinMetric(SelectedMetricOption.Key, SelectedAddModifier);
            StatusMessage = $"Pinned {SelectedMetricOption.Name} ({SelectedAddModifier}) to top bar";
        }
    }

    [RelayCommand]
    public void UnpinMetric(GlobalMetricItemViewModel? item)
    {
        if (item != null)
        {
            _metricsStore.UnpinMetric(item.Key, item.ModifierLabel);
            StatusMessage = $"Unpinned {item.Name} from top bar";
        }
    }

    [RelayCommand]
    public void TogglePinForMetric(GlobalMetricItemViewModel? item)
    {
        if (item != null)
        {
            _metricsStore.TogglePinned(item.Key, SelectedModifier);
            item.IsPinned = _metricsStore.IsPinned(item.Key, SelectedModifier);
            StatusMessage = item.IsPinned
                ? $"Pinned {item.Name} ({SelectedModifier}) to top bar"
                : $"Unpinned {item.Name} ({SelectedModifier}) from top bar";
        }
    }

    [RelayCommand]
    public void SetModifier(string modifier)
    {
        SelectedModifier = modifier;
        IsModifierSum = modifier is "Sum";
        IsModifierAvg = modifier is "Avg" or "Average";
        IsModifierRate = modifier is "Rate of Change" or "Rate";

        foreach (var metric in GlobalMetrics)
        {
            metric.ApplyModifier(SelectedModifier);
            metric.IsPinned = _metricsStore.IsPinned(metric.Key, SelectedModifier);
        }
    }

    public void RefreshPinnedPreview()
    {
        var pinnedConfigs = _metricsStore.GetPinnedItems();
        for (int i = 0; i < pinnedConfigs.Count; i++)
        {
            var cfg = pinnedConfigs[i];
            var def = GlobalMetricsStore.GetMetricDefinition(cfg.Key);
            if (def == null) continue;

            var existing = PinnedMetricsPreview.FirstOrDefault(m => m.Key.Equals(cfg.Key, StringComparison.OrdinalIgnoreCase) && m.ModifierLabel.Equals(cfg.Modifier, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new GlobalMetricItemViewModel(def.Key, def.Name, def.Icon, def.Unit, def.ShortName);
                existing.ApplyModifier(cfg.Modifier);
                existing.IsPinned = true;
                if (i < PinnedMetricsPreview.Count)
                    PinnedMetricsPreview.Insert(i, existing);
                else
                    PinnedMetricsPreview.Add(existing);
            }
        }

        for (int i = PinnedMetricsPreview.Count - 1; i >= 0; i--)
        {
            var m = PinnedMetricsPreview[i];
            if (!pinnedConfigs.Any(cfg => cfg.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase) && cfg.Modifier.Equals(m.ModifierLabel, StringComparison.OrdinalIgnoreCase)))
            {
                PinnedMetricsPreview.RemoveAt(i);
            }
        }
    }

    public void RefreshNodes()
    {
        var currentNodes = _dataProvider.GetFleetNodes();
        bool nodesAddedOrRemoved = false;
        foreach (var node in currentNodes)
        {
            var existing = DetectedNodes.FirstOrDefault(n => n.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                node.GroupName = _nodeGroupStore.GetGroup(node.Id);
                DetectedNodes.Add(node);
                nodesAddedOrRemoved = true;
            }
        }

        if (nodesAddedOrRemoved || NodeGroupItems.Count != DetectedNodes.Count)
        {
            RefreshNodeGroups();
        }
        PopulateAvailableMetricOptions();
        RefreshGlobalMetrics();
    }

    private void PopulateAvailableMetricOptions()
    {
        foreach (var def in GlobalMetricsStore.AvailableCatalog)
        {
            if (!AvailableMetricOptions.Any(o => o.Key.Equals(def.Key, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableMetricOptions.Add(def);
            }
        }

        var disks = DetectedNodes.SelectMany(n => n.Disks).Select(d => d.Name).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct();
        foreach (var dev in disks)
        {
            string[] keys = { $"disk.io.{dev}.read_bytes", $"disk.io.{dev}.write_bytes", $"disk.io.{dev}.read_ops", $"disk.io.{dev}.write_ops" };
            foreach (var k in keys)
            {
                if (!AvailableMetricOptions.Any(o => o.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableMetricOptions.Add(GlobalMetricsStore.GetMetricDefinition(k));
                }
            }
        }

        var nics = DetectedNodes.SelectMany(n => n.Interfaces).Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct();
        foreach (var iface in nics)
        {
            string[] keys = { $"nic.{iface}.rx_bytes", $"nic.{iface}.tx_bytes" };
            foreach (var k in keys)
            {
                if (!AvailableMetricOptions.Any(o => o.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableMetricOptions.Add(GlobalMetricsStore.GetMetricDefinition(k));
                }
            }
        }

        if (SelectedMetricOption == null && AvailableMetricOptions.Count > 0)
        {
            SelectedMetricOption = AvailableMetricOptions[0];
        }
    }

    public void RefreshNodeGroups()
    {
        // Keep existing item instances alive so user typing and caret are never interrupted by 1Hz telemetry updates
        foreach (var node in DetectedNodes)
        {
            if (!NodeGroupItems.Any(item => item.NodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                NodeGroupItems.Add(new NodeGroupItemViewModel(node, _nodeGroupStore));
            }
        }
        for (int i = NodeGroupItems.Count - 1; i >= 0; i--)
        {
            if (!DetectedNodes.Any(n => n.Id.Equals(NodeGroupItems[i].NodeId, StringComparison.OrdinalIgnoreCase)))
            {
                NodeGroupItems.RemoveAt(i);
            }
        }
    }

    public void RefreshGlobalMetrics()
    {
        var nodes = _dataProvider.GetFleetNodes();
        int count = nodes.Count;
        DateTime now = DateTime.UtcNow;

        void UpdateMetric(string key, string name, string icon, string unit, double sum, double avg)
        {
            double rate = 0;
            if (_previousSums.TryGetValue(key, out var prev))
            {
                double dt = (now - prev.PrevTime).TotalSeconds;
                if (dt > 0.05)
                {
                    rate = (sum - prev.PrevSum) / dt;
                }
            }
            _previousSums[key] = (sum, now);

            var item = GlobalMetrics.FirstOrDefault(m => m.Key == key);
            if (item == null)
            {
                var def = GlobalMetricsStore.GetMetricDefinition(key);
                item = new GlobalMetricItemViewModel(key, name, icon, unit, def?.ShortName);
                GlobalMetrics.Add(item);
            }
            item.IsPinned = _metricsStore.IsPinned(key, SelectedModifier);
            item.Update(sum, avg, rate, count, SelectedModifier);

            foreach (var pinned in PinnedMetricsPreview)
            {
                if (pinned.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    pinned.Update(sum, avg, rate, count, pinned.ModifierLabel);
                }
            }
        }

        double ingressSumBps = nodes.Sum(n => n.RxBytesPerSecond) * 8.0;
        double ingressAvgBps = count > 0 ? ingressSumBps / count : 0;
        UpdateMetric("network.ingress", "Network Ingress", "⬇", "bps", ingressSumBps, ingressAvgBps);

        double egressSumBps = nodes.Sum(n => n.TxBytesPerSecond) * 8.0;
        double egressAvgBps = count > 0 ? egressSumBps / count : 0;
        UpdateMetric("network.egress", "Network Egress", "⬆", "bps", egressSumBps, egressAvgBps);

        double cpuSumPct = nodes.Sum(n => n.CpuAvgPct);
        double cpuAvgPct = count > 0 ? cpuSumPct / count : 0;
        UpdateMetric("cpu.load", "CPU Compute Load", "⚙", "%", cpuSumPct, cpuAvgPct);

        double ramSumBytes = nodes.Sum(n => n.GetMetricValue("memory.used") ?? 0.0);
        double ramAvgBytes = count > 0 ? ramSumBytes / count : 0;
        UpdateMetric("memory.bytes", "Memory (RAM) Used", "💾", "B", ramSumBytes, ramAvgBytes);

        double diskReadSum = nodes.Sum(n => n.DiskReadBytesPerSecond > 0 ? n.DiskReadBytesPerSecond : (n.GetMetricValue("disk.io.read_bytes") ?? 0.0));
        double diskReadAvg = count > 0 ? diskReadSum / count : 0;
        UpdateMetric("disk.bytes.read", "Disk Read Throughput", "📖", "B/s", diskReadSum, diskReadAvg);

        double diskWriteSum = nodes.Sum(n => n.DiskWriteBytesPerSecond > 0 ? n.DiskWriteBytesPerSecond : (n.GetMetricValue("disk.io.write_bytes") ?? 0.0));
        double diskWriteAvg = count > 0 ? diskWriteSum / count : 0;
        UpdateMetric("disk.bytes.write", "Disk Write Throughput", "✍", "B/s", diskWriteSum, diskWriteAvg);

        double diskOpsSum = nodes.Sum(n => (n.GetMetricValue("disk.io.read_ops") ?? 0.0) + (n.GetMetricValue("disk.io.write_ops") ?? 0.0));
        double diskOpsAvg = count > 0 ? diskOpsSum / count : 0;
        UpdateMetric("disk.ops", "Disk Operations", "⚡", "IOPS", diskOpsSum, diskOpsAvg);

        var twampNodes = nodes.Where(n => n.Twamp.Available).ToList();
        double twampAvg = twampNodes.Count > 0 ? twampNodes.Average(n => n.Twamp.RttMs) : 0;
        double twampMax = twampNodes.Count > 0 ? twampNodes.Max(n => n.Twamp.RttMs) : 0;
        UpdateMetric("twamp.rtt", "TWAMP Round-Trip Latency", "⏱", "ms", twampMax, twampAvg);

        // Also update any dynamic pinned metrics in PinnedMetricsPreview
        foreach (var pinned in PinnedMetricsPreview)
        {
            if (GlobalMetrics.Any(m => m.Key.Equals(pinned.Key, StringComparison.OrdinalIgnoreCase)))
                continue;

            var def = GlobalMetricsStore.GetMetricDefinition(pinned.Key);
            double dynSum = nodes.Sum(n => n.GetMetricValue(pinned.Key) ?? 0.0);
            double dynAvg = count > 0 ? dynSum / count : 0;
            UpdateMetric(pinned.Key, def.Name, def.Icon, def.Unit, dynSum, dynAvg);
        }
    }

    partial void OnSelectedNodeChanged(FleetNodeModel? value)
    {
        if (value != null)
        {
            var client = _manager.GetClientForNode(value);
            SelectedNodeSettings = new NodeSettingsViewModel(value, client);
            _ = SelectedNodeSettings.LoadConfigAsync();
        }
        else
        {
            SelectedNodeSettings = null;
        }
    }

    [RelayCommand]
    public void SelectCollectorForEdit(CollectorEndpointConfig endpoint)
    {
        if (endpoint == null) return;
        IsEditingCollector = true;
        EditingOriginalAddress = endpoint.Address;
        NewName = endpoint.Name;
        NewAddress = endpoint.Address;
        StatusMessage = $"Editing collector '{endpoint.Name}' ({endpoint.Address})";
        OnPropertyChanged(nameof(SaveCollectorButtonText));
    }

    [RelayCommand]
    public void CancelEditCollector()
    {
        IsEditingCollector = false;
        EditingOriginalAddress = string.Empty;
        NewName = string.Empty;
        NewAddress = string.Empty;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(SaveCollectorButtonText));
    }

    [RelayCommand]
    public async Task SaveCollectorAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAddress))
        {
            StatusMessage = "Collector address cannot be empty.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(NewName) ? "Collector" : NewName.Trim();
        string address = NewAddress.Trim();

        if (IsEditingCollector)
        {
            _manager.UpdateCollector(EditingOriginalAddress, name, address);
            StatusMessage = $"Updated collector: {name} ({address})";
            CancelEditCollector();
        }
        else
        {
            _manager.AddCollector(name, address);
            StatusMessage = $"Added collector: {name} ({address})";
            NewName = string.Empty;
            NewAddress = string.Empty;
        }

        // Trigger immediate fetch
        var nodes = await _manager.FetchAllNodesAsync();
        RefreshNodes();
    }

    [RelayCommand]
    public async Task AddCollectorAsync()
    {
        await SaveCollectorAsync();
    }

    [RelayCommand]
    public void RemoveCollector(string address)
    {
        if (string.IsNullOrEmpty(address)) return;
        if (IsEditingCollector && EditingOriginalAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            CancelEditCollector();
        }
        _manager.RemoveCollector(address);
        StatusMessage = $"Removed collector: {address}";
        RefreshNodes();
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        StatusMessage = "Querying collectors for nodes...";
        var nodes = await _manager.FetchAllNodesAsync();
        RefreshNodes();
        StatusMessage = $"Discovery completed. {nodes.Count} node(s) found.";
    }

    [RelayCommand]
    public void ClearSelectedNode()
    {
        SelectedNode = null;
    }

    [RelayCommand]
    public void Close()
    {
        if (SelectedNodeSettings != null && SelectedNodeSettings.HasUnappliedChanges)
        {
            IsUnsavedPromptOpen = true;
            return;
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public async Task ApplyAndCloseAsync()
    {
        if (SelectedNodeSettings != null)
        {
            await SelectedNodeSettings.SaveConfigAsync();
        }
        IsUnsavedPromptOpen = false;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public void DiscardAndClose()
    {
        if (SelectedNodeSettings != null)
        {
            SelectedNodeSettings.RevertChanges();
        }
        IsUnsavedPromptOpen = false;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    public void CancelClosePrompt()
    {
        IsUnsavedPromptOpen = false;
    }
}
