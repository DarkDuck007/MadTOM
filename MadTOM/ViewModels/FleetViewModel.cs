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
    private readonly List<FleetNodeCardViewModel> _allCards = new();

    [ObservableProperty]
    private string _filterRole = "all"; // all, baremetal, vm

    public bool IsAllSelected => FilterRole.Equals("all", StringComparison.OrdinalIgnoreCase);
    public bool IsBaremetalSelected => FilterRole.Equals("baremetal", StringComparison.OrdinalIgnoreCase);
    public bool IsVmSelected => FilterRole.Equals("vm", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<FleetNodeCardViewModel> Cards { get; } = new();

    public event Action<string>? OpenHostDetailRequested;

    public FleetViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;

        foreach (var node in _telemetryProvider.GetFleetNodes())
        {
            var cardVm = new FleetNodeCardViewModel(node);
            cardVm.OpenDetailRequested += (hostId) => OpenHostDetailRequested?.Invoke(hostId);
            _allCards.Add(cardVm);
        }

        _telemetryProvider.NodeTelemetryUpdated += (s, updated) =>
        {
            var card = _allCards.FirstOrDefault(c => c.Node.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                card.Node = updated;
            }
        };

        ApplyFilter();
    }

    partial void OnFilterRoleChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsBaremetalSelected));
        OnPropertyChanged(nameof(IsVmSelected));
        ApplyFilter();
    }
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void SetFilter(string role)
    {
        FilterRole = role;
    }

    public void ApplyFilter()
    {
        Cards.Clear();
        string query = SearchText.Trim().ToLowerInvariant();

        var filtered = _allCards.Where(card =>
        {
            bool matchRole = FilterRole switch
            {
                "baremetal" => card.Node.Role.Equals("baremetal", StringComparison.OrdinalIgnoreCase),
                "vm" => card.Node.Role.Equals("vm", StringComparison.OrdinalIgnoreCase),
                _ => true
            };

            if (!matchRole) return false;

            if (string.IsNullOrEmpty(query)) return true;

            return card.Node.Id.ToLowerInvariant().Contains(query) ||
                   card.Node.Ip.ToLowerInvariant().Contains(query) ||
                   card.Node.Cores.ToString().Contains(query);
        });

        foreach (var c in filtered)
        {
            Cards.Add(c);
        }
    }
}

