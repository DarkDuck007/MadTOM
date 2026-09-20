using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using MadTOM.Theming;

namespace MADTOM.Console.Theming;

public sealed class ConsoleThemeManager : IDisposable
{
    private static readonly Lazy<ConsoleThemeManager> _lazy = new(() => new ConsoleThemeManager());
    public static ConsoleThemeManager Instance => _lazy.Value;

    private readonly Dictionary<string, ThemePaletteModel> _registeredPalettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Color> _activeColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrush> _activeBrushes = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private int _reloadPending;

    public string CurrentTheme { get; private set; } = "default-dark";
    public IReadOnlyList<ThemePaletteModel> AvailablePalettes => _registeredPalettes.Values.ToList();

    public event EventHandler<string>? ThemeChanged;
    public event EventHandler? ThemesCollectionChanged;

    public static string UserThemesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "themes");

    public static string UserConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM");

    public ConsoleThemeManager()
    {
        LoadAllPalettes();
        InitializeWatcher();
        ApplyTheme("default-dark");
    }

    public ThemePaletteModel? GetPalette(string themeName)
    {
        return _registeredPalettes.TryGetValue(themeName, out var p) ? p : null;
    }

    public void ReloadThemes()
    {
        LoadAllPalettes();

        if (_registeredPalettes.ContainsKey(CurrentTheme))
        {
            ApplyTheme(CurrentTheme);
        }

        ThemesCollectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScanThemesDirectory(string directory, bool isUserDir = true)
    {
        ScanDirectoryForThemes(directory, isUserDir);
        ThemesCollectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadAllPalettes()
    {
        LoadEmbeddedPalettes();
        LoadUserThemes();
        EnsureBaselineFallbacks();
    }

    private void LoadEmbeddedPalettes()
    {
        bool foundAny = false;
        try
        {
            var baseUris = new[]
            {
                new Uri("avares://MADTOM.Studio/Assets/Themes/"),
                new Uri("avares://MADTOM.Console/Assets/Themes/"),
                new Uri("avares://MADTOM.Plugins.Telemetry/Assets/Themes/"),
                new Uri("avares://MadTOM/Assets/Themes/")
            };

            foreach (var baseUri in baseUris)
            {
                var assets = AssetLoader.GetAssets(baseUri, null);
                if (assets != null)
                {
                    foreach (var assetUri in assets)
                    {
                        string ext = Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant();
                        if (ext is ".json" or ".yaml" or ".yml")
                        {
                            try
                            {
                                using var stream = AssetLoader.Open(assetUri);
                                using var reader = new StreamReader(stream);
                                string content = reader.ReadToEnd();
                                string fallbackName = Path.GetFileNameWithoutExtension(assetUri.AbsolutePath);
                                var palette = ThemeParser.Parse(content, fallbackName);
                                if (palette != null && !string.IsNullOrEmpty(palette.ThemeName))
                                {
                                    _registeredPalettes[palette.ThemeName] = palette;
                                    foundAny = true;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback for headless environments
        if (!foundAny || _registeredPalettes.Count < 5)
        {
            ScanDirectoryForThemes(Path.Combine(AppContext.BaseDirectory, "Assets", "Themes"));

            string cur = AppContext.BaseDirectory;
            for (int i = 0; i < 5; i++)
            {
                var parent = Directory.GetParent(cur);
                if (parent == null) break;
                cur = parent.FullName;
                string studioThemes = Path.Combine(cur, "src", "Host", "MADTOM.Studio", "Assets", "Themes");
                if (Directory.Exists(studioThemes))
                {
                    ScanDirectoryForThemes(studioThemes);
                    break;
                }
                string consoleThemes = Path.Combine(cur, "src", "Host", "MADTOM.Console", "Assets", "Themes");
                if (Directory.Exists(consoleThemes))
                {
                    ScanDirectoryForThemes(consoleThemes);
                    break;
                }
                string telemetryThemes = Path.Combine(cur, "src", "Plugins", "Telemetry", "MADTOM.Plugins.Telemetry", "Assets", "Themes");
                if (Directory.Exists(telemetryThemes))
                {
                    ScanDirectoryForThemes(telemetryThemes);
                    break;
                }
            }
        }
    }

    private void LoadUserThemes()
    {
        try
        {
            string dir = UserThemesDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            ScanDirectoryForThemes(dir, isUserDir: true);

            string root = UserConfigDirectory;
            if (Directory.Exists(root))
            {
                var themeFiles = Directory.GetFiles(root, "*.theme.*")
                    .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
                foreach (var file in themeFiles)
                {
                    RegisterFileTheme(file, isUserDir: true);
                }
            }
        }
        catch { }
    }

    private void ScanDirectoryForThemes(string directory, bool isUserDir = false)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                RegisterFileTheme(file, isUserDir);
            }
        }
        catch { }
    }

    private void RegisterFileTheme(string filePath, bool isUserDir)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string fallbackName = Path.GetFileNameWithoutExtension(filePath);
            if (fallbackName.EndsWith(".theme", StringComparison.OrdinalIgnoreCase))
            {
                fallbackName = fallbackName[..^6];
            }

            var palette = ThemeParser.Parse(content, fallbackName);
            if (palette != null && !string.IsNullOrEmpty(palette.ThemeName))
            {
                if (isUserDir && string.IsNullOrWhiteSpace(palette.Category))
                {
                    palette.Category = "Custom";
                }
                _registeredPalettes[palette.ThemeName] = palette;
            }
        }
        catch { }
    }

    private void InitializeWatcher()
    {
        try
        {
            string dir = UserThemesDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            FileSystemEventHandler onChange = (_, _) => ScheduleReload();
            RenamedEventHandler onRename = (_, _) => ScheduleReload();

            _watcher.Created += onChange;
            _watcher.Changed += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += onRename;
        }
        catch { }
    }

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
        {
            Task.Delay(250).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _reloadPending, 0);
                ReloadThemes();
            });
        }
    }

    private void EnsureBaselineFallbacks()
    {
        if (!_registeredPalettes.ContainsKey("default-dark"))
        {
            _registeredPalettes["default-dark"] = new ThemePaletteModel
            {
                ThemeName = "default-dark",
                DisplayName = "Default Dark",
                Category = "Dark",
                Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Background"] = "#070a12", ["HeaderBackground"] = "#0b111e", ["SidebarBackground"] = "#0c1220",
                    ["CardBackground"] = "#0c1220", ["CardHoverBackground"] = "#0f172a", ["DarkBase"] = "#04060b",
                    ["Border"] = "#1e293b", ["BorderSubtle"] = "#0f172a", ["TextPrimary"] = "#f8fafc",
                    ["TextSecondary"] = "#94a3b8", ["TextMuted"] = "#64748b", ["Accent"] = "#06b6d4",
                    ["AccentSubtle"] = "#083344", ["AccentText"] = "#22d3ee", ["Healthy"] = "#10b981",
                    ["HealthySubtle"] = "#064e3b", ["Warning"] = "#f59e0b", ["WarningSubtle"] = "#451a03",
                    ["Critical"] = "#f43f5e", ["CriticalSubtle"] = "#4c0519", ["Indigo"] = "#6366f1",
                    ["IndigoSubtle"] = "#1e1b4b", ["Purple"] = "#a855f7", ["PurpleSubtle"] = "#3b0764"
                }
            };
        }

        if (!_registeredPalettes.ContainsKey("pure-light"))
        {
            _registeredPalettes["pure-light"] = new ThemePaletteModel
            {
                ThemeName = "pure-light",
                DisplayName = "Pure Light",
                Category = "Light",
                Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Background"] = "#f8fafc", ["HeaderBackground"] = "#ffffff", ["SidebarBackground"] = "#f1f5f9",
                    ["CardBackground"] = "#ffffff", ["CardHoverBackground"] = "#f1f5f9", ["DarkBase"] = "#e2e8f0",
                    ["Border"] = "#cbd5e1", ["BorderSubtle"] = "#e2e8f0", ["TextPrimary"] = "#0f172a",
                    ["TextSecondary"] = "#475569", ["TextMuted"] = "#94a3b8", ["Accent"] = "#0284c7",
                    ["AccentSubtle"] = "#e0f2fe", ["AccentText"] = "#0369a1", ["Healthy"] = "#16a34a",
                    ["HealthySubtle"] = "#dcfce7", ["Warning"] = "#d97706", ["WarningSubtle"] = "#fef3c7",
                    ["Critical"] = "#dc2626", ["CriticalSubtle"] = "#fee2e2", ["Indigo"] = "#4f46e5",
                    ["IndigoSubtle"] = "#e0e7ff", ["Purple"] = "#9333ea", ["PurpleSubtle"] = "#f3e8ff"
                }
            };
        }
    }

    public void ApplyTheme(string themeName)
    {
        if (!_registeredPalettes.TryGetValue(themeName, out var palette))
        {
            if (!_registeredPalettes.TryGetValue("default-dark", out palette))
            {
                palette = _registeredPalettes.Values.FirstOrDefault();
            }
        }

        if (palette == null) return;

        _activeColors.Clear();
        _activeBrushes.Clear();

        foreach (var (key, hex) in palette.Colors)
        {
            if (Color.TryParse(hex, out var parsedColor))
            {
                _activeColors[key] = parsedColor;
                _activeBrushes[key] = new ImmutableSolidColorBrush(parsedColor);
            }
        }

        CurrentTheme = palette.ThemeName;
        UpdateApplicationResources();
        ThemeChanged?.Invoke(this, palette.ThemeName);
    }

    private void UpdateApplicationResources()
    {
        if (Application.Current?.Resources == null) return;

        var resources = Application.Current.Resources;
        foreach (var (key, color) in _activeColors)
        {
            var brush = new ImmutableSolidColorBrush(color);
            resources[$"AppColor.{key}"] = color;
            resources[$"AppBrush.{key}"] = brush;
            resources[$"Color{key}"] = color;
            resources[$"{key}Brush"] = brush;
        }

        MapResourceAlias(resources, "Background", "AppBg");
        MapResourceAlias(resources, "HeaderBackground", "HeaderBg");
        MapResourceAlias(resources, "SidebarBackground", "SidebarBg");
        MapResourceAlias(resources, "CardBackground", "CardBg");
        MapResourceAlias(resources, "CardHoverBackground", "CardHoverBg");

        if (_activeColors.TryGetValue("Background", out var bg))
        {
            double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            Application.Current.RequestedThemeVariant = luminance > 0.5 ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }

    private void MapResourceAlias(IResourceDictionary resources, string sourceKey, string targetKey)
    {
        if (_activeColors.TryGetValue(sourceKey, out var color))
        {
            resources[$"Color{targetKey}"] = color;
        }
        if (_activeBrushes.TryGetValue(sourceKey, out var brush))
        {
            resources[$"{targetKey}Brush"] = brush;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

