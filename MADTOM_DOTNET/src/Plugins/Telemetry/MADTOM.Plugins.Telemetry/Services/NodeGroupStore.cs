using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MadTOM.Services;

public sealed class NodeGroupStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _nodeGroups = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MADTOM",
        "node-groups.json");

    public NodeGroupStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath;
        Load();
    }

    public event Action<string, string>? GroupChanged;

    public void Reload()
    {
        Load();
    }

    public string GetGroup(string nodeId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return string.Empty;
            if (_nodeGroups.TryGetValue(nodeId, out var group) &&
                !string.IsNullOrWhiteSpace(group) &&
                !group.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !group.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return group.Trim();
            }
            return string.Empty;
        }
    }

    public void SetGroup(string nodeId, string groupName)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;

        string cleanGroup = string.IsNullOrWhiteSpace(groupName) ? string.Empty : groupName.Trim();
        if (cleanGroup.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
            cleanGroup.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            cleanGroup = string.Empty;
        }

        lock (_lock)
        {
            if (string.IsNullOrEmpty(cleanGroup))
            {
                _nodeGroups.Remove(nodeId);
            }
            else
            {
                _nodeGroups[nodeId] = cleanGroup;
            }
            Save();
        }

        GroupChanged?.Invoke(nodeId, cleanGroup);
    }

    public IReadOnlyList<string> GetAllGroups()
    {
        lock (_lock)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _nodeGroups.Values)
            {
                if (!string.IsNullOrWhiteSpace(g) &&
                    !g.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                    !g.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(g.Trim());
                }
            }
            return set.ToList();
        }
    }

    public Dictionary<string, string> LoadAll()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_nodeGroups, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                string json = File.ReadAllText(_filePath);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map != null)
                {
                    _nodeGroups = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _nodeGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
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
                string json = JsonSerializer.Serialize(_nodeGroups, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Best-effort file write
            }
        }
    }
}

