using System;
using System.Collections.Generic;
using System.Linq;
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

    // Negotiated zstd response envelopes; counters exclude gRPC framing/TLS.
    private readonly ClientPayloadCounter _inventoryPayloads = new();
    private readonly ClientPayloadCounter _historyPayloads = new();
    private readonly ClientPayloadCounter _livePayloads = new();
    private readonly ClientPayloadCounter _configPayloads = new();
    private readonly ClientPayloadCounter _logPayloads = new();
    public IReadOnlyList<(string Name, ClientPayloadCounter Counter)> ReceivedPayloads => new[]
    {
        ("inventory RX", _inventoryPayloads), ("history RX", _historyPayloads),
        ("live RX", _livePayloads), ("configuration RX", _configPayloads),
        ("logs RX", _logPayloads)
    };

    private CompressionDiagnosticRow[] _transportDiagnostics = Array.Empty<CompressionDiagnosticRow>();
    public IReadOnlyList<CompressionDiagnosticRow> TransportDiagnostics => Volatile.Read(ref _transportDiagnostics);

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

    private long _lastResetTicks = 0;

    private static GrpcChannel CreateChannel(string httpEndpoint)
    {
        var handler = new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
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
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastResetTicks != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastResetTicks, nowTicks) < TimeSpan.FromSeconds(15))
        {
            return;
        }

        lock (_channelLock)
        {
            if (_lastResetTicks != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastResetTicks, System.Diagnostics.Stopwatch.GetTimestamp()) < TimeSpan.FromSeconds(15))
            {
                return;
            }
            _lastResetTicks = System.Diagnostics.Stopwatch.GetTimestamp();

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
            var response = await queryClient.ListNodesAsync(new ListNodesRequest(), headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            response = ClientResponseCompression.Decode(response, _inventoryPayloads);
            Volatile.Write(ref _transportDiagnostics, ClientCompressionSnapshot.TransportRows(Endpoint, response));
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
        using var timing = HistoryTiming.Begin("range-rpc", nodeId, metricName);
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
            var response = await queryClient.QueryRangeAsync(req, headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct);
            timing?.Mark("response", $"points={response.Points.Count}; zstdBytes={response.ZstdPayload.Length}; decodedBytes={response.DecodedSize}");
            response = ClientResponseCompression.Decode(response, _historyPayloads);
            timing?.Mark("decoded", $"points={response.Points.Count}");
            timing?.Mark("history-timestamps", $"requestedEndNano={req.EndTimeUnixNano}; newestReturnedNano={(response.Points.Count > 0 ? response.Points.Max(p => p.TimestampUnixNano) : 0)}; responseUtc={DateTime.UtcNow:O}");
            var points = new List<LODPoint>(response.Points.Count);
            foreach (var pt in response.Points)
            {
                points.Add(new LODPoint(pt.TimestampUnixNano, pt.Value, pt.MinValue, pt.MaxValue));
            }
            timing?.Mark("materialized", $"points={points.Count}");
            return points;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            throw;
        }
    }

    public async IAsyncEnumerable<LiveTelemetryEvent> SubscribeLiveAsync(string nodeId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (queryClient, _) = GetClients();
        using var call = queryClient.SubscribeLive(new LiveSubscriptionRequest { NodeId = nodeId }, headers: ClientResponseCompression.AcceptHeaders(), cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            yield return ClientResponseCompression.Decode(call.ResponseStream.Current, _livePayloads);
        }
    }

    public async Task<NodeConfig?> GetNodeConfigAsync(string nodeId, CancellationToken ct = default)
    {
        try
        {
            var (_, configClient) = GetClients();
            var response = await configClient.GetNodeConfigAsync(new GetNodeConfigRequest { NodeId = nodeId }, headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            response = ClientResponseCompression.Decode(response, _configPayloads);
            return response;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ConfigAck?> UpdateNodeConfigAsync(string nodeId, NodeConfig config, CancellationToken ct = default)
    {
        try
        {
            var (_, configClient) = GetClients();
            var res = await configClient.UpdateNodeConfigAsync(new UpdateNodeConfigRequest
            {
                NodeId = nodeId,
                Config = config
            }, headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return ClientResponseCompression.Decode(res, _configPayloads);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LogStatsResponse?> GetLogStatsAsync(string nodeId, CancellationToken ct = default)
    {
        try
        {
            var (queryClient, _) = GetClients();
            var res = await queryClient.GetLogStatsAsync(new LogStatsRequest { NodeId = nodeId }, headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return ClientResponseCompression.Decode(res, _logPayloads);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LogChunkResponse?> QueryLogChunksAsync(string nodeId, ulong startChunkId, uint maxChunks, long seekTs = 0, bool forward = true, CancellationToken ct = default)
    {
        try
        {
            var (queryClient, _) = GetClients();
            var req = new LogChunkQuery
            {
                NodeId = nodeId,
                StartChunkId = startChunkId,
                MaxChunks = maxChunks,
                SeekTimestampUnixNano = seekTs,
                Forward = forward
            };
            var res = await queryClient.QueryLogChunksAsync(req, headers: ClientResponseCompression.AcceptHeaders(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
            var decoded = ClientResponseCompression.Decode(res, _logPayloads);
            if (decoded?.Chunks != null && decoded.Chunks.Count > 0)
            {
                foreach (var chunk in decoded.Chunks)
                {
                    if (chunk.ZstdRecords != null && !chunk.ZstdRecords.IsEmpty)
                    {
                        _logPayloads.RecordCompressed((int)chunk.RawSize, chunk.ZstdRecords.Length);
                    }
                }
            }
            return decoded;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async IAsyncEnumerable<LogChunk> SubscribeLogsAsync(string nodeId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (queryClient, _) = GetClients();
        using var call = queryClient.SubscribeLogs(new LogSubscription { NodeId = nodeId }, cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var chunk = call.ResponseStream.Current;
            if (chunk.ZstdRecords != null && !chunk.ZstdRecords.IsEmpty)
            {
                _logPayloads.RecordCompressed((int)chunk.RawSize, chunk.ZstdRecords.Length);
            }
            else
            {
                _logPayloads.Record(chunk.CalculateSize());
            }
            yield return chunk;
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

