using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MADTOM.Plugins.SlideForge.ViewModels;

internal static class SfJsonHelpers
{
    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
    };

    public static string? GetStringOrNull(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string GetString(JsonElement obj, string prop, string fallback = "") =>
        GetStringOrNull(obj, prop) ?? fallback;

    public static int GetInt(JsonElement obj, string prop, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
            return v;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int s))
            return s;
        return fallback;
    }

    public static int? GetNullableInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
            return v;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int s))
            return s;
        return null;
    }

    public static long GetLong(JsonElement obj, string prop, long fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
            return v;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long s))
            return s;
        return fallback;
    }

    public static double GetDouble(JsonElement obj, string prop, double fallback = 0.0)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double v))
            return v;
        return fallback;
    }

    public static bool GetBool(JsonElement obj, string prop, bool fallback = false)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt64(out long n) && n != 0,
            JsonValueKind.String => bool.TryParse(el.GetString(), out bool b) && b,
            _ => fallback,
        };
    }

    public static List<string> GetStringList(JsonElement obj, string prop)
    {
        var list = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind != JsonValueKind.Null)
                list.Add(item.GetRawText());
        }
        return list;
    }

    public static string PrettyPrint(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "{}";
        return JsonSerializer.Serialize(element, PrettyOptions);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
    }
}
