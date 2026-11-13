using System;
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
public class NoxLogSearchTests
{
    [Theory]
    [InlineData("🙂 ERROR /tmp/a.b*", "error /TMP/a.b*", false, 3, 15)]
    [InlineData("سلام دنیا", "دنیا", false, 5, 4)]
    [InlineData("foo AND bar", "AND", false, 4, 3)]
    [InlineData("abC", "bc", true, -1, 0)]
    [InlineData("straße", "STRASSE", false, -1, 0)]
    public void LiteralMatcherPreservesPunctuationAndUtf16Offsets(string text, string query, bool sensitive, int start, int length)
        => Assert.Equal((start, length), RawLogsApiTabViewModel.FindLiteral(text, query, sensitive));

    [Fact]
    public void ApiFilterMatchesUtcDateAndPersianChatNameWithoutPayload()
    {
        var item = new RawApiCallItemModel { ChatName = "علي", TimestampUnixNano = new DateTimeOffset(2026, 10, 23, 12, 34, 56, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000, CallId = "id-123", RequestJson = "unloaded content" };
        Assert.True(RawLogsApiTabViewModel.MatchesApiFilter(item, "علی"));
        Assert.True(RawLogsApiTabViewModel.MatchesApiFilter(item, "2026-10-23 12:34"));
        Assert.True(RawLogsApiTabViewModel.MatchesApiFilter(item, "id-123"));
        Assert.False(RawLogsApiTabViewModel.MatchesApiFilter(item, "unloaded content"));
    }

    [Fact]
    public void LoadedSnapshotDoesNotDecodeCompressedChunks()
    {
        var cache = new LogChunkCache();
        cache.PutChunk("noxchat", "n", new LogChunk { ChunkId = 1, FirstSeq = 256, LastSeq = 256, RecordCount = 1, ZstdRecords = ByteString.CopyFromUtf8("deliberately invalid compressed content") });
        Assert.Empty(cache.SnapshotDecodedRecords("n"));
        Assert.Equal(0, cache.DecodedChunkCount);
        Assert.False(cache.TryGetChunkError("n", 1, out _));
    }

    private sealed class FakeClient : NoxChatControlService.NoxChatControlServiceClient
    {
        public TaskCompletionSource<NoxSearchLogsResponse> Pending { get; } = new();
        public CancellationToken Token;
        public NoxSearchLogsRequest? Request;
        public RpcException? Error;
        public Func<LogChunkQuery, Task<LogChunkResponse>>? Logs;
        public Func<NoxApiCallChunkQuery, Task<NoxApiCallChunkResponse>>? Api;
        public override AsyncUnaryCall<LogChunkResponse> QueryLogChunksAsync(LogChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Logs?.Invoke(request) ?? Task.FromResult(new LogChunkResponse()));
        public override AsyncUnaryCall<NoxApiCallChunkResponse> QueryApiCallChunksAsync(NoxApiCallChunkQuery request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Api?.Invoke(request) ?? Task.FromResult(new NoxApiCallChunkResponse()));
        private static AsyncUnaryCall<T> Call<T>(Task<T> task) => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        public override AsyncUnaryCall<NoxSearchLogsResponse> SearchLogsAsync(NoxSearchLogsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Request = request; Token = cancellationToken;
            return Call(Error == null ? Pending.Task : Task.FromException<NoxSearchLogsResponse>(Error));
        }
        public override AsyncUnaryCall<NoxNodeStatusResponse> UpdateControlConfigAsync(NoxUpdateControlConfigRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(Task.FromException<NoxNodeStatusResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline test"))));
    }
    private static NoxNodeManager Manager(FakeClient client) => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json"), _ => client);
    private static RawLogsApiTabViewModel ViewModel(NoxNodeManager manager) => new(manager, new LogChunkCache(), new NoxApiCallChunkCache())
    { SelectedNode = new NoxNodeModel("test", "unused", "", "test", LogMode: TelemetryOptInMode.OptInOff), LogQuery = "needle", LogTimeRange = "All retained logs" };

    [Fact]
    public async Task EditingQueryCancelsAndDiscardsLateResponse()
    {
        var client = new FakeClient(); await using var manager = Manager(client); using var vm = ViewModel(manager);
        var work = vm.SearchLogsAsync(); Assert.True(vm.IsLogSearching);
        vm.LogQuery = "different";
        Assert.True(client.Token.IsCancellationRequested);
        client.Pending.SetResult(new NoxSearchLogsResponse { Hits = { new NoxLogSearchHit { Snippet = "stale" } } });
        await work;
        Assert.Empty(vm.LogSearchResults); Assert.False(vm.IsLogSearching); Assert.False(vm.HasMoreLogResults);
    }
    [Fact]
    public async Task SwitchingNodeDiscardsLateResponse()
    {
        var client = new FakeClient(); await using var manager = Manager(client); using var vm = ViewModel(manager);
        var work = vm.SearchLogsAsync(); vm.SelectedNode = null;
        client.Pending.SetResult(new NoxSearchLogsResponse { Hits = { new NoxLogSearchHit { Snippet = "other node" } } });
        await work; Assert.Empty(vm.LogSearchResults); Assert.False(vm.ShowLogResults);
    }
    [Fact]
    public async Task BudgetLimitedEmptyPageStillOffersContinuation()
    {
        var client = new FakeClient(); await using var manager = Manager(client); using var vm = ViewModel(manager);
        client.Pending.SetResult(new NoxSearchLogsResponse { NextCursor = "next", StopReason = "scan_budget" });
        await vm.SearchLogsAsync();
        Assert.True(vm.HasMoreLogResults); Assert.Contains("More history remains", vm.SearchStatus);
        await vm.MoreLogResultsAsync(); Assert.Equal("next", client.Request!.Cursor);
    }
    [Fact]
    public async Task OldProbeShowsLocalFilteringFallback()
    {
        var client = new FakeClient { Error = new RpcException(new Status(StatusCode.Unimplemented, "old probe")) };
        await using var manager = Manager(client); using var vm = ViewModel(manager);
        await vm.SearchLogsAsync(); Assert.Contains("filter loaded logs", vm.SearchStatus); Assert.False(vm.IsLogSearching);
    }
    [Fact]
    public async Task ApiFiltersPreserveExistingItemsAndOnlyMatchLoadedMetadata()
    {
        var client = new FakeClient(); await using var manager = Manager(client); using var vm = ViewModel(manager);
        var a = new RawApiCallItemModel { CallId = "a", ChatName = "علي" };
        var b = new RawApiCallItemModel { CallId = "b", ChatName = "Tokyo" };
        vm.RecentApiCalls.Add(a); vm.RecentApiCalls.Add(b);
        await vm.RefreshLocalFiltersAsync(); Assert.Equal(2, vm.FilteredApiCalls.Count);
        vm.ApiFilter = "علی"; await vm.RefreshLocalFiltersAsync(); Assert.Same(a, Assert.Single(vm.FilteredApiCalls));
        vm.ApiFilter = ""; await vm.RefreshLocalFiltersAsync(); Assert.Equal(2, vm.FilteredApiCalls.Count);
        Assert.Contains("2 loaded API calls", vm.ApiFilterScope);
    }
    [Fact]
    public async Task DisposeCancelsPendingSearch()
    {
        var client = new FakeClient(); await using var manager = Manager(client); var vm = ViewModel(manager);
        var work = vm.SearchLogsAsync(); vm.Dispose(); Assert.True(client.Token.IsCancellationRequested);
        client.Pending.SetResult(new NoxSearchLogsResponse { Hits = { new NoxLogSearchHit { Snippet = "late" } } });
        await work; Assert.Empty(vm.LogSearchResults);
    }

    [Fact]
    public async Task RestartDiscoversEntireHistoryWithoutDownloadingPayloads()
    {
        var client = new FakeClient();
        client.Logs = q => {
            Assert.True(q.SummaryOnly);
            return Task.FromResult(q.StartChunkId == 0
                ? new LogChunkResponse { HasMore = true, Chunks = { new LogChunk { ChunkId = 7, FirstSeq = 2000, LastSeq = 2255, RecordCount = 256 } } }
                : new LogChunkResponse { Chunks = { new LogChunk { ChunkId = 8, FirstSeq = 2256, LastSeq = 2400, RecordCount = 145 } } });
        };
        await using var manager = Manager(client); using var vm = ViewModel(manager);
        await vm.LoadLogHistoryAsync();
        Assert.Equal(2000UL, vm.FirstSeq); Assert.Equal(2400UL, vm.LastSeq); Assert.Equal(401UL, vm.TotalRecords);
        Assert.Equal(0, vm.LogCache.CompressedChunkCount);
        Assert.True(vm.LogCache.TryGetChunkIdForSeq("test", 2256, out var id)); Assert.Equal(8UL, id);
    }

    [Fact]
    public async Task SwitchingNodeDiscardsDelayedHistoryDiscovery()
    {
        var pending = new TaskCompletionSource<LogChunkResponse>();
        var client = new FakeClient { Logs = _ => pending.Task };
        await using var manager = Manager(client); using var vm = ViewModel(manager);
        var loading = vm.LoadLogHistoryAsync(); vm.SelectedNode = null;
        pending.SetResult(new LogChunkResponse { Chunks = { new LogChunk { FirstSeq = 1, LastSeq = 400, RecordCount = 400 } } });
        await loading; Assert.Equal(0UL, vm.TotalRecords); Assert.Equal(0, vm.LogCache.CompressedChunkCount);
    }

    [Theory]
    [InlineData("{\"text\":\"hello request\"}", "", "hello request")]
    [InlineData("", "{\"text\":\"hello response\"}", "HELLO RESPONSE")]
    [InlineData("{\"text\":\"\\u0639\\u0644\\u064a\"}", "", "علی")]
    public void ContentSearchMatchesRequestResponseAndEscapedPersian(string request, string response, string query)
    {
        Assert.True(RawLogsApiTabViewModel.MatchesApiContent(new NoxRawApiCallRecord { RawRequestJson = request, RawResponseJson = response }, query));
    }

    [Fact]
    public async Task ContentSearchFetchesFullRecordsAndKeepsMatchesDuringFilterRefresh()
    {
        var record = new NoxRawApiCallRecord { CallId = "content-only", RawResponseJson = "a hidden needle" };
        var raw = new NoxApiCallRecordList { Records = { record } }.ToByteArray();
        var client = new FakeClient { Api = q => {
            Assert.False(q.SummaryOnly);
            return Task.FromResult(new NoxApiCallChunkResponse { Chunks = { new NoxApiCallChunk {
                RawSize = (uint)raw.Length, ZstdRecords = ByteString.CopyFrom(ZstdCodec.Encode(raw)), RecordCount = 1 } } });
        }};
        await using var manager = Manager(client); using var vm = ViewModel(manager);
        vm.RecentApiCalls.Add(new RawApiCallItemModel { CallId = record.CallId });
        vm.ApiFilter = "needle";
        await vm.SearchApiContentAsync(); await vm.RefreshLocalFiltersAsync();
        Assert.Equal(record.CallId, Assert.Single(vm.FilteredApiCalls).CallId);
        Assert.Contains("Search complete: 1 matches", vm.ApiSearchStatus);
        vm.ApiFilter = "other"; await vm.RefreshLocalFiltersAsync(); Assert.Empty(vm.FilteredApiCalls);
    }

    [Fact]
    public async Task PreloadFetchesEveryPageWarmsCacheAndNotifiesViewer()
    {
        var starts = new System.Collections.Generic.List<ulong>();
        var client = new FakeClient { Logs = q => {
            Assert.Equal("chosen", q.NodeId); Assert.False(q.SummaryOnly); starts.Add(q.StartChunkId);
            ulong id = q.StartChunkId == 0 ? 7UL : 8UL;
            var raw = new LogRecordList { Records = { new LogRecord { Seq = id * 256, Message = "history" } } }.ToByteArray();
            return Task.FromResult(new LogChunkResponse { HasMore = id == 7, Chunks = { new LogChunk {
                ChunkId = id, FirstSeq = id * 256, LastSeq = id * 256, RecordCount = 1,
                RawSize = (uint)raw.Length, ZstdRecords = ByteString.CopyFrom(ZstdCodec.Encode(raw)) } } });
        }};
        await using var manager = Manager(client);
        var cache = new LogChunkCache();
        var settings = new NoxSettingsViewModel(manager, new TelemetryHistoryCache(), cache, new NoxApiCallChunkCache(),
            new NoxCacheSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")), startDiscovery: false);
        settings.SpoolNodeProvider = () => new NoxNodeModel("chosen", "unused", "", "chosen");
        string? notified = null; settings.SpoolPreloaded += id => notified = id;
        await settings.LoadEntireLogSpool();
        Assert.Equal(new ulong[] { 0, 8 }, starts); Assert.Equal("chosen", notified);
        Assert.Equal(2, cache.CompressedChunkCount); Assert.Equal(2, cache.DecodedChunkCount);
        Assert.False(settings.IsPreloading); Assert.Equal("Spool preload completed", settings.PreloadStatusText);
    }

    [Fact]
    public async Task PreloadRetriesSamePageAndReportsFailureWithoutSkippingHistory()
    {
        var starts = new System.Collections.Generic.List<ulong>();
        var client = new FakeClient { Logs = q => {
            starts.Add(q.StartChunkId);
            return Task.FromException<LogChunkResponse>(new InvalidOperationException("failed page"));
        }};
        await using var manager = Manager(client);
        var settings = new NoxSettingsViewModel(manager, new TelemetryHistoryCache(), new LogChunkCache(), new NoxApiCallChunkCache(),
            new NoxCacheSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")), startDiscovery: false);
        settings.SpoolNodeProvider = () => new NoxNodeModel("chosen", "unused", "", "chosen");
        bool notified = false; settings.SpoolPreloaded += _ => notified = true;
        await settings.LoadEntireLogSpool();
        Assert.Equal(new ulong[] { 0, 0, 0 }, starts); Assert.False(notified);
        Assert.Contains("Spool load error", settings.PreloadStatusText); Assert.False(settings.IsPreloading);
    }

}
