using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MadTOM.Models;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

public record CollectorEndpointConfig(string Name, string Address);

/// <summary>
/// Manages multiple collector connections and populates the dashboard with
/// separate, individual node cards for every node reported by all collectors.
/// Configured collector endpoints are persisted to disk (~/.local/share/MADTOM/collectors.json).
/// </summary>
public sealed class MultiCollectorManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CollectorClientService> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _storePath;

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "collectors.json");

    public ObservableCollection<CollectorEndpointConfig> ConfiguredEndpoints { get; } = new();

    public MultiCollectorManager(string? storePath = null)
    {
        _storePath = storePath ?? DefaultStorePath;
        LoadFromStore();
    }

    private void LoadFromStore()
    {
        if (_storePath != ":memory:" && !string.IsNullOrEmpty(_storePath))
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    string json = File.ReadAllText(_storePath).Trim();
                    if (!string.IsNullOrEmpty(json))
                    {
                        var configs = JsonSerializer.Deserialize<List<CollectorEndpointConfig>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (configs != null && configs.Count > 0)
                        {
                            foreach (var cfg in configs)
                            {
                                if (!string.IsNullOrWhiteSpace(cfg.Address))
                                {
                                    AddCollectorInternal(cfg.Name, cfg.Address, persist: false);
                                }
                            }
                            if (ConfiguredEndpoints.Count > 0) return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to default on load failure
            }
        }

        // Default to local collector if store doesn't exist, is empty, or failed to parse
        AddCollectorInternal("Localhost", "127.0.0.1:50051", persist: false);
    }

    private void SaveToStore()
    {
        if (_storePath == ":memory:" || string.IsNullOrEmpty(_storePath)) return;
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temporary = _storePath + ".tmp";
            var list = ConfiguredEndpoints.ToList();
            File.WriteAllText(temporary, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _storePath, overwrite: true);
        }
        catch (Exception)
        {
            // Ignore write failures gracefully (e.g. read-only file systems)
        }
    }

    public void AddCollector(string name, string address) => AddCollectorInternal(name, address, persist: true);

    private void AddCollectorInternal(string name, string address, bool persist)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        address = address.Trim();
        name = string.IsNullOrWhiteSpace(name) ? "Collector" : name.Trim();

        if (_clients.ContainsKey(address)) return;

        var client = new CollectorClientService(address, name);
        if (_clients.TryAdd(address, client))
        {
            ConfiguredEndpoints.Add(new CollectorEndpointConfig(name, address));
            if (persist)
            {
                SaveToStore();
            }
        }
    }

    public void RemoveCollector(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        address = address.Trim();

        if (_clients.TryRemove(address, out var client))
        {
            var match = ConfiguredEndpoints.FirstOrDefault(e => e.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                ConfiguredEndpoints.Remove(match);
            }
            _ = client.DisposeAsync();
            SaveToStore();
        }
    }

    public async Task<IReadOnlyList<FleetNodeModel>> FetchAllNodesAsync(CancellationToken ct = default)
    {
        var groups = await Task.WhenAll(_clients.Values.Select(client => client.ListNodesAsync(ct)));
        return groups.SelectMany(nodes => nodes).ToArray();
    }

    public CollectorClientService? GetClientForNode(FleetNodeModel node)
    {
        if (string.IsNullOrEmpty(node.CollectorEndpoint)) return null;
        _clients.TryGetValue(node.CollectorEndpoint, out var client);
        return client;
    }

    public async Task StartLiveStreamAsync(FleetNodeModel node, Action<LiveTelemetryEvent> onSample, CancellationToken ct)
    {
        var client = GetClientForNode(node);
        if (client == null) return;

        try
        {
            await foreach (var ev in client.SubscribeLiveAsync(node.Id, ct))
            {
                onSample(ev);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected upon cancel
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
        _clients.Clear();
        _cts.Dispose();
    }
}
