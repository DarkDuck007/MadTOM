using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MadTOM.Models;

public sealed partial class TwampTelemetryModel : ObservableObject
{
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
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _role = "baremetal"; // baremetal or vm

    [ObservableProperty]
    private string _ip = string.Empty;

    [ObservableProperty]
    private string _cpuModel = string.Empty;

    [ObservableProperty]
    private int _cores = 64;

    [ObservableProperty]
    private string _ramTotal = "64 GB";

    [ObservableProperty]
    private double _ramUsedPct = 30.0;

    [ObservableProperty]
    private double _cpuAvgPct = 40.0;

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
}

