using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQUEEZE.Models;

namespace SQUEEZE.Services;

public class MockTranscoderBackendService : ITranscoderBackendService
{
    private readonly List<TranscodeJob> _jobs = new();
    private readonly ServerNodeInfo _nodeInfo = new()
    {
        IsOnline = true,
        NodeName = "NODE_01",
        LanAddress = "192.168.1.140:8080",
        HardwareEncoders = new List<string> { "RTX 4070" },
        HardwareStatus = "RTX 4070 [NVENC IDLE]"
    };
    private readonly Random _random = new();

    private Timer? _encodeTimer;
    private Guid? _activeEncodingJobId;
    private long _currentFrame = 0;
    private const long TotalFrames = 5000;

    public string BaseUrl { get; private set; } = "http://127.0.0.1:8080";
    public bool IsConnected { get; private set; } = true;

    public event EventHandler<TranscodeJob>? JobUpdated;
    public event EventHandler<TelemetryMetrics>? TelemetryUpdated;
    public event EventHandler<string>? StdoutLineReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public Task<bool> ConnectAsync(string baseUrl, string? token = null)
    {
        BaseUrl = baseUrl;
        IsConnected = true;
        ConnectionStatusChanged?.Invoke(this, true);
        return Task.FromResult(true);
    }

    public Task<bool> TestConnectionAsync(string baseUrl, string? token = null)
    {
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TranscodePreset>> GetPresetsAsync()
    {
        return Task.FromResult<IReadOnlyList<TranscodePreset>>(new List<TranscodePreset>());
    }

    public MockTranscoderBackendService()
    {
        InitializeSampleJobs();
    }

    private void InitializeSampleJobs()
    {
        _jobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            Name = "raw_footage_4k.mkv",
            Meta = "3840x2160 • 59.94fps • 4.12 GB",
            PresetKey = "fast1080",
            Status = JobStatus.Idle,
            Progress = 0.0
        });

