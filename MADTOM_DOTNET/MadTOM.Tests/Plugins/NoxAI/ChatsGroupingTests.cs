using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using MADTOM.Plugins.Telemetry.Proto.V1;
using MadTOM.Services;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

/// <summary>Chats sub-view: API calls grouped per chat by trace chat_id / registry title, never by sender names.</summary>
[Collection("Sequential")]
public class ChatsGroupingTests
{
    internal sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        private static AsyncUnaryCall<T> Call<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(NoxListChatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new NoxListChatsResponse
            {
                Chats =
                {
                    new NoxChatItem { ChatId = -100, ChatType = "supergroup", Title = "Nest" },
                    new NoxChatItem { ChatId = 188, ChatType = "private", Title = "Pooriya" },
                    new NoxChatItem { ChatId = 7, ChatType = "private", Title = "Twin" },
                    new NoxChatItem { ChatId = 8, ChatType = "private", Title = "Twin" },
                    new NoxChatItem { ChatId = 999, ChatType = "private", Title = "Quiet" },
                }
            }));
        public override AsyncUnaryCall<NoxChatTraceResponse> QueryChatTraceAsync(NoxChatTraceRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromException<NoxChatTraceResponse>(new RpcException(new Status(StatusCode.Unimplemented, "old probe"))));
        public override AsyncUnaryCall<LogChunkResponse> QueryLogChunksAsync(LogChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new LogChunkResponse()));
        public override AsyncUnaryCall<NoxApiCallChunkResponse> QueryApiCallChunksAsync(NoxApiCallChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new NoxApiCallChunkResponse()));
        public override AsyncUnaryCall<NoxNodeStatusResponse> UpdateControlConfigAsync(NoxUpdateControlConfigRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromException<NoxNodeStatusResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline test"))));
    }

    private static RawApiCallItemModel Call(string id, long ts, string name = "", long? chatId = null) => new()
    {
        CallId = id, TimestampUnixNano = ts, ChatName = name, Caller = "stream_step",
        TraceContext = chatId is long c ? new NoxTraceContext { ChatId = c } : null,
        IsDetailsLoaded = true, // keep the test off the payload loader
    };

    [Fact]
    public async Task CallsAreGroupedByChatIdAndExactTitleNotBySenderNames()
    {
        await using var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => new Client());
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), new NoxApiCallChunkCache())
        { SelectedNode = new NoxNodeModel("n", "unused", "", "n", LogMode: TelemetryOptInMode.OptInOff) };
        // A context-diff call whose (old, guessed) chat_name is a sender in that group
        var diff = Call("c1", 300, name: "Diba", chatId: -100);
        var bootstrap = Call("c2", 100, name: "Nest", chatId: -100);
        var legacyDm = Call("c3", 200, name: "Pooriya");      // old probe: no trace context, title matches the DM
        var ambiguous = Call("c4", 250, name: "Twin");        // two chats share this title: never guessed
        var background = Call("c5", 50);                      // sticker analysis etc.
        foreach (var c in new[] { diff, bootstrap, legacyDm, ambiguous, background }) vm.RecentApiCalls.Add(c);

        await vm.RefreshChatsAsync();

        Assert.Equal("id:-100", vm.ChatKeyOf(diff));
        Assert.Equal("Nest", diff.ChatName); // display name comes from the registry, not the prompt
        Assert.Equal("id:188", vm.ChatKeyOf(legacyDm));
        Assert.Equal("name:Twin", vm.ChatKeyOf(ambiguous));
        Assert.Equal(RawLogsApiTabViewModel.NoChatKey, vm.ChatKeyOf(background));

        var nest = vm.ChatGroups.Single(g => g.ChatId == -100);
        Assert.Equal(2, nest.CallCount);
        Assert.Equal("supergroup · -100", nest.MetaLine);
        Assert.Contains(vm.ChatGroups, g => g.ChatId == 999 && g.CallCount == 0); // registry chats without calls are listed
        Assert.Equal(RawLogsApiTabViewModel.NoChatKey, vm.ChatGroups[^1].Key); // background bucket last
        Assert.True(vm.ChatGroups.ToList().IndexOf(nest) < vm.ChatGroups.ToList().FindIndex(g => g.ChatId == 188)); // most recent first

        int resets = 0;
        vm.ChatHistoryReset += () => resets++;
        vm.SelectedChatGroup = nest;
        Assert.Equal(new object[] { bootstrap, diff }, vm.ChatHistory.ToArray()); // chronological: newest at the bottom
        Assert.Equal(1, resets); // the view is told to jump to the bottom

        // filtering keeps the inspected chat selected and its history intact
        vm.ChatFilter = "nes";
        Assert.Same(nest, vm.SelectedChatGroup);
        Assert.Single(vm.ChatGroups);
        Assert.Equal(2, vm.ChatHistory.Count);
    }
}

