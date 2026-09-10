using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class GraphSeriesConfig
{
    public string Metric { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#06B6D4";
}

public sealed class GraphItemConfig
{
    public string Title { get; set; } = string.Empty;
    public List<GraphSeriesConfig> Series { get; set; } = new();
}

public sealed class GraphLayoutStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "graphs.json");

    public static string GetDefaultColor(string metric)
    {
        if (metric.StartsWith("cpu.total")) return "#06B6D4";
        if (metric.StartsWith("cpu.user")) return "#3B82F6";
        if (metric.StartsWith("cpu.system")) return "#8B5CF6";
        if (metric.StartsWith("cpu.iowait")) return "#F59E0B";
        if (metric.StartsWith("memory.used")) return "#10B981";
        if (metric.StartsWith("memory.available")) return "#34D399";
        if (metric.StartsWith("power.battery")) return "#EC4899";
        if (metric.StartsWith("power.rate")) return "#F97316";
        if (metric.StartsWith("twamp.rtt")) return "#06B6D4";
        if (metric.StartsWith("twamp.forward")) return "#6366F1";
        if (metric.StartsWith("twamp.reverse")) return "#A855F7";
        return "#06B6D4";
    }

    public IReadOnlyList<GraphItemConfig>? LoadConfigs()
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(json)) return null;

            // First attempt to deserialize new format
            try
            {
                var configs = JsonSerializer.Deserialize<List<GraphItemConfig>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (configs != null && configs.Count > 0 && configs.Any(c => c.Series != null && c.Series.Count > 0))
                {
                    return configs;
                }
            }
            catch (JsonException) { }

            // Fallback: deserialize legacy string[]
            var legacy = JsonSerializer.Deserialize<string[]>(json);
            if (legacy != null)
            {
                return legacy.Select(m => new GraphItemConfig
                {
                    Title = m,
                    Series = new List<GraphSeriesConfig>
                    {
                        new() { Metric = m, Label = m, ColorHex = GetDefaultColor(m) }
                    }
                }).ToList();
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? Load()
    {
        var configs = LoadConfigs();
        if (configs == null) return null;
        return configs.Select(c => c.Series.FirstOrDefault()?.Metric ?? c.Title)
                      .Where(s => !string.IsNullOrEmpty(s))
                      .Distinct()
                      .ToList();
    }

    public void SaveConfigs(IEnumerable<GraphItemConfig> configs)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(configs.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    public void Save(IEnumerable<string> metrics)
    {
        SaveConfigs(metrics.Distinct().Select(m => new GraphItemConfig
        {
            Title = m,
            Series = new List<GraphSeriesConfig>
            {
                new() { Metric = m, Label = m, ColorHex = GetDefaultColor(m) }
            }
        }));
    }
}
