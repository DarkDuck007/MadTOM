using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQUEEZE.Models;

namespace SQUEEZE.ViewModels;

public partial class AutoConnectPriorityItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _priorityInterface = string.Empty;

    [ObservableProperty]
    private string? _authToken;

    [ObservableProperty]
    private DateTime _lastConnected = DateTime.UtcNow;

    [ObservableProperty]
    private int _priorityRank = 1;

    [ObservableProperty]
    private bool _isAvailable;

    public ObservableCollection<string> AvailableInterfaces { get; } = new();

    public AutoConnectNodePreference ToModel()
    {
        return new AutoConnectNodePreference
        {
            NodeId = NodeId,
            PriorityInterface = PriorityInterface,
            AuthToken = AuthToken,
            LastConnected = LastConnected
        };
    }

    public static AutoConnectPriorityItemViewModel FromModel(AutoConnectNodePreference model, int rank)
    {
        var vm = new AutoConnectPriorityItemViewModel
        {
            NodeId = model.NodeId,
            PriorityInterface = model.PriorityInterface,
            AuthToken = model.AuthToken,
            LastConnected = model.LastConnected,
            PriorityRank = rank
        };
        if (!string.IsNullOrWhiteSpace(model.PriorityInterface))
        {
            vm.AvailableInterfaces.Add(model.PriorityInterface);
        }
        return vm;
    }
}
