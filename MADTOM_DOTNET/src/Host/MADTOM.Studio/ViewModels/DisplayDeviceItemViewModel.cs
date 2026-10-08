using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Console.Hosting;

namespace MADTOM.Console.ViewModels;

public sealed partial class DisplayDeviceItemViewModel : ObservableObject
{
    private readonly IBrightnessService _brightnessService;
    private readonly Action<DisplayDeviceItemViewModel, int, double> _requestLowBrightnessConfirmation;
    private readonly DispatcherTimer _debounceTimer;
    private bool _isSyncingFromSystem;

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

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _debounceTimer.Tick += OnDebounceTimerTick;

        RefreshFromSystem();
    }

    public void RefreshFromSystem()
    {
        _debounceTimer.Stop();
        _isSyncingFromSystem = true;
        try
        {
            int trueRaw = _brightnessService.GetBrightness(Device);
            CurrentBrightness = trueRaw;
            SliderPercent = CurrentPercent;
            OnPropertyChanged(nameof(CurrentPercent));
        }
        finally
        {
            _isSyncingFromSystem = false;
        }
    }

    partial void OnSliderPercentChanged(double value)
    {
        if (_isSyncingFromSystem)
            return;

        // Reset and start debounce timer (approx 1 display frame ~25ms)
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        if (_isSyncingFromSystem)
            return;

        int targetRaw = (int)Math.Round((SliderPercent / 100.0) * MaxBrightness);
        targetRaw = Math.Clamp(targetRaw, 0, MaxBrightness);

        if (targetRaw < MinSafeBrightness)
        {
            // Below 5% safeguard threshold: request confirmation without writing to hardware
            _requestLowBrightnessConfirmation(this, targetRaw, SliderPercent);
        }
        else
        {
            ApplyRawBrightness(targetRaw, updateSlider: false);
        }
    }

    [RelayCommand]
    public void ApplySliderBrightness()
    {
        _debounceTimer.Stop();
        int targetRaw = (int)Math.Round((SliderPercent / 100.0) * MaxBrightness);
        targetRaw = Math.Clamp(targetRaw, 0, MaxBrightness);

        if (targetRaw < MinSafeBrightness)
        {
            _requestLowBrightnessConfirmation(this, targetRaw, SliderPercent);
        }
        else
        {
            ApplyRawBrightness(targetRaw, updateSlider: false);
        }
    }

    [RelayCommand]
    public void SetPreset(object? param)
    {
        if (param != null && double.TryParse(param.ToString(), out double pct))
        {
            _debounceTimer.Stop();
            _isSyncingFromSystem = true;
            try
            {
                SliderPercent = pct;
            }
            finally
            {
                _isSyncingFromSystem = false;
            }

            int targetRaw = (int)Math.Round((pct / 100.0) * MaxBrightness);
            targetRaw = Math.Clamp(targetRaw, 0, MaxBrightness);

            if (targetRaw < MinSafeBrightness)
            {
                _requestLowBrightnessConfirmation(this, targetRaw, pct);
            }
            else
            {
                ApplyRawBrightness(targetRaw, updateSlider: false);
            }
        }
    }

    public void ApplyRawBrightness(int rawValue, bool updateSlider = true)
    {
        int clamped = Math.Clamp(rawValue, 0, MaxBrightness);
        if (_brightnessService.SetBrightness(Device, clamped))
        {
            CurrentBrightness = clamped;
            if (updateSlider)
            {
                _isSyncingFromSystem = true;
                try
                {
                    SliderPercent = CurrentPercent;
                }
                finally
                {
                    _isSyncingFromSystem = false;
                }
            }
            OnPropertyChanged(nameof(CurrentPercent));
        }
    }

    public void RevertSliderToSafe()
    {
        _debounceTimer.Stop();
        _isSyncingFromSystem = true;
        try
        {
            // Revert slider to current brightness or minimum safe
            SliderPercent = Math.Max(CurrentPercent, (MinSafeBrightness * 100.0 / MaxBrightness));
        }
        finally
        {
            _isSyncingFromSystem = false;
        }
    }
}
