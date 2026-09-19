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

    [ObservableProperty]
    private bool _isTwampAvailable;

    public string TwampStatus => IsTwampAvailable ? "ONLINE" : "Unavailable";
    public string TwampTooltipText => $"RFC 5357 TWAMP: {TwampStatus}";

    partial void OnIsTwampAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(TwampStatus));
        OnPropertyChanged(nameof(TwampTooltipText));
    }

    [ObservableProperty]
    private string _zstdSummary = "Unknown";

    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<CompressionDiagnosticRow> _compressionRows = Array.Empty<CompressionDiagnosticRow>();

    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<CompressionDiagnosticRow> _transportCompressionRows = Array.Empty<CompressionDiagnosticRow>();

    public void RefreshCompressionSummary()
    {
        var usage = _telemetryProvider.HistoryCache?.GetUsage(1);
        if (usage != null) ZstdSummary = ClientCompressionSnapshot.FormatBytes(usage.LiveZstdBytes + usage.StoredZstdBytes);
    }

    public void RefreshCompressionDiagnostics()
    {
        var snapshot = _telemetryProvider.GetCompressionDiagnostics();
        ZstdSummary = snapshot.Summary;
        CompressionRows = snapshot.MemoryRows;
        TransportCompressionRows = snapshot.TransportRowsView;
    }

    public ObservableCollection<FleetNodeModel> Nodes { get; } = new();

    public event Action<string>? ViewChangeRequested;
    public event Action<string>? HostSelected;

    public SidebarViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;
        RefreshCompressionDiagnostics();

        foreach (var node in _telemetryProvider.GetFleetNodes())
        {
            Nodes.Add(node);
        }
        UpdateOnlineCount();

        Nodes.CollectionChanged += (_, _) => UpdateOnlineCount();

        _telemetryProvider.NodeTelemetryUpdated += (s, updatedNode) =>
        {
            var existing = Nodes.FirstOrDefault(n => n.Id.Equals(updatedNode.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Nodes.Add(updatedNode);
            }
            else
            {
                existing.Status = updatedNode.Status;
                existing.Twamp = updatedNode.Twamp;
            }
            UpdateOnlineCount();
        };
    }

    private void UpdateOnlineCount()
    {
        OnlineCount = Nodes.Count(n => n.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase) ||
                                       n.Status.Equals("online", StringComparison.OrdinalIgnoreCase));
        IsTwampAvailable = Nodes.Any(n => n.Twamp != null && n.Twamp.Available);
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

