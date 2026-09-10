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
    [ObservableProperty] private string _twampTarget = "";
    [ObservableProperty] private bool _twampClocksSynchronized;


    public string NodeId => _node.Id;
    public string CollectorName => _node.CollectorName;

    // --- Tier 1: Collection Opt-in (Daemon & TSDB Level) ---
    [ObservableProperty]
    private bool _collectCpuOverall = true;

    [ObservableProperty]
    private bool _collectCpuPerCore = true;

    [ObservableProperty]
    private bool _collectMemoryBasic = true;

    [ObservableProperty]
    private bool _collectMemorySwapZram = true;

    [ObservableProperty]
    private bool _collectPowerBattery = true;

    [ObservableProperty]
    private bool _collectNetworkInterfaces = true;

    [ObservableProperty]
    private bool _collectNetworkConnections = false;

    // --- Tier 2: Viewing Opt-in (Client UI Level) ---
    [ObservableProperty]
    private bool _isViewingEnabled = true;

    [ObservableProperty]
    private bool _viewCpuMatrix = true;

    [ObservableProperty]
    private bool _viewSwapZram = true;

    [ObservableProperty]
    private bool _viewPowerBattery = true;

    [ObservableProperty]
    private bool _viewNetworkCounters = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public NodeSettingsViewModel(FleetNodeModel node, CollectorClientService? client)
    {
        _node = node;
        _client = client;
        _isViewingEnabled = node.IsViewingOptedIn;
        ViewCpuMatrix = node.ViewCpuMatrix; ViewSwapZram = node.ViewSwapZram;
        ViewPowerBattery = node.ViewPowerBattery; ViewNetworkCounters = node.ViewNetworkCounters;
    }

    public async Task LoadConfigAsync()
    {
        if (_client == null) return;

        var config = await _client.GetNodeConfigAsync(_node.Id);
        if (config != null)
        {
            _loadedConfig = config;
            TwampTarget = config.TwampTarget;
            TwampClocksSynchronized = config.TwampClocksSynchronized;
            CollectCpuOverall = config.CollectCpuOverall;
            CollectCpuPerCore = config.CollectCpuPerCore;
            CollectMemoryBasic = config.CollectMemoryBasic;
            CollectMemorySwapZram = config.CollectMemorySwapZram;
            CollectPowerBattery = config.CollectPowerBattery;
            CollectNetworkInterfaces = config.CollectNetworkInterfaces;
            CollectNetworkConnections = config.CollectNetworkConnections;
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
            cfg.TwampTarget = TwampTarget.Trim();
            cfg.TwampClocksSynchronized = TwampClocksSynchronized;

            bool success = await _client.UpdateNodeConfigAsync(_node.Id, cfg);
            StatusMessage = success ? "Configuration saved; delivered on the next daemon exchange." : "Failed to reach Collector.";
        }
        else
        {
            StatusMessage = "Local settings updated.";
        }
    }
}

