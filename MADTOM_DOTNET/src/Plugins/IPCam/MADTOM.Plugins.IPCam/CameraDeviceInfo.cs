using System;
using System.Collections.Generic;

namespace MADTOM.Plugins.IPCam;

/// <summary>
/// Information about a physical or synthetic camera device.
/// </summary>
public sealed record CameraDeviceInfo
{
    public string Id { get; init; } = "0";
    public string DisplayName { get; init; } = "Camera 0";
    public string Facing { get; init; } = "back"; // "back", "front", "external"
    public (float Min, float Max, float Step) ZoomLimits { get; init; } = (1.0f, 10.0f, 0.1f);
    public IReadOnlyList<(uint Width, uint Height, uint MaxFps)> SupportedResolutions { get; init; } = Array.Empty<(uint, uint, uint)>();

    public override string ToString() => $"{DisplayName} ({Facing})";
}
