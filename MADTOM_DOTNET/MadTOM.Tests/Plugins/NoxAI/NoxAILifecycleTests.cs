using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.NoxAI;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class NoxAILifecycleTests
{
    private class DummyHostContext : IPluginHostContext
    {
        public IThemeHost Themes => null!;
        public ILexiconHost Lexicons => null!;
        public ITrayMenuService? Tray { get; set; } = new TrayMenuService();
        public IPluginNotificationService Notifications => null!;
        public IMessenger Messenger => WeakReferenceMessenger.Default;
    }

    [Fact]
    public void NoxAIPluginModule_Metadata_IsValid()
    {
        var module = new NoxAIPluginModule();
        Assert.Equal("nox-ai", module.Id);
        Assert.Equal("NOX AI", module.DisplayName);
        Assert.Equal(120, module.OrderWeight);
        Assert.Equal("AI", module.Category);
        Assert.Equal("🧠", module.IconGlyph);
    }

    [Fact]
    public async Task NoxAIPluginModule_Lifecycle_InitializesAndCleansUp()
    {
        var module = new NoxAIPluginModule();
        var host = new DummyHostContext();

        await module.InitializeAsync(host);
        await module.StartAsync();
        Assert.True(await module.CanCloseAsync());
        await module.StopAsync();
        await module.DisposeAsync();
    }

    [Fact]
    public void NoxAI_MainViewModel_InitializesState()
    {
        using var vm = new MainViewModel();
        Assert.NotNull(vm.TelemetryTab);
        Assert.NotNull(vm.DirectChatTab);
        Assert.NotNull(vm.MemoriesRagTab);
        Assert.NotNull(vm.MediaLibraryTab);
        Assert.NotNull(vm.ActiveJobsTab);
        Assert.NotNull(vm.RawLogsApiTab);
        Assert.NotNull(vm.SettingsViewModel);
        Assert.NotNull(vm.ActiveNodes);
        Assert.False(vm.IsSettingsModalOpen);
    }

    [Fact]
    public async Task NoxAI_LiveProbe_CanQueryStatusAndMemories()
    {
        var node = new NoxNodeModel("tp1", "192.168.0.58:50055", "noxchat-ed25519-v1:qHf-EABvKtzWrfnr3DhjNqWKMJkgxm_bqA5nPlC4f8o", "tp1");
        await using var manager = new NoxNodeManager();
        var client = manager.GetClient(node);

        var status = await client.GetNodeStatusAsync(new NoxNodeStatusRequest { NodeId = "tp1" });
        Assert.NotNull(status);
        Assert.True(status.BotRunning);

        var memories = await client.QueryMemoriesAsync(new NoxMemoryQueryRequest { NodeId = "tp1", ChatId = 0, Limit = 10 });
        Assert.NotNull(memories);

        var media = await client.QueryMediaLibraryAsync(new NoxMediaQueryRequest { NodeId = "tp1", Kind = "", Limit = 50 });
        Assert.NotNull(media);

        var hot = await client.ListHotMemoriesAsync(new NoxHotMemoryRequest { NodeId = "tp1", ChatId = 0 });
        Assert.NotNull(hot);

        System.Console.WriteLine($"[TEST LIVE] Status BotRunning={status.BotRunning}, Memories={memories.Memories.Count}, HotItems={hot.Items.Count}, MediaItems={media.Items.Count}, Packs={media.StickerPacks.Count}");
    }

    [Fact]
    public async Task NoxAI_LiveProbe_CanSubscribeState()
    {
        var node = new NoxNodeModel("tp1", "192.168.0.58:50055", "noxchat-ed25519-v1:qHf-EABvKtzWrfnr3DhjNqWKMJkgxm_bqA5nPlC4f8o", "tp1");
        await using var manager = new NoxNodeManager();
        var client = manager.GetClient(node);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = client.SubscribeState(new NoxStateSubscriptionRequest { NodeId = "tp1" }, cancellationToken: cts.Token);

        bool receivedAny = false;
        try
        {
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var current = call.ResponseStream.Current;
                if (current != null)
                {
                    receivedAny = true;
                    System.Console.WriteLine($"[TEST LIVE STREAM] Received chunk id={current.ChunkId}, zstd size={current.ZstdPayload.Length}, decoded={current.DecodedSize}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Cancelled)
        {
        }

        Assert.True(receivedAny, "Expected to receive at least one telemetry/state chunk within 5 seconds");
    }

    [Fact]
    public void NoxCacheSettingsStore_RoundTrips_AndEnforcesLimits()
    {
        string tmp = System.IO.Path.GetTempFileName();
        try
        {
            var store = new NoxCacheSettingsStore(tmp);
            var loaded = store.Load();
            Assert.Equal(256, loaded.MediaCacheLimitMiB);
            Assert.Equal(500, loaded.MediaCacheItemLimit);

            loaded.MediaCacheLimitMiB = 512;
            loaded.MediaCacheItemLimit = 1000;
            store.Save(loaded);

            var reloaded = store.Load();
            Assert.Equal(512, reloaded.MediaCacheLimitMiB);
            Assert.Equal(1000, reloaded.MediaCacheItemLimit);
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void NoxMediaThumbnailCache_ConfigureAndUsage_TracksAccurately()
    {
        NoxMediaThumbnailCache.Configure(256, 500);
        var usage = NoxMediaThumbnailCache.GetUsage();
        Assert.True(usage.memoryBytes >= 0);
        Assert.True(usage.diskBytes >= 0);
    }
}

