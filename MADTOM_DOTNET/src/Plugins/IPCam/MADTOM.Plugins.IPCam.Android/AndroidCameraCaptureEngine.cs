#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using MADTOM.Media.Contracts;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.IPCam.Android;

/// <summary>
/// Android hardware camera capture engine using Camera2 and MediaCodec input Surface.
/// Implements zero-copy camera-to-encoder pipeline and live zoom control.
/// </summary>
public sealed class AndroidCameraCaptureEngine : ICameraCaptureEngine
{
    private readonly Handler _cameraHandler = new Handler(Looper.MainLooper!);
    private TaskCompletionSource? _cameraReady;
    private bool _openingCamera;
    private int _cameraGeneration;
    private readonly Context _context;
    private readonly CameraManager _cameraManager;

    private readonly List<CameraDeviceInfo> _availableCameras = new();
    private CameraDeviceInfo? _selectedCamera;

    private float _zoomRatio = 1.0f;
    private (float Min, float Max, float Step) _limits = (1.0f, 10.0f, 0.1f);
    private bool _isCapturing;
    private CancellationTokenSource? _captureCts;
    private Task? _drainTask;

    private MediaCodec? _encoder;
    private Surface? _encoderSurface;
    private Surface? _previewSurface;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private CaptureRequest.Builder? _previewRequestBuilder;

    public IReadOnlyList<CameraDeviceInfo> AvailableCameras => _availableCameras;
    public CameraDeviceInfo? SelectedCamera => _selectedCamera;
    public float CurrentZoomRatio => _zoomRatio;
    public (float Min, float Max, float Step) ZoomLimits => _limits;
    public bool IsCapturing => _isCapturing;

    public event EventHandler? CamerasChanged;
    public event EventHandler<CameraDeviceInfo>? SelectedCameraChanged;
    public event EventHandler<float>? ZoomChanged;
    public event Action<uint, uint, byte[]>? PreviewFrameArrived;

    public AndroidCameraCaptureEngine(Context context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _cameraManager = (CameraManager)_context.GetSystemService(Context.CameraService)!;
    }

