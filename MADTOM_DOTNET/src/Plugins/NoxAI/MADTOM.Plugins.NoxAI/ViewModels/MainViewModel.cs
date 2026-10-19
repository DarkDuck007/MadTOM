using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly NoxNodeManager _nodeManager;
    private readonly TelemetryHistoryCache _historyCache;
    private readonly LogChunkCache _logCache;
    private readonly NoxApiCallChunkCache _apiCache;
    private readonly NoxCacheSettingsStore _cacheStore;

    [ObservableProperty] private int _selectedWorkspaceTabIndex;
    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private bool _isSettingsModalOpen;
    [ObservableProperty] private bool _isZstdDiagnosticsOpen;
    [ObservableProperty] private string _connectionStatusBadge = "• OFFLINE";
    [ObservableProperty] private string _liveCompressionRatio = "Zstd: 1.0x";
    [ObservableProperty] private string _zstdSummary = "0 B";
    [ObservableProperty] private System.Collections.Generic.IReadOnlyList<CompressionDiagnosticRow> _compressionRows = System.Array.Empty<CompressionDiagnosticRow>();
    [ObservableProperty] private System.Collections.Generic.IReadOnlyList<CompressionDiagnosticRow> _transportCompressionRows = System.Array.Empty<CompressionDiagnosticRow>();
    private NoxNodeStatusResponse? _lastStatusResponse;

    public ObservableCollection<NoxNodeModel> ActiveNodes { get; } = new();

    public NoxTelemetryTabViewModel TelemetryTab { get; }
    public DirectChatTabViewModel DirectChatTab { get; }
    public MemoriesRagTabViewModel MemoriesRagTab { get; }
    public MediaLibraryTabViewModel MediaLibraryTab { get; }
    public ActiveJobsTabViewModel ActiveJobsTab { get; }
    public RawLogsApiTabViewModel RawLogsApiTab { get; }
    public NoxSettingsViewModel SettingsViewModel { get; }

    public MainViewModel()
    {
        _cacheStore = new NoxCacheSettingsStore();
        var cacheSettings = _cacheStore.Load();

        _historyCache = new TelemetryHistoryCache(cacheSettings.StreamedRetentionMinutes);
        _logCache = new LogChunkCache(cacheSettings.LogAndApiCacheLimitMiB * 1048576L, cacheSettings.LogAndApiDecodedChunkLimit);
        _apiCache = new NoxApiCallChunkCache(cacheSettings.LogAndApiCacheLimitMiB * 1048576L / 2, cacheSettings.LogAndApiDecodedChunkLimit / 2);

        _nodeManager = new NoxNodeManager();

        TelemetryTab = new NoxTelemetryTabViewModel(_nodeManager, _historyCache);
        DirectChatTab = new DirectChatTabViewModel(_nodeManager);
        MemoriesRagTab = new MemoriesRagTabViewModel(_nodeManager);
        MediaLibraryTab = new MediaLibraryTabViewModel(_nodeManager);
        ActiveJobsTab = new ActiveJobsTabViewModel(_nodeManager);
        RawLogsApiTab = new RawLogsApiTabViewModel(_nodeManager, _logCache, _apiCache);
        SettingsViewModel = new NoxSettingsViewModel(_nodeManager, _historyCache, _logCache, _apiCache, _cacheStore);

        RefreshActiveNodes();

        SettingsViewModel.CloseRequested += () => IsSettingsModalOpen = false;
        _nodeManager.NodesChanged += () => Dispatcher.UIThread.Post(RefreshActiveNodes);
        _nodeManager.NodeSettingsChanged += updated => Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < ActiveNodes.Count; i++)
            {
                if (ActiveNodes[i].Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveNodes[i] = updated;
                    break;
                }
            }
            if (SelectedNode != null && SelectedNode.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNode = updated;
                RawLogsApiTab.ApplyNodeSettings(updated);
            }
        });

        StartBackgroundStatusLoop();
    }

    private void RefreshActiveNodes()
    {
        ActiveNodes.Clear();
        foreach (var node in _nodeManager.Nodes)
        {
            ActiveNodes.Add(node);
        }

        if (SelectedNode == null || !ActiveNodes.Any(n => n.Id == SelectedNode.Id))
        {
            SelectedNode = ActiveNodes.FirstOrDefault();
        }

        if (ActiveNodes.Count == 0)
        {
            ConnectionStatusBadge = "• NO NODES";
        }
    }

    private CancellationTokenSource? _stateStreamCts;

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        _stateStreamCts?.Cancel();
        _stateStreamCts?.Dispose();
        _stateStreamCts = null;

        if (TelemetryTab != null) TelemetryTab.SelectedNode = value;
        if (DirectChatTab != null) DirectChatTab.SelectedNode = value;
        if (MemoriesRagTab != null) MemoriesRagTab.SelectedNode = value;
        if (MediaLibraryTab != null) MediaLibraryTab.SelectedNode = value;
        if (ActiveJobsTab != null) ActiveJobsTab.SelectedNode = value;
        if (RawLogsApiTab != null) RawLogsApiTab.SelectedNode = value;

        if (value == null)
        {
            ConnectionStatusBadge = "• NO NODES";
        }
        else
        {
            ConnectionStatusBadge = "• CONNECTING";
            _stateStreamCts = new CancellationTokenSource();
            StartStateStreamLoop(value, _stateStreamCts.Token);
        }
    }

    private void StartStateStreamLoop(NoxNodeModel node, CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    using var call = client.SubscribeState(new NoxStateSubscriptionRequest { NodeId = node.Id }, cancellationToken: ct);

                    Dispatcher.UIThread.Post(() => ConnectionStatusBadge = "• STREAMING");

                    while (await call.ResponseStream.MoveNext(ct))
                    {
                        var chunk = call.ResponseStream.Current;
                        if (chunk == null || chunk.ZstdPayload.IsEmpty) continue;

                        try
                        {
                            byte[] raw = ZstdCodec.Decode(chunk.ZstdPayload.Span, (int)chunk.DecodedSize);
                            var payload = NoxStateChunkPayload.Parser.ParseFrom(raw);

                            Dispatcher.UIThread.Post(() =>
                            {
                                if (payload.TelemetrySamples.Count > 0)
                                {
                                    foreach (var sample in payload.TelemetrySamples)
                                    {
                                        TelemetryTab.OnTelemetrySampleReceived(sample);
                                    }
                                }
                                if (payload.JobEvents.Count > 0)
                                {
                                    foreach (var job in payload.JobEvents)
                                    {
                                        ActiveJobsTab.OnJobEventReceived(job);
                                    }
                                }
                                if (payload.LiveTurnEvents.Count > 0)
                                {
                                    foreach (var turn in payload.LiveTurnEvents)
                                    {
                                        DirectChatTab.OnTurnEventReceived(turn);
                                    }
                                }
                            });
                        }
                        catch
                        {
                            // Ignore decompression errors on malformed chunks
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Delay before reconnecting to avoided tight spinning on failure
                    try { await Task.Delay(3000, ct); } catch { break; }
                }
            }
        }, ct);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        SettingsViewModel.RefreshNodesList();
        SettingsViewModel.RefreshCacheStats();
        IsSettingsModalOpen = true;
    }

    [RelayCommand]
    public void OpenZstdDiagnostics()
    {
        RefreshCompressionDiagnostics();
        IsZstdDiagnosticsOpen = true;
    }

    [RelayCommand]
    public void CloseZstdDiagnostics()
    {
        IsZstdDiagnosticsOpen = false;
    }

    private static string Ratio(long decoded, long encoded) => encoded > 0
        ? ((double)decoded / encoded).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "×" : "—";

    private static CompressionDiagnosticRow MemoryRow(string name, long retained, long encoded, long decoded, long count)
        => new(name, encoded > 0 ? "Zstd + raw" : "Raw",
            ClientCompressionSnapshot.FormatBytes(decoded),
            ClientCompressionSnapshot.FormatBytes(encoded),
            Ratio(decoded, encoded),
            count.ToString("N0"),
            "—",
            "~" + ClientCompressionSnapshot.FormatBytes(retained));

    public void RefreshCompressionDiagnostics()
    {
        var usage = _historyCache.GetUsage(1);
        var rows = new System.Collections.Generic.List<CompressionDiagnosticRow>
        {
            MemoryRow("Memory · live history", usage.LiveBytes, usage.LiveZstdBytes, usage.LiveZstdRawBytes, usage.LivePoints),
            MemoryRow("Memory · stored queries", usage.StoredBytes, usage.StoredZstdBytes, usage.StoredZstdRawBytes, usage.StoredPoints),
            MemoryRow("Memory · log chunk cache", _logCache.StorageBytes, _logCache.TotalCompressedBytes, _logCache.TotalRawBytes, _logCache.TotalRecordCount),
            MemoryRow("Memory · raw api call cache", _apiCache.StorageBytes, _apiCache.TotalCompressedBytes, _apiCache.TotalRawBytes, _apiCache.TotalRecordCount),
        };

        long totalRetained = usage.TotalBytes + _logCache.StorageBytes + _apiCache.StorageBytes;
        long totalCompressed = usage.LiveZstdBytes + usage.StoredZstdBytes + _logCache.TotalCompressedBytes + _apiCache.TotalCompressedBytes;
        long totalRaw = usage.LiveZstdRawBytes + usage.StoredZstdRawBytes + _logCache.TotalRawBytes + _apiCache.TotalRawBytes;
        long totalCount = usage.LivePoints + usage.StoredPoints + _logCache.TotalRecordCount + _apiCache.TotalRecordCount;

        rows.Add(MemoryRow("Memory · total", totalRetained, totalCompressed, totalRaw, totalCount));

        ZstdSummary = ClientCompressionSnapshot.FormatBytes(totalCompressed);
        CompressionRows = rows;

        var transportRows = new System.Collections.Generic.List<CompressionDiagnosticRow>();
        string endpoint = SelectedNode?.Endpoint ?? "NoxChat";
        if (_lastStatusResponse != null && _lastStatusResponse.TransportStats.Count > 0)
        {
            foreach (var s in _lastStatusResponse.TransportStats)
            {
                long zstdBytes = (long)Math.Min(s.ZstdBytes, (ulong)long.MaxValue);
                long decodedBytes = (long)Math.Min(s.DecodedZstdBytes, (ulong)long.MaxValue);
                long rawBytes = (long)Math.Min(s.RawBytes, (ulong)long.MaxValue);

                transportRows.Add(new CompressionDiagnosticRow(
                    $"{endpoint} · {s.NodeId} → probe · {s.Mode}",
                    s.ZstdBatches > 0 ? "Zstd observed" : "No zstd seen",
                    ClientCompressionSnapshot.FormatBytes(decodedBytes),
                    ClientCompressionSnapshot.FormatBytes(zstdBytes),
                    Ratio(decodedBytes, zstdBytes),
                    $"{s.ZstdBatches:N0}/{s.Batches:N0}",
                    s.RawBytesSupported ? ClientCompressionSnapshot.FormatBytes(rawBytes) : "—",
                    s.RawBytesSupported ? ClientCompressionSnapshot.FormatBytes(rawBytes + zstdBytes) : "—"
                ));
            }
        }
        else
        {
            transportRows.Add(new CompressionDiagnosticRow($"{endpoint} · daemon → probe", "Awaiting data", "—", "—", "—", "0"));
        }
        TransportCompressionRows = transportRows;
    }

    private void StartBackgroundStatusLoop()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (SelectedNode != null)
                    {
                        var client = _nodeManager.GetClient(SelectedNode);
                        var status = await client.GetNodeStatusAsync(new NoxNodeStatusRequest { NodeId = SelectedNode.Id });
                        _lastStatusResponse = status;
                        ConnectionStatusBadge = status.BotRunning ? "• RUNNING" : "• PROBE READY";
                        if (status.TransportStats.Count > 0 && status.TransportStats[0].ZstdBytes > 0)
                        {
                            var s = status.TransportStats[0];
                            double ratio = (double)s.DecodedZstdBytes / s.ZstdBytes;
                            LiveCompressionRatio = $"Zstd: {ratio:F1}x ({s.ZstdBatches} batches)";
                        }
                        else
                        {
                            long clientComp = _logCache.TotalCompressedBytes + _apiCache.TotalCompressedBytes;
                            long clientRaw = _logCache.TotalRawBytes + _apiCache.TotalRawBytes;
                            if (clientComp > 0 && clientRaw > 0)
                            {
                                double ratio = (double)clientRaw / clientComp;
                                LiveCompressionRatio = $"Zstd: {ratio:F1}x (in-memory)";
                            }
                            else
                            {
                                LiveCompressionRatio = "Zstd: 1.0x";
                            }
                        }

                        if (IsZstdDiagnosticsOpen)
                        {
                            Dispatcher.UIThread.Post(RefreshCompressionDiagnostics);
                        }
                    }
                    else
                    {
                        ConnectionStatusBadge = "• NO NODES";
                        LiveCompressionRatio = "Zstd: 1.0x";
                    }
                }
                catch
                {
                    ConnectionStatusBadge = "• OFFLINE";
                }
                await Task.Delay(5000);
            }
        });
    }

    public void Dispose()
    {
        _stateStreamCts?.Cancel();
        _stateStreamCts?.Dispose();
        _stateStreamCts = null;
        RawLogsApiTab.Dispose();
    }
}