[Collection("Sequential")]
public class ChatsWindowTests
{
    [Fact]
    public async Task ChatOpensOnNewestWindowAndExtendsUpwards()
    {
        await using var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => new ChatsGroupingTests.Client());
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), new NoxApiCallChunkCache())
        { SelectedNode = new NoxNodeModel("n", "unused", "", "n", LogMode: TelemetryOptInMode.OptInOff) };
        await vm.RefreshChatsAsync();
        for (int i = 0; i < 320; i++)
            vm.RecentApiCalls.Insert(0, new RawApiCallItemModel { CallId = "c" + i, TimestampUnixNano = 1000 + i, IsDetailsLoaded = true, TraceContext = new NoxTraceContext { ChatId = 5 } });
        vm.RebuildChatGroups();
        int prepends = 0;
        vm.ChatOlderPrepending += () => prepends++;
        vm.SelectedChatGroup = vm.ChatGroups.Single(g => g.ChatId == 5);
        Assert.Equal(150, vm.ChatHistory.Count);
        Assert.Equal("c319", ((RawApiCallItemModel)vm.ChatHistory[^1]).CallId); // newest last
        Assert.Equal("c170", ((RawApiCallItemModel)vm.ChatHistory[0]).CallId);
        Assert.True(vm.ChatHasOlder);
        var newestBefore = vm.ChatHistory[^1];
        Assert.True(vm.LoadOlderChatHistory());
        Assert.Equal(250, vm.ChatHistory.Count);
        Assert.Same(newestBefore, vm.ChatHistory[^1]); // existing items are kept, older ones prepended
        Assert.True(vm.LoadOlderChatHistory());
        Assert.Equal(320, vm.ChatHistory.Count);
        Assert.False(vm.ChatHasOlder);
        Assert.False(vm.LoadOlderChatHistory());
        Assert.Equal(2, prepends);
    }

    [Fact]
    public async Task ChatTimestampMatchesAgentCallNotPassiveMessageAndSearchFiltersChatContent()
    {
        await using var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => new ChatsGroupingTests.Client());
        var apiCache = new NoxApiCallChunkCache();
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), apiCache)
        { SelectedNode = new NoxNodeModel("n", "unused", "", "n", LogMode: TelemetryOptInMode.OptInOff) };

        long agentCallNano = 1700000000_000_000_000L;
        var call1 = new RawApiCallItemModel
        {
            CallId = "call-1",
            TimestampUnixNano = agentCallNano,
            Caller = "stream_step",
            Model = "gemini-2.5-pro",
            TraceContext = new NoxTraceContext { ChatId = -100 },
            RequestJson = "[{\"role\":\"user\",\"content\":\"[#101 Alice]: Can we deploy the quantum router?\"}]",
            ResponseJson = "{\"content\":\"<msg>Deploying quantum router now.</msg>\"}",
            IsDetailsLoaded = true
        };
        var call2 = new RawApiCallItemModel
        {
            CallId = "call-2",
            TimestampUnixNano = agentCallNano + 5_000_000_000L,
            Caller = "stream_step",
            Model = "gemini-2.5-pro",
            TraceContext = new NoxTraceContext { ChatId = -100 },
            RequestJson = "[{\"role\":\"user\",\"content\":\"[#102 Bob]: What is for lunch?\"}]",
            ResponseJson = "{\"content\":\"<msg>Sushi!</msg>\"}",
            IsDetailsLoaded = true
        };

        apiCache.PutFullRecord("n", new NoxRawApiCallRecord
        {
            CallId = "call-1",
            TimestampUnixNano = call1.TimestampUnixNano,
            RawRequestJson = call1.RequestJson,
            RawResponseJson = call1.ResponseJson
        });
        apiCache.PutFullRecord("n", new NoxRawApiCallRecord
        {
            CallId = "call-2",
            TimestampUnixNano = call2.TimestampUnixNano,
            RawRequestJson = call2.RequestJson,
            RawResponseJson = call2.ResponseJson
        });

        vm.RecentApiCalls.Add(call2);
        vm.RecentApiCalls.Add(call1);
        await vm.RefreshChatsAsync();

        var nest = vm.ChatGroups.Single(g => g.ChatId == -100);
        Assert.Equal(agentCallNano + 5_000_000_000L, nest.LastActiveUnixNano);
        Assert.Equal(2, nest.CallCount);

        vm.SelectedChatGroup = nest;
        Assert.Equal(2, vm.ChatHistory.Count);

        // Search inside the selected chat for "quantum router"
        vm.ChatContentSearchQuery = "quantum router";
        Assert.True(vm.IsChatContentSearchActive);
        Assert.Single(vm.ChatHistory);
        Assert.Equal("call-1", ((RawApiCallItemModel)vm.ChatHistory[0]).CallId);

        // Clear search restores all calls in the chat
        vm.ClearChatHistorySearch();
        Assert.False(vm.IsChatContentSearchActive);
        Assert.Equal(2, vm.ChatHistory.Count);

        // Switching to another chat unloads uncompressed details from call1 & call2 while keeping them in Zstd _apiCache
        var pooriya = vm.ChatGroups.Single(g => g.ChatId == 188);
        vm.SelectedChatGroup = pooriya;
        Assert.False(call1.IsDetailsLoaded);
        Assert.Empty(call1.RequestJson);
        Assert.True(apiCache.HasFullRecord("n", "call-1"));
    }
}
