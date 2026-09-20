using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Plugins.AndroidToolkit.ViewModels;

public sealed partial class AndroidDeviceItem : ObservableObject
{
    public string SerialNumber { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Status { get; init; } = "Connected";
    public string AndroidVersion { get; init; } = "15";
    public int BatteryPercent { get; init; } = 85;
}

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Coming soon";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private AndroidDeviceItem? _selectedDevice;

    public ObservableCollection<AndroidDeviceItem> Devices { get; } = new();

    public string ModuleTitle => "MADTOM ANDROID TOOLKIT";
    public string ModuleSubtitle => "ADB device management, SMS/MMS archiving, app data backups, and wireless sync.";
    public string ComingSoonMessage => "This module is in active development and will integrate with local Android Debug Bridge daemon instances.";

    public MainViewModel()
    {
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        StatusText = "Coming soon";
    }

    [RelayCommand]
    private void StartBackup()
    {
        StatusText = "Coming soon";
    }
}
