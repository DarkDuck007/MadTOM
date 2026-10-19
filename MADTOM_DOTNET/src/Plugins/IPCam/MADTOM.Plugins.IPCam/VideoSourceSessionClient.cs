using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MADTOM.Media.Contracts;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Plugins.IPCam;

public enum SourceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Streaming
}

/// <summary>
/// Client session communicating with MADTOM Hub VideoSourceControlService over gRPC.
/// Handles initial Hello/Welcome handshake, capability negotiation (AT8),
/// live property application and echo (AT7), and NTP-style time probes.
/// </summary>
public sealed class VideoSourceSessionClient : IAsyncDisposable
{
    private readonly ICameraCaptureEngine _cameraEngine;
    private readonly string _hubAddress;
    private readonly string _sourceId;
    private readonly string _displayName;
    private readonly string _platform;
    private readonly StreamProfile? _preferredProfile;
    private readonly ChannelCredentials? _credentials;

    private GrpcChannel? _channel;
    private VideoSourceControl.VideoSourceControlClient? _client;
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private Task? _timeProbeTask;

    private SourceConnectionState _state = SourceConnectionState.Disconnected;
    private ulong _probeSequence;

    private readonly string? _pinnedFingerprint;
    private readonly MADTOM.Identity.DeviceCertificate? _clientCert;

    public SourceConnectionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string LastError { get; private set; } = string.Empty;
    public double ClockOffsetMs { get; private set; }
    public double RttMs { get; private set; }
    public StreamProfile? GrantedProfile { get; private set; }

    public event EventHandler<SourceConnectionState>? StateChanged;
    public event EventHandler<(double OffsetMs, double RttMs)>? TimeSyncUpdated;

    public VideoSourceSessionClient(
        ICameraCaptureEngine cameraEngine,
        string hubAddress,
        string sourceId,
        string displayName,
        string platform = "android",
        ChannelCredentials? credentials = null,
        string? pinnedFingerprint = null,
        MADTOM.Identity.DeviceCertificate? clientCert = null,
        StreamProfile? preferredProfile = null)
    {
        _cameraEngine = cameraEngine ?? throw new ArgumentNullException(nameof(cameraEngine));
        _hubAddress = hubAddress ?? throw new ArgumentNullException(nameof(hubAddress));
        _sourceId = string.IsNullOrEmpty(sourceId) ? "ipcam-" + Guid.NewGuid().ToString("N")[..8] : sourceId;
        _displayName = displayName;
        _platform = platform;
        _credentials = credentials;
        _pinnedFingerprint = pinnedFingerprint;
        _clientCert = clientCert;
        _preferredProfile = preferredProfile;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != SourceConnectionState.Disconnected) return;

        State = SourceConnectionState.Connecting;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _sessionCts.Token;

