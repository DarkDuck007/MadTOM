using System;
using System.Collections.Specialized;
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

/// Chat view: one collection notification per switch / prepend, newest at the bottom, trace not re-queried
/// when switching back to a cached chat.
[Collection("Sequential")]
public class ChatViewPerformanceTests
{
    private sealed class CountingClient : NoxChatControlService.NoxChatControlServiceClient
    {
        public int TraceQueries;
        private static AsyncUnaryCall<T> Call<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(NoxListChatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new NoxListChatsResponse
            {
                Chats =
                {
                    new NoxChatItem { ChatId = 5, ChatType = "private", Title = "Five" },
                    new NoxChatItem { ChatId = 6, ChatType = "private", Title = "Six" },
                }
            }));
        public override AsyncUnaryCall<NoxChatTraceResponse> QueryChatTraceAsync(NoxChatTraceRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref TraceQueries);
            return Call(Task.FromResult(new NoxChatTraceResponse()));
        }
        public override AsyncUnaryCall<LogChunkResponse> QueryLogChunksAsync(LogChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new LogChunkResponse()));
        public override AsyncUnaryCall<NoxApiCallChunkResponse> QueryApiCallChunksAsync(NoxApiCallChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new NoxApiCallChunkResponse()));
        public override AsyncUnaryCall<NoxNodeStatusResponse> UpdateControlConfigAsync(NoxUpdateControlConfigRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromException<NoxNodeStatusResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline test"))));
    }

    private static async Task<(NoxNodeManager, RawLogsApiTabViewModel, CountingClient)> Setup(int callsPerChat)
    {
        var client = new CountingClient();
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), new NoxApiCallChunkCache())
        { SelectedNode = new NoxNodeModel("n", "unused", "", "n", LogMode: TelemetryOptInMode.OptInOff) };
        await vm.RefreshChatsAsync();
        foreach (long chat in new long[] { 5, 6 })
            for (int i = 0; i < callsPerChat; i++)
                vm.RecentApiCalls.Insert(0, new RawApiCallItemModel
                {
                    CallId = $"c{chat}_{i}", TimestampUnixNano = 1000 + i, IsDetailsLoaded = true,
                    TraceContext = new NoxTraceContext { ChatId = chat },
                });
        vm.RebuildChatGroups();
        return (manager, vm, client);
    }

    [Fact]
    public async Task SwitchIsOneReset_AndOlderIsOneRangeInsertAtTop()
    {
        var (manager, vm, _) = await Setup(320);
        await using var _m = manager;
        using var _v = vm;

        var events = new System.Collections.Generic.List<NotifyCollectionChangedEventArgs>();
        vm.ChatHistory.CollectionChanged += (_, e) => events.Add(e);

        vm.SelectedChatGroup = vm.ChatGroups.Single(g => g.ChatId == 5);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);
        Assert.Equal("c5_319", ((RawApiCallItemModel)vm.ChatHistory[^1]).CallId); // newest at the bottom

        events.Clear();
        var newest = vm.ChatHistory[^1];
        Assert.True(vm.LoadOlderChatHistory());
        var add = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, add.Action);
        Assert.Equal(0, add.NewStartingIndex);
        Assert.Equal(100, add.NewItems!.Count);
        Assert.Same(newest, vm.ChatHistory[^1]);

        events.Clear();
        vm.SelectedChatGroup = vm.ChatGroups.Single(g => g.ChatId == 6);
        Assert.Single(events);
        Assert.All(vm.ChatHistory.Cast<RawApiCallItemModel>(), c => Assert.StartsWith("c6_", c.CallId));
    }

    [Fact]
    public async Task ChatHistoryIsChronological_OldestFirst()
    {
        var (manager, vm, _) = await Setup(40);
        await using var _m = manager;
        using var _v = vm;
        vm.SelectedChatGroup = vm.ChatGroups.Single(g => g.ChatId == 5);
        var ts = vm.ChatHistory.Cast<RawApiCallItemModel>().Select(c => c.TimestampUnixNano).ToList();
        Assert.Equal(ts.OrderBy(t => t), ts);
    }

    [Fact]
    public void CacheSizeIsBounded()
    {
        Assert.InRange(RawLogsApiTabViewModel.ChatViewCacheSize, 1, 32);
    }
}
