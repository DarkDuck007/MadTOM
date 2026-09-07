using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class HostMetricsTabViewModel : ViewModelBase
{
    private readonly Random _rand = new(99);

    [ObservableProperty]
    private string _selectedScope = "5m";

    public bool IsScope1m => SelectedScope.Equals("1m", StringComparison.OrdinalIgnoreCase);
    public bool IsScope5m => SelectedScope.Equals("5m", StringComparison.OrdinalIgnoreCase);
    public bool IsScope30m => SelectedScope.Equals("30m", StringComparison.OrdinalIgnoreCase);
    public bool IsScope2h => SelectedScope.Equals("2h", StringComparison.OrdinalIgnoreCase);
    public bool IsScope24h => SelectedScope.Equals("24h", StringComparison.OrdinalIgnoreCase);
    public bool IsScopeCustom => SelectedScope.Equals("custom", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedScopeChanged(string value)
    {
        OnPropertyChanged(nameof(IsScope1m));
        OnPropertyChanged(nameof(IsScope5m));
        OnPropertyChanged(nameof(IsScope30m));
        OnPropertyChanged(nameof(IsScope2h));
        OnPropertyChanged(nameof(IsScope24h));
        OnPropertyChanged(nameof(IsScopeCustom));
    }

    [ObservableProperty]
    private DateTimeOffset _customStartDate = DateTimeOffset.Now.AddHours(-1);

    [ObservableProperty]
    private DateTimeOffset _customEndDate = DateTimeOffset.Now;

    [ObservableProperty]
    private double[] _forwardSeries = Array.Empty<double>();

    [ObservableProperty]
    private double[] _reverseSeries = Array.Empty<double>();

    [ObservableProperty]
    private double[] _asymmetrySeries = Array.Empty<double>();

    [ObservableProperty]
    private string[] _timeLabels = Array.Empty<string>();

    [ObservableProperty]
    private bool _isAggregatedMode;

    [ObservableProperty]
    private int _threadCount = 1024;

    [ObservableProperty]
    private float[] _coreLoads = Array.Empty<float>();

    [ObservableProperty]
    private IReadOnlyList<FleetNodeModel> _clusterNodes = Array.Empty<FleetNodeModel>();

    [ObservableProperty]
    private string _targetHostId = "gander-epyc-01";

    public HostMetricsTabViewModel()
    {
        GenerateSeries(1.84, 3.12);
    }

    [RelayCommand]
    public void SetScope(string scope)
    {
        SelectedScope = scope;
        NotificationService.Instance.ShowToast($"Observation scope adjusted to {scope}");
    }

    public void UpdateForNode(string hostId, FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        TargetHostId = hostId;
        ClusterNodes = allNodes;

        if (hostId.Equals("aggregated", StringComparison.OrdinalIgnoreCase))
        {
            IsAggregatedMode = true;
            ThreadCount = allNodes.Sum(n => n.Cores);
            GenerateSeries(1.84, 3.12);
            return;
        }

        IsAggregatedMode = false;
        if (node != null)
        {
            ThreadCount = node.Cores;
            CoreLoads = node.CoreLoads;
            GenerateSeries(node.Twamp.ForwardMs, node.Twamp.ReverseMs);
        }
    }

    private void GenerateSeries(double baseFwd, double baseRev)
    {
        const int points = 24;
        var fwd = new double[points];
        var rev = new double[points];
        var asym = new double[points];
        var labels = new string[points];

        for (int i = 0; i < points; i++)
        {
            labels[i] = $"{i * 5}s ago";
            fwd[i] = Math.Round(baseFwd + (_rand.NextDouble() - 0.5) * 0.4, 2);
            rev[i] = Math.Round(baseRev + (_rand.NextDouble() - 0.5) * 0.6, 2);
            asym[i] = Math.Round(Math.Abs(fwd[i] - rev[i]), 2);
        }

        Array.Reverse(labels);
        ForwardSeries = fwd;
        ReverseSeries = rev;
        AsymmetrySeries = asym;
        TimeLabels = labels;
    }
}

