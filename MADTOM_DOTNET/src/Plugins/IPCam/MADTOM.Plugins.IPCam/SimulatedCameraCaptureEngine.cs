using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.Media.Contracts;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.IPCam;

/// <summary>
/// Simulated cross-platform capture engine for testing, development, and desktop heads.
/// Emulates multiple cameras, zoom ratio control, live preview generation, and MPEG-TS streaming.
/// </summary>
public sealed class SimulatedCameraCaptureEngine : ICameraCaptureEngine
{
    private readonly List<CameraDeviceInfo> _cameras = new()
    {
        new CameraDeviceInfo
        {
            Id = "0",
            DisplayName = "Back Wide Camera (1080p)",
            Facing = "back",
            ZoomLimits = (1.0f, 10.0f, 0.1f),
            SupportedResolutions = new (uint, uint, uint)[]
            {
                (1920, 1080, 30),
                (1280, 720, 60),
                (3840, 2160, 30)
            }
        },
        new CameraDeviceInfo
        {
            Id = "1",
            DisplayName = "Front Selfie Camera (1080p)",
            Facing = "front",
            ZoomLimits = (1.0f, 3.0f, 0.1f),
            SupportedResolutions = new (uint, uint, uint)[]
            {
                (1920, 1080, 30),
                (1280, 720, 30)
            }
        },
        new CameraDeviceInfo
        {
            Id = "2",
            DisplayName = "Back Ultra-Wide Camera (0.5x)",
            Facing = "back",
            ZoomLimits = (0.5f, 5.0f, 0.1f),
            SupportedResolutions = new (uint, uint, uint)[]
            {
                (1920, 1080, 30),
                (1280, 720, 60)
            }
        }
    };

    private CameraDeviceInfo? _selectedCamera;
    private float _zoomRatio = 1.0f;
    private (float Min, float Max, float Step) _limits = (1.0f, 10.0f, 0.1f);
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private bool _isCapturing;

    public IReadOnlyList<CameraDeviceInfo> AvailableCameras => _cameras;
    public CameraDeviceInfo? SelectedCamera => _selectedCamera;
    public float CurrentZoomRatio => _zoomRatio;
    public (float Min, float Max, float Step) ZoomLimits => _limits;
    public bool IsCapturing => _isCapturing;

    public event EventHandler? CamerasChanged;
    public event EventHandler<CameraDeviceInfo>? SelectedCameraChanged;
    public event EventHandler<float>? ZoomChanged;
    public event Action<uint, uint, byte[]>? PreviewFrameArrived;

