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
    public string TimestampText { get; init; } = string.Empty;
    public uint DurationMs { get; init; }
    public uint PromptTokens { get; init; }
    public uint CompletionTokens { get; init; }
    public string RequestJson { get; init; } = string.Empty;
    public string ResponseJson { get; init; } = string.Empty;
    public ObservableCollection<RawApiCallImageItem> Images { get; } = new();
    public bool HasImages => Images.Count > 0;
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
                await LoadSpooledChunks();
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
                _apiCache.PutChunk(nodeId, apiChunk);
                var decoded = _apiCache.GetDecodedChunk(nodeId, apiChunk.ChunkId);
                if (decoded != null && decoded.Count > 0)
                {
                    if (verifyInnerFields && string.IsNullOrEmpty(decoded[0].CallId) && string.IsNullOrEmpty(decoded[0].Model))
                    {
                        return false;
                    }

                    var models = decoded.Select(BuildApiCallItemModel).ToList();
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

    private static RawApiCallItemModel BuildApiCallItemModel(NoxRawApiCallRecord r)
    {
        string tsText = r.TimestampUnixNano > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(r.TimestampUnixNano / 1_000_000).ToLocalTime().ToString("HH:mm:ss.fff")
            : DateTime.Now.ToString("HH:mm:ss.fff");

        var item = new RawApiCallItemModel
        {
            CallId = string.IsNullOrEmpty(r.CallId) ? Guid.NewGuid().ToString("N")[..12] : r.CallId,
            Caller = string.IsNullOrEmpty(r.Caller) ? "LLMClient" : r.Caller,
            Model = r.Model ?? string.Empty,
            TimestampText = tsText,
            DurationMs = r.DurationMs,
            PromptTokens = r.PromptTokens,
            CompletionTokens = r.CompletionTokens,
            RequestJson = FormatJsonForDisplay(r.RawRequestJson),
            ResponseJson = FormatJsonForDisplay(r.RawResponseJson),
        };

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
        foreach (var item in items)
        {
            if (!_seenApiCallIds.Add(item.CallId))
            {
                continue;
            }
            RecentApiCalls.Insert(0, item);
            if (RecentApiCalls.Count > 200)
            {
                var removed = RecentApiCalls[RecentApiCalls.Count - 1];
                _seenApiCallIds.Remove(removed.CallId);
                RecentApiCalls.RemoveAt(RecentApiCalls.Count - 1);
            }
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

    public void RequestChunk(ulong chunkId)
    {
        var node = SelectedNode;
        if (node == null) return;

        lock (_inflightLogChunks)
        {
            if (!_inflightLogChunks.Add(chunkId)) return;
        }

        _ = Task.Run(async () =>
        {
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
                        _logCache.PutChunk("noxchat", node.Id, c);
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var c in resp.Chunks)
                        {
                            UpdateSeqBounds(c.FirstSeq, c.LastSeq);
                        }
                        CacheRevision++;
                    });
                }
            }
            catch { }
            finally
            {
                lock (_inflightLogChunks)
                {
                    _inflightLogChunks.Remove(chunkId);
                }
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

            SpoolStatusSummary = $"Spool: {stats.TotalSpoolBytes / 1048576.0:F1} / {stats.MaxSpoolBytes / 1048576.0:F0} MiB ({stats.TotalLogRecords:N0} logs, {stats.TotalApiCalls:N0} API calls)";
        }
        catch
        {
            SpoolStatusSummary = "Spool: Offline";
        }
    }

    [RelayCommand]
    public async Task LoadSpooledChunks()
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var client = _nodeManager.GetClient(node);

            var (logResp, apiResp) = await Task.Run(async () =>
            {
                var lTask = client.QueryLogChunksAsync(new LogChunkQuery
                {
                    NodeId = node.Id,
                    StartChunkId = 0,
                    MaxChunks = 100
                }).ResponseAsync;

                var aTask = client.QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                {
                    NodeId = node.Id,
                    StartChunkId = 0,
                    MaxChunks = 100
                }).ResponseAsync;

                await Task.WhenAll(lTask, aTask);
                return (lTask.Result, aTask.Result);
            });

            foreach (var chunk in logResp.Chunks)
            {
                _logCache.PutChunk("noxchat", node.Id, chunk);
                UpdateSeqBounds(chunk.FirstSeq, chunk.LastSeq);
            }
            CacheRevision++;

            var apiModels = await Task.Run(() =>
            {
                var list = new List<RawApiCallItemModel>();
                foreach (var chunk in apiResp.Chunks)
                {
                    _apiCache.PutChunk(node.Id, chunk);
                    var decoded = _apiCache.GetDecodedChunk(node.Id, chunk.ChunkId);
                    if (decoded != null)
                    {
                        foreach (var r in decoded)
                        {
                            list.Add(BuildApiCallItemModel(r));
                        }
                    }
                }
                return list;
            });

            AddOrUpdateApiCallsOnUi(apiModels);
            await RefreshSpoolStatus();
        }
        catch (Exception ex)
        {
            SpoolStatusSummary = $"Spool error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        StopLogStream();
    }
}
