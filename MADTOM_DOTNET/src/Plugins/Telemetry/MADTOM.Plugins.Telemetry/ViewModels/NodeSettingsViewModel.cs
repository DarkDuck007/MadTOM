using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.ViewModels;

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

    // --- Tier 1: Collection Opt-in (Daemon & TSDB Level) ---
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
        ProcessTelemetryMode ProcessMode,
        int TopNProcesses,
        string TwampTarget,
        bool TwampClocksSynchronized,
        bool IsViewingEnabled,
        bool ViewCpuMatrix,
        bool ViewSwapZram,
        bool ViewPowerBattery,
        bool ViewNetworkCounters
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

    private ConfigSnapshot CurrentSnapshot => new(
        CollectCpuOverall,
        CollectCpuPerCore,
        CollectMemoryBasic,
        CollectMemorySwapZram,
        CollectPowerBattery,
        CollectNetworkInterfaces,
        CollectNetworkConnections,
        ProcessMode,
        TopNProcesses,
        TwampTarget,
        TwampClocksSynchronized,
        IsViewingEnabled,
        ViewCpuMatrix,
        ViewSwapZram,
        ViewPowerBattery,
        ViewNetworkCounters
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
            ProcessMode = b.ProcessMode;
            TopNProcesses = b.TopNProcesses;
            TwampTarget = b.TwampTarget;
            TwampClocksSynchronized = b.TwampClocksSynchronized;
            IsViewingEnabled = b.IsViewingEnabled;
            ViewCpuMatrix = b.ViewCpuMatrix;
            ViewSwapZram = b.ViewSwapZram;
            ViewPowerBattery = b.ViewPowerBattery;
            ViewNetworkCounters = b.ViewNetworkCounters;
            OnPropertyChanged(nameof(HasUnappliedChanges));
        }
    }

    public NodeSettingsViewModel(FleetNodeModel node, CollectorClientService? client)
    {
        _node = node;
        _client = client;
        _isViewingEnabled = node.IsViewingOptedIn;
        ViewCpuMatrix = node.ViewCpuMatrix; ViewSwapZram = node.ViewSwapZram;
        ViewPowerBattery = node.ViewPowerBattery; ViewNetworkCounters = node.ViewNetworkCounters;
        CaptureBaseline();
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
        _node.ViewCpuMatrix = ViewCpuMatrix; _node.ViewSwapZram = ViewSwapZram;
        _node.ViewPowerBattery = ViewPowerBattery; _node.ViewNetworkCounters = ViewNetworkCounters;

        // 2. Dispatch Collection opt-in upstream to Collector & Daemon
        if (_client != null)
        {
            var cfg = _loadedConfig?.Clone() ?? new NodeConfig { FastPollIntervalMs = 1000, NormalPollIntervalMs = 10000, SlowPollIntervalMs = 30000, MaxSpoolBytes = 1073741824 };
            cfg.NodeId = _node.Id;
            cfg.CollectCpuOverall = CollectCpuOverall;
            cfg.CollectCpuPerCore = CollectCpuPerCore;
            cfg.CollectMemoryBasic = CollectMemoryBasic;
            cfg.CollectMemorySwapZram = CollectMemorySwapZram;
            cfg.CollectPowerBattery = CollectPowerBattery;
            cfg.CollectNetworkInterfaces = CollectNetworkInterfaces;
            cfg.CollectNetworkConnections = CollectNetworkConnections;
            cfg.ProcessMode = ProcessMode;
            cfg.TopNProcesses = (uint)Math.Clamp(TopNProcesses, 1, 10);
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

