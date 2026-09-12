using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MadTOM.Models;

public sealed partial class TwampTelemetryModel : ObservableObject
{
    [ObservableProperty] private bool _available;
    [ObservableProperty] private bool _oneWayAvailable;
    [ObservableProperty] private double _rttMs;
    [ObservableProperty] private string _error = "No TWAMP reflector configured";
    public string DisplayText => Available ? $"RTT {RttMs:F2} ms" : "Unavailable";
    partial void OnAvailableChanged(bool value) => OnPropertyChanged(nameof(DisplayText));
    partial void OnRttMsChanged(double value) => OnPropertyChanged(nameof(DisplayText));

    [ObservableProperty]
    private double _forwardMs;

    [ObservableProperty]
    private double _reverseMs;

    [ObservableProperty]
    private double _jitterUp;

    [ObservableProperty]
    private double _jitterDown;

    public double AsymmetryMs => Math.Round(Math.Abs(ForwardMs - ReverseMs), 2);

    partial void OnForwardMsChanged(double value) => OnPropertyChanged(nameof(AsymmetryMs));
    partial void OnReverseMsChanged(double value) => OnPropertyChanged(nameof(AsymmetryMs));
}

public sealed partial class FleetNodeModel : ObservableObject
{
    [ObservableProperty] private bool _viewCpuMatrix = true;
    [ObservableProperty] private bool _viewSwapZram = true;
    [ObservableProperty] private bool _viewPowerBattery = true;
    [ObservableProperty] private bool _viewNetworkCounters = true;
    public long TimestampUnixNano { get; set; }
    public ulong MemoryTotalBytes { get; set; }
    public double TxBytesPerSecond { get; set; }
    public double RxBytesPerSecond { get; set; }
    public System.Collections.Generic.IReadOnlyList<ProcessInfoModel> Processes { get; set; } = Array.Empty<ProcessInfoModel>();
    public System.Collections.Generic.IReadOnlyList<MADTOM.Plugins.Telemetry.Proto.V1.NicMetric> Interfaces { get; set; } = Array.Empty<MADTOM.Plugins.Telemetry.Proto.V1.NicMetric>();
    public bool ProcessesAvailable { get; set; }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _role = "baremetal"; // baremetal or vm

    [ObservableProperty]
    private string _ip = string.Empty;

    [ObservableProperty]
    private string _cpuModel = string.Empty;

    [ObservableProperty]
    private int _cores = 0;

    [ObservableProperty]
    private string _ramTotal = "Unknown";

    [ObservableProperty]
    private double _ramUsedPct = 0;

    [ObservableProperty]
    private double _cpuAvgPct = 0;

    [ObservableProperty]
    private string _status = "healthy"; // healthy, warning, critical

    [ObservableProperty]
    private TwampTelemetryModel _twamp = new();

    [ObservableProperty]
    private double[] _sparkNetUp = Array.Empty<double>();

    [ObservableProperty]
    private double[] _sparkNetDown = Array.Empty<double>();

    [ObservableProperty]
    private double[] _sparkCpu = Array.Empty<double>();

    [ObservableProperty]
    private double[] _sparkRam = Array.Empty<double>();

    [ObservableProperty]
    private float[] _coreLoads = Array.Empty<float>();

    [ObservableProperty]
    private string _collectorName = "Local Collector";

    [ObservableProperty]
    private string _collectorEndpoint = "127.0.0.1:50051";

    [ObservableProperty]
    private string _os = "Unknown";

    [ObservableProperty]
    private string _arch = "Unknown";

    [ObservableProperty]
    private bool _isViewingOptedIn = true;

    [ObservableProperty]
    private double _batteryPct;

    [ObservableProperty]
    private bool _hasBattery;

    [ObservableProperty]
    private double _zramRatio;

    [ObservableProperty]
    private double _swapUsedPct;

    public System.Collections.Concurrent.ConcurrentDictionary<string, double> LatestMetricValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double? GetMetricValue(string metricName)
    {
        if (LatestMetricValues.TryGetValue(metricName, out var val))
            return val;

        return metricName.ToLowerInvariant() switch
        {
            "cpu.total" => CpuAvgPct,
            "memory.used" => (double)(MemoryTotalBytes * (RamUsedPct / 100.0)),
            "memory.total" => (double)MemoryTotalBytes,
            "memory.available" => (double)(MemoryTotalBytes * (1.0 - (RamUsedPct / 100.0))),
            "twamp.rtt" => Twamp.Available ? Twamp.RttMs : null,
            "twamp.forward" => Twamp.OneWayAvailable ? Twamp.ForwardMs : null,
            "twamp.reverse" => Twamp.OneWayAvailable ? Twamp.ReverseMs : null,
            "power.battery_pct" => HasBattery ? BatteryPct : null,
            _ => null
        };
    }
}

