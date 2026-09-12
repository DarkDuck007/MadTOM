using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MadTOM.Theming;

public sealed class ThemePaletteModel
{
    [JsonPropertyName("themeName")]
    public string ThemeName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ThemePaletteModel Clone()
    {
        return new ThemePaletteModel
        {
            ThemeName = ThemeName,
            DisplayName = DisplayName,
            Category = Category,
            Colors = new Dictionary<string, string>(Colors, StringComparer.OrdinalIgnoreCase)
        };
    }

    public void Merge(ThemePaletteModel overrides)
    {
        if (overrides == null) return;

        if (!string.IsNullOrWhiteSpace(overrides.ThemeName))
        {
            ThemeName = overrides.ThemeName;
        }

        if (!string.IsNullOrWhiteSpace(overrides.DisplayName))
        {
            DisplayName = overrides.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Category))
        {
            Category = overrides.Category;
        }

        if (overrides.Colors != null)
        {
            foreach (var (key, val) in overrides.Colors)
            {
                if (!string.IsNullOrWhiteSpace(val))
                {
                    Colors[key] = val;
                }
            }
        }
    }

    public string GetEffectiveCategory()
    {
        if (!string.IsNullOrWhiteSpace(Category))
            return Category.Trim();

        string lower = ThemeName.ToLowerInvariant();
        if (lower.Contains("bleed") || lower.Contains("ips")) return "IPS / Anti-Bleed";
        if (lower.Contains("tft") || lower.Contains("crt") || lower.Contains("terminal")) return "TFT / High-Angle";
        if (lower.Contains("contrast")) return "High Contrast";
        if (lower.Contains("mono") || lower.Contains("minimal")) return "Minimal";
        if (lower.Contains("light") || lower.Contains("white") || lower.Contains("paper")) return "Light";
        return "Dark";
    }
}

