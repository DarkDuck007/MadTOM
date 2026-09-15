using System;
using System.IO;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class TelemetryCacheSettingsStore
{
    private readonly string _path;
    public TelemetryCacheSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "telemetry-cache.json");
    public int LoadMinutes()
    {
        try
        {
            int minutes = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path))?.RetentionMinutes ?? 60;
            return minutes is >= 1 and <= 1440 ? minutes : 60;
        }
        catch { return 60; }
    }
    public void SaveMinutes(int minutes)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Settings { RetentionMinutes = minutes }));
        File.Move(_path + ".tmp", _path, true);
    }
    public sealed class Settings { public int RetentionMinutes { get; set; } = 60; }
}
