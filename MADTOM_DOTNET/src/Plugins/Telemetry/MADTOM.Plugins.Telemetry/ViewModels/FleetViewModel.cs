using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class FleetViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;
    private readonly NodeGroupStore _nodeGroupStore;
    private readonly List<FleetNodeCardViewModel> _allCards = new();

    [ObservableProperty]
    private string _selectedGroup = "All";

    public ObservableCollection<string> AvailableGroups { get; } = new() { "All" };

    // Backward-compatibility alias
    public string FilterRole
    {
        get => SelectedGroup;
        set => SelectedGroup = value;
    }

    public bool IsAllSelected => SelectedGroup.Equals("All", StringComparison.OrdinalIgnoreCase);
    public bool IsBaremetalSelected => SelectedGroup.Equals("baremetal", StringComparison.OrdinalIgnoreCase);
    public bool IsVmSelected => SelectedGroup.Equals("vm", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<FleetNodeCardViewModel> Cards { get; } = new();

    public event Action<string>? OpenHostDetailRequested;
    public event Action? OpenCollectorSettingsRequested;
    public event Action<FleetNodeModel>? ConfigureNodeRequested;

    public FleetViewModel(ITelemetryDataProvider telemetryProvider, NodeGroupStore? nodeGroupStore = null)
    {
        _telemetryProvider = telemetryProvider;
        _nodeGroupStore = nodeGroupStore ?? new NodeGroupStore();

        foreach (var node in _telemetryProvider.GetFleetNodes())
        {
            node.GroupName = _nodeGroupStore.GetGroup(node.Id);
            var cardVm = new FleetNodeCardViewModel(node);
            cardVm.OpenDetailRequested += (hostId) => OpenHostDetailRequested?.Invoke(hostId);
            cardVm.ConfigureNodeRequested += (n) => ConfigureNodeRequested?.Invoke(n);
            _allCards.Add(cardVm);
        }

        _nodeGroupStore.GroupChanged += (nodeId, newGroup) =>
        {
            var card = _allCards.FirstOrDefault(c => c.Node.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                card.Node.GroupName = newGroup;
            }
            RefreshGroups();
            ApplyFilter();
        };

        _telemetryProvider.NodeTelemetryUpdated += (s, updated) =>
        {
            var card = _allCards.FirstOrDefault(c => c.Node.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                // Preserve the assigned UI group name; telemetry metrics must never overwrite node groups
                updated.GroupName = card.Node.GroupName;
                card.Node = updated;
            }
            else
            {
                updated.GroupName = _nodeGroupStore.GetGroup(updated.Id);
                var newCard = new FleetNodeCardViewModel(updated);
                newCard.OpenDetailRequested += (hostId) => OpenHostDetailRequested?.Invoke(hostId);
                newCard.ConfigureNodeRequested += (n) => ConfigureNodeRequested?.Invoke(n);
                _allCards.Add(newCard);
                RefreshGroups();
            }

            ApplyFilter();
        };

        RefreshGroups();
        ApplyFilter();
    }

    public void RefreshGroups()
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "All" };
        foreach (var card in _allCards)
        {
            if (!string.IsNullOrWhiteSpace(card.Node.GroupName) &&
                !card.Node.GroupName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !card.Node.GroupName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(card.Node.GroupName.Trim());
            }
        }
        foreach (var g in _nodeGroupStore.GetAllGroups())
        {
            if (!string.IsNullOrWhiteSpace(g) &&
                !g.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !g.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(g.Trim());
            }
        }

        foreach (var g in groups)
        {
            if (!AvailableGroups.Contains(g))
                AvailableGroups.Add(g);
        }
        for (int i = AvailableGroups.Count - 1; i >= 0; i--)
        {
            if (!groups.Contains(AvailableGroups[i]))
                AvailableGroups.RemoveAt(i);
        }

        if (!AvailableGroups.Contains(SelectedGroup))
        {
            SelectedGroup = "All";
        }
    }

    [RelayCommand]
    public void OpenCollectorSettings()
    {
        OpenCollectorSettingsRequested?.Invoke();
    }

    partial void OnSelectedGroupChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsBaremetalSelected));
        OnPropertyChanged(nameof(IsVmSelected));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void SetFilterGroup(string group)
    {
        SelectedGroup = group;
    }

    [RelayCommand]
    public void SetFilter(string role)
    {
        SetFilterGroup(role);
    }

    public void ApplyFilter()
    {
        string query = SearchText.Trim().ToLowerInvariant();

        var matching = _allCards.Where(card =>
        {
            bool matchGroup = SelectedGroup.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                              card.Node.GroupName.Equals(SelectedGroup, StringComparison.OrdinalIgnoreCase) ||
                              card.Node.Role.Equals(SelectedGroup, StringComparison.OrdinalIgnoreCase);

            if (!matchGroup) return false;

            if (string.IsNullOrEmpty(query)) return true;

            return card.Node.Id.ToLowerInvariant().Contains(query) ||
                   card.Node.Ip.ToLowerInvariant().Contains(query) ||
                   card.Node.GroupName.ToLowerInvariant().Contains(query) ||
                   card.Node.Cores.ToString().Contains(query);
        }).ToList();

        // In-place synchronization preserving existing Card VM instances and visual container focus
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!matching.Contains(Cards[i]))
            {
                Cards.RemoveAt(i);
            }
        }

        for (int i = 0; i < matching.Count; i++)
        {
            var target = matching[i];
            int currentIdx = Cards.IndexOf(target);
            if (currentIdx == -1)
            {
                Cards.Insert(Math.Min(i, Cards.Count), target);
            }
            else if (currentIdx != i && i < Cards.Count)
            {
                Cards.Move(currentIdx, i);
            }
        }
    }
}
