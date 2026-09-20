using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.AudioSync.ViewModels;

public sealed partial class AudioZoneItem : ObservableObject
{
    public string ZoneName { get; init; } = string.Empty;
    public string Protocol { get; init; } = "Snapcast / PulseAudio";
    public string LatencyMs { get; init; } = "18 ms";
    public string BufferHealth { get; init; } = "100% (Locked)";
    public bool IsActive { get; set; } = true;
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _masterClockStatus = "Coming soon";

    [ObservableProperty]
    private string _activeStreamState = "Broadcast Stream: Standby";

    [ObservableProperty]
    private AudioZoneItem? _selectedZone;

    public ObservableCollection<AudioZoneItem> AudioZones { get; } = new();

    public string ModuleTitle => "MADTOM AUDIOSYNC";
    public string ModuleSubtitle => "Precision multi-room audio distribution, clock synchronization & latency buffer calibration.";
    public string ComingSoonMessage => "This module is in active development and will integrate with local audio daemons (PipeWire, PulseAudio, Snapcast).";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void CalibrateClock()
    {
        MasterClockStatus = "Coming soon";
    }
}
