using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Identity;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Feed.Engine;

public sealed class VideoSourceControlService : VideoSourceControl.VideoSourceControlBase
{
    private readonly FeedManager _feedManager;
    private readonly ITrustProvider _trustProvider;
    private readonly string _hubHost;

    public VideoSourceControlService(FeedManager feedManager, ITrustProvider trustProvider, string hubHost = "127.0.0.1")
    {
        _feedManager = feedManager ?? throw new ArgumentNullException(nameof(feedManager));
        _trustProvider = trustProvider ?? throw new ArgumentNullException(nameof(trustProvider));
        _hubHost = hubHost;
    }

    public override async Task Session(
        IAsyncStreamReader<SourceMessage> requestStream,
        IServerStreamWriter<HubMessage> responseStream,
        ServerCallContext context)
    {
        try
        {
            var clientCert = context.AuthContext?.PeerIdentity;
        }
        catch { }
        // In mTLS, verify trust against _trustProvider
        // If untrusted, throw RpcException with StatusCode.Unauthenticated (AT3)
        X509Certificate2? clientCertDer = null;
        try
        {
            var httpContext = context.GetHttpContext();
            if (httpContext != null)
            {
                clientCertDer = httpContext.Connection.ClientCertificate;
                if (httpContext.Connection.RemoteIpAddress != null && !System.Net.IPAddress.IsLoopback(httpContext.Connection.RemoteIpAddress))
                {
                    if (clientCertDer == null)
                    {
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Remote device must present a client certificate (AT3)."));
                    }
                }
            }
        }
        catch (RpcException)
        {
            throw;
        }
        catch
        {
            // Outside Kestrel HTTP context (e.g. direct test or in-process call)
        }

        if (clientCertDer != null)
        {
            var fp = Convert.ToHexString(clientCertDer.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
            if (!_trustProvider.IsTrusted(fp, "video.source.publish"))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Device certificate is not trusted in MADTOM trust store."));
            }
            _trustProvider.UpdateLastSeen(fp);
        }

        var sourceTargetHost = _hubHost;
        try { sourceTargetHost = context.GetHttpContext().Connection.LocalIpAddress?.MapToIPv4().ToString() ?? _hubHost; }
        catch (InvalidOperationException) { }
        string sourceId = string.Empty;
        string sourcePlatform = string.Empty;
        string sourceDisplayName = string.Empty;
        string feedId = string.Empty;
        FeedSession? session = null;
        var outgoingCommands = Channel.CreateUnbounded<HubMessage>();

        // Pump outgoing commands to responseStream
        var sendTask = Task.Run(async () =>
        {
            await foreach (var msg in outgoingCommands.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(msg, context.CancellationToken);
            }
        });

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var msg = requestStream.Current;

                switch (msg.BodyCase)
                {
                    case SourceMessage.BodyOneofCase.Hello:
                        sourceId = msg.Hello.SourceId;
                        sourcePlatform = msg.Hello.Platform;
                        sourceDisplayName = msg.Hello.DisplayName;
                        await outgoingCommands.Writer.WriteAsync(new HubMessage
                        {
                            Welcome = new Welcome
                            {
                                HubId = "madtom-hub",
                                HubName = "MADTOM Hub",
                                SessionId = Guid.NewGuid().ToString("N")
                            }
                        }, context.CancellationToken);
                        break;

                    case SourceMessage.BodyOneofCase.Capabilities:
                        var caps = msg.Capabilities;
                        feedId = string.IsNullOrEmpty(sourceId) ? "feed-" + Guid.NewGuid().ToString("N")[..8] : sourceId;

                        // Check if session already exists for this feed (e.g. reconnect after network drop - AT9)
                        session = _feedManager.GetFeed(feedId);
                        if (session != null)
                        {
                            session.UpdateState(FeedState.Waiting);
                            session.PropertySetter = async setReq =>
                            {
                                await outgoingCommands.Writer.WriteAsync(new HubMessage
                                {
                                    Set = setReq
                                }, context.CancellationToken);
                            };
                            session.StartIngest();

                            await outgoingCommands.Writer.WriteAsync(new HubMessage
                            {
                                Start = new StartStream
                                {
                                    RequestId = Guid.NewGuid().ToString("N"),
                                    FeedId = feedId,
                                    Profile = session.Info.RequestedProfile,
                                    Target = session.Info.SrtTarget
                                }
                            }, context.CancellationToken);
                            break;
                        }

                        // Create new feed session
                        int srtPort = _feedManager.AllocatePort();
                        string passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

                        var requestedProfile = new StreamProfile
                        {
                            CameraId = caps.Cameras.Count > 0 ? caps.Cameras[0].CameraId : "0",
                            Codec = "avc",
                            Width = 1280,
                            Height = 720,
                            Fps = 30,
                            BitrateBps = 6_000_000,
                            RateControl = "cbr",
                            IdrIntervalMs = 2000
                        };

                        var feedInfo = new FeedInfo
                        {
                            FeedId = feedId,
                            SourceId = sourceId,
                            DisplayName = !string.IsNullOrEmpty(sourceDisplayName) ? sourceDisplayName : $"IPCam {sourceId}",
                            IsTrusted = true,
                            SourcePlatform = !string.IsNullOrEmpty(sourcePlatform) ? sourcePlatform : "desktop",
                            State = FeedState.Waiting,
                            RequestedProfile = requestedProfile,
                            SrtTarget = new SrtTarget
                            {
                                Host = sourceTargetHost,
                                Port = (uint)srtPort,
                                StreamId = feedId,
                                Passphrase = passphrase,
                                LatencyMs = 120
                            }
                        };

                        foreach (var p in caps.Properties)
                        {
                            feedInfo.Properties.Add(p);
                        }

                        session = _feedManager.CreateOrGetFeed(feedInfo);

                        // Attach property setter callback so hub can send SetProperty down to phone
                        session.PropertySetter = async setReq =>
                        {
                            await outgoingCommands.Writer.WriteAsync(new HubMessage
                            {
                                Set = setReq
                            }, context.CancellationToken);
                        };

                        session.StartIngest();

                        // Send StartStream
                        await outgoingCommands.Writer.WriteAsync(new HubMessage
                        {
                            Start = new StartStream
                            {
                                RequestId = Guid.NewGuid().ToString("N"),
                                FeedId = feedId,
                                Profile = requestedProfile,
                                Target = feedInfo.SrtTarget
                            }
                        }, context.CancellationToken);
                        break;

                    case SourceMessage.BodyOneofCase.Ack:
                        if (session != null && msg.Ack.Granted != null)
                        {
                            // AT8: Encoder actual granted format echo
                            session.UpdateGrantedProfile(msg.Ack.Granted);
                        }
                        break;

                    case SourceMessage.BodyOneofCase.State:
                        if (session != null)
                        {
                            // AT7: Sensor applied zoom ratio echo
                            session.UpdateProperty(msg.State.PropertyId, msg.State.Value, msg.State.StringValue);
                        }
                        break;

                    case SourceMessage.BodyOneofCase.Stats:
                        if (session != null)
                        {
                            session.UpdateStats(msg.Stats.CurrentFps, msg.Stats.CurrentBitrateBps, (int)msg.Stats.RttMs);
                        }
                        break;

                    case SourceMessage.BodyOneofCase.TimeProbe:
                        long serverRx = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long serverReply = serverRx;
                        await outgoingCommands.Writer.WriteAsync(new HubMessage
                        {
                            TimeReply = new TimeReply
                            {
                                ClientSendUtcMs = msg.TimeProbe.ClientSendUtcMs,
                                ServerReceiveUtcMs = serverRx,
                                ServerReplyUtcMs = serverReply,
                                ProbeSequence = msg.TimeProbe.ProbeSequence
                            }
                        }, context.CancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            outgoingCommands.Writer.TryComplete();
            try { await sendTask; } catch { }

            if (session != null)
            {
                session.UpdateState(FeedState.Stopped);
            }
        }
    }
}
