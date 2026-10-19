using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MADTOM.Plugins.NoxAI.Services;

/// <summary>
/// Separate client-side cache and performance configuration for NOX AI Control Center.
/// Follows MADTOM Telemetry's cache style and settings persistence.
/// </summary>
public sealed class NoxCacheSettingsStore
{
    private readonly string _path;

    public NoxCacheSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "noxai-cache.json");

    public Settings Load()
    {
        Settings settings;
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? new();
        }
        catch
        {
            settings = new();
        }

        if (settings.StreamedRetentionMinutes is < 1 or > 1440) settings.StreamedRetentionMinutes = 60;
        if (settings.StreamedContentLimitMiB is < 1 or > 4096) settings.StreamedContentLimitMiB = 64;
        if (settings.StoredQueryLimitMiB is < 1 or > 4096) settings.StoredQueryLimitMiB = 32;
        if (settings.StoredQueryRetentionSeconds is < 1 or > 86400) settings.StoredQueryRetentionSeconds = 30;
        if (settings.LogAndApiCacheLimitMiB is < 16 or > 4096) settings.LogAndApiCacheLimitMiB = 128;
        if (settings.LogAndApiDecodedChunkLimit is < 4 or > 256) settings.LogAndApiDecodedChunkLimit = 32;
        if (settings.MediaCacheLimitMiB is < 16 or > 4096) settings.MediaCacheLimitMiB = 256;
        if (settings.MediaCacheItemLimit is < 50 or > 50000) settings.MediaCacheItemLimit = 500;
        return settings;
    }

    public void Save(Settings settings)
    {
        if (settings.StreamedRetentionMinutes is < 1 or > 1440 ||
            settings.StreamedContentLimitMiB is < 1 or > 4096 ||
            settings.StoredQueryLimitMiB is < 1 or > 4096 ||
            settings.StoredQueryRetentionSeconds is < 1 or > 86400 ||
            settings.LogAndApiCacheLimitMiB is < 16 or > 4096 ||
            settings.LogAndApiDecodedChunkLimit is < 4 or > 256 ||
            settings.MediaCacheLimitMiB is < 16 or > 4096 ||
            settings.MediaCacheItemLimit is < 50 or > 50000)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".tmp", _path, true);
    }

    public sealed class Settings
    {
        public int StreamedRetentionMinutes { get; set; } = 60;
        public int StreamedContentLimitMiB { get; set; } = 64;
        public int StoredQueryLimitMiB { get; set; } = 32;
        public int StoredQueryRetentionSeconds { get; set; } = 30;
        public int LogAndApiCacheLimitMiB { get; set; } = 128;
        public int LogAndApiDecodedChunkLimit { get; set; } = 32;
        public int MediaCacheLimitMiB { get; set; } = 256;
        public int MediaCacheItemLimit { get; set; } = 500;
    }
}
