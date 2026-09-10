using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MadTOM.Models;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

public record CollectorEndpointConfig(string Name, string Address);

/// <summary>
/// Manages multiple collector connections and populates the dashboard with
/// separate, individual node cards for every node reported by all collectors.
/// </summary>
public sealed class MultiCollectorManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CollectorClientService> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<CollectorEndpointConfig> ConfiguredEndpoints { get; } = new();

    public MultiCollectorManager()
    {
        // Default to local collector
        AddCollector("Localhost", "127.0.0.1:50051");
    }

    public void AddCollector(string name, string address)
    {
        if (_clients.ContainsKey(address)) return;

        var client = new CollectorClientService(address, name);
        if (_clients.TryAdd(address, client))
        {
            ConfiguredEndpoints.Add(new CollectorEndpointConfig(name, address));
        }
    }

    public void RemoveCollector(string address)
    {
        if (_clients.TryRemove(address, out var client))
        {
            var match = ConfiguredEndpoints.FirstOrDefault(e => e.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                ConfiguredEndpoints.Remove(match);
            }
            _ = client.DisposeAsync();
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

