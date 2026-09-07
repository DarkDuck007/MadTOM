using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MadTOM.Theming;

public sealed class ThemePaletteModel
{
    [JsonPropertyName("themeName")]
    public string ThemeName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();
}

