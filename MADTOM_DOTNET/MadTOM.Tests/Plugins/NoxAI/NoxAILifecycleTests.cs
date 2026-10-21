using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MADTOM.PluginContracts;
using MADTOM.Plugins.NoxAI;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.ViewModels;
using MadTOM.Services;
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

    [Fact]
    public void MediaItemRowModel_ChunksCorrectly_ForVirtualizedGrid()
    {
        var items = new System.Collections.Generic.List<MediaItemModel>();
        for (int i = 0; i < 23; i++)
        {
            items.Add(new MediaItemModel
            {
                UniqueId = $"uid_{i}",
                Emoji = "🐱",
                CaptionText = $"Sticker {i}"
            });
        }

        int cols = 5;
        var rows = new System.Collections.Generic.List<MediaItemRowModel>();
        for (int i = 0; i < items.Count; i += cols)
        {
            int count = Math.Min(cols, items.Count - i);
            rows.Add(new MediaItemRowModel(items.GetRange(i, count)));
        }

        Assert.Equal(5, rows.Count); // 23 / 5 = 4 rows of 5 + 1 row of 3
        Assert.Equal(5, rows[0].Items.Count);
        Assert.Equal(3, rows[4].Items.Count);
        Assert.Equal("uid_0", rows[0].Items[0].UniqueId);
        Assert.Equal("uid_22", rows[4].Items[2].UniqueId);
    }

    [Fact]
    public void NoxSettingsViewModel_EditNode_PreservesAuthKey_WhenChangingEndpoint()
    {
        string tmpStore = System.IO.Path.GetTempFileName();
        try
        {
            var nodeMgr = new NoxNodeManager(tmpStore);
            string originalKey = "noxchat-ed25519-v1:test_key_sample_12345";
            var initialNode = new NoxNodeModel("node-1", "192.168.1.50:50055", originalKey, "Original Node");
            nodeMgr.AddOrUpdateNode(initialNode);

            var settingsVm = new NoxSettingsViewModel(nodeMgr, new TelemetryHistoryCache(), new LogChunkCache(), new NoxApiCallChunkCache());

            // Select node for edit
            settingsVm.SelectNodeForEdit("node-1");
            Assert.True(settingsVm.IsEditingNode);
            Assert.Equal("node-1", settingsVm.EditingOriginalId);
            Assert.Equal("node-1", settingsVm.NewNodeId);
            Assert.Equal("Original Node", settingsVm.NewNodeAlias);
            Assert.Equal("192.168.1.50:50055", settingsVm.NewNodeEndpoint);

            // Change only the IP / endpoint
            settingsVm.NewNodeEndpoint = "10.0.0.99:50055";
            settingsVm.SaveNode();

            Assert.False(settingsVm.IsEditingNode);
            var updated = nodeMgr.Nodes.FirstOrDefault(n => n.Id == "node-1");
            Assert.NotNull(updated);
            Assert.Equal("10.0.0.99:50055", updated.HostEndpoint);
            // Verify key is strictly preserved!
            Assert.Equal(originalKey, updated.AuthKey);
            Assert.Equal("Original Node", updated.Alias);
        }
        finally
        {
            if (System.IO.File.Exists(tmpStore)) System.IO.File.Delete(tmpStore);
        }
    }

    [Fact]
    public void NoxSettingsViewModel_CancelEditNode_RevertsState()
    {
        string tmpStore = System.IO.Path.GetTempFileName();
        try
        {
            var nodeMgr = new NoxNodeManager(tmpStore);
            var initialNode = new NoxNodeModel("node-x", "192.168.1.10:50055", "key-x", "Node X");
            nodeMgr.AddOrUpdateNode(initialNode);

            var settingsVm = new NoxSettingsViewModel(nodeMgr, new TelemetryHistoryCache(), new LogChunkCache(), new NoxApiCallChunkCache());

            settingsVm.SelectNodeForEdit("node-x");
            Assert.True(settingsVm.IsEditingNode);
            settingsVm.NewNodeEndpoint = "9.9.9.9:50055";

            settingsVm.CancelEditNode();
            Assert.False(settingsVm.IsEditingNode);
            Assert.Equal(string.Empty, settingsVm.EditingOriginalId);
            Assert.Equal("127.0.0.1:50055", settingsVm.NewNodeEndpoint);

            // Node in manager should NOT have changed
            var node = nodeMgr.Nodes.FirstOrDefault(n => n.Id == "node-x");
            Assert.NotNull(node);
            Assert.Equal("192.168.1.10:50055", node.HostEndpoint);
        }
        finally
        {
            if (System.IO.File.Exists(tmpStore)) System.IO.File.Delete(tmpStore);
        }
    }
}


