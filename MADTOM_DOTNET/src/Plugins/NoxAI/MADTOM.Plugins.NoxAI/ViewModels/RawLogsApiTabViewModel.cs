using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    public string Label { get; init; } = string.Empty;
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
    [ObservableProperty] private string _requestJson = string.Empty;
    [ObservableProperty] private string _responseJson = string.Empty;
    public ObservableCollection<RawApiCallImageItem> Images { get; } = new();
    [ObservableProperty] private bool _hasImages;
    public ObservableCollection<ParsedTurnItemModel> ParsedTurns { get; } = new();
    [ObservableProperty] private bool _hasParsedTurns;
    public bool IsDetailsLoaded { get; set; }
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

    public RawLogsApiTabViewModel(
        NoxNodeManager nodeManager,
        LogChunkCache logCache,
        NoxApiCallChunkCache apiCache)
    {
        _nodeManager = nodeManager;
        _logCache = logCache;
        _apiCache = apiCache;
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        _isSeqRangeInitialized = false;
        FirstSeq = 0;
        LastSeq = 0;
        TotalRecords = 0;
        StreamedChunksCount = 0;
        DroppedChunksCount = 0;
        _seenApiCallIds.Clear();
        RecentApiCalls.Clear();
        SelectedApiCall = null;

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

        _ = SyncNodeConfigAndSpoolAsync(node);

        if (node.LogMode == TelemetryOptInMode.OptInOff)
        {
            StopLogStream();
        }
        else
        {
            RestartLogStream();
        }
    }

    private async Task SyncNodeConfigAndSpoolAsync(NoxNodeModel node)
    {
        try
        {
            var client = _nodeManager.GetClient(node);
            bool storeOnServer = node.LogMode == TelemetryOptInMode.OptInMonitorAndStore;
            await client.UpdateControlConfigAsync(new NoxUpdateControlConfigRequest
            {
                NodeId = node.Id,
                StoreRawLogs = storeOnServer
            });
            await RefreshSpoolStatus();
            if (node.LogMode != TelemetryOptInMode.OptInOff)
            {
                // Only load the lightweight API call list summary, NOT the entire 1GB log spool
                await LoadApiCallListAsync();
            }
        }
        catch { }
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
                    Dispatcher.UIThread.Post(() =>
                    {
                        LogStreamStatus = $"Live ({TotalRecords:N0} logs · {RecentApiCalls.Count} API calls)";
                    });

                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        var chunk = call.ResponseStream.Current;
                        ProcessIncomingChunk(node.Id, chunk);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
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

    public void EnsureDetailsLoaded(RawApiCallItemModel item)
    {
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
                    _apiCache.PutRecords(node.Id, new[] { fullRec });
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (SelectedApiCall?.CallId == item.CallId || item.CallId == fullRec.CallId)
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
                            Label = string.IsNullOrEmpty(img.Label) ? "Prompt Image" : img.Label,
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

        item.IsDetailsLoaded = true;
    }

    private static RawApiCallItemModel BuildApiCallItemModel(NoxRawApiCallRecord r, bool loadFullDetails = false)
    {
        string tsText = r.TimestampUnixNano > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampUnixNano / 1_000_000).ToLocalTime().ToString("HH:mm:ss.fff")
            : DateTime.Now.ToString("HH:mm:ss.fff");

        string chatName = NoxChatSessionParser.ExtractChatName(r.RawRequestJson, r.Caller);

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
            }
        }

        const int maxInMemoryCalls = 10_000;
        while (RecentApiCalls.Count > maxInMemoryCalls)
        {
            var removed = RecentApiCalls[RecentApiCalls.Count - 1];
            _seenApiCallIds.Remove(removed.CallId);
            RecentApiCalls.RemoveAt(RecentApiCalls.Count - 1);
        }

        if (SelectedApiCall == null && RecentApiCalls.Count > 0)
        {
            SelectedApiCall = RecentApiCalls[0];
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
                });

                if (resp.Chunks.Count > 0)
                {
                    foreach (var c in resp.Chunks)
                    {
                        if (c.ChunkId == chunkId) found = true;
                        _logCache.PutChunk("noxchat", node.Id, c);
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
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
                    _chunkFetchFailures[chunkId] = DateTime.UtcNow;
                    _logCache.RecordChunkError(node.Id, chunkId, ex.GetType().Name, ex.Message, ex.ToString());
                }
            }
            finally
            {
                lock (_inflightLogChunks)
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
                Dispatcher.UIThread.Post(() =>
                {
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
        if (SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var stats = await client.GetSpoolStatsAsync(new NoxSpoolStatsRequest
            {
                NodeId = SelectedNode.Id
            });

            string loadedText = RecentApiCalls.Count > 0 ? $", {RecentApiCalls.Count:N0} loaded" : string.Empty;
            SpoolStatusSummary = $"Spool: {stats.TotalSpoolBytes / 1048576.0:F1} / {stats.MaxSpoolBytes / 1048576.0:F0} MiB ({stats.TotalLogRecords:N0} logs, {stats.TotalApiCalls:N0} API calls{loadedText})";
        }
        catch
        {
            SpoolStatusSummary = "Spool: Offline";
        }
    }

    private bool _isLoadingSpool;

    [RelayCommand]
    public async Task LoadApiCallListAsync()
    {
        var node = SelectedNode;
        if (node == null) return;

        try
        {
            var client = _nodeManager.GetClient(node);
            ulong nextApiChunkId = 0;
            bool hasMoreApi = true;
            int consecutiveErrors = 0;

            while (hasMoreApi && consecutiveErrors < 3)
            {
                try
                {
                    var apiResp = await client.QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                    {
                        NodeId = node.Id,
                        StartChunkId = nextApiChunkId,
                        MaxChunks = 8,
                        SummaryOnly = true
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
                }
                catch (Exception)
                {
                    consecutiveErrors++;
                    nextApiChunkId += 8;
                }
            }

            if (SelectedApiCall != null)
            {
                EnsureDetailsLoaded(SelectedApiCall);
            }
        }
        catch { }
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

            // 1. Load entire log spool in safe batches of up to 8 chunks
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
                            MaxChunks = 8
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
                        nextLogChunkId += 8;
                    }
                }
            }

            // 2. Load entire API call spool dynamically
            // Query 1 chunk per request to stay strictly within gRPC limits even when containing large multimodal images
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
                            MaxChunks = 1
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
                        nextApiChunkId++; // Advance to next chunk instead of aborting the whole spool
                    }
                }

                _apiCache.FlushPending(node.Id);
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

            // Clear local caches and UI collections
            _apiCache.Clear(node.Id);
            _logCache.Clear(node.Id);

            _seenApiCallIds.Clear();
            RecentApiCalls.Clear();
            SelectedApiCall = null;
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
        StopLogStream();
    }
}
