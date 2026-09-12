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
    public bool IsRateOfChange { get; set; }
}

public sealed class GraphItemConfig
{
    public string Title { get; set; } = string.Empty;
    public List<GraphSeriesConfig> Series { get; set; } = new();
}

public sealed class GraphGroupConfig
{
    public string Title { get; set; } = string.Empty;
    public List<GraphItemConfig> Graphs { get; set; } = new();

    public GraphGroupConfig() { }

    public GraphGroupConfig(IEnumerable<GraphItemConfig> graphs, string title = "")
    {
        Title = title;
        Graphs = graphs.ToList();
    }

    public GraphGroupConfig(string title, IEnumerable<GraphItemConfig> graphs)
    {
        Title = title;
        Graphs = graphs.ToList();
    }

    public GraphGroupConfig(GraphItemConfig singleGraph)
    {
        Title = singleGraph.Title;
        Graphs = new List<GraphItemConfig> { singleGraph };
    }
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
        if (metric.StartsWith("disk.io.read")) return "#06B6D4";
        if (metric.StartsWith("disk.io.write")) return "#F97316";
        if (metric.StartsWith("disk.io")) return "#3B82F6";
        return "#06B6D4";
    }

    public Dictionary<string, List<GraphGroupConfig>> LoadAllNodeGroupConfigs()
    {
        var result = new Dictionary<string, List<GraphGroupConfig>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;
            string json = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(json)) return result;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // 1. Try modern dictionary with GraphGroupConfig: { "aggregated": [ { "Graphs": [ ... ] } ] }
            try
            {
                var groupDict = JsonSerializer.Deserialize<Dictionary<string, List<GraphGroupConfig>>>(json, options);
                if (groupDict != null && groupDict.Count > 0)
                {
                    bool isValidGroupFormat = groupDict.Values.Any(v => v != null && v.Any(g => g.Graphs != null && g.Graphs.Count > 0));
                    if (isValidGroupFormat)
                    {
                        foreach (var kvp in groupDict)
                        {
                            result[kvp.Key] = kvp.Value ?? new List<GraphGroupConfig>();
                        }
                        return result;
                    }
                }
            }
            catch (JsonException) { }

            // 2. Fallback: dictionary of flat GraphItemConfig: { "aggregated": [ { "Series": [ ... ] } ] }
            try
            {
                var flatDict = JsonSerializer.Deserialize<Dictionary<string, List<GraphItemConfig>>>(json, options);
                if (flatDict != null && flatDict.Count > 0)
                {
                    foreach (var kvp in flatDict)
                    {
                        var groups = (kvp.Value ?? new List<GraphItemConfig>())
                            .Select(item => new GraphGroupConfig(item))
                            .ToList();
                        result[kvp.Key] = groups;
                    }
                    return result;
                }
            }
            catch (JsonException) { }

            // 3. Fallback: single List<GraphItemConfig> (legacy format) -> map to "aggregated"
            try
            {
                var list = JsonSerializer.Deserialize<List<GraphItemConfig>>(json, options);
                if (list != null && list.Count > 0)
                {
                    result["aggregated"] = list.Select(item => new GraphGroupConfig(item)).ToList();
                    return result;
                }
            }
            catch (JsonException) { }

            // 4. Fallback: legacy string[] metrics -> map to "aggregated"
            try
            {
                var legacy = JsonSerializer.Deserialize<string[]>(json, options);
                if (legacy != null)
                {
                    result["aggregated"] = legacy.Select(m => new GraphGroupConfig(new GraphItemConfig
                    {
                        Title = m,
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = m, Label = m, ColorHex = GetDefaultColor(m) }
                        }
                    })).ToList();
                    return result;
                }
            }
            catch (JsonException) { }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return result;
        }
    }

    public IReadOnlyList<GraphGroupConfig>? LoadGroupConfigs(string? nodeKey = null)
    {
        string key = string.IsNullOrEmpty(nodeKey) ? "aggregated" : nodeKey.Trim().ToLowerInvariant();
        var all = LoadAllNodeGroupConfigs();
        if (all.TryGetValue(key, out var configs) && configs.Count > 0)
        {
            return configs;
        }
        return null;
    }

    public IReadOnlyList<GraphItemConfig>? LoadConfigs(string? nodeKey = null)
    {
        var groups = LoadGroupConfigs(nodeKey);
        if (groups == null) return null;
        return groups.SelectMany(g => g.Graphs).ToList();
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

    public void SaveGroupConfigs(string nodeKey, IEnumerable<GraphGroupConfig> groups)
    {
        string key = string.IsNullOrEmpty(nodeKey) ? "aggregated" : nodeKey.Trim().ToLowerInvariant();
        var all = LoadAllNodeGroupConfigs();
        all[key] = groups.ToList();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    public void SaveConfigs(string nodeKey, IEnumerable<GraphItemConfig> configs)
    {
        SaveGroupConfigs(nodeKey, configs.Select(c => new GraphGroupConfig(c)));
    }

    public void SaveConfigs(IEnumerable<GraphItemConfig> configs)
    {
        SaveConfigs("aggregated", configs);
    }

    public void Save(IEnumerable<string> metrics)
    {
        SaveConfigs("aggregated", metrics.Distinct().Select(m => new GraphItemConfig
        {
            Title = m,
            Series = new List<GraphSeriesConfig>
            {
                new() { Metric = m, Label = m, ColorHex = GetDefaultColor(m) }
            }
        }));
    }
}
