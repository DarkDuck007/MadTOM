using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.Feed.Engine;
using MADTOM.Protocol.Video.V1;
using Xunit;

namespace MadTOM.Tests.Plugins.VideoFeedCenter;

public class FeedEngineTests
{
    [Fact]
    public async Task FeedManager_CreatesAndRemovesFeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"feed_test_{Guid.NewGuid():N}");
        try
        {
            await using var manager = new FeedManager(tempDir);

            // Test AT10: Unauthenticated OBS listener creation
            var obsFeed = manager.CreateObsListener("obs-cam-1", "Studio OBS", "secret123", port: 8554);
            Assert.NotNull(obsFeed);
            Assert.Equal("obs-cam-1", obsFeed.FeedId);
            Assert.False(obsFeed.Info.IsTrusted);
            Assert.Equal("secret123", obsFeed.Info.SrtTarget.Passphrase);

            var list = manager.ListFeeds();
            Assert.Single(list);
            Assert.Equal("obs-cam-1", list[0].FeedId);

            // Remove feed
            var removed = await manager.RemoveFeedAsync("obs-cam-1");
            Assert.True(removed);
            Assert.Empty(manager.ListFeeds());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FeedSession_SyntheticIngest_PushesFramesAndUpdatesState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"feed_synth_{Guid.NewGuid():N}");
        try
        {
            var info = new FeedInfo
            {
                FeedId = "synth-1",
                DisplayName = "Synthetic Feed",
                IsTrusted = true
            };

            await using var session = new FeedSession(info, tempDir);
            var previewReader = session.SubscribePreview();

            session.StartIngest(useSyntheticSource: true);

            // Wait for at least 1 preview frame
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var frame = await previewReader.ReadAsync(cts.Token);

            Assert.NotNull(frame);
            Assert.Equal("synth-1", frame.FeedId);
            Assert.Equal(640u, frame.Width);
            Assert.Equal(360u, frame.Height);
            Assert.Equal("bgra32", frame.Format);
            Assert.NotEmpty(frame.Data);
            Assert.Equal(FeedState.Live, session.State);

            // AT7: Update property zoom ratio
            session.UpdateProperty("camera.zoom_ratio", 2.5);
            var zoomProp = session.Info.Properties.FirstOrDefault(p => p.Id == "camera.zoom_ratio");
            Assert.NotNull(zoomProp);
            Assert.Equal(2.5, zoomProp.Value);

            // AT8: Update granted profile
            session.UpdateGrantedProfile(new StreamProfile
            {
                Codec = "hevc",
                Width = 1920,
                Height = 1080,
                Fps = 30
            });
            Assert.NotNull(session.Info.GrantedProfile);
            Assert.Equal(1920u, session.Info.GrantedProfile.Width);
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
    public async Task FeedEngineHost_Starts_And_Serves_GetPairingQr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"engine_test_{Guid.NewGuid():N}");
        try
        {
            await using var host = new FeedEngineHost(
                recordingsDir: tempDir,
                trustStorePath: Path.Combine(tempDir, "trust.json"),
                certPfxPath: Path.Combine(tempDir, "hub.pfx"),
                hubHost: "127.0.0.1",
                tcpPort: 17443);

            await host.StartAsync(17443);

            var pairingRes = host.PairingServer.CreateSession("127.0.0.1", 17443, out var qrUri);
            Assert.NotNull(pairingRes);
            Assert.NotEmpty(qrUri);
            Assert.NotEmpty(pairingRes.ComparisonCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
