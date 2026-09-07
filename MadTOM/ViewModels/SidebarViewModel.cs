using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class SidebarViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;

    [ObservableProperty]
    private string _activeView = "fleet";

    [ObservableProperty]
    private bool _isNodesExpanded = true;

    [ObservableProperty]
    private string? _selectedHostId;

    public ObservableCollection<FleetNodeModel> Nodes { get; } = new();

    public event Action<string>? ViewChangeRequested;
    public event Action<string>? HostSelected;

    public SidebarViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;

        foreach (var node in _telemetryProvider.GetFleetNodes())
        {
            Nodes.Add(node);
        }

        _telemetryProvider.NodeTelemetryUpdated += (s, updatedNode) =>
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Id.Equals(updatedNode.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Nodes[i] = updatedNode;
                    break;
                }
            }
        };
    }

    [RelayCommand]
    public void Navigate(string view)
    {
        ActiveView = view;
        ViewChangeRequested?.Invoke(view);
    }

    [RelayCommand]
    public void SelectHost(string hostId)
    {
        SelectedHostId = hostId;
        HostSelected?.Invoke(hostId);
    }

    [ObservableProperty]
    private bool _isCollapsed;

    public double SidebarWidth => IsCollapsed ? 68.0 : 240.0;

    public bool IsFleetActive => ActiveView.Equals("fleet", StringComparison.OrdinalIgnoreCase);
    public bool IsRadarActive => ActiveView.Equals("radar", StringComparison.OrdinalIgnoreCase);

    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
    }

    partial void OnActiveViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsFleetActive));
        OnPropertyChanged(nameof(IsRadarActive));
    }

    [RelayCommand]
    public void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    [RelayCommand]
    public void ToggleNodesExpanded()
    {
        IsNodesExpanded = !IsNodesExpanded;
    }
}