        if (!string.IsNullOrEmpty(_pinnedFingerprint) || _clientCert != null || _hubAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    {
                        if (certificate == null) return false;
                        if (string.IsNullOrEmpty(_pinnedFingerprint)) return true;

                        var certHash = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).ToLowerInvariant();
                        return string.Equals(certHash, _pinnedFingerprint, StringComparison.OrdinalIgnoreCase);
                    },
                    ClientCertificates = _clientCert != null ? new System.Security.Cryptography.X509Certificates.X509CertificateCollection { _clientCert.Certificate } : null
                }
            };
            var channelOptions = new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true
            };
            _channel = GrpcChannel.ForAddress(_hubAddress, channelOptions);
        }
        else
        {
            var channelOptions = new GrpcChannelOptions();
            if (_credentials != null)
            {
                channelOptions.Credentials = _credentials;
            }
            _channel = GrpcChannel.ForAddress(_hubAddress, channelOptions);
        }

        _client = new VideoSourceControl.VideoSourceControlClient(_channel);

        _sessionTask = Task.Run(() => RunSessionLoopAsync(token), token);
        await Task.CompletedTask;
    }

    private async Task RunSessionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            var outgoingChannel = Channel.CreateUnbounded<SourceMessage>();
            Task? sendTask = null;
            try
            {
                using var call = _client!.Session(cancellationToken: attempt.Token);


                // Send task
                sendTask = Task.Run(async () =>
                {
                    await foreach (var msg in outgoingChannel.Reader.ReadAllAsync(attempt.Token))
                    {
                        await call.RequestStream.WriteAsync(msg, token);
                    }
                }, token);

                // 1. Send Hello
                await outgoingChannel.Writer.WriteAsync(new SourceMessage
                {
                    Hello = new Hello
                    {
                        SourceId = _sourceId,
                        DisplayName = _displayName,
                        Platform = _platform,
                        ClientVersion = "1.0.0"
                    }
                }, token);

                // Receive loop
                while (await call.ResponseStream.MoveNext(attempt.Token))
                {
                    var hubMsg = call.ResponseStream.Current;

                    switch (hubMsg.BodyCase)
                    {
                        case HubMessage.BodyOneofCase.Welcome:
                            State = SourceConnectionState.Connected;

                            // Send Capabilities
                            var caps = _cameraEngine.GetCapabilityReport();
                            await outgoingChannel.Writer.WriteAsync(new SourceMessage
                            {
                                Capabilities = caps
                            }, token);

                            // Start time probes
                            StartTimeProbes(outgoingChannel, attempt);
                            break;

                        case HubMessage.BodyOneofCase.Start:
                            var reqProfile = hubMsg.Start.Profile;
                            var srtTarget = hubMsg.Start.Target;

                            // AT8: Encoder actual format read-back and granted echo
                            var granted = _cameraEngine.GetGrantedProfile(_preferredProfile ?? reqProfile);
                            GrantedProfile = granted;

                            await _cameraEngine.StartCaptureAsync(granted, srtTarget, token);
                            State = SourceConnectionState.Streaming;

                            // Send Ack with Granted profile
                            await outgoingChannel.Writer.WriteAsync(new SourceMessage
                            {
                                Ack = new Ack
                                {
                                    RequestId = hubMsg.Start.RequestId,
                                    Ok = true,
                                    Message = "Stream started successfully",
                                    Granted = granted
                                }
                            }, token);
                            break;

                        case HubMessage.BodyOneofCase.Stop:
                            await _cameraEngine.StopCaptureAsync(token);
                            State = SourceConnectionState.Connected;
                            break;

                        case HubMessage.BodyOneofCase.Set:
                            if (hubMsg.Set.PropertyId == "camera.zoom_ratio")
                            {
                                // AT7: Sensor applied zoom ratio echo
                                float appliedZoom = await _cameraEngine.SetZoomRatioAsync((float)hubMsg.Set.Value, token);

                                await outgoingChannel.Writer.WriteAsync(new SourceMessage
                                {
                                    State = new PropertyState
                                    {
                                        PropertyId = "camera.zoom_ratio",
                                        Value = appliedZoom,
                                        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    }
                                }, token);
                            }
                            break;

                        case HubMessage.BodyOneofCase.TimeReply:
                            long t4 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var sync = ClockSyncCalculator.Calculate(
                                hubMsg.TimeReply.ClientSendUtcMs,
                                hubMsg.TimeReply.ServerReceiveUtcMs,
                                hubMsg.TimeReply.ServerReplyUtcMs,
                                t4);

                            ClockOffsetMs = sync.OffsetMs;
                            RttMs = sync.RttMs;
                            TimeSyncUpdated?.Invoke(this, (ClockOffsetMs, RttMs));
                            break;
                    }
                }

                outgoingChannel.Writer.TryComplete();
                attempt.Cancel();
                try { await sendTask; } catch { }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) break;
                State = SourceConnectionState.Disconnected;
                await Task.Delay(1000, token);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = SourceConnectionState.Disconnected;
                // Wait with backoff before reconnecting
                try
                {
                    await Task.Delay(3000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                attempt.Cancel();
                outgoingChannel.Writer.TryComplete();
                if (sendTask != null) { try { await sendTask; } catch { } }
                if (_timeProbeTask != null) { try { await _timeProbeTask; } catch { } }
                await _cameraEngine.StopCaptureAsync();
                State = SourceConnectionState.Disconnected;
            }
        }

        State = SourceConnectionState.Disconnected;
    }

    private void StartTimeProbes(Channel<SourceMessage> outgoing, CancellationTokenSource attempt)
    {
        var token = attempt.Token;
        _timeProbeTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && State != SourceConnectionState.Disconnected)
            {
                try
                {
                    if (State == SourceConnectionState.Streaming && !_cameraEngine.IsCapturing)
                    {
                        LastError = "Capture stopped; reconnecting.";
                        attempt.Cancel();
                        break;
                    }
                    long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await outgoing.Writer.WriteAsync(new SourceMessage
                    {
                        TimeProbe = new TimeProbe
                        {
                            ClientSendUtcMs = t1,
                            ProbeSequence = ++_probeSequence
                        }
                    }, token);

                    if (State == SourceConnectionState.Streaming)
                    {
                        await outgoing.Writer.WriteAsync(new SourceMessage
                        {
                            Stats = new Stats
                            {
                                FeedId = _sourceId,
                                CurrentFps = GrantedProfile?.Fps ?? 30,
                                CurrentBitrateBps = GrantedProfile?.BitrateBps ?? 6_000_000,
                                RttMs = (uint)Math.Max(0, RttMs)
                            }
                        }, token);
                    }

                    await Task.Delay(3000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { break; }
            }
        }, token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionCts != null)
        {
            _sessionCts.Cancel();
            if (_sessionTask != null)
            {
                try { await _sessionTask; } catch { }
            }
            if (_timeProbeTask != null)
            {
                try { await _timeProbeTask; } catch { }
            }
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        await _cameraEngine.StopCaptureAsync(cancellationToken);

        if (_channel != null)
        {
            _channel.Dispose();
            _channel = null;
        }

        State = SourceConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

    }
}
