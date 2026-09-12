using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MadTOM.Theming;

public static class ThemeParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ThemePaletteModel? Parse(string content, string? fallbackName = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string trimmed = content.Trim();
        ThemePaletteModel? model = null;

        // Try JSON first if starts with '{'
        if (trimmed.StartsWith("{"))
        {
            try
            {
                model = JsonSerializer.Deserialize<ThemePaletteModel>(trimmed, JsonOpts);
            }
            catch (JsonException)
            {
                model = null;
            }
        }

        // If not JSON or JSON parsing failed, try YAML parsing
        if (model == null)
        {
            model = ParseYaml(trimmed);
        }

        if (model == null) return null;

        // Ensure case-insensitive colors dictionary
        if (model.Colors == null)
        {
            model.Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        else if (model.Colors.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            model.Colors = new Dictionary<string, string>(model.Colors, StringComparer.OrdinalIgnoreCase);
        }

        // Ensure ThemeName
        if (string.IsNullOrWhiteSpace(model.ThemeName))
        {
            model.ThemeName = !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName.Trim() : "custom-theme";
        }

        // Ensure DisplayName
        if (string.IsNullOrWhiteSpace(model.DisplayName))
        {
            model.DisplayName = FormatDisplayName(model.ThemeName);
        }

        return model;
    }

    private static ThemePaletteModel? ParseYaml(string yaml)
    {
        try
        {
            var model = new ThemePaletteModel
            {
                Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            bool inColorsSection = false;

            using var reader = new StringReader(yaml);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = StripComment(line);

                if (string.IsNullOrWhiteSpace(line)) continue;

                string trimmedLine = line.Trim();
                if (trimmedLine.Length == 0) continue;

                // Check indentation
                int indent = 0;
                while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;

                int colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                string key = line[..colonIdx].Trim();
                string val = line[(colonIdx + 1)..].Trim().Trim('"', '\'');

                if (key.Equals("colors", StringComparison.OrdinalIgnoreCase))
                {
                    inColorsSection = true;
                    continue;
                }

                if (indent == 0)
                {
                    inColorsSection = false;
                    if (key.Equals("themeName", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        model.ThemeName = val;
                    }
                    else if (key.Equals("displayName", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("title", StringComparison.OrdinalIgnoreCase))
                    {
                        model.DisplayName = val;
                    }
                    else if (key.Equals("category", StringComparison.OrdinalIgnoreCase))
                    {
                        model.Category = val;
                    }
                }
                else if (inColorsSection)
                {
                    if (!string.IsNullOrEmpty(val))
                    {
                        model.Colors[key] = val;
                    }
                }
            }

            return model.Colors.Count > 0 ? model : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripComment(string line)
    {
        char inQuote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                if (inQuote == '\0') inQuote = c;
                else if (inQuote == c) inQuote = '\0';
            }
            else if (c == '#' && inQuote == '\0')
            {
                return line[..i];
            }
        }
        return line;
    }

    public static string FormatDisplayName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Custom Theme";
        var parts = name.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            }
        }
        return string.Join(" ", parts);
    }
}

