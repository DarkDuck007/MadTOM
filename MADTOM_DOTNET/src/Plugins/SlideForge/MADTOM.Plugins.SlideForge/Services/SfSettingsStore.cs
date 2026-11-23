using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MADTOM.Plugins.SlideForge.Services;

/// <summary>
/// Persists SlideForge plugin settings (nodes, auth keys, and TLS pin store path)
/// in the MADTOM LocalApplicationData directory, using atomic temp-file writes.
/// </summary>
public sealed class SfSettingsStore
{
    public static string DefaultSettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM");

    public string SettingsPath { get; }
    public string DefaultPinFilePath { get; }

    public SfSettingsStore(string? settingsDir = null)
    {
        string dir = string.IsNullOrWhiteSpace(settingsDir) ? DefaultSettingsDir : settingsDir;
        SettingsPath = Path.Combine(dir, "slideforge-settings.json");
        DefaultPinFilePath = Path.Combine(dir, "slideforge-pins.json");
    }

    public Settings Load()
    {
        Settings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings()
                : new Settings();
        }
        catch
        {
            settings = new Settings();
        }

        if (string.IsNullOrWhiteSpace(settings.PinFilePath))
            settings.PinFilePath = DefaultPinFilePath;

        settings.Nodes ??= new List<SfNodeModel>();
        return settings;
    }

    public void Save(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.PinFilePath))
            settings.PinFilePath = DefaultPinFilePath;

        string? dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    public sealed class Settings
    {
        public string PinFilePath { get; set; } = string.Empty;
        public List<SfNodeModel> Nodes { get; set; } = new();
    }
}
