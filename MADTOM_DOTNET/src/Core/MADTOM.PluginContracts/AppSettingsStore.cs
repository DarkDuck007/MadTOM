using System;
using System.IO;
using System.Text.Json;

namespace MADTOM.PluginContracts;

/// <summary>
/// Display mode of the MADTOM Studio navigation sidebar.
/// </summary>
public enum StudioSidebarState
{
    Expanded = 0,
    Collapsed = 1,
    Hidden = 2
}

/// <summary>
/// Persisted user application settings and preferences across sessions.
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "default-dark";
    public string Language { get; set; } = "goose";
    private StudioSidebarState _consoleSidebarState = StudioSidebarState.Expanded;
    public StudioSidebarState ConsoleSidebarState
    {
        get => _consoleSidebarState;
        set
        {
            _consoleSidebarState = value;
            _isConsoleSidebarCollapsed = (value == StudioSidebarState.Collapsed);
        }
    }

    private bool _isConsoleSidebarCollapsed = false;
    public bool IsConsoleSidebarCollapsed
    {
        get => _isConsoleSidebarCollapsed;
        set
        {
            _isConsoleSidebarCollapsed = value;
            if (value && _consoleSidebarState == StudioSidebarState.Expanded)
            {
                _consoleSidebarState = StudioSidebarState.Collapsed;
            }
            else if (!value && _consoleSidebarState == StudioSidebarState.Collapsed)
            {
                _consoleSidebarState = StudioSidebarState.Expanded;
            }
        }
    }

    public bool IsTelemetrySidebarCollapsed { get; set; } = false;
    public int UiScalePercent { get; set; } = 100;
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool ShowTrayIcon { get; set; } = true;

    public bool EnableSidebarExpandedState { get; set; } = true;
    public bool EnableSidebarCollapsedState { get; set; } = true;
    public bool EnableSidebarHiddenState { get; set; } = true;
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
                if (settings != null)
                {
                    if (settings.IsConsoleSidebarCollapsed && settings.ConsoleSidebarState == StudioSidebarState.Expanded)
                    {
                        settings.ConsoleSidebarState = StudioSidebarState.Collapsed;
                    }
                    if (!settings.EnableSidebarExpandedState && !settings.EnableSidebarCollapsedState && !settings.EnableSidebarHiddenState)
                    {
                        settings.EnableSidebarExpandedState = true;
                        settings.EnableSidebarCollapsedState = true;
                        settings.EnableSidebarHiddenState = true;
                    }
                    return settings;
                }
                return new AppSettings();
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

