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
    private bool _isCustomScopeModalOpen;

    [ObservableProperty]
    private DateTimeOffset? _customStartDate = DateTimeOffset.Now.AddHours(-1);

    [ObservableProperty]
    private TimeSpan? _customStartTime = DateTime.Now.AddHours(-1).TimeOfDay;

    [ObservableProperty]
    private DateTimeOffset? _customEndDate = DateTimeOffset.Now;

    [ObservableProperty]
    private TimeSpan? _customEndTime = DateTime.Now.TimeOfDay;

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
        GenerateSeries(1.84, 3.12, 1200);
    }

    [RelayCommand]
    public void SetScope(string scope)
    {
        SelectedScope = scope;
        if (scope.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            IsCustomScopeModalOpen = true;
            return;
        }

        NotificationService.Instance.ShowToast($"Observation scope adjusted to {scope}");
    }

    [RelayCommand]
    public void ApplyCustomScope()
    {
        IsCustomScopeModalOpen = false;
        string startStr = CustomStartDate?.ToString("yyyy-MM-dd") ?? "Start";
        string startTimeStr = CustomStartTime.HasValue ? $" {CustomStartTime.Value.Hours:D2}:{CustomStartTime.Value.Minutes:D2}" : "";
        string endStr = CustomEndDate?.ToString("yyyy-MM-dd") ?? "End";
        string endTimeStr = CustomEndTime.HasValue ? $" {CustomEndTime.Value.Hours:D2}:{CustomEndTime.Value.Minutes:D2}" : "";

        NotificationService.Instance.ShowToast($"Applied custom window: {startStr}{startTimeStr} → {endStr}{endTimeStr}");
        GenerateSeries(1.84, 3.12, 1200);
    }

    [RelayCommand]
    public void CancelCustomScope()
    {
        IsCustomScopeModalOpen = false;
        if (SelectedScope.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            SelectedScope = "5m";
        }
    }

    public void UpdateForNode(string hostId, FleetNodeModel? node, IReadOnlyList<FleetNodeModel> allNodes)
    {
        TargetHostId = hostId;
        ClusterNodes = allNodes;

        if (hostId.Equals("aggregated", StringComparison.OrdinalIgnoreCase))
        {
            IsAggregatedMode = true;
            ThreadCount = allNodes.Sum(n => n.Cores);
            GenerateSeries(1.84, 3.12, 1200);
            return;
        }

        IsAggregatedMode = false;
        if (node != null)
        {
            ThreadCount = node.Cores;
            CoreLoads = node.CoreLoads;
            GenerateSeries(node.Twamp.ForwardMs, node.Twamp.ReverseMs, 1200);
        }
    }

    public void PushLiveSample(double fwd, double rev)
    {
        int count = ForwardSeries.Length;
        if (count == 0)
        {
            GenerateSeries(fwd, rev, 1200);
            return;
        }

        var nextFwd = new double[count];
        var nextRev = new double[count];
        var nextAsym = new double[count];
        var nextLabels = new string[count];

        Array.Copy(ForwardSeries, 1, nextFwd, 0, count - 1);
        Array.Copy(ReverseSeries, 1, nextRev, 0, count - 1);
        Array.Copy(AsymmetrySeries, 1, nextAsym, 0, count - 1);

        nextFwd[count - 1] = Math.Round(fwd, 2);
        nextRev[count - 1] = Math.Round(rev, 2);
        nextAsym[count - 1] = Math.Round(Math.Abs(fwd - rev), 2);

        if (TimeLabels != null && TimeLabels.Length == count)
        {
            Array.Copy(TimeLabels, 1, nextLabels, 0, count - 1);
            nextLabels[count - 1] = "now";
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int s = count - 1 - i;
                nextLabels[i] = s == 0 ? "now" : $"{s}s";
            }
        }

        ForwardSeries = nextFwd;
        ReverseSeries = nextRev;
        AsymmetrySeries = nextAsym;
        TimeLabels = nextLabels;
    }

    private void GenerateSeries(double baseFwd, double baseRev, int points = 1200)
    {
        var fwd = new double[points];
        var rev = new double[points];
        var asym = new double[points];
        var labels = new string[points];

        double curFwd = baseFwd;
        double curRev = baseRev;

        for (int i = 0; i < points; i++)
        {
            curFwd = Math.Clamp(curFwd + (_rand.NextDouble() - 0.5) * 0.1, 0.4, 60.0);
            curRev = Math.Clamp(curRev + (_rand.NextDouble() - 0.5) * 0.15, 0.6, 70.0);
            fwd[i] = Math.Round(curFwd, 2);
            rev[i] = Math.Round(curRev, 2);
            asym[i] = Math.Round(Math.Abs(curFwd - curRev), 2);

            int secondsAgo = points - 1 - i;
            labels[i] = secondsAgo == 0 ? "now" : $"{secondsAgo}s";
        }

        ForwardSeries = fwd;
        ReverseSeries = rev;
        AsymmetrySeries = asym;
        TimeLabels = labels;
    }
}

