using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.Telemetry.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.Services;

public sealed record NoxNodeModel(
    string Id,
    string HostEndpoint,
    string AuthKey,
    string Alias,
    bool IsMdnsDiscovered = false,
    TelemetryOptInMode LogMode = TelemetryOptInMode.OptInMonitorAndStore,
    bool IncludeApiCalls = true,
    bool IncludeImages = true
)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Id : $"{Alias} ({Id})";
    public string AliasOrId => string.IsNullOrWhiteSpace(Alias) ? Id : Alias;
    public string Endpoint => HostEndpoint;
}

/// <summary>
/// Manages active gRPC channels, resilient streaming subscriptions, and node persistence.
/// </summary>
public sealed class NoxNodeManager : IAsyncDisposable
{
    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NoxNodeModel> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public event Action? NodesChanged;
    public event Action<NoxNodeModel>? NodeSettingsChanged;

    public IReadOnlyCollection<NoxNodeModel> Nodes => _nodes.Values.ToList();

    public NoxNodeManager(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "noxchat-nodes.json");
        LoadNodes();
    }

    public void AddOrUpdateNode(NoxNodeModel node)
    {
        _nodes[node.Id] = node;
        SaveNodes();
        NodesChanged?.Invoke();
    }

    public void UpdateNodeSettings(string nodeId, TelemetryOptInMode logMode, bool includeApiCalls, bool includeImages)
    {
        if (_nodes.TryGetValue(nodeId, out var existing))
        {
            var updated = existing with
            {
                LogMode = logMode,
                IncludeApiCalls = includeApiCalls,
                IncludeImages = includeImages
            };
            _nodes[nodeId] = updated;
            SaveNodes();
            NodeSettingsChanged?.Invoke(updated);
        }
    }

    public void RemoveNode(string nodeId)
    {
        if (_nodes.TryRemove(nodeId, out _))
        {
            if (_channels.TryRemove(nodeId, out var ch))
            {
                ch.Dispose();
            }
            SaveNodes();
            NodesChanged?.Invoke();
        }
    }

    public NoxChatControlService.NoxChatControlServiceClient GetClient(NoxNodeModel node)
    {
        var channel = _channels.GetOrAdd(node.Id, _ =>
        {
            string url = node.HostEndpoint.StartsWith("http") ? node.HostEndpoint : $"https://{node.HostEndpoint}";
            var httpHandler = new SocketsHttpHandler
            {
                // Accept self-signed TLS cert from probe
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
                },
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            };

            return GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = httpHandler,
                MaxReceiveMessageSize = 32 * 1024 * 1024
            });
        });

        var callInvoker = channel.Intercept(new NoxAuthInterceptor(node));
        return new NoxChatControlService.NoxChatControlServiceClient(callInvoker);
    }

    private sealed class NoxAuthInterceptor : Interceptor
    {
        private readonly NoxNodeModel _node;

        public NoxAuthInterceptor(NoxNodeModel node) => _node = node;

        private void AddAuthHeaders(Metadata metadata)
        {
            metadata.Add("madtom-accept-zstd", "1");
            if (!string.IsNullOrEmpty(_node.AuthKey))
            {
                var (ts, nonce, sig) = NoxAuthTokenGenerator.CreateAuthHeaders(_node.AuthKey);
                if (!string.IsNullOrEmpty(sig))
                {
                    metadata.Add("x-nox-auth-ts", ts);
                    metadata.Add("x-nox-auth-nonce", nonce);
                    metadata.Add("x-nox-auth-sig", sig);
                }
            }
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            var headers = context.Options.Headers ?? new Metadata();
            AddAuthHeaders(headers);
            var newOptions = context.Options.WithHeaders(headers);
            var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
            return continuation(request, newContext);
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            var headers = context.Options.Headers ?? new Metadata();
            AddAuthHeaders(headers);
            var newOptions = context.Options.WithHeaders(headers);
            var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
            return continuation(request, newContext);
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            var headers = context.Options.Headers ?? new Metadata();
            AddAuthHeaders(headers);
            var newOptions = context.Options.WithHeaders(headers);
            var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
            return continuation(newContext);
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            var headers = context.Options.Headers ?? new Metadata();
            AddAuthHeaders(headers);
            var newOptions = context.Options.WithHeaders(headers);
            var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
            return continuation(newContext);
        }
    }

    private void LoadNodes()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                string rawJson = File.ReadAllText(_storePath);
                bool hasLogModeField = rawJson.Contains("\"LogMode\"", StringComparison.OrdinalIgnoreCase);
                var list = JsonSerializer.Deserialize<List<NoxNodeModel>>(rawJson);
                if (list != null)
                {
                    foreach (var n in list)
                    {
                        var model = hasLogModeField
                            ? n
                            : n with
                            {
                                LogMode = TelemetryOptInMode.OptInMonitorAndStore,
                                IncludeApiCalls = true,
                                IncludeImages = true
                            };
                        _nodes[model.Id] = model;
                    }
                    if (!hasLogModeField)
                    {
                        SaveNodes();
                    }
                }
            }
        }
        catch { }

        // Default local node if empty
        if (_nodes.IsEmpty)
        {
            var def = new NoxNodeModel("Localhost", "127.0.0.1:50055", string.Empty, "Local NoxChat Node");
            _nodes[def.Id] = def;
        }
    }

    private void SaveNodes()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var list = new List<NoxNodeModel>(_nodes.Values);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var ch in _channels.Values)
        {
            ch.Dispose();
        }
        _channels.Clear();
        return ValueTask.CompletedTask;
    }
}
