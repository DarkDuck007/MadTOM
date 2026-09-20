using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQUEEZE.Models;
using SQUEEZE.Services;

namespace SQUEEZE.ViewModels;

public partial class TelemetryViewModel : ViewModelBase
{
    private readonly ITranscoderBackendService _backendService;

    [ObservableProperty]
    private string _statusText = "STATUS: IDLE";

    [ObservableProperty]
    private string _statusColor = "#E6EDF3";

    [ObservableProperty]
    private string _percentageText = "0.0%";

    [ObservableProperty]
    private double _progressValue = 0.0;

    [ObservableProperty]
    private bool _isFinished = false;

    [ObservableProperty]
    private string _fpsText = "0.0 FPS";

    [ObservableProperty]
    private string _speedText = "0.00x SPEED";

    [ObservableProperty]
    private string _bitrateText = "0 kbps";

    [ObservableProperty]
    private string _etaText = "00:00:00";

    [ObservableProperty]
    private ObservableCollection<string> _stdoutLogs = new();

    public Action? OnDownloadTriggered { get; set; }

    public TelemetryViewModel(ITranscoderBackendService backendService)
    {
        _backendService = backendService;

        _backendService.StdoutLineReceived += OnBackendStdoutLineReceived;

        StdoutLogs.Add("[SYSTEM] Waiting for job submission...");
    }

    public void ShowJob(TranscodeJob? job)
    {
        if (job == null) { Reset(); return; }
        StatusText = $"STATUS: {job.Status.ToString().ToUpperInvariant()}";
        IsFinished = job.Status == JobStatus.Completed;
        StatusColor = job.Status switch
        {
            JobStatus.Completed => "#3FB950",
            JobStatus.Encoding => "#00F0FF",
            JobStatus.Paused => "#F5A623",
            JobStatus.Failed => "#F85149",
            _ => "#E6EDF3"
        };
        ProgressValue = job.Progress;
        PercentageText = $"{job.Progress:F1}%";
        FpsText = $"{job.Fps:F1} FPS";
        SpeedText = $"{job.Speed:F2}x SPEED";
        BitrateText = $"{job.BitrateKbps} kbps";
        EtaText = job.EtaText ?? "--:--:--";
    }

    private void OnBackendStdoutLineReceived(object? sender, string line)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (StdoutLogs.Count > 100)
            {
                StdoutLogs.RemoveAt(0);
            }
            StdoutLogs.Add(line);
        });
    }

    public void Reset()
    {
        StatusText = "STATUS: IDLE";
        StatusColor = "#E6EDF3";
        PercentageText = "0.0%";
        ProgressValue = 0.0;
        IsFinished = false;
        FpsText = "0.0 FPS";
        SpeedText = "0.00x SPEED";
        BitrateText = "0 kbps";
        EtaText = "00:00:00";
    }

    [RelayCommand]
    public void TriggerDownload()
    {
        OnDownloadTriggered?.Invoke();
    }
}