    public SimulatedCameraCaptureEngine()
    {
        _selectedCamera = _cameras.FirstOrDefault();
        if (_selectedCamera != null)
        {
            _limits = _selectedCamera.ZoomLimits;
            _zoomRatio = _limits.Min;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartSyntheticPreview();
        CamerasChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SelectCameraAsync(CameraDeviceInfo camera, CancellationToken cancellationToken = default)
    {
        if (camera == null) return Task.CompletedTask;

        _selectedCamera = camera;
        _limits = camera.ZoomLimits;
        _zoomRatio = Math.Clamp(_zoomRatio, _limits.Min, _limits.Max);

        SelectedCameraChanged?.Invoke(this, camera);
        ZoomChanged?.Invoke(this, _zoomRatio);
        return Task.CompletedTask;
    }

    public CapabilityReport GetCapabilityReport()
    {
        var report = new CapabilityReport();

        foreach (var cam in _cameras)
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
            Name = "SimulatedHardwareEncoder",
            IsHardware = true
        };
        enc.SupportedCodecs.Add("hevc");
        enc.SupportedCodecs.Add("avc");
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
            CameraId = string.IsNullOrEmpty(requestedProfile.CameraId) ? (_selectedCamera?.Id ?? "0") : requestedProfile.CameraId,
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

    public Task<float> SetZoomRatioAsync(float ratio, CancellationToken cancellationToken = default)
    {
        float clamped = Math.Clamp(ratio, _limits.Min, _limits.Max);
        float stepped = MathF.Round(clamped / _limits.Step) * _limits.Step;
        _zoomRatio = stepped;
        ZoomChanged?.Invoke(this, stepped);
        return Task.FromResult(stepped);
    }

    private void StartSyntheticPreview()
    {
        if (_previewCts != null) return;

        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _previewTask = Task.Run(async () =>
        {
            const uint width = 640;
            const uint height = 360;
            byte[] frameBuffer = new byte[width * height * 4];
            int frameCount = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    GenerateSyntheticPreviewFrame(frameBuffer, width, height, frameCount++);
                    PreviewFrameArrived?.Invoke(width, height, frameBuffer);
                    await Task.Delay(50, token); // ~20 fps preview
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, token);
    }

    private void GenerateSyntheticPreviewFrame(byte[] buffer, uint width, uint height, int frameIndex)
    {
        // Simple test pattern with animated color bar and scanline
        int barWidth = (int)width / 6;
        int animOffset = (frameIndex * 4) % (int)width;

        for (int y = 0; y < (int)height; y++)
        {
            int rowStart = y * (int)width * 4;
            for (int x = 0; x < (int)width; x++)
            {
                int pixelIdx = rowStart + (x * 4);
                int colIndex = (x / barWidth) % 6;

                byte r = 0, g = 0, b = 0;
                switch (colIndex)
                {
                    case 0: r = 200; g = 50;  b = 50;  break; // Red
                    case 1: r = 50;  g = 180; b = 50;  break; // Green
                    case 2: r = 50;  g = 100; b = 220; break; // Blue
                    case 3: r = 220; g = 180; b = 40;  break; // Yellow
                    case 4: r = 180; g = 50;  b = 200; break; // Magenta
                    case 5: r = 50;  g = 200; b = 200; break; // Cyan
                }

                // Add scanline / moving bar highlight
                if (Math.Abs(x - animOffset) < 12)
                {
                    r = (byte)Math.Min(255, r + 60);
                    g = (byte)Math.Min(255, g + 60);
                    b = (byte)Math.Min(255, b + 60);
                }

                // Subtle grid
                if (x % 32 == 0 || y % 32 == 0)
                {
                    r = (byte)(r * 3 / 4);
                    g = (byte)(g * 3 / 4);
                    b = (byte)(b * 3 / 4);
                }

                buffer[pixelIdx + 0] = b;     // Blue
                buffer[pixelIdx + 1] = g;     // Green
                buffer[pixelIdx + 2] = r;     // Red
                buffer[pixelIdx + 3] = 255;   // Alpha
            }
        }
    }

    public Task StartCaptureAsync(StreamProfile requestedProfile, SrtTarget target, CancellationToken cancellationToken = default)
    {
        if (_isCapturing) return Task.CompletedTask;

        _captureCts = new CancellationTokenSource();
        _isCapturing = true;

        var token = _captureCts.Token;
        _captureTask = Task.Run(async () =>
        {
            var codecType = requestedProfile.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                ? VideoCodecType.Hevc
                : VideoCodecType.H264;
            var muxer = new MpegTsMuxer(codecType);
            using var udpClient = new UdpClient();

            long pts = 0;
            const long ptsStep = 90000 / 30; // 30 fps in 90kHz clock
            int frameIndex = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool isKey = (frameIndex % 30 == 0);
                    byte[] syntheticNal = new byte[512];
                    Random.Shared.NextBytes(syntheticNal);
                    syntheticNal[0] = 0x00;
                    syntheticNal[1] = 0x00;
                    syntheticNal[2] = 0x00;
                    syntheticNal[3] = 0x01;
                    syntheticNal[4] = isKey ? (byte)0x26 : (byte)0x01;

                    var tsPackets = muxer.MuxAccessUnit(syntheticNal, pts, isKey);
                    pts += ptsStep;
                    frameIndex++;

                    if (!string.IsNullOrEmpty(target.Host) && target.Port > 0)
                    {
                        try
                        {
                            await udpClient.SendAsync(tsPackets, tsPackets.Length, target.Host, (int)target.Port);
                        }
                        catch { }
                    }

                    await Task.Delay(33, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, token);

        return Task.CompletedTask;
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing) return;

        _isCapturing = false;
        if (_captureCts != null)
        {
            _captureCts.Cancel();
            if (_captureTask != null)
            {
                try { await _captureTask; } catch { }
            }
            _captureCts.Dispose();
            _captureCts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        if (_previewCts != null)
        {
            _previewCts.Cancel();
            if (_previewTask != null)
            {
                try { await _previewTask; } catch { }
            }
            _previewCts.Dispose();
            _previewCts = null;
        }
    }
}
