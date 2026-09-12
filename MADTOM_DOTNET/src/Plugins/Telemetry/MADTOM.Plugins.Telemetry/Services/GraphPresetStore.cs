using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class GraphPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public List<GraphGroupConfig> Groups { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class GraphPresetStore(string? customPath = null)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "graph-presets.json");

    private readonly string _path = customPath ?? DefaultPath;

    public static IReadOnlyList<GraphPreset> GetBuiltInPresets()
    {
        return new List<GraphPreset>
        {
            new()
            {
                Id = "builtin-standard-stack",
                Name = "Standard Stack",
                Description = "Full-width vertically stacked CPU and Memory breakdown graphs",
                IsBuiltIn = true,
                Groups = new List<GraphGroupConfig>
                {
                    new(new GraphItemConfig
                    {
                        Title = "CPU Breakdown",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "cpu.total", Label = "Total", ColorHex = "#06B6D4" },
                            new() { Metric = "cpu.user", Label = "User", ColorHex = "#3B82F6" },
                            new() { Metric = "cpu.system", Label = "System", ColorHex = "#8B5CF6" },
                            new() { Metric = "cpu.iowait", Label = "IOWait", ColorHex = "#F59E0B" }
                        }
                    }),
                    new(new GraphItemConfig
                    {
                        Title = "Memory Breakdown",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "memory.used", Label = "Used", ColorHex = "#10B981" },
                            new() { Metric = "memory.available", Label = "Available", ColorHex = "#34D399" }
                        }
                    })
                }
            },
            new()
            {
                Id = "builtin-dual-side-by-side",
                Name = "Dual Side-by-Side",
                Description = "Compact 2-column layout: CPU & Memory on Row 1, Network & TWAMP on Row 2",
                IsBuiltIn = true,
                Groups = new List<GraphGroupConfig>
                {
                    new(new[]
                    {
                        new GraphItemConfig
                        {
                            Title = "CPU Breakdown",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "cpu.total", Label = "Total", ColorHex = "#06B6D4" },
                                new() { Metric = "cpu.user", Label = "User", ColorHex = "#3B82F6" },
                                new() { Metric = "cpu.system", Label = "System", ColorHex = "#8B5CF6" }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "Memory Breakdown",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "memory.used", Label = "Used", ColorHex = "#10B981" },
                                new() { Metric = "memory.available", Label = "Available", ColorHex = "#34D399" }
                            }
                        }
                    }),
                    new(new[]
                    {
                        new GraphItemConfig
                        {
                            Title = "TWAMP Latency",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "twamp.rtt", Label = "RTT", ColorHex = "#06B6D4" },
                                new() { Metric = "twamp.forward", Label = "Forward", ColorHex = "#6366F1" },
                                new() { Metric = "twamp.reverse", Label = "Reverse", ColorHex = "#A855F7" }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "Network Throughput",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "nic.eth0.rx_bytes", Label = "RX", ColorHex = "#10B981", IsRateOfChange = true },
                                new() { Metric = "nic.eth0.tx_bytes", Label = "TX", ColorHex = "#3B82F6", IsRateOfChange = true }
                            }
                        }
                    })
                }
            },
            new()
            {
                Id = "builtin-quad-horizontal",
                Name = "Quad Horizontal Grid",
                Description = "High-density 4 graphs side-by-side in a single row",
                IsBuiltIn = true,
                Groups = new List<GraphGroupConfig>
                {
                    new(new[]
                    {
                        new GraphItemConfig
                        {
                            Title = "CPU Total",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "cpu.total", Label = "Total", ColorHex = "#06B6D4" }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "Memory Used",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "memory.used", Label = "Used", ColorHex = "#10B981" }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "Network RX",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "nic.eth0.rx_bytes", Label = "RX/s", ColorHex = "#3B82F6", IsRateOfChange = true }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "TWAMP RTT",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "twamp.rtt", Label = "RTT", ColorHex = "#EC4899" }
                            }
                        }
                    })
                }
            },
            new()
            {
                Id = "builtin-network-latency",
                Name = "Network & Latency Focus",
                Description = "Full-width TWAMP latency breakdown with side-by-side interface rates",
                IsBuiltIn = true,
                Groups = new List<GraphGroupConfig>
                {
                    new(new GraphItemConfig
                    {
                        Title = "TWAMP Latency Breakdown",
                        Series = new List<GraphSeriesConfig>
                        {
                            new() { Metric = "twamp.rtt", Label = "RTT", ColorHex = "#06B6D4" },
                            new() { Metric = "twamp.forward", Label = "Forward (Up)", ColorHex = "#6366F1" },
                            new() { Metric = "twamp.reverse", Label = "Reverse (Down)", ColorHex = "#A855F7" }
                        }
                    }),
                    new(new[]
                    {
                        new GraphItemConfig
                        {
                            Title = "eth0 Ingress (RX)",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "nic.eth0.rx_bytes", Label = "RX/s", ColorHex = "#10B981", IsRateOfChange = true }
                            }
                        },
                        new GraphItemConfig
                        {
                            Title = "eth0 Egress (TX)",
                            Series = new List<GraphSeriesConfig>
                            {
                                new() { Metric = "nic.eth0.tx_bytes", Label = "TX/s", ColorHex = "#3B82F6", IsRateOfChange = true }
                            }
                        }
                    })
                }
            }
        };
    }

    public List<GraphPreset> LoadAllPresets()
    {
        var presets = new List<GraphPreset>(GetBuiltInPresets());

        try
        {
            if (!File.Exists(_path)) return presets;
            string json = File.ReadAllText(_path).Trim();
            if (string.IsNullOrEmpty(json)) return presets;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var userPresets = JsonSerializer.Deserialize<List<GraphPreset>>(json, options);
            if (userPresets != null)
            {
                foreach (var up in userPresets)
                {
                    up.IsBuiltIn = false;
                    presets.Add(up);
                }
            }
        }
        catch { }

        return presets;
    }

    public void SaveUserPreset(string name, IEnumerable<GraphGroupConfig> groups, string description = "")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Preset name cannot be empty.", nameof(name));

        var existingPresets = LoadUserPresetsOnly();
        var existing = existingPresets.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Description = description;
            existing.Groups = groups.ToList();
        }
        else
        {
            existingPresets.Add(new GraphPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                Description = description,
                IsBuiltIn = false,
                Groups = groups.ToList()
            });
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(existingPresets, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    public bool DeleteUserPreset(string nameOrId)
    {
        var existingPresets = LoadUserPresetsOnly();
        int countBefore = existingPresets.Count;
        existingPresets.RemoveAll(p => p.Id.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
                                       p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

        if (existingPresets.Count != countBefore)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(existingPresets, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }

        return false;
    }

    private List<GraphPreset> LoadUserPresetsOnly()
    {
        try
        {
            if (!File.Exists(_path)) return new List<GraphPreset>();
            string json = File.ReadAllText(_path).Trim();
            if (string.IsNullOrEmpty(json)) return new List<GraphPreset>();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<GraphPreset>>(json, options) ?? new List<GraphPreset>();
        }
        catch
        {
            return new List<GraphPreset>();
        }
    }
}

