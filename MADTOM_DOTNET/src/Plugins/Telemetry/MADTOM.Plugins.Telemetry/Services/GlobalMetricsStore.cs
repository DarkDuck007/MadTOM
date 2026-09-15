using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class GlobalMetricConfigItem
{
    public string Key { get; set; } = string.Empty;
    public string Modifier { get; set; } = "Sum"; // "Sum", "Avg", "Rate of Change"
    public bool IsPinned { get; set; } = true;
    public int Order { get; set; }
}

public sealed record MetricDefinition(string Key, string Name, string ShortName, string Icon, string Unit);

public sealed class GlobalMetricsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<GlobalMetricConfigItem> _items = new();

    public static readonly IReadOnlyList<MetricDefinition> AvailableCatalog = new List<MetricDefinition>
    {
        new("network.ingress", "Network Ingress", "Ingress", "⬇", "bps"),
        new("network.egress", "Network Egress", "Egress", "⬆", "bps"),
        new("cpu.load", "CPU Compute Load", "CPU", "⚙", "%"),
        new("memory.bytes", "Memory (RAM) Used", "RAM", "💾", "B"),
        new("disk.bytes.read", "Disk Read Throughput", "Disk R", "📖", "B/s"),
        new("disk.bytes.write", "Disk Write Throughput", "Disk W", "✍", "B/s"),
        new("disk.ops", "Disk Operations", "IOPS", "⚡", "IOPS"),
        new("twamp.rtt", "TWAMP Round-Trip Latency", "TWAMP", "⏱", "ms")
    };

    public static MetricDefinition GetMetricDefinition(string key)
    {
        var existing = AvailableCatalog.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        if (key.StartsWith("disk.io.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('.');
            if (parts.Length >= 4)
            {
                string dev = parts[2];
                string type = parts[3];
                return type switch
                {
                    "read_bytes" => new MetricDefinition(key, $"Disk Read ({dev})", $"{dev} R", "📖", "B/s"),
                    "write_bytes" => new MetricDefinition(key, $"Disk Write ({dev})", $"{dev} W", "✍", "B/s"),
                    "read_ops" => new MetricDefinition(key, $"Disk Read IOPS ({dev})", $"{dev} R-IOPS", "⚡", "IOPS"),
                    "write_ops" => new MetricDefinition(key, $"Disk Write IOPS ({dev})", $"{dev} W-IOPS", "⚡", "IOPS"),
                    _ => new MetricDefinition(key, $"Disk {dev} {type}", dev, "💽", "")
                };
            }
        }
        else if (key.StartsWith("nic.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('.');
            if (parts.Length >= 3)
            {
                string iface = parts[1];
                string type = parts[2];
                return type switch
                {
                    "rx_bytes" => new MetricDefinition(key, $"Network Ingress ({iface})", $"{iface} In", "⬇", "bps"),
                    "tx_bytes" => new MetricDefinition(key, $"Network Egress ({iface})", $"{iface} Out", "⬆", "bps"),
                    _ => new MetricDefinition(key, $"NIC {iface} {type}", iface, "🌐", "")
                };
            }
        }
        else if (key.StartsWith("cpu.core.", StringComparison.OrdinalIgnoreCase))
        {
            string coreIdx = key.Substring("cpu.core.".Length);
            return new MetricDefinition(key, $"CPU Core {coreIdx}", $"Core {coreIdx}", "⚙", "%");
        }
        else if (key.Equals("memory.swap_used", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricDefinition(key, "Swap Memory Used", "Swap Used", "💾", "B");
        }
        else if (key.Equals("memory.swap_total", StringComparison.OrdinalIgnoreCase))
        {
            return new MetricDefinition(key, "Swap Total Size", "Swap Total", "💾", "B");
        }
        else if (key.StartsWith("swap.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('.');
            if (parts.Length >= 3)
            {
                string dev = parts[1];
                string type = parts[2].Replace("_", " ");
                return new MetricDefinition(key, $"Swap {dev} ({type})", $"{dev} {type}", "💾", "B");
            }
        }
        else if (key.StartsWith("zram.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('.');
            if (parts.Length >= 3)
            {
                string dev = parts[1];
                string type = parts[2].Replace("_", " ");
                return new MetricDefinition(key, $"ZRAM {dev} ({type})", $"{dev} {type}", "🗜", "B");
            }
        }

        return new MetricDefinition(key, key, key, "📊", "");
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MADTOM",
        "global-metrics.json");

    public event Action? ConfigChanged;

    public GlobalMetricsStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<GlobalMetricConfigItem> GetPinnedItems()
    {
        lock (_lock)
        {
            return _items.Where(i => i.IsPinned).OrderBy(i => i.Order).ToList();
        }
    }

    public IReadOnlyList<GlobalMetricConfigItem> GetAllItems()
    {
        lock (_lock)
        {
            return _items.OrderBy(i => i.Order).ToList();
        }
    }

    public bool IsPinned(string key, string modifier)
    {
        lock (_lock)
        {
            return _items.Any(i => i.IsPinned &&
                                   i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                   i.Modifier.Equals(modifier, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void PinMetric(string key, string modifier)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(i =>
                i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                i.Modifier.Equals(modifier, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.IsPinned = true;
            }
            else
            {
                int nextOrder = _items.Count > 0 ? _items.Max(i => i.Order) + 1 : 0;
                _items.Add(new GlobalMetricConfigItem
                {
                    Key = key,
                    Modifier = modifier,
                    IsPinned = true,
                    Order = nextOrder
                });
            }

            Save();
        }

        ConfigChanged?.Invoke();
    }

    public void UnpinMetric(string key, string modifier)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(i =>
                i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                i.Modifier.Equals(modifier, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _items.Remove(existing);
                Save();
            }
        }

        ConfigChanged?.Invoke();
    }

    public void TogglePinned(string key, string modifier)
    {
        if (IsPinned(key, modifier))
        {
            UnpinMetric(key, modifier);
        }
        else
        {
            PinMetric(key, modifier);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var items = JsonSerializer.Deserialize<List<GlobalMetricConfigItem>>(json);
                    if (items != null && items.Count > 0)
                    {
                        _items = items;
                        return;
                    }
                }
            }
            catch
            {
                // Fallback to defaults
            }

            // Defaults: Ingress and Egress Sum
            _items = new List<GlobalMetricConfigItem>
            {
                new() { Key = "network.ingress", Modifier = "Sum", IsPinned = true, Order = 0 },
                new() { Key = "network.egress", Modifier = "Sum", IsPinned = true, Order = 1 }
            };
            Save();
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Best-effort
            }
        }
    }
}