    public void SetPreviewSurface(Surface? surface)
    {
        _previewSurface = surface;
        if (_cameraDevice != null) OnCameraOpened(_cameraDevice);
        else if (surface != null && _selectedCamera != null)
            OpenSelectedCamera();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _availableCameras.Clear();

        try
        {
            var cameraIds = _cameraManager.GetCameraIdList();
            foreach (var camId in cameraIds)
            {
                var chars = _cameraManager.GetCameraCharacteristics(camId);
                var facingObj = chars.Get(CameraCharacteristics.LensFacing);
                string facing = "back";
                string displayName = $"Rear Camera {camId}";

                if (facingObj != null)
                {
                    int facingInt = (int)(Java.Lang.Integer)facingObj;
                    if (facingInt == (int)LensFacing.Front)
                    {
                        facing = "front";
                        displayName = $"Front Selfie Camera {camId}";
                    }
                    else if (facingInt == (int)LensFacing.External)
                    {
                        facing = "external";
                        displayName = $"External Camera {camId}";
                    }
                }

                (float Min, float Max, float Step) zoomLimits = (1.0f, 10.0f, 0.1f);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    var zoomRange = (global::Android.Util.Range?)chars.Get(CameraCharacteristics.ControlZoomRatioRange);
                    if (zoomRange != null)
                    {
                        float min = (float)(Java.Lang.Float)zoomRange.Lower;
                        float max = (float)(Java.Lang.Float)zoomRange.Upper;
                        zoomLimits = (min, max, 0.1f);
                    }
                }

                var supportedRes = new List<(uint Width, uint Height, uint MaxFps)>();
                var map = (StreamConfigurationMap?)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                if (map != null)
                {
                    var sizes = map.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaCodec)));
                    if (sizes != null)
                    {
                        foreach (var sz in sizes)
                        {
                            if (sz.Width >= 640 && sz.Height >= 360)
                            {
                                supportedRes.Add(((uint)sz.Width, (uint)sz.Height, 30));
                            }
                        }
                    }
                }

                if (supportedRes.Count == 0) continue;

                _availableCameras.Add(new CameraDeviceInfo
                {
                    Id = camId,
                    DisplayName = displayName,
                    Facing = facing,
                    ZoomLimits = zoomLimits,
                    SupportedResolutions = supportedRes
                });
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Camera enumeration failed: " + ex.Message, ex);
        }

        _selectedCamera = _availableCameras.FirstOrDefault();
        if (_selectedCamera != null)
        {
            _limits = _selectedCamera.ZoomLimits;
            _zoomRatio = _limits.Min;
        }

        CamerasChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SelectCameraAsync(CameraDeviceInfo camera, CancellationToken cancellationToken = default)
    {
        if (camera == null || camera.Id == _selectedCamera?.Id) return Task.CompletedTask;
        if (_isCapturing) throw new InvalidOperationException("Disconnect before switching cameras.");
        _cameraGeneration++;
        _openingCamera = false;
        _captureSession?.Close();
        _cameraDevice?.Close();
        _cameraDevice = null;

        _selectedCamera = camera;
        _limits = camera.ZoomLimits;
        _zoomRatio = Math.Clamp(_zoomRatio, _limits.Min, _limits.Max);

        if (_previewSurface != null) OpenSelectedCamera();
        SelectedCameraChanged?.Invoke(this, camera);
        ZoomChanged?.Invoke(this, _zoomRatio);
        return Task.CompletedTask;
    }

    public CapabilityReport GetCapabilityReport()
    {
        var report = new CapabilityReport();

        foreach (var cam in _availableCameras.OrderByDescending(c => c.Id == _selectedCamera?.Id))
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
            Name = "HardwareMediaCodec",
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
        var mode = _selectedCamera?.SupportedResolutions
            .OrderBy(m => Math.Abs((long)m.Width - requestedProfile.Width) + Math.Abs((long)m.Height - requestedProfile.Height))
            .FirstOrDefault() ?? (1280u, 720u, 30u);
        bool useHevc = requestedProfile.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        return new StreamProfile
        {
            CameraId = _selectedCamera?.Id ?? requestedProfile.CameraId,
            Codec = useHevc ? "hevc" : "avc",
            Width = mode.Item1,
            Height = mode.Item2,
            Fps = Math.Min(requestedProfile.Fps > 0 ? requestedProfile.Fps : 30, mode.Item3),
            BitrateBps = requestedProfile.BitrateBps > 0 ? requestedProfile.BitrateBps : 6_000_000,
            RateControl = "cbr",
            IdrIntervalMs = requestedProfile.IdrIntervalMs > 0 ? requestedProfile.IdrIntervalMs : 2000
        };
    }

    public Task<float> SetZoomRatioAsync(float ratio, CancellationToken cancellationToken = default)
    {
        float clamped = Math.Clamp(ratio, _limits.Min, _limits.Max);
        float stepped = MathF.Round(clamped / _limits.Step) * _limits.Step;
        _zoomRatio = stepped;

        if (_previewRequestBuilder != null && _captureSession != null)
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    _previewRequestBuilder.Set(CaptureRequest.ControlZoomRatio, new Java.Lang.Float(stepped));
                }
                _captureSession.SetRepeatingRequest(_previewRequestBuilder.Build(), null, null);
            }
            catch { }
        }

        ZoomChanged?.Invoke(this, stepped);
        return Task.FromResult(stepped);
    }

    public async Task StartCaptureAsync(StreamProfile requestedProfile, SrtTarget target, CancellationToken cancellationToken = default)
    {
        if (_isCapturing) return;

        _captureCts = new CancellationTokenSource();
        _isCapturing = true;

        bool useHevc = requestedProfile.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        string mimeType = useHevc ? MediaFormat.MimetypeVideoHevc : MediaFormat.MimetypeVideoAvc;

        var format = MediaFormat.CreateVideoFormat(
            mimeType,
            (int)requestedProfile.Width,
            (int)requestedProfile.Height);

        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, (int)requestedProfile.BitrateBps);
        format.SetInteger(MediaFormat.KeyFrameRate, (int)requestedProfile.Fps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, (int)(requestedProfile.IdrIntervalMs / 1000));

        _encoder = MediaCodec.CreateEncoderByType(mimeType);
        _encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        _encoderSurface = _encoder.CreateInputSurface();
        _encoder.Start();

        _cameraReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_cameraDevice != null) OnCameraOpened(_cameraDevice);
        else OpenSelectedCamera();

        var token = _captureCts.Token;
        _drainTask = Task.Run(async () =>
        {
            var codecType = useHevc ? VideoCodecType.Hevc : VideoCodecType.H264;
            var muxer = new MpegTsMuxer(codecType);
            var bufferInfo = new MediaCodec.BufferInfo();
            using var udpClient = new UdpClient();
            var destination = new System.Net.IPEndPoint((await System.Net.Dns.GetHostAddressesAsync(target.Host, token))[0], (int)target.Port);
            byte[] codecConfig = Array.Empty<byte>();

            while (!token.IsCancellationRequested && _encoder != null)
            {
                try
                {
                    int outputBufferIndex = _encoder.DequeueOutputBuffer(bufferInfo, 10000);
                    if (outputBufferIndex >= 0)
                    {
                        var outputBuffer = _encoder.GetOutputBuffer(outputBufferIndex);
                        if (outputBuffer != null && bufferInfo.Size > 0)
                        {
                            byte[] chunk = new byte[bufferInfo.Size];
                            outputBuffer.Position(bufferInfo.Offset);
                            outputBuffer.Get(chunk);

                            if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                            {
                                codecConfig = chunk;
                                _encoder.ReleaseOutputBuffer(outputBufferIndex, false);
                                continue;
                            }
                            bool isKeyframe = (bufferInfo.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                            long pts90k = bufferInfo.PresentationTimeUs * 9 / 100;

                            if (isKeyframe && codecConfig.Length > 0) chunk = codecConfig.Concat(chunk).ToArray();
                            var tsPackets = muxer.MuxAccessUnit(chunk, pts90k, isKeyframe);

                            if (!string.IsNullOrEmpty(target.Host) && target.Port > 0)
                            {
                                // Seven TS packets per datagram avoids oversized UDP payloads.
                                for (int offset = 0; offset < tsPackets.Length; offset += 1316)
                                    await udpClient.SendAsync(tsPackets.AsMemory(offset, Math.Min(1316, tsPackets.Length - offset)),
                                        destination, token);
                            }
                        }

                        _encoder.ReleaseOutputBuffer(outputBufferIndex, false);
                    }
                }
                catch (System.OperationCanceledException) { break; }
                catch { }
            }
        }, token);

        await _cameraReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private void OpenSelectedCamera()
    {
        if (_openingCamera) return;
        _openingCamera = true;
        try { _cameraManager.OpenCamera(_selectedCamera?.Id ?? "0", new DeviceCallback(this, _cameraGeneration), _cameraHandler); }
        catch { _openingCamera = false; throw; }
    }

    private void OnCameraOpened(CameraDevice camera)
    {
        _cameraDevice = camera;
        try
        {
            _captureSession?.Close();
            var surfaces = new List<Surface>();
            if (_encoderSurface != null) surfaces.Add(_encoderSurface);
            if (_previewSurface != null) surfaces.Add(_previewSurface);

            _previewRequestBuilder = camera.CreateCaptureRequest(CameraTemplate.Record);
            foreach (var s in surfaces)
            {
                _previewRequestBuilder.AddTarget(s);
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                _previewRequestBuilder.Set(CaptureRequest.ControlZoomRatio, new Java.Lang.Float(_zoomRatio));
            }

            camera.CreateCaptureSession(surfaces, new SessionCallback(this), _cameraHandler);
        }
        catch { }
    }

    private void OnSessionConfigured(CameraCaptureSession session)
    {
        _captureSession = session;
        try
        {
            if (_previewRequestBuilder != null)
            {
                session.SetRepeatingRequest(_previewRequestBuilder.Build(), null, _cameraHandler);
                _cameraReady?.TrySetResult();
            }
        }
        catch { }
    }

    private sealed class DeviceCallback : CameraDevice.StateCallback
    {
        private readonly AndroidCameraCaptureEngine _parent;
        private readonly int _generation;
        public DeviceCallback(AndroidCameraCaptureEngine parent, int generation) { _parent = parent; _generation = generation; }
        public override void OnOpened(CameraDevice camera)
        {
            if (_generation != _parent._cameraGeneration) { camera.Close(); return; }
            _parent._openingCamera = false;
            _parent.OnCameraOpened(camera);
        }
        public override void OnDisconnected(CameraDevice camera) => camera.Close();
        public override void OnError(CameraDevice camera, CameraError error) { _parent._cameraReady?.TrySetException(new InvalidOperationException($"Camera error: {error}")); camera.Close(); }
    }

    private sealed class SessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly AndroidCameraCaptureEngine _parent;
        public SessionCallback(AndroidCameraCaptureEngine parent) => _parent = parent;
        public override void OnConfigured(CameraCaptureSession session) => _parent.OnSessionConfigured(session);
        public override void OnConfigureFailed(CameraCaptureSession session) { _parent._cameraReady?.TrySetException(new InvalidOperationException("Camera session configuration failed.")); }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {

        _isCapturing = false;
        _cameraGeneration++;
        _openingCamera = false;
        if (_captureCts != null)
        {
            _captureCts.Cancel();
            if (_drainTask != null)
            {
                try { await _drainTask; } catch { }
            }
            _captureCts.Dispose();
            _captureCts = null;
        }

        try
        {
            _captureSession?.Close();
            _cameraDevice?.Close();
            _encoder?.Stop();
            _encoder?.Release();
            _encoderSurface?.Release();
        }
        catch { }

        _captureSession = null;
        _cameraDevice = null;
        _encoder = null;
        _encoderSurface = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
    }
}
#endif
