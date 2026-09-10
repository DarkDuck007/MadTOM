using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;

namespace MadTOM.Theming;

public sealed class ThemeService : IThemeService
{
    private static readonly Lazy<ThemeService> _lazyInstance = new(() => new ThemeService());
    public static ThemeService Instance => _lazyInstance.Value;

    private readonly Dictionary<string, ThemePaletteModel> _registeredPalettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Color> _activeColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrush> _activeBrushes = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentTheme { get; private set; } = "default-dark";
    public IReadOnlyList<string> AvailableThemes => new List<string>(_registeredPalettes.Keys);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? ThemeChanged;

    public ThemeService()
    {
        LoadBuiltinPalettes();
        ApplyTheme("default-dark");
    }

    private void LoadBuiltinPalettes()
    {
        string[] themes = [
            "default-dark",
            "high-contrast",
            "pure-light",
            "paper-white",
            "minimal-mono",
            "anti-bleed-grey",
            "tft-amber-terminal",
            "solarized-dark"
        ];

        foreach (var t in themes)
        {
            LoadEmbeddedPalette(t, $"avares://MADTOM.Plugins.Telemetry/Assets/Themes/{t}.json");
        }
    }

    private void LoadEmbeddedPalette(string name, string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
            if (!AssetLoader.Exists(uri))
            {
                uri = new Uri($"avares://MadTOM/Assets/Themes/{name}.json");
            }

            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var palette = JsonSerializer.Deserialize<ThemePaletteModel>(json, options);
                if (palette != null)
                {
                    _registeredPalettes[name] = palette;
                    return;
                }
            }
        }
        catch
        {
            // Fallback handled below
        }

        if (!_registeredPalettes.ContainsKey(name))
        {
            _registeredPalettes[name] = CreateDefaultFallbackPalette(name);
        }
    }

    private static ThemePaletteModel CreateDefaultFallbackPalette(string name)
    {
        var model = new ThemePaletteModel
        {
            ThemeName = name,
            DisplayName = name switch
            {
                "pure-light" => "Pure Light",
                "paper-white" => "Warm Paper",
                "minimal-mono" => "Minimal Mono",
                "anti-bleed-grey" => "IPS Neutralizer",
                "tft-amber-terminal" => "TFT Amber High-Vis",
                "solarized-dark" => "Solarized Dark",
                "high-contrast" => "High Contrast Dark",
                _ => "Default Dark"
            }
        };

        // Calibrated fallback dictionaries
        switch (name.ToLowerInvariant())
        {
            case "pure-light":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#f8fafc", ["HeaderBackground"] = "#ffffff", ["SidebarBackground"] = "#f1f5f9",
                    ["CardBackground"] = "#ffffff", ["CardHoverBackground"] = "#f1f5f9", ["DarkBase"] = "#e2e8f0",
                    ["Border"] = "#cbd5e1", ["BorderSubtle"] = "#e2e8f0", ["TextPrimary"] = "#0f172a",
                    ["TextSecondary"] = "#475569", ["TextMuted"] = "#94a3b8", ["Accent"] = "#0284c7",
                    ["AccentSubtle"] = "#e0f2fe", ["AccentText"] = "#0369a1", ["Healthy"] = "#16a34a",
                    ["HealthySubtle"] = "#dcfce7", ["Warning"] = "#d97706", ["WarningSubtle"] = "#fef3c7",
                    ["Critical"] = "#dc2626", ["CriticalSubtle"] = "#fee2e2", ["Indigo"] = "#4f46e5",
                    ["IndigoSubtle"] = "#e0e7ff", ["Purple"] = "#9333ea", ["PurpleSubtle"] = "#f3e8ff"
                };
                break;

            case "paper-white":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#fbf9f5", ["HeaderBackground"] = "#f4efe6", ["SidebarBackground"] = "#efe9de",
                    ["CardBackground"] = "#ffffff", ["CardHoverBackground"] = "#f7f3eb", ["DarkBase"] = "#e8dfd1",
                    ["Border"] = "#dcd3c4", ["BorderSubtle"] = "#eae2d5", ["TextPrimary"] = "#2d2820",
                    ["TextSecondary"] = "#615646", ["TextMuted"] = "#8c7f6e", ["Accent"] = "#b45309",
                    ["AccentSubtle"] = "#fef3c7", ["AccentText"] = "#92400e", ["Healthy"] = "#15803d",
                    ["HealthySubtle"] = "#dcfce7", ["Warning"] = "#b45309", ["WarningSubtle"] = "#fef3c7",
                    ["Critical"] = "#b91c1c", ["CriticalSubtle"] = "#fee2e2", ["Indigo"] = "#4338ca",
                    ["IndigoSubtle"] = "#e0e7ff", ["Purple"] = "#7e22ce", ["PurpleSubtle"] = "#f3e8ff"
                };
                break;

            case "minimal-mono":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#121214", ["HeaderBackground"] = "#18181b", ["SidebarBackground"] = "#18181b",
                    ["CardBackground"] = "#1f1f23", ["CardHoverBackground"] = "#27272a", ["DarkBase"] = "#09090b",
                    ["Border"] = "#27272a", ["BorderSubtle"] = "#1e1e22", ["TextPrimary"] = "#fafafa",
                    ["TextSecondary"] = "#a1a1aa", ["TextMuted"] = "#71717a", ["Accent"] = "#e4e4e7",
                    ["AccentSubtle"] = "#27272a", ["AccentText"] = "#ffffff", ["Healthy"] = "#22c55e",
                    ["HealthySubtle"] = "#14532d", ["Warning"] = "#eab308", ["WarningSubtle"] = "#713f12",
                    ["Critical"] = "#ef4444", ["CriticalSubtle"] = "#7f1d1d", ["Indigo"] = "#818cf8",
                    ["IndigoSubtle"] = "#312e81", ["Purple"] = "#c084fc", ["PurpleSubtle"] = "#581c87"
                };
                break;

            case "anti-bleed-grey":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#23272e", ["HeaderBackground"] = "#2a2f38", ["SidebarBackground"] = "#262a32",
                    ["CardBackground"] = "#2c313a", ["CardHoverBackground"] = "#333842", ["DarkBase"] = "#1d2026",
                    ["Border"] = "#434c5e", ["BorderSubtle"] = "#353b45", ["TextPrimary"] = "#f0f2f5",
                    ["TextSecondary"] = "#c0c5ce", ["TextMuted"] = "#8b949e", ["Accent"] = "#00d4ff",
                    ["AccentSubtle"] = "#1b3b4f", ["AccentText"] = "#38e1ff", ["Healthy"] = "#00e676",
                    ["HealthySubtle"] = "#0f3d26", ["Warning"] = "#ffd600", ["WarningSubtle"] = "#473b00",
                    ["Critical"] = "#ff1744", ["CriticalSubtle"] = "#4a0b17", ["Indigo"] = "#7c83fd",
                    ["IndigoSubtle"] = "#252857", ["Purple"] = "#b388ff", ["PurpleSubtle"] = "#352254"
                };
                break;

            case "tft-amber-terminal":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#1b1c18", ["HeaderBackground"] = "#22241e", ["SidebarBackground"] = "#20211b",
                    ["CardBackground"] = "#252720", ["CardHoverBackground"] = "#2e3028", ["DarkBase"] = "#141511",
                    ["Border"] = "#4e523e", ["BorderSubtle"] = "#383b2d", ["TextPrimary"] = "#ffb000",
                    ["TextSecondary"] = "#d49200", ["TextMuted"] = "#8f6500", ["Accent"] = "#ffb000",
                    ["AccentSubtle"] = "#3b2c05", ["AccentText"] = "#ffc83b", ["Healthy"] = "#55ff55",
                    ["HealthySubtle"] = "#143b14", ["Warning"] = "#ffaa00", ["WarningSubtle"] = "#3b2800",
                    ["Critical"] = "#ff3333", ["CriticalSubtle"] = "#421010", ["Indigo"] = "#c678dd",
                    ["IndigoSubtle"] = "#3b1e45", ["Purple"] = "#da70d6", ["PurpleSubtle"] = "#3e1940"
                };
                break;

            case "solarized-dark":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#002b36", ["HeaderBackground"] = "#073642", ["SidebarBackground"] = "#073642",
                    ["CardBackground"] = "#073642", ["CardHoverBackground"] = "#0e4452", ["DarkBase"] = "#001e26",
                    ["Border"] = "#586e75", ["BorderSubtle"] = "#0d4754", ["TextPrimary"] = "#93a1a1",
                    ["TextSecondary"] = "#839496", ["TextMuted"] = "#586e75", ["Accent"] = "#2aa198",
                    ["AccentSubtle"] = "#0c4547", ["AccentText"] = "#2ee0d3", ["Healthy"] = "#859900",
                    ["HealthySubtle"] = "#253b00", ["Warning"] = "#b58900", ["WarningSubtle"] = "#423200",
                    ["Critical"] = "#dc322f", ["CriticalSubtle"] = "#4a0f0e", ["Indigo"] = "#268bd2",
                    ["IndigoSubtle"] = "#0c324e", ["Purple"] = "#6c71c4", ["PurpleSubtle"] = "#222554"
                };
                break;

            case "high-contrast":
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#000000", ["HeaderBackground"] = "#050505", ["SidebarBackground"] = "#050505",
                    ["CardBackground"] = "#0a0a0a", ["CardHoverBackground"] = "#171717", ["DarkBase"] = "#000000",
                    ["Border"] = "#383838", ["BorderSubtle"] = "#262626", ["TextPrimary"] = "#ffffff",
                    ["TextSecondary"] = "#d4d4d4", ["TextMuted"] = "#a3a3a3", ["Accent"] = "#00f0ff",
                    ["AccentSubtle"] = "#003840", ["AccentText"] = "#00f0ff", ["Healthy"] = "#00ff66",
                    ["HealthySubtle"] = "#003314", ["Warning"] = "#ffbb00", ["WarningSubtle"] = "#332500",
                    ["Critical"] = "#ff2255", ["CriticalSubtle"] = "#400010", ["Indigo"] = "#7075ff",
                    ["IndigoSubtle"] = "#141533", ["Purple"] = "#d444ff", ["PurpleSubtle"] = "#300040"
                };
                break;

            default:
                model.Colors = new Dictionary<string, string>
                {
                    ["Background"] = "#070a12", ["HeaderBackground"] = "#0b111e", ["SidebarBackground"] = "#0c1220",
                    ["CardBackground"] = "#0c1220", ["CardHoverBackground"] = "#0f172a", ["DarkBase"] = "#04060b",
                    ["Border"] = "#1e293b", ["BorderSubtle"] = "#0f172a", ["TextPrimary"] = "#f8fafc",
                    ["TextSecondary"] = "#94a3b8", ["TextMuted"] = "#64748b", ["Accent"] = "#06b6d4",
                    ["AccentSubtle"] = "#083344", ["AccentText"] = "#22d3ee", ["Healthy"] = "#10b981",
                    ["HealthySubtle"] = "#064e3b", ["Warning"] = "#f59e0b", ["WarningSubtle"] = "#451a03",
                    ["Critical"] = "#f43f5e", ["CriticalSubtle"] = "#4c0519", ["Indigo"] = "#6366f1",
                    ["IndigoSubtle"] = "#1e1b4b", ["Purple"] = "#a855f7", ["PurpleSubtle"] = "#3b0764"
                };
                break;
        }

        return model;
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
        if (!_registeredPalettes.TryGetValue(themeName, out var palette))
        {
            if (!_registeredPalettes.TryGetValue("default-dark", out palette))
            {
                palette = CreateDefaultFallbackPalette("default-dark");
            }
        }

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

        CurrentTheme = themeName;

        // Apply dynamically to Application resources if available
        UpdateApplicationResources();

        OnPropertyChanged(nameof(CurrentTheme));
        ThemeChanged?.Invoke(this, themeName);
    }

    public void ImportTheme(string themeName, string jsonOrFilePath)
    {
        string json = jsonOrFilePath;
        if (File.Exists(jsonOrFilePath))
        {
            json = File.ReadAllText(jsonOrFilePath);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var palette = JsonSerializer.Deserialize<ThemePaletteModel>(json, options);
        if (palette == null)
        {
            throw new InvalidOperationException("Failed to parse theme JSON.");
        }

        palette.ThemeName = string.IsNullOrWhiteSpace(palette.ThemeName) ? themeName : palette.ThemeName;
        _registeredPalettes[themeName] = palette;

        ApplyTheme(themeName);
        OnPropertyChanged(nameof(AvailableThemes));
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

        // Common UI alias mappings
        MapResourceAlias(resources, "Background", "AppBg");
        MapResourceAlias(resources, "HeaderBackground", "HeaderBg");
        MapResourceAlias(resources, "SidebarBackground", "SidebarBg");
        MapResourceAlias(resources, "CardBackground", "CardBg");
        MapResourceAlias(resources, "CardHoverBackground", "CardHoverBg");

        // Determine background luminance and adapt theme variant accordingly
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
            var brush = new ImmutableSolidColorBrush(color);
            resources[$"Color{targetKey}"] = color;
            resources[$"{targetKey}Brush"] = brush;
            resources[$"AppColor.{targetKey}"] = color;
            resources[$"AppBrush.{targetKey}"] = brush;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
