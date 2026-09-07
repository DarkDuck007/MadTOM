using System;
using System.Collections.Generic;

namespace MadTOM.Models;

public sealed class TwampTelemetryModel
{
    public double ForwardMs { get; set; }
    public double ReverseMs { get; set; }
    public double JitterUp { get; set; }
    public double JitterDown { get; set; }
    public double AsymmetryMs => Math.Round(Math.Abs(ForwardMs - ReverseMs), 2);
}

public sealed class FleetNodeModel
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = "baremetal"; // baremetal or vm
    public string Ip { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int Cores { get; set; } = 64;
    public string RamTotal { get; set; } = "64 GB";
    public double RamUsedPct { get; set; } = 30.0;
    public double CpuAvgPct { get; set; } = 40.0;
    public string Status { get; set; } = "healthy"; // healthy, warning, critical

    public TwampTelemetryModel Twamp { get; set; } = new();

    public double[] SparkNetUp { get; set; } = Array.Empty<double>();
    public double[] SparkNetDown { get; set; } = Array.Empty<double>();
    public double[] SparkCpu { get; set; } = Array.Empty<double>();
    public double[] SparkRam { get; set; } = Array.Empty<double>();

    public float[] CoreLoads { get; set; } = Array.Empty<float>();
}

