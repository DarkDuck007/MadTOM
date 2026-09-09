using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class HostFlightTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;

    [ObservableProperty]
    private string _dropRate = "22 drops/s";

    public ObservableCollection<DropRuleModel> DropRules { get; } = new();

    public HostFlightTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;

        foreach (var rule in _telemetryProvider.GetDropRules("gander-epyc-01"))
        {
            DropRules.Add(rule);
        }
    }
}

