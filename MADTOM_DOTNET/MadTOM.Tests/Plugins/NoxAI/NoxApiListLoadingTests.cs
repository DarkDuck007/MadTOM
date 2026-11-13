using System;
using System.Collections.Generic;
using System.IO;
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
public class NoxApiListLoadingTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public required Func<NoxApiCallChunkQuery, Task<NoxApiCallChunkResponse>> Query { get; init; }
        public CancellationToken Token;
        private static AsyncUnaryCall<T> Call<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        public override AsyncUnaryCall<NoxApiCallChunkResponse> QueryApiCallChunksAsync(NoxApiCallChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Call(Query(request)); }
        public override AsyncUnaryCall<LogChunkResponse> QueryLogChunksAsync(LogChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromResult(new LogChunkResponse()));
        public override AsyncUnaryCall<NoxNodeStatusResponse> UpdateControlConfigAsync(NoxUpdateControlConfigRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromException<NoxNodeStatusResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline test"))));
    }
    private static NoxNodeManager Manager(Client client) => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
    private static NoxNodeModel Node(string id) => new(id, "unused", "", id, LogMode: TelemetryOptInMode.OptInOff);

    [Fact]
    public async Task ApiListStopsWhenProbeRepeatsAnEarlierPage()
    {
        var starts = new List<ulong>();
        var client = new Client { Query = q => {
            Assert.True(q.SummaryOnly);
            starts.Add(q.StartChunkId);
            return Task.FromResult(new NoxApiCallChunkResponse { HasMore = true, Chunks = { new NoxApiCallChunk { ChunkId = 7 } } });
        }};
        await using var manager = Manager(client);
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), new NoxApiCallChunkCache()) { SelectedNode = Node("first") };
        await vm.LoadApiCallListAsync();
        Assert.Equal(new ulong[] { 0, 8 }, starts);
    }

    [Fact]
    public async Task ApiListCancelsAndDiscardsLateResponseAfterNodeSwitch()
    {
        var pending = new TaskCompletionSource<NoxApiCallChunkResponse>();
        var client = new Client { Query = _ => pending.Task };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), cache) { SelectedNode = Node("first") };
        var work = vm.LoadApiCallListAsync();
        vm.SelectedNode = Node("second");
        Assert.True(client.Token.IsCancellationRequested);
        pending.SetResult(new NoxApiCallChunkResponse { Chunks = { new NoxApiCallChunk { ChunkId = 1, ZstdRecords = ByteString.CopyFromUtf8("stale") } } });
        await work;
        Assert.Equal(0, cache.CompressedChunkCount);
        Assert.Empty(vm.RecentApiCalls);
    }
    [Fact]
    public async Task ApiDetailsAreParsedOnlyWhenApiWorkspaceIsVisible()
    {
        var client = new Client { Query = _ => Task.FromResult(new NoxApiCallChunkResponse()) };
        await using var manager = Manager(client);
        var cache = new NoxApiCallChunkCache();
        cache.PutFullRecord("first", new NoxRawApiCallRecord
        {
            CallId = "call", RawRequestJson = "{\"messages\":[]}", RawResponseJson = "{}"
        });
        using var vm = new RawLogsApiTabViewModel(manager, new LogChunkCache(), cache) { SelectedNode = Node("first") };
        var item = new RawApiCallItemModel { CallId = "call" };
        vm.RecentApiCalls.Add(item);
        vm.SelectedApiCall = item;
        vm.EnsureDetailsLoaded(item);
        Assert.False(item.IsDetailsLoaded);
        Assert.Empty(item.RequestJson);

        vm.IsTabActive = true;
        Assert.False(item.IsDetailsLoaded); // Bot Logs is still selected.
        vm.SelectedSubViewIndex = 1;
        Assert.True(item.IsDetailsLoaded);
        Assert.Contains("messages", item.RequestJson);
    }

}
