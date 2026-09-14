using System;
using System.IO;
using System.Text.Json;

namespace MADTOM.PluginContracts;

/// <summary>
/// Persisted user application settings and preferences across sessions.
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "default-dark";
    public string Language { get; set; } = "goose";
    public bool IsConsoleSidebarCollapsed { get; set; } = false;
    public bool IsTelemetrySidebarCollapsed { get; set; } = false;
    public int UiScalePercent { get; set; } = 100;
}

/// <summary>
/// Thread-safe and atomic file store for <see cref="AppSettings"/>.
/// Saves to ~/.local/share/MADTOM/settings.json (or OS equivalent).
/// </summary>
public static class AppSettingsStore
{
    private static readonly object _lock = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "settings.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load(string? customPath = null)
    {
        string path = customPath ?? DefaultPath;
        lock (_lock)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new AppSettings();
                }

                var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                return settings ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public static void Save(AppSettings settings, string? customPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string path = customPath ?? DefaultPath;
        lock (_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpPath = path + ".tmp";
                string json = JsonSerializer.Serialize(settings, _jsonOptions);
                File.WriteAllText(tmpPath, json);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmpPath, path);
            }
            catch
            {
                // Non-fatal error during persistence
            }
        }
    }
}

