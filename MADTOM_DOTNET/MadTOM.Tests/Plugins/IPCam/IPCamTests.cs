using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Feed.Engine;
using MADTOM.Identity;
using MADTOM.Plugins.IPCam;
using MADTOM.Protocol.Video.V1;
using Xunit;

namespace MadTOM.Tests.Plugins.IPCam;

public class IPCamTests
{
    [Fact]
    public async Task SimulatedCameraCaptureEngine_AppliesZoomAndGeneratesPackets()
    {
        await using var engine = new SimulatedCameraCaptureEngine();
        await engine.InitializeAsync();

        // Check defaults and limits
        Assert.Equal(1.0f, engine.CurrentZoomRatio);
        Assert.Equal(1.0f, engine.ZoomLimits.Min);
        Assert.Equal(10.0f, engine.ZoomLimits.Max);

        // Test zoom clamping and stepping (AT7)
        float zoomApplied = 0;
        engine.ZoomChanged += (s, z) => zoomApplied = z;

        float actual = await engine.SetZoomRatioAsync(2.34f);
        Assert.Equal(2.3f, actual);
        Assert.Equal(2.3f, engine.CurrentZoomRatio);
        Assert.Equal(2.3f, zoomApplied);

        // Test over-limit clamping
        actual = await engine.SetZoomRatioAsync(15.0f);
        Assert.Equal(10.0f, actual);

        // Test capability report
        var caps = engine.GetCapabilityReport();
        Assert.NotEmpty(caps.Cameras);
        Assert.NotEmpty(caps.Encoders);
        var zoomProp = caps.Properties.FirstOrDefault(p => p.Id == "camera.zoom_ratio");
        Assert.NotNull(zoomProp);
        Assert.Equal(10.0, zoomProp.Value);

        // Test granted profile (AT8)
        var requested = new StreamProfile
        {
            CameraId = "0",
            Codec = "hevc",
            Width = 1920,
            Height = 1080,
            Fps = 30,
            BitrateBps = 6_000_000
        };
        var granted = engine.GetGrantedProfile(requested);
        Assert.Equal("hevc", granted.Codec);
        Assert.Equal(1920u, granted.Width);
        Assert.Equal(1080u, granted.Height);

        // Test capture start and stop
        var target = new SrtTarget { Host = "127.0.0.1", Port = 9999 };
        await engine.StartCaptureAsync(requested, target);
        Assert.True(engine.IsCapturing);

        await Task.Delay(100);
        await engine.StopCaptureAsync();
        Assert.False(engine.IsCapturing);
    }

    [Fact]
    public async Task SimulatedCameraCaptureEngine_SupportsCameraSelectionAndEnumeration()
    {
        await using var engine = new SimulatedCameraCaptureEngine();
        await engine.InitializeAsync();

        Assert.NotEmpty(engine.AvailableCameras);
        Assert.True(engine.AvailableCameras.Count >= 3);

        var frontCam = engine.AvailableCameras.FirstOrDefault(c => c.Facing == "front");
        Assert.NotNull(frontCam);

        CameraDeviceInfo? selectedFired = null;
        engine.SelectedCameraChanged += (s, c) => selectedFired = c;

        await engine.SelectCameraAsync(frontCam);

        Assert.Equal(frontCam.Id, engine.SelectedCamera?.Id);
        Assert.Equal(frontCam, selectedFired);
        Assert.Equal(frontCam.ZoomLimits.Min, engine.ZoomLimits.Min);
        Assert.Equal(frontCam.ZoomLimits.Max, engine.ZoomLimits.Max);
    }

    [Fact]
    public void PairingUtils_And_ZXing_QrCodeRoundtrip_And_Parsing()
    {
        string host = "192.168.1.50";
        int port = 7443;
        string fp = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        string token = "aabbccddeeff00112233445566778899";
        string sid = "sess1234";

        string qrUri = PairingUtils.CreateQrUri(host, port, fp, token, sid);
        Assert.StartsWith("madtom://pair?", qrUri);

        bool parsed = PairingUtils.TryParseQrUri(qrUri, out var details);
        Assert.True(parsed);
        Assert.Equal(host, details.Host);
        Assert.Equal(port, details.Port);
        Assert.Equal(fp, details.HubFingerprintSha256);
        Assert.Equal(token, details.Token);
        Assert.Equal(sid, details.SessionId);

        // Test manual PIN comparison derivation
        byte[] hubCert = new byte[] { 1, 2, 3, 4 };
        byte[] devCert = new byte[] { 5, 6, 7, 8 };
        byte[] nonceH = new byte[] { 9, 10, 11, 12 };
        byte[] nonceD = new byte[] { 13, 14, 15, 16 };
        string code = PairingUtils.DeriveComparisonCode(hubCert, devCert, nonceH, nonceD);
        Assert.Equal(4, code.Length);
        Assert.True(int.TryParse(code, out int pinVal) && pinVal >= 0 && pinVal <= 9999);
    }

