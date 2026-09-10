using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class CollectorSettingsViewModel : ViewModelBase
{
    private readonly MultiCollectorManager _manager;
    private readonly ITelemetryDataProvider _dataProvider;

    public ObservableCollection<CollectorEndpointConfig> Endpoints => _manager.ConfiguredEndpoints;
    public ObservableCollection<FleetNodeModel> DetectedNodes { get; } = new();

    [ObservableProperty]
    private string _newName = "Local Collector";

    [ObservableProperty]
    private string _newAddress = "127.0.0.1:50051";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private FleetNodeModel? _selectedNode;

    [ObservableProperty]
    private NodeSettingsViewModel? _selectedNodeSettings;

    public event Action? CloseRequested;

    public CollectorSettingsViewModel(MultiCollectorManager manager, ITelemetryDataProvider dataProvider)
    {
        _manager = manager;
        _dataProvider = dataProvider;

        RefreshNodes();
        _dataProvider.NodeTelemetryUpdated += (_, _) => RefreshNodes();
    }

    public void RefreshNodes()
    {
        var currentNodes = _dataProvider.GetFleetNodes();
        foreach (var node in currentNodes)
        {
            if (!DetectedNodes.Any(n => n.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                DetectedNodes.Add(node);
            }
        }

        if (SelectedNode == null && DetectedNodes.Count > 0)
        {
            SelectedNode = DetectedNodes[0];
        }
    }

    partial void OnSelectedNodeChanged(FleetNodeModel? value)
    {
        if (value != null)
        {
            var client = _manager.GetClientForNode(value);
            SelectedNodeSettings = new NodeSettingsViewModel(value, client);
            _ = SelectedNodeSettings.LoadConfigAsync();
        }
        else
        {
            SelectedNodeSettings = null;
        }
    }

    [RelayCommand]
    public async Task AddCollectorAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAddress))
        {
            StatusMessage = "Collector address cannot be empty.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(NewName) ? "Collector" : NewName.Trim();
        string address = NewAddress.Trim();

        _manager.AddCollector(name, address);
        StatusMessage = $"Added collector: {name} ({address})";
        NewName = string.Empty;
        NewAddress = string.Empty;

        // Trigger immediate fetch
        var nodes = await _manager.FetchAllNodesAsync();
        RefreshNodes();
    }

    [RelayCommand]
    public void RemoveCollector(string address)
    {
        if (string.IsNullOrEmpty(address)) return;
        _manager.RemoveCollector(address);
        StatusMessage = $"Removed collector: {address}";
        RefreshNodes();
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        StatusMessage = "Querying collectors for nodes...";
        var nodes = await _manager.FetchAllNodesAsync();
        RefreshNodes();
        StatusMessage = $"Discovery completed. {nodes.Count} node(s) found.";
    }

    [RelayCommand]
    public void Close()
    {
        CloseRequested?.Invoke();
    }
}

