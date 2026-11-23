using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.SlideForge.Services;

public sealed record SfNodeModel(
    string Id,
    string HostEndpoint,
    string AuthKey,
    string Alias,
    bool IsMdnsDiscovered = false,
    bool StoreRawLogs = false,
    bool IncludeApiCalls = true
)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Id : $"{Alias} ({Id})";
    public string AliasOrId => string.IsNullOrWhiteSpace(Alias) ? Id : Alias;
    public string Endpoint => HostEndpoint;
}

/// <summary>
/// Manages SlideForge gRPC channels, Ed25519 request signing, TOFU TLS certificate pinning, and node persistence.
/// </summary>
public sealed class SfNodeManager : IAsyncDisposable
{
    private readonly SfSettingsStore _settingsStore;
    private readonly Func<SfNodeModel, SlideForgeControlService.SlideForgeControlServiceClient>? _clientFactory;
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SfNodeModel> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public SfCertificatePins Pins { get; }

    public event Action? NodesChanged;
    public event Action<string, string>? CertificateFirstUseDetected;
    public event Action<string, string, string>? CertificateMismatchDetected;

    public IReadOnlyCollection<SfNodeModel> Nodes => _nodes.Values.ToList();

    public SfNodeManager(
        string? settingsDir = null,
        Func<SfNodeModel, SlideForgeControlService.SlideForgeControlServiceClient>? clientFactory = null)
    {
        _clientFactory = clientFactory;
        _settingsStore = new SfSettingsStore(settingsDir);
        var loaded = _settingsStore.Load();
        Pins = new SfCertificatePins(loaded.PinFilePath);

        foreach (var n in loaded.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(n.Id))
                _nodes[n.Id] = n;
        }