        _jobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            Name = "interview_a_roll.mov",
            Meta = "1920x1080 • 29.97fps • 1.85 GB",
            PresetKey = "hevc",
            Status = JobStatus.Queued,
            Progress = 0.0
        });

        _jobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            Name = "promo_bumper.mp4",
            Meta = "1920x1080 • 60.00fps • 320 MB",
            PresetKey = "discord",
            Status = JobStatus.Completed,
            Progress = 100.0,
            DownloadUrl = "http://192.168.1.140:8080/api/v1/jobs/promo_bumper_squeezed.mp4"
        });
    }

    public Task<ServerNodeInfo> GetNodeInfoAsync()
    {
        return Task.FromResult(_nodeInfo);
    }

    public Task<ServerNodeInfo> RescanHardwareAsync()
    {
        _nodeInfo.HardwareStatus = "active_hardware_verified (Mock Rescanned)";
        return Task.FromResult(_nodeInfo);
    }

    public Task<IReadOnlyList<TranscodeJob>> GetJobsAsync()
    {
        return Task.FromResult<IReadOnlyList<TranscodeJob>>(_jobs.ToList());
    }

    public Task<TranscodeJob> AddJobAsync(string name, string meta, string presetKey, string? filePath = null, TranscodePreset? options = null)
    {
        var job = new TranscodeJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            Meta = meta,
            PresetKey = presetKey,
            Status = JobStatus.Queued,
            Progress = 0.0
        };
        _jobs.Add(job);
        JobUpdated?.Invoke(this, job);
        return Task.FromResult(job);
    }

    public Task<bool> StartJobAsync(Guid jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return Task.FromResult(false);

        // If another job is encoding, pause it
        if (_activeEncodingJobId.HasValue && _activeEncodingJobId.Value != jobId)
        {
            PauseJobAsync(_activeEncodingJobId.Value);
        }

        _activeEncodingJobId = jobId;
        job.Status = JobStatus.Encoding;
        JobUpdated?.Invoke(this, job);

        StdoutLineReceived?.Invoke(this, $"[SYSTEM] Starting FFmpeg process for job: {job.Name}");

        if (_encodeTimer == null)
        {
            _encodeTimer = new Timer(OnEncodeTick, null, 100, 250);
        }

        return Task.FromResult(true);
    }

    private void OnEncodeTick(object? state)
    {
        if (!_activeEncodingJobId.HasValue) return;

        var job = _jobs.FirstOrDefault(j => j.Id == _activeEncodingJobId.Value);
        if (job == null || job.Status != JobStatus.Encoding) return;

        _currentFrame += _random.Next(105, 125);
        if (_currentFrame >= TotalFrames)
        {
            _currentFrame = TotalFrames;
            job.Progress = 100.0;
            job.Status = JobStatus.Completed;
            job.DownloadUrl = $"http://192.168.1.140:8080/api/v1/jobs/{job.Id}/download";
            _activeEncodingJobId = null;

            _encodeTimer?.Dispose();
            _encodeTimer = null;

            JobUpdated?.Invoke(this, job);
            StdoutLineReceived?.Invoke(this, $"[SSE] Final completion reached for {job.Name}. Squeezed output ready for download.");

            TelemetryUpdated?.Invoke(this, new TelemetryMetrics
            {
                CurrentFrame = TotalFrames,
                TotalFrames = TotalFrames,
                Fps = 0,
                SpeedFactor = 0,
                BitrateKbps = 0,
                Eta = TimeSpan.Zero,
                StatusText = "COMPLETE"
            });

            // Auto-advance to next queued job
            var next = _jobs.FirstOrDefault(j => j.Status == JobStatus.Queued);
            if (next != null)
            {
                _currentFrame = 0;
                StartJobAsync(next.Id);
            }
            return;
        }

        job.Progress = Math.Min(99.9, (_currentFrame / (double)TotalFrames) * 100.0);
        var fps = 115.0 + _random.NextDouble() * 15.0;
        var bitrate = _random.Next(4200, 5000);
        var speed = 2.1 + _random.NextDouble() * 0.3;
        var remainingFrames = TotalFrames - _currentFrame;
        var remainingSeconds = Math.Max(0, (int)(remainingFrames / fps));

        var metrics = new TelemetryMetrics
        {
            CurrentFrame = _currentFrame,
            TotalFrames = TotalFrames,
            Fps = Math.Round(fps, 1),
            SpeedFactor = Math.Round(speed, 2),
            BitrateKbps = bitrate,
            Eta = TimeSpan.FromSeconds(remainingSeconds),
            StatusText = $"ENCODING ({job.Name})"
        };

        job.Fps = Math.Round(fps, 1);
        job.Speed = Math.Round(speed, 2);
        job.BitrateKbps = bitrate;
        job.EtaText = metrics.Eta.ToString(@"hh\:mm\:ss");

        JobUpdated?.Invoke(this, job);
        TelemetryUpdated?.Invoke(this, metrics);

        var timeStr = metrics.Eta.ToString(@"hh\:mm\:ss");
        StdoutLineReceived?.Invoke(this, $"[SSE] frame={_currentFrame} fps={metrics.Fps} q=22.0 size={_currentFrame * 18 / 1024}MB time={timeStr}");
    }

    public Task<bool> PauseJobAsync(Guid jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return Task.FromResult(false);

        if (_activeEncodingJobId == jobId)
        {
            _encodeTimer?.Dispose();
            _encodeTimer = null;
            _activeEncodingJobId = null;
        }

        if (job.Status == JobStatus.Encoding)
        {
            job.Status = JobStatus.Paused;
            JobUpdated?.Invoke(this, job);
            StdoutLineReceived?.Invoke(this, $"[SYSTEM] Paused transcoding job: {job.Name}");

            TelemetryUpdated?.Invoke(this, new TelemetryMetrics
            {
                CurrentFrame = _currentFrame,
                TotalFrames = TotalFrames,
                Fps = 0,
                SpeedFactor = 0,
                BitrateKbps = 0,
                Eta = TimeSpan.Zero,
                StatusText = "PAUSED"
            });
        }

        return Task.FromResult(true);
    }

    public Task<bool> ResumeJobAsync(Guid jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return Task.FromResult(false);

        if (job.Status == JobStatus.Paused)
        {
            job.Status = JobStatus.Encoding;
            _activeEncodingJobId = jobId;
            _encodeTimer?.Dispose();
            _encodeTimer = new Timer(OnEncodeTick, null, 100, 200);
            JobUpdated?.Invoke(this, job);
            StdoutLineReceived?.Invoke(this, $"[SYSTEM] Resumed transcoding job: {job.Name}");
        }

        return Task.FromResult(true);
    }

    public Task<bool> CancelJobAsync(Guid jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return Task.FromResult(false);

        if (_activeEncodingJobId == jobId)
        {
            _encodeTimer?.Dispose();
            _encodeTimer = null;
            _activeEncodingJobId = null;
        }

        job.Status = JobStatus.Cancelled;
        JobUpdated?.Invoke(this, job);
        StdoutLineReceived?.Invoke(this, $"[SYSTEM] Cancelled job: {job.Name}");
        return Task.FromResult(true);
    }

    public Task ClearCompletedJobsAsync()
    {
        _jobs.RemoveAll(j => j.Status == JobStatus.Completed);
        return Task.CompletedTask;
    }

    public Task<string> GetDownloadUrlAsync(Guid jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        return Task.FromResult(job?.DownloadUrl ?? $"http://192.168.1.140:8080/api/v1/jobs/{jobId}/download");
    }
}

