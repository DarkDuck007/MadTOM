using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class HostProcessesTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly List<ProcessInfoModel> _allProcesses = new();

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _targetHostId = "gander-epyc-01";

    public ObservableCollection<ProcessInfoModel> Processes { get; } = new();

    public event Action<int, string, int, string>? ActionConfirmationRequested;

    public HostProcessesTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;
        LoadProcesses();
    }

    public void SetTargetHost(string hostId)
    {
        TargetHostId = hostId;
        LoadProcesses();
    }

    private void LoadProcesses()
    {
        _allProcesses.Clear();
        foreach (var p in _telemetryProvider.GetProcesses(TargetHostId))
        {
            _allProcesses.Add(p);
        }
        ApplyFilter();
    }

    partial void OnSearchFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Processes.Clear();
        string q = SearchFilter.Trim().ToLowerInvariant();

        var filtered = _allProcesses.Where(p =>
            string.IsNullOrEmpty(q) ||
            p.Name.ToLowerInvariant().Contains(q) ||
            p.User.ToLowerInvariant().Contains(q) ||
            p.Pid.ToString().Contains(q));

        foreach (var p in filtered)
        {
            Processes.Add(p);
        }
    }

    [RelayCommand]
    public void DispatchTerm(ProcessInfoModel proc)
    {
        if (proc != null)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 15, TargetHostId);
        }
    }

    [RelayCommand]
    public void DispatchKill(ProcessInfoModel proc)
    {
        if (proc != null)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 9, TargetHostId);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        LoadProcesses();
        NotificationService.Instance.ShowToast("Polled latest process snapshot via gRPC");
    }
}

