using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using MADTOM.Media.Contracts;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Feed.Engine;

public sealed class FeedSession : IAsyncDisposable
{
    private readonly FeedInfo _info;
    private readonly string _recordingsDir;
    private readonly List<Channel<PreviewFrame>> _previewSubscribers = new();
    public event Action<FeedEvent>? Changed;
    private readonly List<Channel<FeedEvent>> _eventSubscribers = new();
    private readonly object _lock = new();

    private Process? _ffmpegProcess;
    private CancellationTokenSource? _sessionCts;
    private Task? _pipelineTask;
    private bool _isDisposed;

    // Latency tracking
    private long _lastFrameUtcMs;
    private long _lastSourceCaptureUtcMs;
    private ulong _frameSequence;

    public FeedInfo Info => _info;
    public string FeedId => _info.FeedId;
    public FeedState State => _info.State;

    public Func<SetProperty, Task>? PropertySetter { get; set; }

    public FeedSession(FeedInfo info, string baseRecordingsDir)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _recordingsDir = Path.Combine(baseRecordingsDir, _info.FeedId);

    }

    public void UpdateState(FeedState newState, string? error = null)
    {
        lock (_lock)
        {
            if (_info.State == newState && error == null) return;
            _info.State = newState;
            if (error != null) _info.LastError = error;
            else if (newState == FeedState.Live) _info.LastError = string.Empty;

            BroadcastEvent(FeedEvent.Types.EventType.Updated);
        }
    }

    public void UpdateGrantedProfile(StreamProfile granted)
    {
        lock (_lock)
        {
            _info.GrantedProfile = granted;
            BroadcastEvent(FeedEvent.Types.EventType.Updated);
        }
    }

    public void UpdateProperty(string propertyId, double value, string stringValue = "")
    {
        lock (_lock)
        {
            var prop = _info.Properties.FirstOrDefault(p => string.Equals(p.Id, propertyId, StringComparison.OrdinalIgnoreCase));
            if (prop != null)
            {
                prop.Value = value;
                prop.StringValue = stringValue;
            }
            else
            {
                _info.Properties.Add(new Property
                {
                    Id = propertyId,
                    Value = value,
                    StringValue = stringValue,
                    Writable = true,
                    Type = PropType.Double
                });
            }

            BroadcastEvent(FeedEvent.Types.EventType.Updated);
        }
    }

    public void UpdateStats(double fps, uint bitrateBps, int rttMs)
    {
        lock (_lock)
        {
            _info.CurrentFps = fps;
            _info.CurrentBitrateBps = bitrateBps;
            if (rttMs > 0)
            {
                _info.ClockOffsetMs = rttMs / 2;
            }
            BroadcastEvent(FeedEvent.Types.EventType.Updated);
        }
    }

    public void StartIngest(bool useSyntheticSource = false)
    {
        lock (_lock)
        {
            if (_pipelineTask is { IsCompleted: false }) return;
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();

            if (useSyntheticSource || _info.SrtTarget == null)
            {
                _pipelineTask = Task.Run(() => RunSyntheticPipelineAsync(_sessionCts.Token));
            }
            else
            {
                _pipelineTask = Task.Run(() => RunIngestLoopAsync(_sessionCts.Token));
            }
        }
    }

    public ChannelReader<PreviewFrame> SubscribePreview()
    {
        lock (_lock)
        {
            var channel = Channel.CreateBounded<PreviewFrame>(new BoundedChannelOptions(1)
                { FullMode = BoundedChannelFullMode.DropOldest });
            _previewSubscribers.Add(channel);
            return channel.Reader;
        }
    }

    public void UnsubscribePreview(ChannelReader<PreviewFrame> reader)
    {
        lock (_lock)
        {
            foreach (var channel in _previewSubscribers.Where(c => c.Reader == reader)) channel.Writer.TryComplete();
            _previewSubscribers.RemoveAll(c => c.Reader == reader);
        }
    }

    public ChannelReader<FeedEvent> SubscribeEvents()
    {
        lock (_lock)
        {
            var ch = Channel.CreateUnbounded<FeedEvent>();
            _eventSubscribers.Add(ch);
            // Send initial state
            ch.Writer.TryWrite(new FeedEvent
            {
                Type = FeedEvent.Types.EventType.Added,
                Feed = _info.Clone()
            });
            return ch.Reader;
        }
    }

    public void UnsubscribeEvents(ChannelReader<FeedEvent> reader)
    {
        lock (_lock)
        {
            _eventSubscribers.RemoveAll(ch => ch.Reader == reader);
        }
    }

    private void BroadcastEvent(FeedEvent.Types.EventType type)
    {
        lock (_lock)
        {
            var ev = new FeedEvent
            {
                Type = type,
                Feed = _info.Clone()
            };

            Changed?.Invoke(ev);
            for (int i = _eventSubscribers.Count - 1; i >= 0; i--)
            {
                if (!_eventSubscribers[i].Writer.TryWrite(ev))
                {
                    _eventSubscribers.RemoveAt(i);
                }
            }
        }
    }

    public void PushPreviewFrame(uint width, uint height, byte[] bgraData, long sourceCaptureUtcMs = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastFrameUtcMs = now;
        _lastSourceCaptureUtcMs = sourceCaptureUtcMs > 0 ? sourceCaptureUtcMs : now;

        var frame = new PreviewFrame
        {
            FeedId = _info.FeedId,
            Width = width,
            Height = height,
            Stride = width * 4,
            Format = "bgra32",
            Data = ByteString.CopyFrom(bgraData),
            TimestampUtcMs = now,
            SourceCaptureUtcMs = _lastSourceCaptureUtcMs,
            FrameNumber = Interlocked.Increment(ref _frameSequence)
        };

        lock (_lock)
            foreach (var subscriber in _previewSubscribers) subscriber.Writer.TryWrite(frame);
    }

    public (double GlassToGlassMs, double NetworkRttMs, double IngestMs) RecordLatencyTick(long displayedMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double glassToGlass;
        if (displayedMs > 0)
        {
            glassToGlass = Math.Max(0, now - displayedMs);
        }
        else
        {
            long sourceTime = _lastSourceCaptureUtcMs > 0 ? _lastSourceCaptureUtcMs : _lastFrameUtcMs;
            glassToGlass = sourceTime > 0 ? Math.Max(0, now - sourceTime) : 0;
        }

        double ingestMs = _lastFrameUtcMs > 0 ? Math.Max(0, now - _lastFrameUtcMs) : 0;
        double networkRtt = Math.Max(0, _info.ClockOffsetMs * 2);

        return (glassToGlass, networkRtt, ingestMs);
    }

    private async Task RunIngestLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunFfmpegPipelineAsync(ct);
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunFfmpegPipelineAsync(CancellationToken ct)
    {
        UpdateState(FeedState.Waiting);

        string ingestUrl;
        if (string.Equals(_info.SourcePlatform, "android", StringComparison.OrdinalIgnoreCase))
        {
            // Android MPEG-TS UDP datagrams
            ingestUrl = $"udp://0.0.0.0:{_info.SrtTarget.Port}?fifo_size=1000000&overrun_nonfatal=1";
        }
        else
        {
            // SRT caller sources (Desktop IPCam, OBS)
            ingestUrl = $"srt://0.0.0.0:{_info.SrtTarget.Port}?mode=listener";
            if (!string.IsNullOrEmpty(_info.SrtTarget.Passphrase))
            {
                ingestUrl += $"&passphrase={_info.SrtTarget.Passphrase}";
            }
        }

        // Phase 1 only decodes a preview. Recording must not prevent a live feed.
        var ffmpegArgs = $"-hide_banner -nostdin -loglevel warning -filter_threads 1 -threads 2 -analyzeduration 1000000 -probesize 1000000 -i \"{ingestUrl}\" " +
                         "-map 0:v:0 -an -vf scale=640:360 -c:v rawvideo -threads 1 -pix_fmt bgra -f rawvideo -r 15 pipe:1";
        string lastError = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _ffmpegProcess = Process.Start(psi);
            if (_ffmpegProcess == null)
            {
                UpdateState(FeedState.Stalled, "Failed to start ffmpeg process.");
                return;
            }

            // Asynchronously drain stderr to prevent pipe buffer deadlocks
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
                    {
                        var line = await _ffmpegProcess.StandardError.ReadLineAsync(ct);
                        if (line == null) break;
                        lastError = line;
                    }
                }
                catch { }
            }, ct);

            var stdout = _ffmpegProcess.StandardOutput.BaseStream;
            int frameBytes = 640 * 360 * 4;
            var buffer = new byte[frameBytes];
            ulong totalBytes = 0;
            int frameCounter = 0;
            var lastStatTime = DateTime.UtcNow;

            while (!ct.IsCancellationRequested && !_ffmpegProcess.HasExited)
            {
                int read = 0;
                while (read < frameBytes && !ct.IsCancellationRequested)
                {
                    int r = await stdout.ReadAsync(buffer.AsMemory(read, frameBytes - read), ct);
                    if (r <= 0) break;
                    read += r;
                }

                if (read == frameBytes)
                {
                    if (_info.State != FeedState.Live)
                    {
                        _info.IsRecording = false;
                        UpdateState(FeedState.Live);
                    }

                    PushPreviewFrame(640, 360, buffer);
                    totalBytes += (ulong)frameBytes;
                    frameCounter++;

                    var elapsed = (DateTime.UtcNow - lastStatTime).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        _info.CurrentFps = Math.Round(frameCounter / elapsed, 1);
                        _info.CurrentBitrateBps = (uint)((frameBytes * 8 * frameCounter) / elapsed);
                        _info.BytesReceived = totalBytes;
                        frameCounter = 0;
                        lastStatTime = DateTime.UtcNow;
                        BroadcastEvent(FeedEvent.Types.EventType.Updated);
                    }
                }
                else
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateState(FeedState.Stalled, ex.Message);
        }
        finally
        {
            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _info.IsRecording = false;
            UpdateState(ct.IsCancellationRequested ? FeedState.Stopped : FeedState.Stalled,
                ct.IsCancellationRequested ? null : (string.IsNullOrWhiteSpace(lastError) ? "Video input ended." : lastError));
        }
    }

    private async Task RunSyntheticPipelineAsync(CancellationToken ct)
    {
        UpdateState(FeedState.Live);
        _info.IsRecording = false;

        const int width = 640;
        const int height = 360;
        var frameBuffer = new byte[width * height * 4];

        try
        {
            var startUtc = DateTime.UtcNow;
            uint frameIndex = 0;

            while (!ct.IsCancellationRequested)
            {
                frameIndex++;
                RenderSyntheticPattern(frameBuffer, width, height, frameIndex);
                PushPreviewFrame(width, height, frameBuffer);

                await Task.Delay(66, ct); // ~15 fps preview
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _info.IsRecording = false;
            UpdateState(FeedState.Stopped);

        }
    }

    private static void RenderSyntheticPattern(byte[] buffer, int width, int height, uint frameIndex)
    {
        // Generates dynamic test pattern: dark blue gradient background with animated color bar and scanline
        byte r = (byte)((frameIndex * 3) % 256);
        byte g = (byte)((frameIndex * 5) % 256);
        byte b = 180;

        int scanline = (int)((frameIndex * 4) % height);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            bool isScanline = Math.Abs(y - scanline) < 3;

            for (int x = 0; x < width; x++)
            {
                int pixelOffset = rowOffset + (x * 4);
                if (isScanline)
                {
                    buffer[pixelOffset + 0] = 255; // B
                    buffer[pixelOffset + 1] = 255; // G
                    buffer[pixelOffset + 2] = 255; // R
                    buffer[pixelOffset + 3] = 255; // A
                }
                else
                {
                    buffer[pixelOffset + 0] = (byte)(b * (x + 1) / width);
                    buffer[pixelOffset + 1] = (byte)(g * (y + 1) / height);
                    buffer[pixelOffset + 2] = (byte)(r / 2);
                    buffer[pixelOffset + 3] = 255;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _sessionCts?.Cancel();
        try
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }

        if (_pipelineTask != null)
        {
            try { await _pipelineTask; } catch { }
        }

        _sessionCts?.Dispose();
        _ffmpegProcess?.Dispose();
        lock (_lock)
        {
            foreach (var channel in _previewSubscribers) channel.Writer.TryComplete();
            foreach (var channel in _eventSubscribers) channel.Writer.TryComplete();
            _previewSubscribers.Clear();
            _eventSubscribers.Clear();
        }
    }
}

// C# 10 compatibility alias for int64_t
internal static class Int64Extensions { }
