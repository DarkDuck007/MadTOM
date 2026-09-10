using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTOM.Models;
using MadTOM.Services;

namespace MadTOM.ViewModels;

public partial class HostLogsTabViewModel : ViewModelBase
{
    private readonly ITelemetryDataProvider _telemetryProvider;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _pauseButtonText = "Pause Stream";

    public ObservableCollection<LogEntryModel> Logs { get; } = new();

    public HostLogsTabViewModel(ITelemetryDataProvider telemetryProvider)
    {
        _telemetryProvider = telemetryProvider;

        Logs.Add(new LogEntryModel { Message = "Remote log collection is not available on this collector.", Source = "UI" });

        _telemetryProvider.LogReceived += (s, entry) =>
        {
            if (IsPaused) return;

            Dispatcher.UIThread.Post(() =>
            {
                Logs.Add(entry);
                if (Logs.Count > 80)
                {
                    Logs.RemoveAt(0);
                }
            });
        };
    }

    [RelayCommand]
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        PauseButtonText = IsPaused ? "Resume Stream" : "Pause Stream";
        _telemetryProvider.PauseLogs(IsPaused);
    }

    [RelayCommand]
    public void Clear()
    {
        Logs.Clear();
        Logs.Add(new LogEntryModel { Message = "// System log buffer cleared", Source = "system" });
    }
}

