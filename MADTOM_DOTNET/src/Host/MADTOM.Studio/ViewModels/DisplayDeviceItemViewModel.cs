using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Console.Hosting;

namespace MADTOM.Console.ViewModels;

public sealed partial class DisplayDeviceItemViewModel : ObservableObject
{
    private readonly IBrightnessService _brightnessService;
    private readonly Action<DisplayDeviceItemViewModel, int, double> _requestLowBrightnessConfirmation;

    public DisplayBacklightDevice Device { get; }

    public string Id => Device.Id;
    public string DisplayName => Device.DisplayName;
    public int MaxBrightness => Device.MaxBrightness;
    public int MinSafeBrightness => Device.MinSafeBrightness;

    [ObservableProperty]
    private double _sliderPercent;

    [ObservableProperty]
    private int _currentBrightness;

    public double CurrentPercent => MaxBrightness > 0 ? (CurrentBrightness * 100.0 / MaxBrightness) : 0;

    public DisplayDeviceItemViewModel(
        DisplayBacklightDevice device,
        IBrightnessService brightnessService,
        Action<DisplayDeviceItemViewModel, int, double> requestLowBrightnessConfirmation)
    {
        Device = device;
        _brightnessService = brightnessService;
        _requestLowBrightnessConfirmation = requestLowBrightnessConfirmation;

        _currentBrightness = device.CurrentBrightness;
        _sliderPercent = CurrentPercent;
    }

    [RelayCommand]
    public void ApplySliderBrightness()
    {
        int targetRaw = (int)Math.Round((SliderPercent / 100.0) * MaxBrightness);
        targetRaw = Math.Clamp(targetRaw, 0, MaxBrightness);

        if (targetRaw < MinSafeBrightness)
        {
            // Below 5% safeguard threshold: request confirmation before applying
            _requestLowBrightnessConfirmation(this, targetRaw, SliderPercent);
        }
        else
        {
            ApplyRawBrightness(targetRaw);
        }
    }

    [RelayCommand]
    public void SetPreset(object? param)
    {
        if (param != null && double.TryParse(param.ToString(), out double pct))
        {
            SliderPercent = pct;
            ApplySliderBrightness();
        }
    }

    public void ApplyRawBrightness(int rawValue)
    {
        int clamped = Math.Clamp(rawValue, 0, MaxBrightness);
        if (_brightnessService.SetBrightness(Device, clamped))
        {
            CurrentBrightness = clamped;
            SliderPercent = CurrentPercent;
            OnPropertyChanged(nameof(CurrentPercent));
        }
    }

    public void RevertSliderToSafe()
    {
        // Revert slider to current brightness or minimum safe
        SliderPercent = Math.Max(CurrentPercent, (MinSafeBrightness * 100.0 / MaxBrightness));
    }
}
