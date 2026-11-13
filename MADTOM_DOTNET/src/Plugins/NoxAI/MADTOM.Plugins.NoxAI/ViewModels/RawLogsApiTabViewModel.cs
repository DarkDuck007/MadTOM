using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed class RawApiCallImageItem
{
    public string Label { get; set; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public Bitmap? Preview { get; init; }
}

public sealed partial class RawApiCallItemModel : ObservableObject
{
    public string CallId { get; init; } = string.Empty;
    public string Caller { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChatName))]
    private string _chatName = string.Empty;
    public bool HasChatName => !string.IsNullOrWhiteSpace(ChatName);
    public string TimestampText { get; init; } = string.Empty;
    public long TimestampUnixNano { get; init; }
    public uint DurationMs { get; init; }
    public uint PromptTokens { get; init; }
    public uint CompletionTokens { get; init; }
    public uint CachedTokens { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public NoxTraceContext? TraceContext { get; init; }
    [ObservableProperty] private string _requestJson = string.Empty;
    [ObservableProperty] private string _responseJson = string.Empty;
    public ObservableCollection<RawApiCallImageItem> Images { get; } = new();
    [ObservableProperty] private bool _hasImages;
    public ObservableCollection<ParsedTurnItemModel> ParsedTurns { get; } = new();
    [ObservableProperty] private bool _hasParsedTurns;
    [ObservableProperty] private bool _isDetailsLoaded;
    // Chats view: raw payload panes are collapsed by default to keep the history dense
    [ObservableProperty] private bool _showRequestJson;
    [ObservableProperty] private bool _showResponseJson;
}

public sealed partial class RawLogsApiTabViewModel : ObservableObject, IDisposable
{
    private readonly NoxNodeManager _nodeManager;
    private readonly LogChunkCache _logCache;
    private readonly NoxApiCallChunkCache _apiCache;
    private readonly HashSet<ulong> _inflightLogChunks = new();
    private readonly HashSet<string> _seenApiCallIds = new(StringComparer.Ordinal);

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _nodeId = string.Empty;
    [ObservableProperty] private TelemetryOptInMode _logMode = TelemetryOptInMode.OptInMonitorAndStore;
    [ObservableProperty] private string _modeBadgeText = "STREAM & STORE";
    [ObservableProperty] private int _selectedSubViewIndex; // 0 = Virtual Logs, 1 = Raw API Calls
    [ObservableProperty] private bool _isTabActive;
    [ObservableProperty] private RawApiCallItemModel? _selectedApiCall;
    [ObservableProperty] private string _spoolStatusSummary = "Spool: Standby";

    // Virtual log view properties
    [ObservableProperty] private ulong _totalRecords;
    [ObservableProperty] private ulong _firstSeq;
    [ObservableProperty] private ulong _lastSeq;
    [ObservableProperty] private bool _isFollowMode = true;

    partial void OnIsFollowModeChanged(bool value)
    {
        if (value)
        {
            HasNewLogs = false;
        }
    }

    [ObservableProperty] private bool _hasNewLogs;
    [ObservableProperty] private int _cacheRevision;
    private bool _isSeqRangeInitialized;

    [RelayCommand]
    public void ScrollToLatest()
    {
        IsFollowMode = true;
        HasNewLogs = false;
    }

    // Live Streaming state (configured per-node in Settings)
    [ObservableProperty] private bool _includeApiCalls = true;
    [ObservableProperty] private bool _includeImages = true;
    [ObservableProperty] private string _logStreamStatus = "Initializing...";
    [ObservableProperty] private ulong _streamedChunksCount;
    [ObservableProperty] private ulong _droppedChunksCount;

    private CancellationTokenSource? _logStreamCts;

    public LogChunkCache LogCache => _logCache;
    public NoxApiCallChunkCache ApiCache => _apiCache;

    public ObservableCollection<RawApiCallItemModel> RecentApiCalls { get; } = new();

    [ObservableProperty] private bool _showGoToNewestButton;
    [ObservableProperty] private bool _isScrolledDown;

    public event Action? ScrollToTopRequested;

    [RelayCommand]
    public void GoToNewest()
    {
        if (FilteredApiCalls.Count > 0)
        {
            SelectedApiCall = FilteredApiCalls[0];
        }
        else if (RecentApiCalls.Count > 0)
        {
            SelectedApiCall = RecentApiCalls[0];
        }
        ShowGoToNewestButton = false;
        IsScrolledDown = false;
        ScrollToTopRequested?.Invoke();
    }

