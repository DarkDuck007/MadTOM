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
    private const int PageSize = 100;

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private string _targetHostId = "";

    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageCount = 1;
    public bool CanPreviousPage => Page > 1;
    public bool CanNextPage => Page < PageCount;

    public bool CanSendSignals => _telemetryProvider is not CollectorTelemetryDataProvider && TargetHostId != "all";
    public ObservableCollection<ProcessInfoModel> Processes { get; } = new();

    public event Action<int, string, int, string>? ActionConfirmationRequested;

    public HostProcessesTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;
        LoadProcesses();
        _telemetryProvider.NodeTelemetryUpdated += (_, node) => { if (TargetHostId == "all" || node.Id == TargetHostId) LoadProcesses(); };
    }

    public void SetTargetHost(string hostId)
    {
        TargetHostId = hostId;
        OnPropertyChanged(nameof(CanSendSignals));
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

        var list = filtered.ToList();
        PageCount = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        Page = Math.Clamp(Page, 1, PageCount);
        foreach (var p in list.Skip((Page - 1) * PageSize).Take(PageSize))
        {
            Processes.Add(p);
        }
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    partial void OnPageChanged(int value) => ApplyFilter();

    [RelayCommand] public void PreviousPage() { if (CanPreviousPage) Page--; }
    [RelayCommand] public void NextPage() { if (CanNextPage) Page++; }

    [RelayCommand]
    public void DispatchTerm(ProcessInfoModel proc)
    {
        if (proc != null && CanSendSignals)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 15, TargetHostId);
        }
    }

    [RelayCommand]
    public void DispatchKill(ProcessInfoModel proc)
    {
        if (proc != null && CanSendSignals)
        {
            ActionConfirmationRequested?.Invoke(proc.Pid, proc.Name, 9, TargetHostId);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        LoadProcesses();
        NotificationService.Instance.ShowToast("Showing latest received process snapshot");
    }
}
