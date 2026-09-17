using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MadTOM.Models;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Services;

public sealed class CollectorClientService : IAsyncDisposable
{
    private readonly object _channelLock = new();
    private readonly string _httpEndpoint;
    private GrpcChannel _channel;
    private QueryService.QueryServiceClient _queryClient;
    private ConfigService.ConfigServiceClient _configClient;

    public string Endpoint { get; }
    public string CollectorName { get; private set; }

    public CollectorClientService(string endpoint, string initialName = "Local Collector")
    {
        Endpoint = endpoint;
        CollectorName = initialName;

        _httpEndpoint = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"http://{endpoint}";

        _channel = CreateChannel(_httpEndpoint);
        _queryClient = new QueryService.QueryServiceClient(_channel);
        _configClient = new ConfigService.ConfigServiceClient(_channel);
    }

    private static GrpcChannel CreateChannel(string httpEndpoint)
    {
        var handler = new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(5),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(3),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true
        };

        return GrpcChannel.ForAddress(httpEndpoint, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
    }

    public void ResetChannel()
    {
        lock (_channelLock)
        {
            try { _channel.Dispose(); } catch { }
            _channel = CreateChannel(_httpEndpoint);
            _queryClient = new QueryService.QueryServiceClient(_channel);
            _configClient = new ConfigService.ConfigServiceClient(_channel);
        }
    }

    private (QueryService.QueryServiceClient query, ConfigService.ConfigServiceClient config) GetClients()
    {
        lock (_channelLock)
        {
            return (_queryClient, _configClient);
        }
    }

    public async Task<IReadOnlyList<FleetNodeModel>> ListNodesAsync(CancellationToken ct = default)
    {
        try
        {
            var (queryClient, _) = GetClients();
            var response = await queryClient.ListNodesAsync(new ListNodesRequest(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
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
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            ResetChannel();
            return Array.Empty<FleetNodeModel>();
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
        var req = new RangeQueryRequest
        {
            NodeId = nodeId,
            MetricName = metricName,
            StartTimeUnixNano = new DateTimeOffset(start).ToUnixTimeMilliseconds() * 1_000_000,
            EndTimeUnixNano = new DateTimeOffset(end).ToUnixTimeMilliseconds() * 1_000_000,
            TargetPoints = (uint)Math.Clamp(targetPoints, 4, 100000)
        };

        try
        {
            var (queryClient, _) = GetClients();
            var response = await queryClient.QueryRangeAsync(req, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct);
            var points = new List<LODPoint>(response.Points.Count);
            foreach (var pt in response.Points)
            {
                points.Add(new LODPoint(pt.TimestampUnixNano, pt.Value, pt.MinValue, pt.MaxValue));
            }
            return points;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            ResetChannel();
            throw;
        }
    }

    public async IAsyncEnumerable<LiveTelemetryEvent> SubscribeLiveAsync(string nodeId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (queryClient, _) = GetClients();
        using var call = queryClient.SubscribeLive(new LiveSubscriptionRequest { NodeId = nodeId }, cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task<NodeConfig?> GetNodeConfigAsync(string nodeId, CancellationToken ct = default)
    {
        try
        {
            var (_, configClient) = GetClients();
            return await configClient.GetNodeConfigAsync(new GetNodeConfigRequest { NodeId = nodeId }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            ResetChannel();
            return null;
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
            var (_, configClient) = GetClients();
            var res = await configClient.UpdateNodeConfigAsync(new UpdateNodeConfigRequest
            {
                NodeId = nodeId,
                Config = config
            }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return res.Success;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            ResetChannel();
            return false;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_channelLock)
        {
            try { _channel.Dispose(); } catch { }
        }
        return ValueTask.CompletedTask;
    }
}

