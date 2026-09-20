using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SQUEEZE.Models;

namespace SQUEEZE.ViewModels;

public partial class PresetGroupViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<TranscodePreset> Presets { get; } = new();
    [ObservableProperty] private bool _isExpanded;
    private bool _searching;
    private bool _expandedBeforeSearch;

    public void UpdateSearch(bool searching)
    {
        if (searching == _searching) return;
        if (searching) { _expandedBeforeSearch = IsExpanded; IsExpanded = true; }
        else IsExpanded = _expandedBeforeSearch;
        _searching = searching;
    }
    public PresetGroupViewModel(string name) => Name = name;
}
