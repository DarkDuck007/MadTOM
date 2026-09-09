using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;

namespace MadTOM.ViewModels;

public partial class FleetNodeCardViewModel : ViewModelBase
{
    [ObservableProperty]
    private FleetNodeModel _node;

    [ObservableProperty]
    private int _hoveredIndex = -1;

    [ObservableProperty]
    private float _hoveredLoad;

    [ObservableProperty]
    private bool _isHovered;

    public event Action<string>? OpenDetailRequested;

    public FleetNodeCardViewModel(FleetNodeModel node)
    {
        _node = node;
    }

    [RelayCommand]
    public void OpenDetail()
    {
        OpenDetailRequested?.Invoke(Node.Id);
    }
}

