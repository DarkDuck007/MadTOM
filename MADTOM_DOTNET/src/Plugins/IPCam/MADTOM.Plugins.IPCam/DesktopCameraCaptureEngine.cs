using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.IPCam;

/// <summary>
/// Real desktop camera capture and streaming engine.
/// Enumerates physical video devices (V4L2 on Linux), generates live preview frames via FFmpeg,
/// and streams live H.264/HEVC video over SRT or UDP to the MADTOM Hub.
/// </summary>
public sealed class DesktopCameraCaptureEngine : ICameraCaptureEngine
{
    private readonly List<CameraDeviceInfo> _cameras = new();
    private CameraDeviceInfo? _selectedCamera;
    private float _zoomRatio = 1.0f;
    private (float Min, float Max, float Step) _limits = (1.0f, 8.0f, 0.1f);
    private bool _isCapturing;
    private bool _initialized;
    private bool _previewEnabled;
    private bool _disposed;
    public event Action<string>? CaptureError;


    private Process? _activeProcess;
    private CancellationTokenSource? _workerCts;
    private Task? _readTask;
    private readonly object _processLock = new();

    private StreamProfile? _currentRequestedProfile;
    private SrtTarget? _currentTarget;

    public IReadOnlyList<CameraDeviceInfo> AvailableCameras => _cameras;
    public CameraDeviceInfo? SelectedCamera => _selectedCamera;
    public float CurrentZoomRatio => _zoomRatio;
    public (float Min, float Max, float Step) ZoomLimits => _limits;
    public bool IsCapturing => _isCapturing;

    public event EventHandler? CamerasChanged;
    public event EventHandler<CameraDeviceInfo>? SelectedCameraChanged;
    public event EventHandler<float>? ZoomChanged;
    public event Action<uint, uint, byte[]>? PreviewFrameArrived;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        DiscoverCameras();
        _selectedCamera = _cameras.FirstOrDefault();
        if (_selectedCamera != null)
        {
            _limits = _selectedCamera.ZoomLimits;
            _zoomRatio = _limits.Min;
        }

        CamerasChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void DiscoverCameras()
    {
        _cameras.Clear();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var v4lDir = new DirectoryInfo("/sys/class/video4linux");
                if (v4lDir.Exists)
                {
                    var dirs = v4lDir.GetDirectories("video*").OrderBy(d => d.Name).ToList();
                    foreach (var dir in dirs)
                    {
                        var devPath = $"/dev/{dir.Name}";
                        if (!File.Exists(devPath)) continue;

                        string devName = dir.Name;
                        var nameFile = Path.Combine(dir.FullName, "name");
                        if (File.Exists(nameFile))
                        {
                            try { devName = File.ReadAllText(nameFile).Trim(); } catch { }
                        }

                        // Test if device can output video frames
                        if (CanDeviceCapture(devPath))
                        {
                            bool isIr = devName.Contains("IR", StringComparison.OrdinalIgnoreCase);
                            string facing = isIr ? "front" : "front";
                            _cameras.Add(new CameraDeviceInfo
                            {
                                Id = devPath,
                                DisplayName = $"{devName} ({devPath})",
                                Facing = facing,
                                ZoomLimits = (1.0f, 8.0f, 0.1f),
                                SupportedResolutions = new (uint, uint, uint)[]
                                {
                                    (1920, 1080, 30),
                                    (1280, 720, 30),
                                    (640, 480, 30),
                                    (640, 360, 30)
                                }
                            });
                        }
                    }
                }
            }
            catch { }
        }

        // Always add a synthetic fallback camera
        _cameras.Add(new CameraDeviceInfo
        {
            Id = "synthetic",
            DisplayName = "Synthetic Color Bar Test Pattern",
            Facing = "front",
            ZoomLimits = (1.0f, 5.0f, 0.1f),
            SupportedResolutions = new (uint, uint, uint)[]
            {
                (1920, 1080, 30),
                (1280, 720, 30),
                (640, 360, 30)
            }
        });
    }

    // VIDIOC_QUERYCAP inspects capabilities without requesting buffers or starting capture.
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, byte[] data);

    private static bool CanDeviceCapture(string devPath)
    {
        int fd = open(devPath, 0x800); // O_RDONLY | O_NONBLOCK
        if (fd < 0) return false;
        try
        {
            var data = new byte[104];
            if (ioctl(fd, 0x80685600, data) != 0) return false;
            uint caps = BitConverter.ToUInt32(data, 84);
            if ((caps & 0x80000000) != 0) caps = BitConverter.ToUInt32(data, 88);
            return (caps & 1) != 0 && (caps & 0x04000000) != 0;
        }
        finally { close(fd); }
    }

    public Task StartPreviewAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _previewEnabled = true;
        if (!_isCapturing) StartPreviewProcess();
        return Task.CompletedTask;
    }

    public Task StopPreviewAsync()
    {
        _previewEnabled = false;
        if (!_isCapturing) StopCurrentProcess();
        return Task.CompletedTask;
    }

    public Task SelectCameraAsync(CameraDeviceInfo camera, CancellationToken cancellationToken = default)
    {
        if (camera == null || camera.Id == _selectedCamera?.Id) return Task.CompletedTask;

        _selectedCamera = camera;
        _limits = camera.ZoomLimits;
        _zoomRatio = Math.Clamp(_zoomRatio, _limits.Min, _limits.Max);

        SelectedCameraChanged?.Invoke(this, camera);
        ZoomChanged?.Invoke(this, _zoomRatio);

        if (_isCapturing && _currentRequestedProfile != null && _currentTarget != null)
        {
            StartStreamProcess(_currentRequestedProfile, _currentTarget);
        }
        else if (_previewEnabled)
        {
            StartPreviewProcess();
        }

        return Task.CompletedTask;
    }

    public Task<float> SetZoomRatioAsync(float ratio, CancellationToken cancellationToken = default)
    {
        float clamped = Math.Clamp(ratio, _limits.Min, _limits.Max);
        float stepped = MathF.Round(clamped / _limits.Step) * _limits.Step;
        if (Math.Abs(_zoomRatio - stepped) < 0.001f) return Task.FromResult(stepped);
        _zoomRatio = stepped;
        ZoomChanged?.Invoke(this, stepped);

        // Restart active pipeline with new crop/zoom
        if (_isCapturing && _currentRequestedProfile != null && _currentTarget != null)
        {
            StartStreamProcess(_currentRequestedProfile, _currentTarget);
        }
        else if (_previewEnabled)
        {
            StartPreviewProcess();
        }

        return Task.FromResult(stepped);
    }

    public CapabilityReport GetCapabilityReport()
    {
        var report = new CapabilityReport();

        foreach (var cam in _cameras.OrderByDescending(c => c.Id == _selectedCamera?.Id))
        {
            var camProto = new CameraInfo
            {
                CameraId = cam.Id,
                Facing = cam.Facing
            };
            foreach (var mode in cam.SupportedResolutions)
            {
                camProto.SupportedModes.Add(new ResolutionFps
                {
                    Width = mode.Width,
                    Height = mode.Height,
                    MaxFps = mode.MaxFps
                });
            }
            report.Cameras.Add(camProto);
        }

        var enc = new EncoderInfo
        {
            Name = "DesktopFFmpegEncoder",
            IsHardware = false
        };
        enc.SupportedCodecs.Add("avc");
        enc.SupportedCodecs.Add("hevc");
        report.Encoders.Add(enc);

        report.Properties.Add(new Property
        {
            Id = "camera.zoom_ratio",
            Type = PropType.Double,
            Min = _limits.Min,
            Max = _limits.Max,
            Step = _limits.Step,
            Writable = true,
            Unit = "x",
            Value = _zoomRatio
        });

        return report;
    }

    public StreamProfile GetGrantedProfile(StreamProfile requestedProfile)
    {
        var granted = new StreamProfile
        {
            CameraId = string.IsNullOrEmpty(requestedProfile.CameraId) ? (_selectedCamera?.Id ?? "synthetic") : (_selectedCamera?.Id ?? requestedProfile.CameraId),
            Codec = requestedProfile.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "avc",
            Width = requestedProfile.Width > 0 ? (requestedProfile.Width & ~1u) : 1920,
            Height = requestedProfile.Height > 0 ? (requestedProfile.Height & ~1u) : 1080,
            Fps = requestedProfile.Fps > 0 ? Math.Min(requestedProfile.Fps, 60u) : 30u,
            BitrateBps = requestedProfile.BitrateBps > 0 ? requestedProfile.BitrateBps : 6_000_000u,
            RateControl = "cbr",
            IdrIntervalMs = requestedProfile.IdrIntervalMs > 0 ? requestedProfile.IdrIntervalMs : 2000u
        };

        return granted;
    }

    public async Task StartCaptureAsync(StreamProfile requestedProfile, SrtTarget target, CancellationToken cancellationToken = default)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Frame(uint w, uint h, byte[] data) => ready.TrySetResult();
        void Error(string error) => ready.TrySetException(new InvalidOperationException(error));
        PreviewFrameArrived += Frame;
        CaptureError += Error;
        try
        {
            _isCapturing = true;
            _currentRequestedProfile = GetGrantedProfile(requestedProfile);
            _currentTarget = target;
            StartStreamProcess(_currentRequestedProfile, target);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            _isCapturing = false;
            StopCurrentProcess();
            throw;
        }
        finally { PreviewFrameArrived -= Frame; CaptureError -= Error; }
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        _isCapturing = false;
        _currentRequestedProfile = null;
        _currentTarget = null;

        StopCurrentProcess();
        if (_previewEnabled) StartPreviewProcess();
        return Task.CompletedTask;
    }

    private void StartPreviewProcess()
    {
        StopCurrentProcess();

        string inputArgs = GetInputArgs();
        string filter = GetVideoFilter(640, 360);

        // Preview only: output rawvideo BGRA 640x360 @ 15fps to pipe:1
        string args = $"-hide_banner -nostdin -loglevel error -filter_threads 1 -filter_complex_threads 1 {inputArgs} -vf \"{filter}\" -c:v rawvideo -threads 1 -pix_fmt bgra -f rawvideo -r 15 pipe:1";

        StartProcessAndReader(args, 640, 360);
    }

    private void StartStreamProcess(StreamProfile profile, SrtTarget target)
    {
        StopCurrentProcess();

        string inputArgs = GetInputArgs();
        string previewFilter = GetVideoFilter(640, 360);

        string codec = profile.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "libx265" : "libx264";
        string bitrate = profile.BitrateBps.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Build target URL (SRT with caller mode and optional passphrase)
        string targetUrl;
        if (!string.IsNullOrEmpty(target.Passphrase))
        {
            targetUrl = $"srt://{target.Host}:{target.Port}?mode=caller&passphrase={target.Passphrase}";
        }
        else
        {
            targetUrl = $"srt://{target.Host}:{target.Port}?mode=caller";
        }

        // Dual output: Map 0:v encoded to SRT caller, Map 0:v scaled to BGRA pipe:1 for local viewfinder
        string args = $"-hide_banner -nostdin -loglevel error -filter_threads 1 -filter_complex_threads 1 {inputArgs} " +
                      $"-map 0:v -vf \"{GetVideoFilter(profile.Width, profile.Height)}\" -r {profile.Fps} -pix_fmt yuv420p -c:v {codec} -threads 2 -preset ultrafast -tune zerolatency -b:v {bitrate} -g {Math.Max(15, (int)profile.Fps * 2)} -f mpegts \"{targetUrl}\" " +
                      $"-map 0:v -vf \"{previewFilter}\" -c:v rawvideo -threads 1 -pix_fmt bgra -f rawvideo -r 15 pipe:1";

        StartProcessAndReader(args, 640, 360);
    }

    private string GetInputArgs()
    {
        var cam = _selectedCamera ?? _cameras.FirstOrDefault();
        if (cam != null && cam.Id.StartsWith("/dev/video"))
        {
            return $"-f v4l2 -i {cam.Id}";
        }
        return "-re -f lavfi -i testsrc2=size=1280x720:rate=30";
    }

    private string GetVideoFilter(uint targetWidth, uint targetHeight)
    {
        if (_zoomRatio > 1.05f)
        {
            // Apply digital center crop according to zoom ratio
            return FormattableString.Invariant($"crop=in_w/{_zoomRatio:F2}:in_h/{_zoomRatio:F2},scale={targetWidth}:{targetHeight}");
        }
        return $"scale={targetWidth}:{targetHeight}";
    }

    private void StartProcessAndReader(string arguments, uint frameWidth, uint frameHeight)
    {
        lock (_processLock)
        {
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _activeProcess = Process.Start(psi);
                if (_activeProcess == null) throw new InvalidOperationException("FFmpeg did not start.");
                var process = _activeProcess;
                string lastError = string.Empty;

                // Asynchronously drain stderr to avoid deadlocks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && !process.HasExited)
                        {
                            var line = await process.StandardError.ReadLineAsync(token);
                            if (line == null) break;
                            lastError = line;
                        }
                    }
                    catch { }
                }, token);

                var stdout = _activeProcess.StandardOutput.BaseStream;
                int frameBytes = (int)(frameWidth * frameHeight * 4);
                var frameBuffer = new byte[frameBytes];

                _readTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested && !process.HasExited)
                    {
                        try
                        {
                            int read = 0;
                            while (read < frameBytes && !token.IsCancellationRequested)
                            {
                                int r = await stdout.ReadAsync(frameBuffer.AsMemory(read, frameBytes - read), token);
                                if (r <= 0) break;
                                read += r;
                            }

                            if (read == frameBytes)
                            {
                                PreviewFrameArrived?.Invoke(frameWidth, frameHeight, frameBuffer);
                            }
                            else
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { lastError = ex.Message; break; }
                    }
                    if (!token.IsCancellationRequested)
                    {
                        _isCapturing = false;
                        CaptureError?.Invoke(string.IsNullOrEmpty(lastError) ? "FFmpeg stopped without a video frame." : lastError);
                    }
                }, token);
            }
            catch (Exception ex) { _isCapturing = false; CaptureError?.Invoke(ex.Message); throw; }
        }
    }

    private void StopCurrentProcess()
    {
        lock (_processLock)
        {
            if (_workerCts != null)
            {
                try { _workerCts.Cancel(); } catch { }
                _workerCts.Dispose();
                _workerCts = null;
            }

            if (_activeProcess != null)
            {
                try
                {
                    if (!_activeProcess.HasExited)
                    {
                        _activeProcess.Kill(entireProcessTree: true);
                        _activeProcess.WaitForExit(3000);
                    }
                }
                catch { }
                _activeProcess.Dispose();
                _activeProcess = null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _previewEnabled = false;
        _isCapturing = false;
        StopCurrentProcess();
        return ValueTask.CompletedTask;
    }
}
