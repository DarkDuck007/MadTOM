using System;

namespace SQUEEZE.Models;

public enum JobStatus
{
    Idle,
    Queued,
    Encoding,
    Paused,
    Completed,
    Failed,
    Uploading,
    Downloading,
    Cancelled
}

public class TranscodeJob
{
    public TranscodeJob Clone() => (TranscodeJob)MemberwiseClone();
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServerJobId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
    public string PresetKey { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Idle;
    public double Progress { get; set; } = 0.0;
    public double Fps { get; set; } = 0.0;
    public double Speed { get; set; } = 0.0;
    public int BitrateKbps { get; set; } = 0;
    public string? EtaText { get; set; }
    public bool HasLiveStats => Status == JobStatus.Encoding || Fps > 0 || Speed > 0;
    public string Container { get; set; } = "mp4";
    public string? Error { get; set; }
    public TranscodePreset? Options { get; set; }
    public bool CanDownload => Status == JobStatus.Completed;
    public bool CanPause => Status == JobStatus.Encoding;
    public bool CanResume => Status == JobStatus.Paused;
    public bool CanCancel => Status is JobStatus.Uploading or JobStatus.Queued or JobStatus.Encoding or JobStatus.Paused or JobStatus.Downloading;
    public string ProgressLabel => Status switch
    {
        JobStatus.Uploading => $"UPLOADING {(int)Progress}%",
        JobStatus.Downloading => $"DOWNLOADING {(int)Progress}%",
        JobStatus.Encoding => $"{(int)Progress}%",
        JobStatus.Queued => "QUEUED",
        JobStatus.Completed => "DONE",
        JobStatus.Failed => "FAILED",
        JobStatus.Cancelled => "CANCELLED",
        JobStatus.Paused => "PAUSED",
        _ => $"{(int)Progress}%"
    };
    public string TargetOutputName => string.IsNullOrWhiteSpace(Name)
        ? "output.mp4"
        : $"{System.IO.Path.GetFileNameWithoutExtension(Name)}_squeezed.{Container}";
    public string? DownloadUrl { get; set; }
}

