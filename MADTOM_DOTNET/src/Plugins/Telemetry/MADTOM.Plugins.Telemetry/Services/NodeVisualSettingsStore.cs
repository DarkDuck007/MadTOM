using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MadTOM.Models;

namespace MadTOM.Services;

/// <summary>
/// Client-side persistence store for per-node visual rendering modes (e.g. CPU Core Matrix, Per-Core Graphs).
/// Saved locally in ~/.local/share/MADTOM/node-visuals.json and independent of remote daemon configurations.
/// </summary>
public sealed class NodeVisualSettingsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, CpuVisualMode> _nodeVisuals = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MADTOM",
        "node-visuals.json");

    public NodeVisualSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath;
        Load();
    }

    public CpuVisualMode GetVisualMode(string nodeId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return CpuVisualMode.CoreMatrix;
            return _nodeVisuals.TryGetValue(nodeId, out var mode) ? mode : CpuVisualMode.CoreMatrix;
        }
    }

    public void SetVisualMode(string nodeId, CpuVisualMode mode)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        lock (_lock)
        {
            _nodeVisuals[nodeId] = mode;
            SaveLocked();
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        _nodeVisuals.Clear();
                        foreach (var (k, v) in dict)
                        {
                            if (Enum.TryParse<CpuVisualMode>(v, true, out var m))
                            {
                                _nodeVisuals[k] = m;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to empty on corrupt or missing file
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dict = _nodeVisuals.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, true);
        }
        catch
        {
            // Ignore transient file IO failures
        }
    }
}
