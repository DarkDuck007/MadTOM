using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;

namespace MadTOM.Theming;

public sealed class ThemeService : IThemeService, IDisposable
{
    private static readonly Lazy<ThemeService> _lazyInstance = new(() => new ThemeService());
    public static ThemeService Instance => _lazyInstance.Value;

    private readonly Dictionary<string, ThemePaletteModel> _fallbackPalettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThemePaletteModel> _pluginAssetOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThemePaletteModel> _pluginDirOverrides = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Color> _activeColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrush> _activeBrushes = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private int _reloadPending;

    public string CurrentTheme { get; private set; } = "default-dark";

    public IReadOnlyList<string> AvailableThemes
    {
        get
        {
            var keys = new HashSet<string>(_fallbackPalettes.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in _pluginAssetOverrides.Keys) keys.Add(k);
            foreach (var k in _pluginDirOverrides.Keys) keys.Add(k);
            return keys.ToList();
        }
    }

    public IReadOnlyList<ThemePaletteModel> AvailablePalettes =>
        AvailableThemes.Select(t => GetPalette(t)!).Where(p => p != null).ToList();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ThemeChanged;
    public event EventHandler? ThemesCollectionChanged;

    public static string PluginThemesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "themes", "telemetry");

    public static string AlternatePluginThemesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "plugins", "telemetry", "themes");

    public static string UserThemesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "themes");

    public ThemeService()
    {
        LoadAllFallbacks();
        LoadPluginAssetOverrides();
        LoadPluginDirectoryOverrides();
        InitializeWatcher();
        ApplyTheme("default-dark");
    }

    public ThemePaletteModel? GetPalette(string themeName)
    {
        var fallback = GetFallbackPalette(themeName)
            ?? _pluginDirOverrides.GetValueOrDefault(themeName)
            ?? _pluginAssetOverrides.GetValueOrDefault(themeName);

        if (fallback == null)
        {
            return null;
        }

        var effective = fallback.Clone();
        effective.ThemeName = themeName;

        if (_pluginAssetOverrides.TryGetValue(themeName, out var assetOverride))
        {
            effective.Merge(assetOverride);
        }

        if (_pluginDirOverrides.TryGetValue(themeName, out var dirOverride))
        {
            effective.Merge(dirOverride);
        }

        return effective;
    }

    public ThemePaletteModel? GetFallbackPalette(string themeName)
    {
        if (_fallbackPalettes.TryGetValue(themeName, out var p))
            return p;

        EnsureBaselineFallbacks();
        return _fallbackPalettes.TryGetValue(themeName, out p) ? p : null;
    }

    public void RegisterFallbackPalette(ThemePaletteModel palette)
    {
        if (palette == null || string.IsNullOrWhiteSpace(palette.ThemeName)) return;
        _fallbackPalettes[palette.ThemeName] = palette;
    }

    public void ReloadThemes()
    {
        LoadAllFallbacks();
        LoadPluginAssetOverrides();
        LoadPluginDirectoryOverrides();

        ApplyTheme(CurrentTheme);

        OnPropertyChanged(nameof(AvailableThemes));
        OnPropertyChanged(nameof(AvailablePalettes));
        ThemesCollectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScanThemesDirectory(string directory, bool isUserDir = true)
    {
        ScanDirectoryForPalettes(directory, _pluginDirOverrides, isUserDir);
        OnPropertyChanged(nameof(AvailableThemes));
        OnPropertyChanged(nameof(AvailablePalettes));
        ThemesCollectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadAllFallbacks()
    {
        LoadEmbeddedFallbacks();
        EnsureBaselineFallbacks();
    }

    private void LoadEmbeddedFallbacks()
    {
        bool foundAny = false;
        try
        {
            var baseUris = new[]
            {
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
                                    _fallbackPalettes[palette.ThemeName] = palette;
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

        // Fallback for headless environments or tests
        if (!foundAny || _fallbackPalettes.Count < 5)
        {
            ScanDirectoryForPalettes(Path.Combine(AppContext.BaseDirectory, "Assets", "Themes"), _fallbackPalettes);

            string cur = AppContext.BaseDirectory;
            for (int i = 0; i < 5; i++)
            {
                var parent = Directory.GetParent(cur);
                if (parent == null) break;
                cur = parent.FullName;

                string consoleThemes = Path.Combine(cur, "src", "Host", "MADTOM.Console", "Assets", "Themes");
                if (Directory.Exists(consoleThemes))
                {
                    ScanDirectoryForPalettes(consoleThemes, _fallbackPalettes);
                }

                string telemetryThemes = Path.Combine(cur, "src", "Plugins", "Telemetry", "MADTOM.Plugins.Telemetry", "Assets", "Themes");
                if (Directory.Exists(telemetryThemes))
                {
                    ScanDirectoryForPalettes(telemetryThemes, _fallbackPalettes);
                }

                if (_fallbackPalettes.Count >= 5) break;
            }
        }
    }

    private void LoadPluginAssetOverrides()
    {
        try
        {
            var uri = new Uri("avares://MADTOM.Plugins.Telemetry/Assets/Themes/");
            var assets = AssetLoader.GetAssets(uri, null);
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
                            string fileName = Path.GetFileNameWithoutExtension(assetUri.AbsolutePath);
                            var overrideModel = ThemeParser.Parse(content, fileName);
                            if (overrideModel != null)
                            {
                                if (!string.IsNullOrEmpty(overrideModel.ThemeName))
                                {
                                    _pluginAssetOverrides[overrideModel.ThemeName] = overrideModel;
                                }
                                _pluginAssetOverrides[fileName] = overrideModel;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        // Local dev environment scan for plugin asset overrides
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            var parent = Directory.GetParent(cur);
            if (parent == null) break;
            cur = parent.FullName;

            string devDir = Path.Combine(cur, "src", "Plugins", "Telemetry", "MADTOM.Plugins.Telemetry", "Assets", "Themes");
            if (Directory.Exists(devDir))
            {
                ScanDirectoryForPalettes(devDir, _pluginAssetOverrides);
                break;
            }
        }
    }

    private void LoadPluginDirectoryOverrides()
    {
        try
        {
            string dir = PluginThemesDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            ScanDirectoryForPalettes(dir, _pluginDirOverrides, isUserDir: true);

            string altDir = AlternatePluginThemesDirectory;
            if (Directory.Exists(altDir))
            {
                ScanDirectoryForPalettes(altDir, _pluginDirOverrides, isUserDir: true);
            }
        }
        catch { }
    }

    private void ScanDirectoryForPalettes(string directory, Dictionary<string, ThemePaletteModel> targetDict, bool isUserDir = false)
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
                try
                {
                    string content = File.ReadAllText(file);
                    string fallbackName = Path.GetFileNameWithoutExtension(file);
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
                        targetDict[palette.ThemeName] = palette;
                        targetDict[fallbackName] = palette;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void InitializeWatcher()
    {
        try
        {
            string dir = PluginThemesDirectory;
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
        if (!_fallbackPalettes.ContainsKey("default-dark"))
        {
            _fallbackPalettes["default-dark"] = new ThemePaletteModel
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

        if (!_fallbackPalettes.ContainsKey("pure-light"))
        {
            _fallbackPalettes["pure-light"] = new ThemePaletteModel
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

    public Color GetColor(string key, Color fallback = default)
    {
        return _activeColors.TryGetValue(key, out var color) ? color : fallback;
    }

    public IBrush GetBrush(string key)
    {
        if (_activeBrushes.TryGetValue(key, out var brush))
        {
            return brush;
        }

        if (_activeColors.TryGetValue(key, out var color))
        {
            var newBrush = new ImmutableSolidColorBrush(color);
            _activeBrushes[key] = newBrush;
            return newBrush;
        }

        return new ImmutableSolidColorBrush(Colors.Transparent);
    }

    public void ApplyTheme(string themeName)
    {
        ApplyTheme(themeName, null);
    }

    public void ApplyTheme(string themeName, ThemePaletteModel? hostFallback)
    {
        if (hostFallback != null && !string.IsNullOrWhiteSpace(hostFallback.ThemeName))
        {
            _fallbackPalettes[hostFallback.ThemeName] = hostFallback;
        }

        var basePalette = hostFallback 
            ?? GetFallbackPalette(themeName)
            ?? _pluginDirOverrides.GetValueOrDefault(themeName)
            ?? _pluginAssetOverrides.GetValueOrDefault(themeName);

        if (basePalette == null)
        {
            EnsureBaselineFallbacks();
            basePalette = _fallbackPalettes.GetValueOrDefault("default-dark") ?? _fallbackPalettes.Values.FirstOrDefault();
        }

        if (basePalette == null) return;

        var effective = basePalette.Clone();
        effective.ThemeName = themeName;

        // 1. Check for plugin asset override
        if (_pluginAssetOverrides.TryGetValue(themeName, out var assetOverride))
        {
            effective.Merge(assetOverride);
        }

        // 2. Check for plugin directory override (takes highest precedence)
        if (_pluginDirOverrides.TryGetValue(themeName, out var dirOverride))
        {
            effective.Merge(dirOverride);
        }

        _activeColors.Clear();
        _activeBrushes.Clear();

        foreach (var (key, hex) in effective.Colors)
        {
            if (Color.TryParse(hex, out var parsedColor))
            {
                _activeColors[key] = parsedColor;
                _activeBrushes[key] = new ImmutableSolidColorBrush(parsedColor);
            }
        }

        CurrentTheme = effective.ThemeName;

        UpdateApplicationResources();

        OnPropertyChanged(nameof(CurrentTheme));
        ThemeChanged?.Invoke(this, effective.ThemeName);
    }

    public void ImportTheme(string themeName, string jsonOrYamlOrFilePath)
    {
        string content = jsonOrYamlOrFilePath;
        if (File.Exists(jsonOrYamlOrFilePath))
        {
            content = File.ReadAllText(jsonOrYamlOrFilePath);
        }

        var palette = ThemeParser.Parse(content, themeName);
        if (palette == null)
        {
            throw new InvalidOperationException("Failed to parse theme JSON or YAML.");
        }

        palette.ThemeName = string.IsNullOrWhiteSpace(palette.ThemeName) ? themeName : palette.ThemeName;
        _pluginDirOverrides[palette.ThemeName] = palette;
        _pluginDirOverrides[themeName] = palette;

        ApplyTheme(palette.ThemeName);
        OnPropertyChanged(nameof(AvailableThemes));
        OnPropertyChanged(nameof(AvailablePalettes));
        ThemesCollectionChanged?.Invoke(this, EventArgs.Empty);
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