        if (_nodes.IsEmpty)
        {
            var def = new SfNodeModel("slideforge", "127.0.0.1:50057", string.Empty, "SlideForge");
            _nodes[def.Id] = def;
        }
    }

    public static string NormalizeNodeKey(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        string s = endpoint.Trim();
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s["https://".Length..];
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s["http://".Length..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s.ToLowerInvariant();
    }

    public void AddOrUpdateNode(SfNodeModel node)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            if (!string.Equals(existing.HostEndpoint, node.HostEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateChannel(node.Id);
            }
        }
        _nodes[node.Id] = node;
        SaveSettings();
        NodesChanged?.Invoke();
    }

    public void RemoveNode(string nodeId)
    {
        if (_nodes.TryRemove(nodeId, out _))
        {
            InvalidateChannel(nodeId);
            SaveSettings();
            NodesChanged?.Invoke();
        }
    }

    public void TrustPin(string nodeKey, string fingerprint, string? nodeId = null)
    {
        Pins.Pin(nodeKey, fingerprint);
        if (!string.IsNullOrWhiteSpace(nodeId))
            InvalidateChannel(nodeId);
        else
            InvalidateAllChannels();
    }

    public void ForgetPin(string nodeKey, string? nodeId = null)
    {
        Pins.Forget(nodeKey);
        if (!string.IsNullOrWhiteSpace(nodeId))
            InvalidateChannel(nodeId);
        else
            InvalidateAllChannels();
    }

    public void InvalidateChannel(string nodeId)
    {
        if (_channels.TryRemove(nodeId, out var oldCh))
        {
            try { oldCh.Dispose(); } catch { }
        }
    }

    public void InvalidateAllChannels()
    {
        foreach (var key in _channels.Keys.ToList())
            InvalidateChannel(key);
    }

    public SlideForgeControlService.SlideForgeControlServiceClient GetClient(SfNodeModel node)
    {
        if (_clientFactory != null) return _clientFactory(node);

        string nodeKey = NormalizeNodeKey(node.HostEndpoint);
        var channel = _channels.GetOrAdd(node.Id, _ =>
        {
            string url = node.HostEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? node.HostEndpoint
                : $"https://{node.HostEndpoint}";

            var httpHandler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => ValidateTlsCertificate(nodeKey, certificate)
                },
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            };

            return GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = httpHandler,
                MaxReceiveMessageSize = 256 * 1024 * 1024,
                MaxSendMessageSize = 256 * 1024 * 1024
            });
        });

        var callInvoker = channel.Intercept(new SfAuthInterceptor(node));
        return new SlideForgeControlService.SlideForgeControlServiceClient(callInvoker);
    }

    public SfRpc CreateRpc(SfNodeModel node) => new(GetClient(node), node.Id);

    private bool ValidateTlsCertificate(string nodeKey, X509Certificate? certificate)
    {
        if (certificate is null) return false;

        X509Certificate2? owned = null;
        try
        {
            X509Certificate2 x509;
            if (certificate is X509Certificate2 existing)
            {
                x509 = existing;
            }
            else
            {
                owned = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                x509 = owned;
            }

            if (Pins.Validate(nodeKey, x509, trustOnFirstUse: false))
                return true;

            string presented = SfCertificatePins.Fingerprint(x509);
            var eval = Pins.Evaluate(nodeKey, presented);
            if (eval == SfPinResult.FirstUse)
            {
                CertificateFirstUseDetected?.Invoke(nodeKey, presented);
            }
            else
            {
                string pinned = Pins.GetPin(nodeKey) ?? Pins.LastMismatch?.Pinned ?? "<corrupt>";
                CertificateMismatchDetected?.Invoke(nodeKey, pinned, presented);
            }

            return false;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(new SfSettingsStore.Settings
            {
                PinFilePath = Pins.FilePath,
                Nodes = _nodes.Values.ToList()
            });
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        InvalidateAllChannels();
        return ValueTask.CompletedTask;
    }

    private sealed class SfAuthInterceptor : Interceptor
    {
        private readonly SfNodeModel _node;

        public SfAuthInterceptor(SfNodeModel node) => _node = node;

        private void AddAuthHeaders(Metadata metadata)
        {
            metadata.Add("madtom-accept-zstd", "1");
            if (!string.IsNullOrEmpty(_node.AuthKey))
            {
                var (ts, nonce, sig) = SfAuthTokenGenerator.CreateAuthHeaders(_node.AuthKey);
                if (!string.IsNullOrEmpty(sig))
                {
                    metadata.Add("x-nox-auth-ts", ts);
                    metadata.Add("x-nox-auth-nonce", nonce);
                    metadata.Add("x-nox-auth-sig", sig);
                }
            }
        }

        private static async Task<TResponse> DecodeResponseAsync<TResponse>(Task<TResponse> responseTask)
        {
            var response = await responseTask.ConfigureAwait(false);
            if (response is IMessage msg)
            {
                var payloadField = msg.Descriptor.FindFieldByName("zstd_payload");
                var sizeField = msg.Descriptor.FindFieldByName("decoded_size");
                if (payloadField == null || sizeField == null)
                    return response;

                var payload = (ByteString)payloadField.Accessor.GetValue(msg);
                var size = (uint)sizeField.Accessor.GetValue(msg);
                if (payload.IsEmpty)
                {
                    if (size != 0) throw new InvalidDataException("Missing zstd response payload.");
                    return response;
                }
                if (size == 0 || size > ZstdCodec.MaxDecodedBytes)
                    throw new InvalidDataException("Invalid zstd response size.");

                var raw = ZstdCodec.Decode(payload.Span, (int)size);
                var decoded = msg.Descriptor.Parser.ParseFrom(raw);
                return (TResponse)decoded;
            }
            return response;
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
            var inner = continuation(request, newContext);
            return new AsyncUnaryCall<TResponse>(
                DecodeResponseAsync(inner.ResponseAsync),
                inner.ResponseHeadersAsync,
                inner.GetStatus,
                inner.GetTrailers,
                inner.Dispose);
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
}
