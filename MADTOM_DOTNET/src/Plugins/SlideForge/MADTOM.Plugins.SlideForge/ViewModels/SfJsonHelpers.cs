using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public static class SfJsonHelpers
{
    public const int MaxPreviewBase64Bytes = 16 * 1024 * 1024;

    private static bool _avaloniaSkiaInitialized;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "csv", "tsv", "json", "yaml", "yml", "xml", "html", "htm",
        "css", "js", "py", "log", "ini", "toml", "sh", "sql", "svg"
    };

    private static readonly HashSet<string> SandboxPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "gif", "bmp", "pdf",
        "docx", "doc", "xlsx", "xls", "pptx", "ppt", "odt", "ods", "odp", "rtf", "svg"
    };

    private static readonly HashSet<string> JobPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "gif", "pdf",
        // Office documents are converted to PDF page images by the SlideForge control server.
        "docx", "doc", "odt", "rtf", "pptx", "ppt", "odp", "xlsx", "xls", "ods"
    };

    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        // Keep non-ASCII (Persian etc.) readable instead of \uXXXX escapes; this output is display-only.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

    public static long? GetNullableLong(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
            return v;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long s))
            return s;
        return null;
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

    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public static string FormatEpoch(long epochSeconds)
    {
        if (epochSeconds <= 0) return "—";
        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatTimestamp(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            if (dto.Year <= 1) return "—";
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return iso;
    }

    public static bool IsTextExt(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path.Trim()).TrimStart('.');
        return !string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext);
    }

    public static bool IsSandboxPreviewExt(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path.Trim()).TrimStart('.');
        return !string.IsNullOrEmpty(ext) && SandboxPreviewExtensions.Contains(ext);
    }

    public static bool IsJobPreviewExt(string path, string? kind = null)
    {
        if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "pdf", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path.Trim()).TrimStart('.');
        return !string.IsNullOrEmpty(ext) && JobPreviewExtensions.Contains(ext);
    }

    public static Bitmap? DecodePreviewBitmap(string b64, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(b64))
            return null;

        if (b64.Length > MaxPreviewBase64Bytes)
        {
            error = "Preview refused: png_b64 exceeds 16 MB limit.";
            return null;
        }

        byte[] rawBytes = Convert.FromBase64String(b64);
        if (rawBytes.Length > MaxPreviewBase64Bytes)
        {
            error = "Preview refused: decoded PNG exceeds 16 MB limit.";
            return null;
        }

        EnsureAvaloniaSkiaInitialized();
        return new Bitmap(new MemoryStream(rawBytes));
    }

    private static void EnsureAvaloniaSkiaInitialized()
    {
        if (_avaloniaSkiaInitialized || Application.Current != null) return;
        _avaloniaSkiaInitialized = true;
        try
        {
            string[] nativeCandidates =
            {
                Path.Combine(AppContext.BaseDirectory, "../../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/runtimes/linux-x64/native/libSkiaSharp.so"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libSkiaSharp.so")
            };
            string nativePath = string.Empty;
            foreach (var np in nativeCandidates)
            {
                if (File.Exists(np))
                {
                    nativePath = Path.GetFullPath(np);
                    break;
                }
            }

            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "../../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "Avalonia.Skia.dll")
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    var asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(path));
                    var skiaSharpAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SkiaSharp")
                        ?? System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "SkiaSharp.dll"));

                    if (skiaSharpAsm != null && !string.IsNullOrEmpty(nativePath))
                    {
                        try
                        {
                            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(skiaSharpAsm, (name, _, _) =>
                                name.Contains("SkiaSharp")
                                    ? System.Runtime.InteropServices.NativeLibrary.Load(nativePath)
                                    : IntPtr.Zero);
                        }
                        catch
                        {
                            // Resolver may already be registered
                        }
                    }

                    var skiaPlatform = asm.GetType("Avalonia.Skia.SkiaPlatform");
                    skiaPlatform?.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
                    break;
                }
            }
        }
        catch
        {
            // Ignore in headless environments without Skia binaries
        }
    }
}
