using System;
using System.IO;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class TelemetryCacheSettingsStore
{
    private readonly string _path;
    public TelemetryCacheSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "telemetry-cache.json");
    public Settings Load()
    {
        Settings settings;
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? new(); }
        catch { settings = new(); }
        if (settings.RetentionMinutes is < 1 or > 1440) settings.RetentionMinutes = 60;
        if (settings.LiveLimitMiB is < 1 or > 4096) settings.LiveLimitMiB = 64;
        if (settings.StoredLimitMiB is < 1 or > 4096) settings.StoredLimitMiB = 32;
        if (settings.StoredRetentionSeconds is < 1 or > 86400) settings.StoredRetentionSeconds = 30;
        return settings;
    }
    public int LoadMinutes() => Load().RetentionMinutes;
    public void SaveMinutes(int minutes)
    {
        var settings = Load();
        settings.RetentionMinutes = minutes;
        Save(settings);
    }
    public void Save(Settings settings)
    {
        if (settings.RetentionMinutes is < 1 or > 1440 || settings.LiveLimitMiB is < 1 or > 4096 ||
            settings.StoredLimitMiB is < 1 or > 4096 || settings.StoredRetentionSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(_path + ".tmp", _path, true);
    }
    public sealed class Settings
    {
        public int RetentionMinutes { get; set; } = 60;
        public int LiveLimitMiB { get; set; } = 64;
        public int StoredLimitMiB { get; set; } = 32;
        public int StoredRetentionSeconds { get; set; } = 30;
    }
}
