using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

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
        LoadEmbeddedPalette("default-dark", "avares://MadTOM/Assets/Themes/default-dark.json");
        LoadEmbeddedPalette("high-contrast", "avares://MadTOM/Assets/Themes/high-contrast.json");
    }

    private void LoadEmbeddedPalette(string name, string uriString)
    {
        try
        {
            var uri = new Uri(uriString);
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
                }
            }
        }
        catch
        {
            // Fallback default palette if running in non-Avalonia headless test environment
            if (!_registeredPalettes.ContainsKey(name))
            {
                _registeredPalettes[name] = CreateDefaultFallbackPalette(name);
            }
        }
    }

    private static ThemePaletteModel CreateDefaultFallbackPalette(string name)
    {
        return new ThemePaletteModel
        {
            ThemeName = name,
            DisplayName = name,
            Colors = new Dictionary<string, string>
            {
                ["Background"] = "#070a12",
                ["HeaderBackground"] = "#0b111e",
                ["SidebarBackground"] = "#0c1220",
                ["CardBackground"] = "#0c1220",
                ["CardHoverBackground"] = "#0f172a",
                ["DarkBase"] = "#04060b",
                ["Border"] = "#1e293b",
                ["BorderSubtle"] = "#0f172a",
                ["TextPrimary"] = "#f8fafc",
                ["TextSecondary"] = "#94a3b8",
                ["TextMuted"] = "#64748b",
                ["Accent"] = "#06b6d4",
                ["AccentSubtle"] = "#083344",
                ["AccentText"] = "#22d3ee",
                ["Healthy"] = "#10b981",
                ["HealthySubtle"] = "#064e3b",
                ["Warning"] = "#f59e0b",
                ["WarningSubtle"] = "#451a03",
                ["Critical"] = "#f43f5e",
                ["CriticalSubtle"] = "#4c0519",
                ["Indigo"] = "#6366f1",
                ["IndigoSubtle"] = "#1e1b4b",
                ["Purple"] = "#a855f7",
                ["PurpleSubtle"] = "#3b0764"
            }
        };
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
            resources[$"AppColor.{key}"] = color;
            resources[$"AppBrush.{key}"] = new ImmutableSolidColorBrush(color);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