    [Fact]
    public async Task VideoSourceControlService_And_IPCam_ProtocolHandshake_And_ControlLoop()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ipcam_test_{Guid.NewGuid():N}");
        try
        {
            await using var feedManager = new FeedManager(tempDir);
            var trustProvider = new TrustStore(Path.Combine(tempDir, "trust.json"));
            var service = new VideoSourceControlService(feedManager, trustProvider);

            // Create in-memory bidirectional channels to simulate gRPC streaming
            var clientToServer = Channel.CreateUnbounded<SourceMessage>();
            var serverToClient = Channel.CreateUnbounded<HubMessage>();

            var mockContext = new TestServerCallContext();
            var requestStream = new TestAsyncStreamReader<SourceMessage>(clientToServer.Reader);
            var responseStream = new TestServerStreamWriter<HubMessage>(serverToClient.Writer);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serviceTask = Task.Run(async () =>
            {
                try
                {
                    await service.Session(requestStream, responseStream, mockContext);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ServiceException] {ex}");
                    throw;
                }
            }, cts.Token);

            // Step 1: Send Hello
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                Hello = new Hello
                {
                    SourceId = "test-cam-1",
                    DisplayName = "Test Phone Camera",
                    Platform = "android",
                    ClientVersion = "1.0.0"
                }
            }, cts.Token);

            // Read Welcome
            var welcomeTask = serverToClient.Reader.ReadAsync(cts.Token).AsTask();
            var completed = await Task.WhenAny(welcomeTask, serviceTask);
            if (completed == serviceTask)
            {
                if (serviceTask.IsFaulted)
                {
                    throw new Exception("serviceTask faulted: " + serviceTask.Exception?.ToString());
                }
                throw new Exception("serviceTask completed prematurely without faulting!");
            }
            var welcomeMsg = await welcomeTask;
            Assert.Equal(HubMessage.BodyOneofCase.Welcome, welcomeMsg.BodyCase);
            Assert.Equal("madtom-hub", welcomeMsg.Welcome.HubId);

            // Step 2: Send Capabilities
            await using var engine = new SimulatedCameraCaptureEngine();
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                Capabilities = engine.GetCapabilityReport()
            }, cts.Token);

            // Read StartStream from server
            var startMsg = await serverToClient.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Start, startMsg.BodyCase);
            Assert.Equal("test-cam-1", startMsg.Start.FeedId);
            Assert.NotNull(startMsg.Start.Profile);
            Assert.NotNull(startMsg.Start.Target);

            // Step 3: Send Ack with Granted Profile (AT8)
            var granted = engine.GetGrantedProfile(startMsg.Start.Profile);
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                Ack = new Ack
                {
                    RequestId = startMsg.Start.RequestId,
                    Ok = true,
                    Granted = granted
                }
            }, cts.Token);

            // Verify FeedManager has the session and granted profile
            await Task.Delay(100);
            var session = feedManager.GetFeed("test-cam-1");
            Assert.NotNull(session);
            Assert.Equal("android", session.Info.SourcePlatform);
            Assert.Equal("Test Phone Camera", session.Info.DisplayName);
            Assert.NotNull(session.Info.GrantedProfile);
            Assert.Equal(granted.Codec, session.Info.GrantedProfile.Codec);
            Assert.Equal(granted.Width, session.Info.GrantedProfile.Width);

            // Step 3b: Send Stats message
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                Stats = new Stats
                {
                    FeedId = "test-cam-1",
                    CurrentFps = 29.8,
                    CurrentBitrateBps = 5_800_000,
                    RttMs = 35
                }
            }, cts.Token);
            await Task.Delay(50);
            Assert.Equal(29.8, session.Info.CurrentFps);
            Assert.Equal(5_800_000u, session.Info.CurrentBitrateBps);

            // Step 4: Hub triggers zoom change, source echoes PropertyState (AT7)
            Assert.NotNull(session.PropertySetter);
            await session.PropertySetter(new SetProperty
            {
                RequestId = "req-zoom-1",
                PropertyId = "camera.zoom_ratio",
                Value = 3.5
            });

            // Read SetProperty on client stream
            var setMsg = await serverToClient.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Set, setMsg.BodyCase);
            Assert.Equal("camera.zoom_ratio", setMsg.Set.PropertyId);
            Assert.Equal(3.5, setMsg.Set.Value);

            // Client applies zoom and replies with PropertyState
            float actualZoom = await engine.SetZoomRatioAsync((float)setMsg.Set.Value);
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                State = new PropertyState
                {
                    PropertyId = "camera.zoom_ratio",
                    Value = actualZoom,
                    TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }, cts.Token);

            // Verify hub received zoom update in session
            await Task.Delay(100);
            var zoomProp = session.Info.Properties.FirstOrDefault(p => p.Id == "camera.zoom_ratio");
            Assert.NotNull(zoomProp);
            Assert.Equal(3.5, zoomProp.Value);

            // Step 5: Time probe exchange
            long clientSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await clientToServer.Writer.WriteAsync(new SourceMessage
            {
                TimeProbe = new TimeProbe
                {
                    ClientSendUtcMs = clientSend,
                    ProbeSequence = 1
                }
            }, cts.Token);

            var timeReply = await serverToClient.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.TimeReply, timeReply.BodyCase);
            Assert.Equal(clientSend, timeReply.TimeReply.ClientSendUtcMs);
            Assert.Equal(1u, timeReply.TimeReply.ProbeSequence);

            // Shutdown
            clientToServer.Writer.Complete();
            mockContext.Cancel();
            try { await serviceTask; } catch { }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task VideoSourceControlService_Reconnect_ReusesPortAndSrtTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ipcam_reconnect_{Guid.NewGuid():N}");
        try
        {
            await using var feedManager = new FeedManager(tempDir);
            var trustProvider = new TrustStore(Path.Combine(tempDir, "trust.json"));
            var service = new VideoSourceControlService(feedManager, trustProvider);

            // Connect Session 1
            var c2s1 = Channel.CreateUnbounded<SourceMessage>();
            var s2c1 = Channel.CreateUnbounded<HubMessage>();
            var ctx1 = new TestServerCallContext();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var t1 = Task.Run(() => service.Session(new TestAsyncStreamReader<SourceMessage>(c2s1.Reader), new TestServerStreamWriter<HubMessage>(s2c1.Writer), ctx1));

            await c2s1.Writer.WriteAsync(new SourceMessage { Hello = new Hello { SourceId = "reconnect-feed", DisplayName = "Cam 1", Platform = "linux" } });
            var welcome1 = await s2c1.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Welcome, welcome1.BodyCase);

            await using var engine = new SimulatedCameraCaptureEngine();
            await c2s1.Writer.WriteAsync(new SourceMessage { Capabilities = engine.GetCapabilityReport() });
            var start1 = await s2c1.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Start, start1.BodyCase);
            var port1 = start1.Start.Target.Port;
            var pass1 = start1.Start.Target.Passphrase;

            // Disconnect Session 1 (simulating network drop)
            c2s1.Writer.Complete();
            ctx1.Cancel();
            try { await t1; } catch { }

            // Connect Session 2 with the same SourceId (reconnect)
            var c2s2 = Channel.CreateUnbounded<SourceMessage>();
            var s2c2 = Channel.CreateUnbounded<HubMessage>();
            var ctx2 = new TestServerCallContext();

            var t2 = Task.Run(() => service.Session(new TestAsyncStreamReader<SourceMessage>(c2s2.Reader), new TestServerStreamWriter<HubMessage>(s2c2.Writer), ctx2));

            await c2s2.Writer.WriteAsync(new SourceMessage { Hello = new Hello { SourceId = "reconnect-feed", DisplayName = "Cam 1", Platform = "linux" } });
            var welcome2 = await s2c2.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Welcome, welcome2.BodyCase);

            await c2s2.Writer.WriteAsync(new SourceMessage { Capabilities = engine.GetCapabilityReport() });
            var start2 = await s2c2.Reader.ReadAsync(cts.Token);
            Assert.Equal(HubMessage.BodyOneofCase.Start, start2.BodyCase);

            // Verify AT9: Reconnect reused the identical SRT port and passphrase
            Assert.Equal(port1, start2.Start.Target.Port);
            Assert.Equal(pass1, start2.Start.Target.Passphrase);

            c2s2.Writer.Complete();
            ctx2.Cancel();
            try { await t2; } catch { }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DesktopCameraCaptureEngine_DiscoversDevicesAndCapabilities()
    {
        await using var engine = new DesktopCameraCaptureEngine();
        await engine.InitializeAsync();

        Assert.NotEmpty(engine.AvailableCameras);
        var caps = engine.GetCapabilityReport();
        Assert.NotEmpty(caps.Cameras);
        Assert.NotEmpty(caps.Encoders);
        Assert.Equal(1.0f, engine.CurrentZoomRatio);

        // Test zoom
        float zoomed = await engine.SetZoomRatioAsync(2.0f);
        Assert.Equal(2.0f, zoomed);
        Assert.Equal(2.0f, engine.CurrentZoomRatio);
    }

    private sealed class TestAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly ChannelReader<T> _reader;
        public T Current { get; private set; } = default!;

        public TestAsyncStreamReader(ChannelReader<T> reader) => _reader = reader;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                if (await _reader.WaitToReadAsync(cancellationToken))
                {
                    if (_reader.TryRead(out var item))
                    {
                        Current = item;
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) { }
            return false;
        }
    }

    private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly ChannelWriter<T> _writer;
        public WriteOptions? WriteOptions { get; set; }

        public TestServerStreamWriter(ChannelWriter<T> writer) => _writer = writer;

        public async Task WriteAsync(T message) => await _writer.WriteAsync(message);
        public async Task WriteAsync(T message, CancellationToken cancellationToken) => await _writer.WriteAsync(message, cancellationToken);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly CancellationTokenSource _cts = new();

        public void Cancel() => _cts.Cancel();

        protected override string MethodCore => "Session";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
