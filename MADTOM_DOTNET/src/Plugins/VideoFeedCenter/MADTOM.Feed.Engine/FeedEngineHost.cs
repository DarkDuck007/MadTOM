using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MADTOM.Identity;
using MADTOM.Ipc;
using MADTOM.Protocol.Core.V1;

namespace MADTOM.Feed.Engine;

public sealed class FeedEngineHost : IAsyncDisposable
{
    private readonly FeedManager _feedManager;
    private readonly TrustStore _trustStore;
    private readonly DeviceCertificate _hubCertificate;
    private readonly PairingServer _pairingServer;
    private readonly VideoFeedIpcService _feedIpcService;
    private readonly VideoSourceControlService _sourceControlService;

    private IHost? _host;
    private bool _isDisposed;

    public string LocalAddress { get; private set; } = string.Empty;

    public FeedManager FeedManager => _feedManager;
    public TrustStore TrustStore => _trustStore;
    public DeviceCertificate HubCertificate => _hubCertificate;
    public PairingServer PairingServer => _pairingServer;

    public FeedEngineHost(
        string? recordingsDir = null,
        string? trustStorePath = null,
        string? certPfxPath = null,
        string? hubHost = null,
        int tcpPort = 7443)
    {
        hubHost = !string.IsNullOrEmpty(hubHost) ? hubHost : PairingUtils.GetLocalLanIpAddress();
        _feedManager = new FeedManager(recordingsDir);
        _trustStore = new TrustStore(trustStorePath);

        var defaultPfx = certPfxPath ?? Path.Combine(Path.GetDirectoryName(TrustStore.GetDefaultStorePath())!, "hub_cert.pfx");
        _hubCertificate = DeviceCertificate.LoadOrCreate(defaultPfx, "MADTOM Hub");

        _pairingServer = new PairingServer(_trustStore, _hubCertificate);
        _feedIpcService = new VideoFeedIpcService(_feedManager, _pairingServer, hubHost, tcpPort);
        _sourceControlService = new VideoSourceControlService(_feedManager, _trustStore, hubHost);
    }

    public async Task StartAsync(int tcpPort = 7443, CancellationToken ct = default)
    {
        if (_host != null) return;
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);

                // Endpoint 2: TCP (HTTP/2 for mTLS / control from devices)
                options.Listen(IPAddress.Any, tcpPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificate = _hubCertificate.Certificate;
                        httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
                        httpsOptions.AllowAnyClientCertificate();
                    });
                });
            });

            webBuilder.ConfigureServices(services =>
            {
                services.AddGrpc();
                services.AddSingleton(_feedManager);
                services.AddSingleton(_pairingServer);
                services.AddSingleton(_feedIpcService);
                services.AddSingleton(_sourceControlService);
            });

            webBuilder.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<VideoFeedIpcService>();
                    endpoints.MapGrpcService<VideoSourceControlService>();
                    endpoints.MapGrpcService<PairingServer>();
                });
            });
        });

        _host = builder.Build();
        await _host.StartAsync(ct);

        var server = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        LocalAddress = System.Linq.Enumerable.First(addresses!.Addresses, address => address.StartsWith("http://"));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_host != null)
        {
            await _host.StopAsync(ct);
            _host.Dispose();
            _host = null;
        }

        await _feedManager.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync();
        _hubCertificate.Dispose();
    }
}