    public RawLogsApiTabViewModel(
        NoxNodeManager nodeManager,
        LogChunkCache logCache,
        NoxApiCallChunkCache apiCache)
    {
        _nodeManager = nodeManager;
        _logCache = logCache;
        _apiCache = apiCache;
        InitializeSearch();
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();
    }

    private async Task OpenChatCallInRawAsync(string callId)
    {
        var node = SelectedNode;
        var item = RecentApiCalls.FirstOrDefault(c => c.CallId == callId);
        if (item == null)
        {
            ChatHistoryStatus = "That call is not in the loaded Raw API Calls list (it came from the probe's per-chat index).";
            return;
        }
        // The destination ListBox must contain the call before its selection is set.
        ApiFilter = string.Empty;
        CancelApiContentSearch();
        await RefreshLocalFiltersAsync();
        if (SelectedNode != node || !RecentApiCalls.Contains(item)) return;
        if (!FilteredApiCalls.Contains(item))
        {
            int index = 0;
            while (index < FilteredApiCalls.Count && FilteredApiCalls[index].TimestampUnixNano > item.TimestampUnixNano) index++;
            FilteredApiCalls.Insert(index, item);
        }
        SelectedApiCall = item;
        SelectedSubViewIndex = 1;
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        ResetConnectionRecovery();
        ResetSearch();
        CancelApiContentSearch();
        _historyCts.Cancel(); _historyCts.Dispose(); _historyCts = new();
        lock (_inflightLogChunks) { _inflightLogChunks.Clear(); _chunkFetchFailures.Clear(); }
        _isSeqRangeInitialized = false;
        FirstSeq = 0;
        LastSeq = 0;
        TotalRecords = 0;
        StreamedChunksCount = 0;
        DroppedChunksCount = 0;
        _seenApiCallIds.Clear();
        RecentApiCalls.Clear();
        FilteredApiCalls.Clear();
        SelectedApiCall = null;
        ResetChatsState();

        if (value != null)
        {
            ApplyNodeSettings(value);
        }
        else
        {
            NodeId = string.Empty;
            LogMode = TelemetryOptInMode.OptInOff;
            ModeBadgeText = "NO NODE";
            SpoolStatusSummary = "Spool: Standby";
            StopLogStream();
        }
    }

    public void ApplyNodeSettings(NoxNodeModel node)
    {
        NodeId = node.Id;
        LogMode = node.LogMode;
        IncludeApiCalls = node.IncludeApiCalls;
        IncludeImages = node.IncludeImages;

        ModeBadgeText = node.LogMode switch
        {
            TelemetryOptInMode.OptInMonitorAndStore => "STREAM & STORE",
            TelemetryOptInMode.OptInMonitorOnly => "LIVE STREAM",
            _ => "OFF (CONFIGURE IN ⚙)"
        };

        MarkHistoryRecoveryNeeded();

        if (node.LogMode == TelemetryOptInMode.OptInOff)
        {
            StopLogStream();
        }
        else
        {
            RestartLogStream();
        }
    }

