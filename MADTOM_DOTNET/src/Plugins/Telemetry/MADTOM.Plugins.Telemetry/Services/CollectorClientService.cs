using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MadTOM.Models;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

public sealed class CollectorClientService : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly QueryService.QueryServiceClient _queryClient;
    private readonly ConfigService.ConfigServiceClient _configClient;

    public string Endpoint { get; }
    public string CollectorName { get; private set; }

    public CollectorClientService(string endpoint, string initialName = "Local Collector")
    {
        Endpoint = endpoint;
        CollectorName = initialName;

        var httpEndpoint = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"http://{endpoint}";

        _channel = GrpcChannel.ForAddress(httpEndpoint);
        _queryClient = new QueryService.QueryServiceClient(_channel);
        _configClient = new ConfigService.ConfigServiceClient(_channel);
    }

    public async Task<IReadOnlyList<FleetNodeModel>> ListNodesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _queryClient.ListNodesAsync(new ListNodesRequest(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            if (!string.IsNullOrEmpty(response.CollectorName))
            {
                CollectorName = response.CollectorName;
            }

            var nodes = new List<FleetNodeModel>();
            foreach (var protoNode in response.Nodes)
            {
                nodes.Add(new FleetNodeModel
                {
                    Id = protoNode.NodeId,
                    CollectorName = CollectorName,
                    CollectorEndpoint = Endpoint,
                    Os = protoNode.Os,
                    Arch = protoNode.Arch,
                    Status = protoNode.Status.ToLowerInvariant(),
                    Role = protoNode.ConnectionMode.ToLowerInvariant()
                });
            }
            return nodes;
        }
        catch
        {
            return Array.Empty<FleetNodeModel>();
        }
    }

    public async Task<IReadOnlyList<LODPoint>> QueryRangeAsync(
        string nodeId,
        string metricName,
        DateTime start,
        DateTime end,
        int targetPoints = 1200,
        CancellationToken ct = default)
    {
        {
            var req = new RangeQueryRequest
            {
                NodeId = nodeId,
                MetricName = metricName,
                StartTimeUnixNano = new DateTimeOffset(start).ToUnixTimeMilliseconds() * 1_000_000,
                EndTimeUnixNano = new DateTimeOffset(end).ToUnixTimeMilliseconds() * 1_000_000,
                TargetPoints = (uint)Math.Max(2, targetPoints)
            };

            var response = await _queryClient.QueryRangeAsync(req, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct);
            var points = new List<LODPoint>(response.Points.Count);
            foreach (var pt in response.Points)
            {
                points.Add(new LODPoint(pt.TimestampUnixNano, pt.Value, pt.MinValue, pt.MaxValue));
            }
            return points;
        }

    }

    public async IAsyncEnumerable<LiveTelemetryEvent> SubscribeLiveAsync(string nodeId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var call = _queryClient.SubscribeLive(new LiveSubscriptionRequest { NodeId = nodeId }, cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task<NodeConfig?> GetNodeConfigAsync(string nodeId, CancellationToken ct = default)
    {
        try
        {
            return await _configClient.GetNodeConfigAsync(new GetNodeConfigRequest { NodeId = nodeId }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> UpdateNodeConfigAsync(string nodeId, NodeConfig config, CancellationToken ct = default)
    {
        try
        {
            var res = await _configClient.UpdateNodeConfigAsync(new UpdateNodeConfigRequest
            {
                NodeId = nodeId,
                Config = config
            }, cancellationToken: ct);
            return res.Success;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}

