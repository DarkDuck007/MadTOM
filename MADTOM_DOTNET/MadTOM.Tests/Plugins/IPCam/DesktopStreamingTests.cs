using System.Net;
using System.Net.Sockets;
using MADTOM.Feed.Engine;
using MADTOM.Plugins.IPCam;
using MADTOM.Protocol.Video.V1;

namespace MadTOM.Tests.Plugins.IPCam;

public class DesktopStreamingTests
{
    [Fact]
    public async Task EmbeddedTcpControlConnectsAndCameraSurvivesClientDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "madtom-control-" + Guid.NewGuid().ToString("N"));
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            await using var host = new FeedEngineHost(directory, Path.Combine(directory, "trust.json"),
                Path.Combine(directory, "hub.pfx"), "127.0.0.1", port);
            await host.StartAsync(port, timeout.Token);
            Assert.StartsWith("http://127.0.0.1:", host.LocalAddress);
            await using var camera = new DesktopCameraCaptureEngine();
            await camera.InitializeAsync(timeout.Token);
            await camera.SelectCameraAsync(camera.AvailableCameras.Single(c => c.Id == "synthetic"));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using var client = new VideoSourceSessionClient(camera, host.LocalAddress,
                    "tcp-camera", "TCP camera", "linux");
                await client.StartAsync(timeout.Token);
                FeedSession? feed;
                while ((feed = host.FeedManager.GetFeed("tcp-camera")) == null)
                    await Task.Delay(50, timeout.Token);
                var reader = feed.SubscribePreview();
                try
                {
                    var frame = await reader.ReadAsync(timeout.Token);
                    Assert.Equal(640u, frame.Width);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"Attempt {attempt}: client {client.State}: {client.LastError}; feed {feed.State}: {feed.Info.LastError}");
                }
                finally { feed.UnsubscribePreview(reader); }
                await client.StopAsync(timeout.Token);
            }
            await camera.StartPreviewAsync();
            await camera.StopPreviewAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SyntheticCameraStreamsThroughRealFfmpegToTwoPreviewSubscribers()
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = (uint)((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        var directory = Path.Combine(Path.GetTempPath(), "madtom-video-" + Guid.NewGuid().ToString("N"));
        var target = new SrtTarget { Host = "127.0.0.1", Port = port, Passphrase = "madtom-test-passphrase" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await using var feed = new FeedSession(new FeedInfo { FeedId = "test", SourcePlatform = "linux", SrtTarget = target }, directory);
            var first = feed.SubscribePreview();
            var second = feed.SubscribePreview();
            feed.StartIngest();
            await using var camera = new DesktopCameraCaptureEngine();
            await camera.InitializeAsync(timeout.Token);
            await camera.SelectCameraAsync(camera.AvailableCameras.Single(c => c.Id == "synthetic"), timeout.Token);
            var localFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            camera.PreviewFrameArrived += (_, _, _) => localFrame.TrySetResult();
            var errors = new List<string>();
            camera.CaptureError += error => errors.Add(error);
            // Allow the external SRT listener process to bind before the caller connects.
            await Task.Delay(500, timeout.Token);
            await camera.StartCaptureAsync(new StreamProfile { Codec = "avc", Width = 640, Height = 360, Fps = 15, BitrateBps = 500000 }, target, timeout.Token);
            try
            {
                var a = await first.ReadAsync(timeout.Token);
                var b = await second.ReadAsync(timeout.Token);
                await localFrame.Task.WaitAsync(timeout.Token);
                Assert.Equal(640u, a.Width);
                Assert.Equal(640 * 360 * 4, a.Data.Length);
                Assert.Equal(a.FrameNumber, b.FrameNumber);
                Assert.Equal(FeedState.Live, feed.State);
                Assert.False(feed.Info.IsRecording);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"No preview. Ingest: {feed.Info.LastError}; capture: {string.Join("; ", errors)}");
            }
            feed.UnsubscribePreview(first);
            feed.UnsubscribePreview(second);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
