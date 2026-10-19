using System;
using System.IO;
using System.Text.Json;

namespace MADTOM.Plugins.IPCam;

public sealed class IPCamConfig
{
    public string HubAddress { get; set; } = "https://127.0.0.1:7443";
    public string HubFingerprintSha256 { get; set; } = string.Empty;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "IPCam Client";
    public string SelectedCameraId { get; set; } = "0";
    public uint PreferredResolutionWidth { get; set; } = 1280;
    public uint PreferredResolutionHeight { get; set; } = 720;
    public uint PreferredFps { get; set; } = 30;
    public uint PreferredBitrateBps { get; set; } = 6_000_000;
    public string PreferredCodec { get; set; } = "avc";
    public float ZoomRatio { get; set; } = 1.0f;
    public bool AutoStart { get; set; } = false;
    public bool IsPaired { get; set; } = false;
    public string TrustId { get; set; } = string.Empty;

    public static string GetDefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "MADTOM", "ipcam");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static IPCamConfig Load(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<IPCamConfig>(json) ?? new IPCamConfig();
            }
        }
        catch { }
        return new IPCamConfig();
    }

    public void Save(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