    private async Task<bool> SyncNodeConfigAsync(NoxNodeModel node, CancellationToken token)
    {
        try
        {
            var client = _nodeManager.GetClient(node);
            await client.UpdateControlConfigAsync(new NoxUpdateControlConfigRequest
            {
                NodeId = node.Id,
                StoreRawLogs = node.LogMode == TelemetryOptInMode.OptInMonitorAndStore
            }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
            return !token.IsCancellationRequested && SelectedNode == node;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented) { return true; }
        catch { return false; }
    }

    private void StartLogStream()
    {
        StopLogStream();
        if (SelectedNode == null)
        {
            LogStreamStatus = "No node selected";
            return;
        }

        if (LogMode == TelemetryOptInMode.OptInOff)
        {
            LogStreamStatus = "Disabled in Node Settings (⚙)";
            return;
        }

        _logStreamCts = new CancellationTokenSource();
        var ct = _logStreamCts.Token;
        var node = SelectedNode;
        LogStreamStatus = "Connecting live stream...";

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    var req = new NoxLogApiSubscriptionRequest
                    {
                        NodeId = node.Id,
                        IncludeRawApiCalls = IncludeApiCalls,
                        IncludeImages = IncludeImages
                    };

                    using var call = client.SubscribeLogsAndApi(req, cancellationToken: ct);
                    // Constructing a streaming call does not establish connectivity.
                    await call.ResponseHeadersAsync.WaitAsync(ct);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedNode != node) return;
                        _ = RecoverHistoryAfterConnectionAsync(force: true);
                        LogStreamStatus = $"Live ({TotalRecords:N0} logs · {RecentApiCalls.Count} API calls)";
                    });

                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        var chunk = call.ResponseStream.Current;
                        if (ct.IsCancellationRequested) break;
                        ProcessIncomingChunk(node.Id, chunk);
                    }
                    if (!ct.IsCancellationRequested)
                        throw new System.IO.IOException("Live stream ended");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedNode != node) return;
                        MarkHistoryRecoveryNeeded();
                        LogStreamStatus = $"Reconnecting ({ex.Message})...";
                    });
                    try { await Task.Delay(2500, ct); } catch { break; }
                }
            }
        }, ct);
    }

    private void ProcessIncomingChunk(string nodeId, NoxLogApiChunk chunk)
    {
        if (chunk.ZstdPayload == null || chunk.ZstdPayload.IsEmpty) return;

        string kind = chunk.ChunkKind ?? string.Empty;

        if (kind == "chat_trace")
        {
            try
            {
                var trace = NoxChatTraceEvent.Parser.ParseFrom(chunk.ZstdPayload);
                Dispatcher.UIThread.Post(() =>
                {
                    if (NodeId == nodeId) IngestChatTraceEvent(trace);
                });
            }
            catch (Google.Protobuf.InvalidProtocolBufferException) { }
            return;
        }

        // 1. Explicit API call chunk
        if (kind == "api")
        {
            TryIngestApiChunk(nodeId, chunk.ZstdPayload, chunk.DroppedChunks);
            return;
        }

        // 2. Explicit Log chunk
        if (kind == "log")
        {
            TryIngestLogChunk(nodeId, chunk.ZstdPayload, chunk.DroppedChunks);
            return;
        }

        // 3. Fallback if ChunkKind was empty (legacy probe): inspect inner decompressed records
        if (TryIngestApiChunk(nodeId, chunk.ZstdPayload, chunk.DroppedChunks, verifyInnerFields: true))
        {
            return;
        }

        TryIngestLogChunk(nodeId, chunk.ZstdPayload, chunk.DroppedChunks);
    }

    private bool TryIngestLogChunk(string nodeId, ByteString payload, ulong dropped)
    {
        try
        {
            var logChunk = LogChunk.Parser.ParseFrom(payload);
            if (logChunk != null && (logChunk.RecordCount > 0 || logChunk.ZstdRecords?.Length > 0))
            {
                _logCache.PutChunk("noxchat", nodeId, logChunk);
                Dispatcher.UIThread.Post(() =>
                {
                    if (NodeId != nodeId) return;
                    StreamedChunksCount++;
                    if (dropped > 0) DroppedChunksCount += (uint)dropped;
                    UpdateSeqBounds(logChunk.FirstSeq, logChunk.LastSeq);
                    CacheRevision++;
                    if (!IsFollowMode)
                    {
                        HasNewLogs = true;
                    }
                    LogStreamStatus = $"Active ({StreamedChunksCount} chunks · {TotalRecords:N0} logs · {RecentApiCalls.Count} API calls)";
                });
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool TryIngestApiChunk(string nodeId, ByteString payload, ulong dropped, bool verifyInnerFields = false)
    {
        try
        {
            var apiChunk = NoxApiCallChunk.Parser.ParseFrom(payload);
            if (apiChunk != null && (apiChunk.RecordCount > 0 || apiChunk.ZstdRecords?.Length > 0))
            {
                var decoded = _apiCache.PutChunk(nodeId, apiChunk);
                if (decoded != null && decoded.Count > 0)
                {
                    if (verifyInnerFields && string.IsNullOrEmpty(decoded[0].CallId) && string.IsNullOrEmpty(decoded[0].Model))
                    {
                        return false;
                    }

                    var models = decoded.Select(r => BuildApiCallItemModel(r, loadFullDetails: false)).ToList();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (NodeId != nodeId) return;
                    StreamedChunksCount++;
                        if (dropped > 0) DroppedChunksCount += (uint)dropped;
                        AddOrUpdateApiCallsOnUi(models);
                        LogStreamStatus = $"Active ({StreamedChunksCount} chunks · {TotalRecords:N0} logs · {RecentApiCalls.Count} API calls)";
                    });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    partial void OnSelectedApiCallChanged(RawApiCallItemModel? value)
    {
        if (value == null || value.IsDetailsLoaded) return;
        EnsureDetailsLoaded(value);
    }

    partial void OnIsTabActiveChanged(bool value)
    {
        UpdateChatsActivity();
        if (value && SelectedApiCall != null) EnsureDetailsLoaded(SelectedApiCall);
    }

    partial void OnSelectedSubViewIndexChanged(int value)
    {
        UpdateChatsActivity();
        if (value == 1 && SelectedApiCall != null) EnsureDetailsLoaded(SelectedApiCall);
    }

    private void UpdateChatsActivity()
    {
        OnChatsViewActivityChanged();
    }

    public void EnsureDetailsLoaded(RawApiCallItemModel item)
    {
        // Hidden tabs still receive live summaries; defer expensive JSON/image parsing
        // until the operator actually opens the API detail workspace.
        if (!IsTabActive || SelectedSubViewIndex != 1) return;
        if (item.IsDetailsLoaded) return;
        var node = SelectedNode;
        if (node == null) return;

        var rec = _apiCache.GetRecord(node.Id, item.CallId);
        if (rec != null && !string.IsNullOrWhiteSpace(rec.RawRequestJson))
        {
            PopulateItemDetails(item, rec);
            return;
        }

        // Fetch full record on-demand from server
        _ = Task.Run(async () =>
        {
            try
            {
                var client = _nodeManager.GetClient(node);
                var fullRec = await client.GetApiCallDetailAsync(new NoxApiCallDetailRequest
                {
                    NodeId = node.Id,
                    CallId = item.CallId
                });

                if (fullRec != null && !string.IsNullOrEmpty(fullRec.CallId))
                {
                    _apiCache.PutFullRecord(node.Id, fullRec);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (SelectedNode == node && RecentApiCalls.Contains(item) && IsTabActive && SelectedSubViewIndex == 1)
                        {
                            PopulateItemDetails(item, fullRec);
                        }
                    });
                }
            }
            catch { }
        });
    }

    private static void PopulateItemDetails(RawApiCallItemModel item, NoxRawApiCallRecord r)
    {
        item.RequestJson = FormatJsonForDisplay(r.RawRequestJson);
        item.ResponseJson = FormatJsonForDisplay(r.RawResponseJson);

        item.Images.Clear();
        if (r.ImageDataUrls != null)
        {
            foreach (var img in r.ImageDataUrls)
            {
                if (img.ImageBytes != null && !img.ImageBytes.IsEmpty)
                {
                    try
                    {
                        using var ms = new MemoryStream(img.ImageBytes.ToByteArray());
                        var bmp = new Bitmap(ms);
                        item.Images.Add(new RawApiCallImageItem
                        {
                            Label = string.IsNullOrEmpty(img.Label) ? $"Image #{item.Images.Count + 1}" : img.Label,
                            ContentHash = Convert.ToHexString(SHA256.HashData(img.ImageBytes.Span)),
                            MimeType = img.MimeType ?? "image/jpeg",
                            Preview = bmp
                        });
                    }
                    catch { }
                }
            }
        }
        item.HasImages = item.Images.Count > 0;

        item.ParsedTurns.Clear();
        try
        {
            var parsed = NoxChatSessionParser.Parse(item);
            foreach (var p in parsed)
            {
                item.ParsedTurns.Add(p);
            }
        }
        catch { }
        item.HasParsedTurns = item.ParsedTurns.Count > 0;
        item.HasImages = item.Images.Count > 0;

        if (string.IsNullOrEmpty(item.ChatName))
        {
            item.ChatName = !string.IsNullOrEmpty(r.ChatName)
                ? r.ChatName
                : NoxChatSessionParser.ExtractChatName(r.RawRequestJson, r.Caller);
        }

        item.IsDetailsLoaded = true;
    }

    private static RawApiCallItemModel BuildApiCallItemModel(NoxRawApiCallRecord r, bool loadFullDetails = false)
    {
        string tsText = r.TimestampUnixNano > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampUnixNano / 1_000_000).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
            : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        string chatName = !string.IsNullOrEmpty(r.ChatName)
            ? r.ChatName
            : NoxChatSessionParser.ExtractChatName(r.RawRequestJson, r.Caller);

        var item = new RawApiCallItemModel
        {
            CallId = string.IsNullOrEmpty(r.CallId) ? Guid.NewGuid().ToString("N")[..12] : r.CallId,
            Caller = string.IsNullOrEmpty(r.Caller) ? "LLMClient" : r.Caller,
            Model = r.Model ?? string.Empty,
            ChatName = chatName,
            TimestampText = tsText,
            TimestampUnixNano = r.TimestampUnixNano > 0 ? r.TimestampUnixNano : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
            DurationMs = r.DurationMs,
            PromptTokens = r.PromptTokens,
            CompletionTokens = r.CompletionTokens,
            CachedTokens = r.CachedTokens,
            BaseUrl = r.BaseUrl,
            Temperature = r.Temperature,
            ErrorMessage = r.ErrorMessage,
            TraceContext = r.TraceContext?.Clone(),
            HasImages = r.ImageDataUrls != null && r.ImageDataUrls.Count > 0,
            IsDetailsLoaded = loadFullDetails
        };

        if (loadFullDetails)
        {
            PopulateItemDetails(item, r);
        }

        return item;
    }

    private static readonly JsonSerializerOptions JsonDisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex UnicodeEscapeRegex = new(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);
    private static readonly Regex SurrogatePairRegex = new(@"\\u([dD][89abAB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})", RegexOptions.Compiled);

    public static string FormatJsonForDisplay(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return string.Empty;
        string result;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            result = JsonSerializer.Serialize(doc, JsonDisplayOptions);
        }
        catch
        {
            result = rawJson;
        }

        if (result.Contains(@"\u", StringComparison.Ordinal))
        {
            result = TryUnescapeUnicode(result);
        }
        return result;
    }

    private static string TryUnescapeUnicode(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains(@"\u", StringComparison.Ordinal)) return input;
        try
        {
            // First decode surrogate pairs \uD8xx\uDCxx (emojis, etc.)
            string pass1 = SurrogatePairRegex.Replace(input, m =>
            {
                int high = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                int low = int.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
                return char.ConvertFromUtf32(char.ConvertToUtf32((char)high, (char)low));
            });

            // Then decode all remaining BMP \uXXXX characters (Persian, Arabic, CJK, etc.)
            return UnicodeEscapeRegex.Replace(pass1, m =>
            {
                int code = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                return ((char)code).ToString();
            });
        }
        catch
        {
            return input;
        }
    }

    private void AddOrUpdateApiCallsOnUi(IReadOnlyList<RawApiCallItemModel> items)
    {
        if (items.Count == 0) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddOrUpdateApiCallsOnUi(items));
            return;
        }

        var newItems = new List<RawApiCallItemModel>(items.Count);
        foreach (var item in items)
        {
            if (_seenApiCallIds.Add(item.CallId))
            {
                newItems.Add(item);
            }
        }

        if (newItems.Count == 0) return;

        if (RecentApiCalls.Count == 0)
        {
            // Fast path: sort descending by timestamp and add
            var sorted = newItems.OrderByDescending(x => x.TimestampUnixNano);
            foreach (var s in sorted)
            {
                RecentApiCalls.Add(s);
                if (MatchesCurrentApiFilter(s))
                {
                    FilteredApiCalls.Add(s);
                }
            }
        }
        else
        {
            // Insert in descending order (newest first)
            foreach (var item in newItems)
            {
                int index = 0;
                while (index < RecentApiCalls.Count && RecentApiCalls[index].TimestampUnixNano > item.TimestampUnixNano)
                {
                    index++;
                }
                RecentApiCalls.Insert(index, item);
                if (MatchesCurrentApiFilter(item))
                {
                    int filteredIndex = 0;
                    while (filteredIndex < FilteredApiCalls.Count && FilteredApiCalls[filteredIndex].TimestampUnixNano > item.TimestampUnixNano)
                    {
                        filteredIndex++;
                    }
                    FilteredApiCalls.Insert(filteredIndex, item);
                }
            }
        }

        const int maxInMemoryCalls = 10_000;
        while (RecentApiCalls.Count > maxInMemoryCalls)
        {
            var removed = RecentApiCalls[RecentApiCalls.Count - 1];
            _seenApiCallIds.Remove(removed.CallId);
            RecentApiCalls.RemoveAt(RecentApiCalls.Count - 1);
            FilteredApiCalls.Remove(removed);
        }

        OnApiCallsAddedForChats(newItems);

        if (FilteredApiCalls.Count > 0)
        {
            if (SelectedApiCall == null || !IsScrolledDown)
            {
                SelectedApiCall = FilteredApiCalls[0];
            }
            else
            {
                ShowGoToNewestButton = true;
            }
        }
    }

    private void UpdateSeqBounds(ulong firstSeq, ulong lastSeq)
    {
        if (lastSeq == 0 && firstSeq == 0) return;
        if (!_isSeqRangeInitialized)
        {
            FirstSeq = firstSeq;
            LastSeq = lastSeq;
            _isSeqRangeInitialized = true;
        }
        else
        {
            if (firstSeq < FirstSeq) FirstSeq = firstSeq;
            if (lastSeq > LastSeq) LastSeq = lastSeq;
        }
        TotalRecords = LastSeq >= FirstSeq ? (LastSeq - FirstSeq + 1) : 0;
    }

    private readonly Dictionary<ulong, DateTime> _chunkFetchFailures = new();
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(10);

    public bool IsChunkUnavailable(ulong chunkId)
    {
        lock (_inflightLogChunks)
        {
            return _chunkFetchFailures.TryGetValue(chunkId, out var failTime) && (DateTime.UtcNow - failTime < RetryCooldown);
        }
    }

    public Func<ulong, bool> IsChunkUnavailablePredicate => IsChunkUnavailable;

    public void RequestChunk(ulong chunkId)
    {
        var node = SelectedNode;
        if (node == null) return;

        var token = _historyCts.Token;
        lock (_inflightLogChunks)
        {
            if (IsChunkUnavailable(chunkId)) return;
            if (!_inflightLogChunks.Add(chunkId)) return;
        }

        _ = Task.Run(async () =>
        {
            bool found = false;
            try
            {
                var client = _nodeManager.GetClient(node);
                var resp = await client.QueryLogChunksAsync(new LogChunkQuery
                {
                    NodeId = node.Id,
                    StartChunkId = chunkId,
                    MaxChunks = 4
                }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                token.ThrowIfCancellationRequested();

                if (resp.Chunks.Count > 0)
                {
                    foreach (var c in resp.Chunks)
                    {
                        if (c.ChunkId == chunkId) found = true;
                        _logCache.PutChunk("noxchat", node.Id, c);
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || SelectedNode != node) return;
                        foreach (var c in resp.Chunks)
                        {
                            UpdateSeqBounds(c.FirstSeq, c.LastSeq);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                lock (_inflightLogChunks)
                {
                    if (token.IsCancellationRequested) return;
                    _chunkFetchFailures[chunkId] = DateTime.UtcNow;
                    _logCache.RecordChunkError(node.Id, chunkId, ex.GetType().Name, ex.Message, ex.ToString());
                }
            }
            finally
            {
                lock (_inflightLogChunks)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _inflightLogChunks.Remove(chunkId);
                        if (!found && !_chunkFetchFailures.ContainsKey(chunkId))
                        {
                            _chunkFetchFailures[chunkId] = DateTime.UtcNow;
                            _logCache.RecordChunkError(node.Id, chunkId, "NOT_FOUND",
                                $"Chunk #{chunkId} was not found on node {node.Id} spool.");
                        }
                        else if (found)
                        {
                            _chunkFetchFailures.Remove(chunkId);
                        }
                    }
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || SelectedNode != node) return;
                    CacheRevision++;
                });
            }
        });
    }

    private void StopLogStream()
    {
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
        _logStreamCts = null;
        LogStreamStatus = LogMode == TelemetryOptInMode.OptInOff
            ? "Disabled in Node Settings (⚙)"
            : "Stopped";
    }

    private void RestartLogStream()
    {
        StopLogStream();
        StartLogStream();
    }

    [RelayCommand]
    public async Task RefreshSpoolStatus()
    {
        var node = SelectedNode;
        if (node == null || _disposed) return;
        var token = _historyCts.Token;
        try
        {
            var client = _nodeManager.GetClient(node);
            var stats = await client.GetSpoolStatsAsync(new NoxSpoolStatsRequest
            {
                NodeId = node.Id
            }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
            if (token.IsCancellationRequested || SelectedNode != node) return;

            string loadedText = RecentApiCalls.Count > 0 ? $", {RecentApiCalls.Count:N0} loaded" : string.Empty;
            SpoolStatusSummary = $"Spool: {stats.TotalSpoolBytes / 1048576.0:F1} / {stats.MaxSpoolBytes / 1048576.0:F0} MiB ({stats.TotalLogRecords:N0} logs, {stats.TotalApiCalls:N0} API calls{loadedText})";
        }
        catch
        {
            if (!token.IsCancellationRequested && SelectedNode == node)
                SpoolStatusSummary = "Spool: Offline";
        }
    }

    private bool _isLoadingSpool;

    private int _apiListRevision;

    [RelayCommand]
    public async Task LoadApiCallListAsync() => await LoadApiCallListCoreAsync();

    private Task<bool>? _apiListTask;
    private CancellationToken _apiListToken;

    private Task<bool> LoadApiCallListCoreAsync()
    {
        lock (_historyLoadGate)
        {
            var token = _historyCts.Token;
            if (_apiListTask is { IsCompleted: false } && _apiListToken == token)
                return _apiListTask;
            _apiListToken = token;
            return _apiListTask = DiscoverApiCallsAsync();
        }
    }

    private async Task<bool> DiscoverApiCallsAsync()
    {
        var node = SelectedNode;
        if (node == null) return false;
        var token = _historyCts.Token;
        int revision = ++_apiListRevision;
        bool IsCurrent() => !token.IsCancellationRequested && SelectedNode == node && revision == _apiListRevision;

        try
        {
            var client = _nodeManager.GetClient(node);
            ulong nextApiChunkId = 0;
            while (IsCurrent())
            {
                var apiResp = await client.QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                {
                    NodeId = node.Id,
                    StartChunkId = nextApiChunkId,
                    MaxChunks = 32,
                    SummaryOnly = true
                }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                if (!IsCurrent()) return false;
                if (apiResp.Chunks.Count == 0) return true;

                // A summary response is still compressed protobuf. Decode and build
                // metadata away from the dispatcher, including for older probes
                // that return full bodies despite SummaryOnly.
                var apiModels = await Task.Run(() =>
                {
                    var models = new List<RawApiCallItemModel>();
                    foreach (var chunk in apiResp.Chunks)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var record in _apiCache.PutChunk(node.Id, chunk))
                        {
                            token.ThrowIfCancellationRequested();
                            models.Add(BuildApiCallItemModel(record, loadFullDetails: false));
                        }
                    }
                    return models;
                }, token);
                if (!IsCurrent()) return false;
                if (apiModels.Count > 0)
                {
                    // Recheck when the queued publication actually runs: the user
                    // may have selected another node in the meantime.
                    if (Dispatcher.UIThread.CheckAccess()) AddOrUpdateApiCallsOnUi(apiModels);
                    else Dispatcher.UIThread.Post(() => { if (IsCurrent()) AddOrUpdateApiCallsOnUi(apiModels); });
                }

                ulong lastChunkId = apiResp.Chunks.Max(c => c.ChunkId);
                if (!apiResp.HasMore || lastChunkId == ulong.MaxValue || lastChunkId < nextApiChunkId) break;
                nextApiChunkId = lastChunkId + 1;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; /* The connection recovery loop retries. */ }
        return IsCurrent();
    }

    [RelayCommand]
    public async Task LoadSpooledChunks()
    {
        var node = SelectedNode;
        if (node == null || _isLoadingSpool) return;
        _isLoadingSpool = true;

        try
        {
            var client = _nodeManager.GetClient(node);

            NoxSpoolStatsResponse? stats = null;
            try
            {
                stats = await client.GetSpoolStatsAsync(new NoxSpoolStatsRequest { NodeId = node.Id });
            }
            catch { }

            // 1. Load entire log spool in safe batches of up to 32 chunks
            if (stats == null || stats.TotalLogRecords > 0)
            {
                ulong nextLogChunkId = 0;
                bool hasMoreLogs = true;
                int consecutiveErrors = 0;

                while (hasMoreLogs && consecutiveErrors < 5)
                {
                    try
                    {
                        var logResp = await client.QueryLogChunksAsync(new LogChunkQuery
                        {
                            NodeId = node.Id,
                            StartChunkId = nextLogChunkId,
                            MaxChunks = 32
                        });

                        if (logResp.Chunks.Count == 0)
                        {
                            break;
                        }

                        consecutiveErrors = 0;
                        ulong maxChunkId = nextLogChunkId;
                        foreach (var chunk in logResp.Chunks)
                        {
                            if (chunk.ChunkId >= maxChunkId) maxChunkId = chunk.ChunkId;
                            _logCache.PutChunk("noxchat", node.Id, chunk);
                            UpdateSeqBounds(chunk.FirstSeq, chunk.LastSeq);
                        }
                        CacheRevision++;
                        nextLogChunkId = maxChunkId + 1;
                        hasMoreLogs = logResp.HasMore;
                    }
                    catch (Exception logEx)
                    {
                        consecutiveErrors++;
                        SpoolStatusSummary = $"Log chunk #{nextLogChunkId} error: {logEx.Message}";
                        nextLogChunkId += 32;
                    }
                }
            }

            // 2. Load entire API call spool dynamically
            if (stats == null || stats.TotalApiCalls > 0)
            {
                ulong nextApiChunkId = 0;
                bool hasMoreApi = true;
                int consecutiveErrors = 0;

                while (hasMoreApi && consecutiveErrors < 5)
                {
                    try
                    {
                        var apiResp = await client.QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                        {
                            NodeId = node.Id,
                            StartChunkId = nextApiChunkId,
                            MaxChunks = 32
                        });

                        if (apiResp.Chunks.Count == 0)
                        {
                            break;
                        }

                        consecutiveErrors = 0;
                        ulong maxChunkId = nextApiChunkId;
                        var apiModels = new List<RawApiCallItemModel>();
                        foreach (var chunk in apiResp.Chunks)
                        {
                            if (chunk.ChunkId >= maxChunkId) maxChunkId = chunk.ChunkId;
                            var decoded = _apiCache.PutChunk(node.Id, chunk);
                            if (decoded != null)
                            {
                                foreach (var r in decoded)
                                {
                                    apiModels.Add(BuildApiCallItemModel(r, loadFullDetails: false));
                                }
                            }
                        }

                        if (apiModels.Count > 0)
                        {
                            AddOrUpdateApiCallsOnUi(apiModels);
                        }

                        nextApiChunkId = maxChunkId + 1;
                        hasMoreApi = apiResp.HasMore;
                        SpoolStatusSummary = $"Loading spool: API chunk #{maxChunkId} ({RecentApiCalls.Count:N0} calls loaded)...";
                    }
                    catch (Exception apiEx)
                    {
                        consecutiveErrors++;
                        SpoolStatusSummary = $"API chunk #{nextApiChunkId} error: {apiEx.Message}";
                        nextApiChunkId += 32;
                    }
                }

                _apiCache.FlushPending(node.Id);
                if (FilteredApiCalls.Count > 0)
                {
                    SelectedApiCall = FilteredApiCalls[0];
                    ScrollToTopRequested?.Invoke();
                }
                else if (RecentApiCalls.Count > 0)
                {
                    SelectedApiCall = RecentApiCalls[0];
                    ScrollToTopRequested?.Invoke();
                }
                if (SelectedApiCall != null)
                {
                    EnsureDetailsLoaded(SelectedApiCall);
                }
            }

            if (stats != null)
            {
                SpoolStatusSummary = $"Spool: {stats.TotalSpoolBytes / 1048576.0:F1} / {stats.MaxSpoolBytes / 1048576.0:F0} MiB ({stats.TotalLogRecords:N0} logs, {RecentApiCalls.Count:N0} API calls loaded)";
            }
            else
            {
                await RefreshSpoolStatus();
            }

            LogStreamStatus = $"Spool Loaded ({TotalRecords:N0} logs · {RecentApiCalls.Count:N0} API calls)";
        }
        catch (Exception ex)
        {
            SpoolStatusSummary = $"Spool error: {ex.Message}";
        }
        finally
        {
            _isLoadingSpool = false;
        }
    }

    [RelayCommand]
    public async Task ClearSpool()
    {
        var node = SelectedNode;
        if (node == null) return;

        try
        {
            var client = _nodeManager.GetClient(node);
            var resp = await client.ClearSpoolAsync(new NoxClearSpoolRequest
            {
                NodeId = node.Id
            });

            if (!resp.Success)
            {
                SpoolStatusSummary = $"Clear spool failed: {resp.Message}";
                return;
            }

            ResetSearch();
            CancelApiContentSearch();
            _historyCts.Cancel(); _historyCts.Dispose(); _historyCts = new();
            lock (_inflightLogChunks) { _inflightLogChunks.Clear(); _chunkFetchFailures.Clear(); }
            // Clear local caches and UI collections
            _apiCache.Clear(node.Id);
            _logCache.Clear(node.Id);

            _seenApiCallIds.Clear();
            RecentApiCalls.Clear();
            FilteredApiCalls.Clear();
            SelectedApiCall = null;
            ResetChatsState();
            if (IsChatsViewActive) OnChatsViewActivityChanged();
            FirstSeq = 0;
            LastSeq = 0;
            TotalRecords = 0;
            _isSeqRangeInitialized = false;
            StreamedChunksCount = 0;
            CacheRevision++;

            await RefreshSpoolStatus();
            SpoolStatusSummary = "Spool: Cleared (server & client reset)";
        }
        catch (Exception ex)
        {
            SpoolStatusSummary = $"Clear spool error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelChatLoads();
        _chatRefreshTimer?.Stop();
        CancelApiContentSearch();
        _historyCts.Cancel(); _historyCts.Dispose();
        CancelLogSearch();
        _filterTimer?.Stop();
        StopLogStream();
    }
}
