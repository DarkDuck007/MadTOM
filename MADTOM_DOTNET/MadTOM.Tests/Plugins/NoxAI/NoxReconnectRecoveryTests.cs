using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using MADTOM.Plugins.Telemetry.Proto.V1;
using MadTOM.Services;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class NoxReconnectRecoveryTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public bool Online;
        public bool RejectConfig;
        public int ApiFailures;
        public int ApiQueries;
        public int ChatQueries;
        public int TraceFailures;
        public int TraceQueries;
        public TaskCompletionSource<bool> PendingLogsRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ulong LastSeq = 10;
        public readonly List<string> CallIds = new() { "first" };
        public TaskCompletionSource<LogChunkResponse>? PendingLogs;
        private static AsyncUnaryCall<T> Call<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        private AsyncUnaryCall<T> Reply<T>(T value) => Call(Online ? Task.FromResult(value) : Task.FromException<T>(Offline()));
        private static RpcException Offline() => new(new Status(StatusCode.Unavailable, "network disconnected"));
        public override AsyncUnaryCall<NoxNodeStatusResponse> UpdateControlConfigAsync(NoxUpdateControlConfigRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => RejectConfig ? Call(Task.FromException<NoxNodeStatusResponse>(Offline())) : Reply(new NoxNodeStatusResponse());
        public override AsyncUnaryCall<NoxSpoolStatsResponse> GetSpoolStatsAsync(NoxSpoolStatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Reply(new NoxSpoolStatsResponse());
        public override AsyncUnaryCall<LogChunkResponse> QueryLogChunksAsync(LogChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            if (PendingLogs != null && request.NodeId == "old")
            {
                PendingLogsRequested.TrySetResult(true);
                return Call(PendingLogs.Task);
            }
            return Reply(new LogChunkResponse { Chunks = { new LogChunk { ChunkId = 7, FirstSeq = 1, LastSeq = LastSeq, RecordCount = (uint)LastSeq } } });
        }
        public override AsyncUnaryCall<NoxApiCallChunkResponse> QueryApiCallChunksAsync(NoxApiCallChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ApiQueries++;
            if (ApiFailures-- > 0) return Call(Task.FromException<NoxApiCallChunkResponse>(Offline()));
            var records = new NoxApiCallRecordList();
            records.Records.AddRange(CallIds.Select((id, i) => new NoxRawApiCallRecord { CallId = id, TimestampUnixNano = i + 1, TraceContext = new NoxTraceContext { ChatId = 42 } }));
            byte[] raw = records.ToByteArray();
            return Reply(new NoxApiCallChunkResponse { Chunks = { new NoxApiCallChunk { ChunkId = 4, RawSize = (uint)raw.Length, ZstdRecords = ByteString.CopyFrom(ZstdCodec.Encode(raw)), RecordCount = (uint)records.Records.Count } } });
        }
        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(NoxListChatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ChatQueries++;
            return Reply(new NoxListChatsResponse { Chats = { new NoxChatItem { ChatId = 42, Title = "Recovered chat", ChatType = "private" } } });
        }
        public override AsyncUnaryCall<NoxChatTraceResponse> QueryChatTraceAsync(NoxChatTraceRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            TraceQueries++;
            if (TraceFailures-- > 0) return Call(Task.FromException<NoxChatTraceResponse>(Offline()));
            return Reply(new NoxChatTraceResponse { Events = { new NoxChatTraceEvent {
                EventId = "recovered-event", Kind = "tool_result", Content = "Recovered trace",
                TimestampUnixNano = 100, TraceContext = new NoxTraceContext { ChatId = 42 }
            } } });
        }
        private sealed class IdleReader : IAsyncStreamReader<NoxLogApiChunk>
        {
            public NoxLogApiChunk Current => new();
            public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
        }
        public override AsyncServerStreamingCall<NoxLogApiChunk> SubscribeLogsAndApi(NoxLogApiSubscriptionRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => new(new IdleReader(), new TaskCompletionSource<Metadata>().Task, () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    private static NoxNodeManager Manager(Client client) => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
    private static NoxNodeModel Node(string id = "node") => new(id, "unused", "", id);
    private static RawLogsApiTabViewModel ViewModel(NoxNodeManager manager, NoxApiCallChunkCache cache)
        => new(manager, new LogChunkCache(), cache) { SelectedNode = Node() };

    [Fact]
    public async Task ProbeUnavailableAtLaunch_RecoversAllHistoryWhenItAppears()
    {
        var client = new Client();
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = ViewModel(manager, cache);
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.Empty(vm.ChatGroups);
        client.Online = true;
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.NotNull(cache.GetRecord("node", "first"));
        Assert.Contains(vm.ChatGroups, c => c.ChatId == 42 && c.Title == "Recovered chat");
        Assert.Equal(10UL, vm.LastSeq);
    }

    [Fact]
    public async Task FailedConfigSync_DoesNotSuppressLogsApiCallsOrChats()
    {
        var client = new Client { Online = true, RejectConfig = true };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = ViewModel(manager, cache);
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.NotNull(cache.GetRecord("node", "first"));
        Assert.Contains(vm.ChatGroups, c => c.ChatId == 42);
        Assert.Equal(10UL, vm.LastSeq);
    }

    [Fact]
    public async Task ReconnectionBackfillsMissedCallsAndLogRanges_WithoutDuplicatingHealthyPolls()
    {
        var client = new Client { Online = true };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = ViewModel(manager, cache);
        await vm.RecoverHistoryAfterConnectionAsync();
        int queries = client.ApiQueries;
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.Equal(queries, client.ApiQueries);
        client.Online = false;
        vm.MarkHistoryRecoveryNeeded();
        await vm.RecoverHistoryAfterConnectionAsync();
        client.Online = true;
        client.CallIds.Add("during-outage");
        client.LastSeq = 20;
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.NotNull(cache.GetRecord("node", "first"));
        Assert.NotNull(cache.GetRecord("node", "during-outage"));
        Assert.Equal(20UL, vm.LastSeq);
    }

    [Fact]
    public async Task FailedApiBackfill_RetriesEvenIfStatusPollingNeverSawAnOutage()
    {
        var client = new Client { Online = true, ApiFailures = 1 };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = ViewModel(manager, cache);
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.Null(cache.GetRecord("node", "first"));
        Assert.Contains(vm.ChatGroups, c => c.ChatId == 42);
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.NotNull(cache.GetRecord("node", "first"));
    }

    [Fact]
    public async Task NodeSwitch_DiscardsDelayedHistoryAndAllowsNewRecovery()
    {
        var client = new Client { Online = true, PendingLogs = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), cache) { SelectedNode = Node("old") };
        var old = vm.RecoverHistoryAfterConnectionAsync();
        await client.PendingLogsRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedNode = Node("new");
        await vm.RecoverHistoryAfterConnectionAsync();
        client.PendingLogs.SetResult(new LogChunkResponse { Chunks = { new LogChunk { ChunkId = 99, FirstSeq = 900, LastSeq = 999 } } });
        await old;
        Assert.Equal(10UL, vm.LastSeq);
        Assert.Null(cache.GetRecord("old", "first"));
        Assert.NotNull(cache.GetRecord("new", "first"));
    }
    [Fact]
    public async Task SelectedChatTraceFailure_IsRetriedAfterConnectionRecovery()
    {
        var client = new Client { Online = true };
        await using var manager = Manager(client);
        using var vm = ViewModel(manager, new NoxApiCallChunkCache());
        await vm.RecoverHistoryAfterConnectionAsync();
        vm.IsTabActive = true;
        vm.SelectedSubViewIndex = 2;
        client.TraceFailures = 2; // selection fetch and first recovery fetch fail
        vm.SelectedChatGroup = vm.ChatGroups.Single(c => c.ChatId == 42);
        await vm.RecoverHistoryAfterConnectionAsync(force: true);
        int failedQueries = client.TraceQueries;
        await vm.RecoverHistoryAfterConnectionAsync();
        Assert.True(client.TraceQueries > failedQueries);
        Assert.Contains(vm.ChatHistory.OfType<ChatTraceRowModel>(), row => row.Content == "Recovered trace");
    }

}
