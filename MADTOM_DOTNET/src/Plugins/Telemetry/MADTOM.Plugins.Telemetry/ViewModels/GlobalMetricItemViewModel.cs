using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MadTOM.ViewModels;

public sealed partial class GlobalMetricItemViewModel : ObservableObject
{
    public string Key { get; }
    public string Name { get; }
    public string ShortName { get; }
    public string Icon { get; }
    public string Unit { get; }

    [ObservableProperty]
    private double _sumValue;

    [ObservableProperty]
    private double _avgValue;

    [ObservableProperty]
    private double _rateOfChangeValue;

    [ObservableProperty]
    private string _formattedValue = string.Empty;

    [ObservableProperty]
    private string _context = string.Empty;

    [ObservableProperty]
    private string _modifierLabel = "Sum";

    [ObservableProperty]
    private string _modifierTag = "SUM";

    [ObservableProperty]
    private bool _isPinned;

    public GlobalMetricItemViewModel(string key, string name, string icon, string unit, string? shortName = null)
    {
        Key = key;
        Name = name;
        Icon = icon;
        Unit = unit;
        ShortName = shortName ?? name;
    }

    public void Update(double sum, double avg, double rate, int activeNodes, string modifier)
    {
        SumValue = sum;
        AvgValue = avg;
        RateOfChangeValue = rate;
        Context = activeNodes == 1 ? "1 active node" : $"{activeNodes} active nodes";
        ApplyModifier(modifier);
    }

    public void ApplyModifier(string modifier)
    {
        ModifierLabel = modifier;
        ModifierTag = modifier switch
        {
            "Avg" or "Average" => "AVG",
            "Rate of Change" or "Rate" => "Δ/s",
            _ => "SUM"
        };
        double v = modifier switch
        {
            "Avg" or "Average" => AvgValue,
            "Rate of Change" or "Rate" => RateOfChangeValue,
            _ => SumValue
        };

        bool isRate = modifier is "Rate of Change" or "Rate";
        string rateSuffix = isRate ? "/s" : "";

        if (Key.StartsWith("network") || Key.StartsWith("nic."))
        {
            // Unit is bits/sec or bytes/sec
            double abs = Math.Abs(v);
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            if (abs >= 1e9) FormattedValue = $"{prefix}{abs / 1e9:F3} Gbps{rateSuffix}";
            else if (abs >= 1e6) FormattedValue = $"{prefix}{abs / 1e6:F2} Mbps{rateSuffix}";
            else if (abs >= 1e3) FormattedValue = $"{prefix}{abs / 1e3:F1} Kbps{rateSuffix}";
            else FormattedValue = $"{prefix}{abs:F0} bps{rateSuffix}";
        }
        else if (Key.StartsWith("disk.bytes") || (Key.StartsWith("disk.io.") && (Key.EndsWith(".read_bytes") || Key.EndsWith(".write_bytes"))))
        {
            double abs = Math.Abs(v);
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            if (abs >= 1073741824) FormattedValue = $"{prefix}{abs / 1073741824:F2} GB{rateSuffix}";
            else if (abs >= 1048576) FormattedValue = $"{prefix}{abs / 1048576:F1} MB{rateSuffix}";
            else if (abs >= 1024) FormattedValue = $"{prefix}{abs / 1024:F0} KB{rateSuffix}";
            else FormattedValue = $"{prefix}{abs:F0} B{rateSuffix}";
        }
        else if (Key.StartsWith("disk.ops") || (Key.StartsWith("disk.io.") && (Key.EndsWith(".read_ops") || Key.EndsWith(".write_ops"))))
        {
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            FormattedValue = $"{prefix}{Math.Abs(v):F0} IOPS{rateSuffix}";
        }
        else if (Key.StartsWith("cpu") || Key.StartsWith("memory.pct"))
        {
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            FormattedValue = $"{prefix}{v:F1}%{rateSuffix}";
        }
        else if (Key.StartsWith("memory.bytes"))
        {
            double abs = Math.Abs(v);
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            if (abs >= 1073741824) FormattedValue = $"{prefix}{abs / 1073741824:F2} GB{rateSuffix}";
            else if (abs >= 1048576) FormattedValue = $"{prefix}{abs / 1048576:F1} MB{rateSuffix}";
            else FormattedValue = $"{prefix}{abs / 1024:F0} KB{rateSuffix}";
        }
        else if (Key.StartsWith("twamp"))
        {
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            FormattedValue = $"{prefix}{v:F2} ms{rateSuffix}";
        }
        else
        {
            string prefix = isRate && v > 0 ? "+" : (isRate && v < 0 ? "-" : "");
            FormattedValue = $"{prefix}{v:F2} {Unit}{rateSuffix}".Trim();
        }
    }
}
