using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.IPCam;

/// <summary>
/// Abstraction for camera frame capture, hardware encoding, and streaming.
/// Implemented by Android Camera2 engine or desktop synthetic simulation engine.
/// </summary>
public interface ICameraCaptureEngine : IAsyncDisposable
{
    IReadOnlyList<CameraDeviceInfo> AvailableCameras { get; }
    CameraDeviceInfo? SelectedCamera { get; }
    float CurrentZoomRatio { get; }
    (float Min, float Max, float Step) ZoomLimits { get; }
    bool IsCapturing { get; }

    event EventHandler? CamerasChanged;
    event EventHandler<CameraDeviceInfo>? SelectedCameraChanged;
    event EventHandler<float>? ZoomChanged;
    event Action<uint, uint, byte[]>? PreviewFrameArrived;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SelectCameraAsync(CameraDeviceInfo camera, CancellationToken cancellationToken = default);
    Task StartCaptureAsync(StreamProfile requestedProfile, SrtTarget target, CancellationToken cancellationToken = default);
    Task StopCaptureAsync(CancellationToken cancellationToken = default);
    Task<float> SetZoomRatioAsync(float ratio, CancellationToken cancellationToken = default);
    StreamProfile GetGrantedProfile(StreamProfile requestedProfile);
    CapabilityReport GetCapabilityReport();
}
