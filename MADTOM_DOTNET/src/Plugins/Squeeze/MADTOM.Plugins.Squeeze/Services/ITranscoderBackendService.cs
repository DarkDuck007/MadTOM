using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SQUEEZE.Models;

namespace SQUEEZE.Services;

public interface ITranscoderBackendService
{
    string BaseUrl { get; }
    bool IsConnected { get; }

    Task<bool> ConnectAsync(string baseUrl, string? token = null);
    Task<bool> TestConnectionAsync(string baseUrl, string? token = null);
    Task<ServerNodeInfo> GetNodeInfoAsync();
    Task<IReadOnlyList<TranscodePreset>> GetPresetsAsync();
    Task<IReadOnlyList<TranscodeJob>> GetJobsAsync();
    Task<TranscodeJob> AddJobAsync(string name, string meta, string presetKey, string? filePath = null, TranscodePreset? options = null);
    Task<bool> StartJobAsync(Guid jobId);
    Task<bool> PauseJobAsync(Guid jobId);
    Task<bool> ResumeJobAsync(Guid jobId);
    Task<bool> CancelJobAsync(Guid jobId);
    Task ClearCompletedJobsAsync();
    Task<string> GetDownloadUrlAsync(Guid jobId);
    Task DownloadJobAsync(Guid jobId, string destinationPath) => throw new NotSupportedException("Downloads require a real server connection.");
    Task<ServerNodeInfo> RescanHardwareAsync();

    event EventHandler<TranscodeJob>? JobUpdated;
    event EventHandler<TelemetryMetrics>? TelemetryUpdated;
    event EventHandler<string>? StdoutLineReceived;
    event EventHandler<bool>? ConnectionStatusChanged;
}
