using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    [ObservableProperty]
    private int _onlineCount;

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
        UpdateOnlineCount();

        Nodes.CollectionChanged += (_, _) => UpdateOnlineCount();

        _telemetryProvider.NodeTelemetryUpdated += (s, updatedNode) =>
        {
            void Apply()
            {
                var existing = Nodes.FirstOrDefault(n => n.Id.Equals(updatedNode.Id, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    Nodes.Add(updatedNode);
                }
                else
                {
                    existing.Status = updatedNode.Status;
                }
                UpdateOnlineCount();
            }

            if (Avalonia.Threading.Dispatcher.UIThread?.CheckAccess() == false)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
            }
            else
            {
                Apply();
            }
        };
    }

    private void UpdateOnlineCount()
    {
        OnlineCount = Nodes.Count(n => n.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase) ||
                                       n.Status.Equals("online", StringComparison.OrdinalIgnoreCase));
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

    private double _uncollapsedWidth = 240.0;

    public double SidebarWidth
    {
        get => IsCollapsed ? 68.0 : _uncollapsedWidth;
        set
        {
            if (!IsCollapsed)
            {
                _uncollapsedWidth = Math.Clamp(value, 140.0, 600.0);
                OnPropertyChanged(nameof(SidebarWidth));
            }
        }
    }

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

