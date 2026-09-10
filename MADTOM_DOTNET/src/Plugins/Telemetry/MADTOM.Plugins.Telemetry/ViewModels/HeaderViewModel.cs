using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Localization;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public sealed record PersonaOption(string Id, string DisplayName);

public partial class HeaderViewModel : ViewModelBase
{
    private readonly ILexiconService _lexiconService;
    private readonly ITelemetryDataProvider _telemetryProvider;

    [ObservableProperty]
    private ClusterTelemetrySummary _clusterSummary = new();

    [ObservableProperty]
    private string _currentPersona = "goose";

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public IReadOnlyList<PersonaOption> AvailablePersonas { get; } = new List<PersonaOption>
    {
        new("goose", "🪿 Goose Farm"),
        new("standard", "🏢 Enterprise"),
        new("feline", "🐱 The Clowder")
    };

    [ObservableProperty]
    private PersonaOption _selectedPersona;

    public event Action? ToggleSidebarCollapseRequested;

    public HeaderViewModel(ILexiconService lexiconService, ITelemetryDataProvider telemetryProvider)
    {
        _lexiconService = lexiconService;
        _telemetryProvider = telemetryProvider;

        ClusterSummary = _telemetryProvider.GetClusterSummary();
 _telemetryProvider.NodeTelemetryUpdated += (_, _) => ClusterSummary = _telemetryProvider.GetClusterSummary();
        CurrentPersona = _lexiconService.CurrentPack;

        _selectedPersona = AvailablePersonas.FirstOrDefault(p => p.Id.Equals(CurrentPersona, StringComparison.OrdinalIgnoreCase))
                           ?? AvailablePersonas[0];

        _lexiconService.LexiconChanged += (s, pack) =>
        {
            CurrentPersona = pack;
            var match = AvailablePersonas.FirstOrDefault(p => p.Id.Equals(pack, StringComparison.OrdinalIgnoreCase));
            if (match != null && match != SelectedPersona)
            {
                SelectedPersona = match;
            }
        };
    }

    partial void OnSelectedPersonaChanged(PersonaOption value)
    {
        if (value != null && !value.Id.Equals(_lexiconService.CurrentPack, StringComparison.OrdinalIgnoreCase))
        {
            SwitchPersona(value.Id);
        }
    }

    [RelayCommand]
    public void SwitchPersona(string persona)
    {
        _lexiconService.LoadLexicon(persona);
        CurrentPersona = persona;
    }

    [RelayCommand]
    public void ToggleSidebarCollapse()
    {
        ToggleSidebarCollapseRequested?.Invoke();
    }
}

