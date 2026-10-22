using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;
using MadTOM.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class ConfiguredNodeItem : ObservableObject
{
    private readonly Action<ConfiguredNodeItem>? _onChanged;
    private bool _suppressCallback;

    public string Id { get; }
    public string Endpoint { get; }
    public string Alias { get; }
    public string PublicKeyDisplay { get; }

    [ObservableProperty] private bool _isModeOff;
    [ObservableProperty] private bool _isModeStream;
    [ObservableProperty] private bool _isModeStreamAndStore;
    [ObservableProperty] private bool _includeApiCalls;
    [ObservableProperty] private bool _includeImages;
    [ObservableProperty] private bool _isBeingEdited;

    public TelemetryOptInMode SelectedLogMode =>
        IsModeStreamAndStore ? TelemetryOptInMode.OptInMonitorAndStore :
        IsModeStream ? TelemetryOptInMode.OptInMonitorOnly :
        TelemetryOptInMode.OptInOff;

    public ConfiguredNodeItem(
        string id,
        string endpoint,
        string alias,
        string publicKeyDisplay,
        TelemetryOptInMode logMode,
        bool includeApiCalls,
        bool includeImages,
        Action<ConfiguredNodeItem>? onChanged = null)
    {
        Id = id;
        Endpoint = endpoint;
        Alias = alias;
        PublicKeyDisplay = publicKeyDisplay;

        _suppressCallback = true;
        _isModeOff = logMode == TelemetryOptInMode.OptInOff;
        _isModeStream = logMode == TelemetryOptInMode.OptInMonitorOnly;
        _isModeStreamAndStore = logMode == TelemetryOptInMode.OptInMonitorAndStore;
        _includeApiCalls = includeApiCalls;
        _includeImages = includeImages;
        _suppressCallback = false;
        _onChanged = onChanged;
    }

    partial void OnIsModeOffChanged(bool value)
    {
        if (_suppressCallback || !value) return;
        _suppressCallback = true;
        IsModeStream = false;
        IsModeStreamAndStore = false;
        _suppressCallback = false;
        _onChanged?.Invoke(this);
    }

    partial void OnIsModeStreamChanged(bool value)
    {
        if (_suppressCallback || !value) return;
        _suppressCallback = true;
        IsModeOff = false;
        IsModeStreamAndStore = false;
        _suppressCallback = false;
        _onChanged?.Invoke(this);
    }

    partial void OnIsModeStreamAndStoreChanged(bool value)
    {
        if (_suppressCallback || !value) return;
        _suppressCallback = true;
        IsModeOff = false;
        IsModeStream = false;
        _suppressCallback = false;
        _onChanged?.Invoke(this);
    }

    partial void OnIncludeApiCallsChanged(bool value)
    {
        if (_suppressCallback) return;
        _onChanged?.Invoke(this);
    }

    partial void OnIncludeImagesChanged(bool value)
    {
        if (_suppressCallback) return;
        _onChanged?.Invoke(this);
    }
}

public sealed partial class NoxSettingsViewModel : ObservableObject
{
    private readonly NoxCacheSettingsStore _cacheStore;
    private readonly NoxNodeManager _nodeManager;
    private readonly TelemetryHistoryCache _historyCache;
    private readonly LogChunkCache _logCache;
    private readonly NoxApiCallChunkCache _apiCache;

    private readonly NoxMdnsDiscoveryService _mdnsService = new();
    public ObservableCollection<ConfiguredNodeItem> ConfiguredNodes { get; } = new();
    public ObservableCollection<DiscoveredNoxNode> DiscoveredMdnsNodes { get; } = new();

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Node adding & mDNS fields
    private string _clientPrivateKey = string.Empty;
    [ObservableProperty] private string _newNodeId = string.Empty;
    [ObservableProperty] private string _newNodeEndpoint = "127.0.0.1:50055";
    [ObservableProperty] private string _newNodeAlias = "NoxChat Node";
    [ObservableProperty] private string _clientPublicKey = string.Empty;
    [ObservableProperty] private string _envSnippet = string.Empty;
    [ObservableProperty] private string _copyButtonText = "📋 Copy Snippet";
    [ObservableProperty] private bool _isScanningMdns;
    [ObservableProperty] private string _scanButtonText = "⟳ Scan LAN / mDNS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveNodeButtonText))]
    [NotifyPropertyChangedFor(nameof(NodeFormSectionTitle))]
    private bool _isEditingNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeFormSectionTitle))]
    private string _editingOriginalId = string.Empty;

    public string SaveNodeButtonText => IsEditingNode ? "💾 Save Changes" : "+ Add Node";
    public string NodeFormSectionTitle => IsEditingNode
        ? $"EDIT NOXCHAT NODE CONNECTION ('{EditingOriginalId}')"
        : "CONNECT NEW NODE (SSH-STYLE KEY PROVISIONING)";

    // Performance & Resolution fields
    [ObservableProperty] private double? _graphPointsPerPixel = 1.0;
    [ObservableProperty] private double? _historyPointsPerPixel = 3.0;

    // Cache limits & usages (Separate Cache architecture)
    [ObservableProperty] private int? _streamedRetentionMinutes = 60;
    [ObservableProperty] private int? _streamedContentLimitMiB = 64;
    [ObservableProperty] private int? _storedQueryLimitMiB = 32;
    [ObservableProperty] private int? _storedQueryRetentionSeconds = 30;
    [ObservableProperty] private int? _logAndApiCacheLimitMiB = 128;
    [ObservableProperty] private int? _logAndApiDecodedChunkLimit = 32;
    [ObservableProperty] private int? _mediaCacheLimitMiB = 256;
    [ObservableProperty] private int? _mediaCacheItemLimit = 500;

    [ObservableProperty] private string _streamedCacheUsage = "0 MiB";
    [ObservableProperty] private string _storedQueryUsage = "0 MiB";
    [ObservableProperty] private string _logAndApiCacheUsage = "0 MiB";
    [ObservableProperty] private string _mediaCacheUsage = "0 MiB memory (0 items) · 0 MiB disk (0 files)";
    [ObservableProperty] private string _totalCacheUsage = "0 MiB";
    [ObservableProperty] private string _totalCacheEstimate = "Up to 480 MiB";

    // Garbage Collection Diagnostics fields
    [ObservableProperty] private string _gcLastRunText = "Never executed";
    [ObservableProperty] private string _gcMemoryFreedText = "--";
    [ObservableProperty] private string _gcHeapSummaryText = "--";
    [ObservableProperty] private string _gcGenCountsText = "--";

    // Preload & Stress Test fields
    [ObservableProperty] private ObservableCollection<string> _preloadScopes = new()
    {
        "1 Hour (1H)",
        "6 Hours (6H)",
        "24 Hours (24H)",
        "7 Days (7D)",
        "Entire History"
    };
    [ObservableProperty] private string _selectedPreloadScope = "24 Hours (24H)";
    [ObservableProperty] private ObservableCollection<string> _preloadBucketingModes = new()
    {
        "Standard (2,400 pts)",
        "High Density (5,000 pts)",
        "Ultra Density (10,000 pts)",
        "Stress Test (50,000 pts)",
        "Raw (All Points / No Downsampling)"
    };
    [ObservableProperty] private string _selectedPreloadBucketingMode = "Standard (2,400 pts)";
    [ObservableProperty] private int? _preloadConcurrency = Environment.ProcessorCount;
    [ObservableProperty] private bool _isPreloading;
    [ObservableProperty] private string _preloadStatusText = "Idle";
    [ObservableProperty] private string _preloadSummaryText = "No preload executed yet.";

    public event Action? CloseRequested;

    public NoxSettingsViewModel(
        NoxNodeManager nodeManager,
        TelemetryHistoryCache historyCache,
        LogChunkCache logCache,
        NoxApiCallChunkCache apiCache,
        NoxCacheSettingsStore? cacheStore = null)
    {
        _nodeManager = nodeManager;
        _historyCache = historyCache;
        _logCache = logCache;
        _apiCache = apiCache;
        _cacheStore = cacheStore ?? new NoxCacheSettingsStore();

        RefreshNodesList();
        GenerateNewClientKey();

        _mdnsService.NodeDiscovered += node =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!DiscoveredMdnsNodes.Any(x => x.Endpoint.Equals(node.Endpoint, StringComparison.OrdinalIgnoreCase)))
                {
                    DiscoveredMdnsNodes.Add(node);
                }
            });
        };

        var loaded = _cacheStore.Load();
        StreamedRetentionMinutes = loaded.StreamedRetentionMinutes;
        StreamedContentLimitMiB = loaded.StreamedContentLimitMiB;
        StoredQueryLimitMiB = loaded.StoredQueryLimitMiB;
        StoredQueryRetentionSeconds = loaded.StoredQueryRetentionSeconds;
        LogAndApiCacheLimitMiB = loaded.LogAndApiCacheLimitMiB;
        LogAndApiDecodedChunkLimit = loaded.LogAndApiDecodedChunkLimit;
        MediaCacheLimitMiB = loaded.MediaCacheLimitMiB;
        MediaCacheItemLimit = loaded.MediaCacheItemLimit;
        NoxMediaThumbnailCache.Configure(loaded.MediaCacheLimitMiB, loaded.MediaCacheItemLimit);

        RefreshCacheStats();
        _ = ScanMdns();
    }

    [RelayCommand]
    public void GenerateNewClientKey()
    {
        var (pub, priv) = NoxAuthTokenGenerator.GenerateKeyPair();
        _clientPrivateKey = priv;
        ClientPublicKey = pub;
        EnvSnippet = $"NOXCHAT_CONTROL_KEY={pub}";
    }

    [RelayCommand]
    public async Task CopyEnvSnippet()
    {
        if (string.IsNullOrWhiteSpace(EnvSnippet)) return;
        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime switch
            {
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
                Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView => Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView),
                _ => null
            };
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(EnvSnippet);
                CopyButtonText = "✓ Copied!";
                StatusMessage = "Copied public key snippet to clipboard! Paste it into NoxChat's .env file.";
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.UIThread.Post(() => CopyButtonText = "📋 Copy Snippet"));
            }
            else
            {
                StatusMessage = "Clipboard not accessible on this system.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ScanMdns()
    {
        if (IsScanningMdns) return;
        IsScanningMdns = true;
        ScanButtonText = "Scanning...";
        StatusMessage = "Scanning LAN and mDNS for active NoxChat probe daemons...";
        try
        {
            var nodes = await _mdnsService.ScanAsync();
            DiscoveredMdnsNodes.Clear();
            foreach (var n in nodes)
            {
                DiscoveredMdnsNodes.Add(n);
            }
            StatusMessage = DiscoveredMdnsNodes.Count > 0
                ? $"Discovered {DiscoveredMdnsNodes.Count} NoxChat node(s) on LAN."
                : "No NoxChat nodes discovered on LAN/mDNS.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery error: {ex.Message}";
        }
        finally
        {
            IsScanningMdns = false;
            ScanButtonText = "⟳ Scan LAN / mDNS";
        }
    }

    [RelayCommand]
    public void SelectDiscoveredNode(DiscoveredNoxNode? node)
    {
        if (node == null) return;
        var existing = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectNodeForEdit(existing.Id);
            NewNodeEndpoint = node.Endpoint;
            StatusMessage = $"Selected LAN node '{node.NodeId}' ({node.Endpoint}). Existing key preserved.";
            return;
        }

        NewNodeId = node.NodeId;
        NewNodeEndpoint = node.Endpoint;
        NewNodeAlias = $"{node.NodeId} (LAN)";
        StatusMessage = $"Selected {node.NodeId} ({node.Endpoint}). Copy public key snippet to your server.";
    }

    [RelayCommand]
    public void SelectNodeForEdit(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        var node = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (node == null) return;

        IsEditingNode = true;
        EditingOriginalId = node.Id;
        NewNodeId = node.Id;
        NewNodeAlias = node.Alias;
        NewNodeEndpoint = node.HostEndpoint;

        // Preserve existing private & public key
        _clientPrivateKey = node.AuthKey;
        ClientPublicKey = NoxAuthTokenGenerator.GetPublicKey(node.AuthKey);
        EnvSnippet = $"NOXCHAT_CONTROL_KEY={ClientPublicKey}";
        StatusMessage = $"Editing node '{node.Id}'. Change IP/endpoint and click Save (existing key is preserved).";

        foreach (var item in ConfiguredNodes)
        {
            item.IsBeingEdited = string.Equals(item.Id, node.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    public void CancelEditNode()
    {
        IsEditingNode = false;
        EditingOriginalId = string.Empty;
        NewNodeId = string.Empty;
        NewNodeAlias = string.Empty;
        NewNodeEndpoint = "127.0.0.1:50055";
        StatusMessage = string.Empty;

        foreach (var item in ConfiguredNodes)
        {
            item.IsBeingEdited = false;
        }

        GenerateNewClientKey();
    }

    [RelayCommand]
    public void SaveNode()
    {
        string id = string.IsNullOrWhiteSpace(NewNodeId)
            ? (string.IsNullOrWhiteSpace(NewNodeAlias) ? "NoxNode" : NewNodeAlias.Replace(" ", "-"))
            : NewNodeId.Trim();

        string endpoint = string.IsNullOrWhiteSpace(NewNodeEndpoint) ? "127.0.0.1:50055" : NewNodeEndpoint.Trim();
        string alias = string.IsNullOrWhiteSpace(NewNodeAlias) ? id : NewNodeAlias.Trim();

        if (IsEditingNode)
        {
            var existing = _nodeManager.Nodes.FirstOrDefault(n => n.Id.Equals(EditingOriginalId, StringComparison.OrdinalIgnoreCase));
            string keyToUse = !string.IsNullOrWhiteSpace(_clientPrivateKey) ? _clientPrivateKey : (existing?.AuthKey ?? string.Empty);

            if (!id.Equals(EditingOriginalId, StringComparison.OrdinalIgnoreCase))
            {
                _nodeManager.RemoveNode(EditingOriginalId);
            }

            var updatedNode = new NoxNodeModel(
                Id: id,
                HostEndpoint: endpoint,
                AuthKey: keyToUse,
                Alias: alias,
                IsMdnsDiscovered: existing?.IsMdnsDiscovered ?? false,
                LogMode: existing?.LogMode ?? TelemetryOptInMode.OptInMonitorAndStore,
                IncludeApiCalls: existing?.IncludeApiCalls ?? true,
                IncludeImages: existing?.IncludeImages ?? true
            );

            _nodeManager.AddOrUpdateNode(updatedNode);
            StatusMessage = $"Node '{id}' updated with endpoint '{endpoint}' (existing authentication key preserved).";
            CancelEditNode();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_clientPrivateKey))
            {
                GenerateNewClientKey();
            }

            var node = new NoxNodeModel(id, endpoint, _clientPrivateKey, alias);
            _nodeManager.AddOrUpdateNode(node);
            StatusMessage = $"Node '{node.Id}' saved. Paste the public key snippet into NoxChat's .env file!";
            NewNodeId = string.Empty;
            NewNodeAlias = string.Empty;
            NewNodeEndpoint = "127.0.0.1:50055";
            GenerateNewClientKey();
        }

        RefreshNodesList();
    }

    [RelayCommand]
    public void AddNode()
    {
        SaveNode();
    }

    [RelayCommand]
    public void RemoveNode(string nodeId)
    {
        if (IsEditingNode && EditingOriginalId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
        {
            CancelEditNode();
        }
        _nodeManager.RemoveNode(nodeId);
        RefreshNodesList();
        StatusMessage = $"Node '{nodeId}' removed.";
    }

    public void RefreshNodesList()
    {
        ConfiguredNodes.Clear();
        foreach (var n in _nodeManager.Nodes)
        {
            string pubKey = NoxAuthTokenGenerator.GetPublicKey(n.AuthKey);
            string pubDisplay = string.IsNullOrEmpty(pubKey) ? "(legacy / unverified)" : pubKey;
            var item = new ConfiguredNodeItem(
                n.Id,
                n.HostEndpoint,
                n.Alias,
                pubDisplay,
                n.LogMode,
                n.IncludeApiCalls,
                n.IncludeImages,
                OnNodeModeEdited);
            if (IsEditingNode && string.Equals(n.Id, EditingOriginalId, StringComparison.OrdinalIgnoreCase))
            {
                item.IsBeingEdited = true;
            }
            ConfiguredNodes.Add(item);
        }
    }

    private void OnNodeModeEdited(ConfiguredNodeItem item)
    {
        var mode = item.SelectedLogMode;
        _nodeManager.UpdateNodeSettings(item.Id, mode, item.IncludeApiCalls, item.IncludeImages);

        string modeLabel = mode switch
        {
            TelemetryOptInMode.OptInMonitorAndStore => "Stream & Store (WAL Spool)",
            TelemetryOptInMode.OptInMonitorOnly => "Stream Only",
            _ => "Off"
        };
        StatusMessage = $"Node '{item.Id}' log mode set to: {modeLabel}";

        var node = _nodeManager.Nodes.FirstOrDefault(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        if (node != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = _nodeManager.GetClient(node);
                    await client.UpdateControlConfigAsync(new NoxUpdateControlConfigRequest
                    {
                        NodeId = node.Id,
                        StoreRawLogs = mode == TelemetryOptInMode.OptInMonitorAndStore
                    });
                }
                catch { }
            });
        }
    }

    [RelayCommand]
    public void RefreshCacheStats()
    {
        int retMin = StreamedRetentionMinutes ?? 60;
        var usage = _historyCache.GetUsage(retMin);
        StreamedCacheUsage = $"{usage.LiveBytes / 1048576.0:F2} MiB · {usage.SeriesCount} series";
        StoredQueryUsage = $"{usage.StoredBytes / 1048576.0:F2} MiB · {usage.StoredRanges} ranges";

        long logBytes = _logCache.TotalCompressedBytes;
        long apiBytes = _apiCache.TotalCompressedBytes;
        LogAndApiCacheUsage = $"{(logBytes + apiBytes) / 1048576.0:F2} MiB · {_logCache.CompressedChunkCount} log chunks, {_apiCache.CompressedChunkCount} api chunks";

        var mediaUsage = NoxMediaThumbnailCache.GetUsage();
        MediaCacheUsage = $"{mediaUsage.memoryBytes / 1048576.0:F2} MiB memory ({mediaUsage.memoryCount} items) · {mediaUsage.diskBytes / 1048576.0:F2} MiB disk ({mediaUsage.diskCount} files)";

        long totalBytes = usage.TotalBytes + logBytes + apiBytes + mediaUsage.memoryBytes;
        TotalCacheUsage = $"{totalBytes / 1048576.0:F2} MiB";

        int totalCap = (StreamedContentLimitMiB ?? 64) + (StoredQueryLimitMiB ?? 32) + (LogAndApiCacheLimitMiB ?? 128) + (MediaCacheLimitMiB ?? 256);
        TotalCacheEstimate = $"Cap {totalCap} MiB · Streamed ({StreamedContentLimitMiB} MiB) + Stored ({StoredQueryLimitMiB} MiB) + Log/API ({LogAndApiCacheLimitMiB} MiB) + Media ({MediaCacheLimitMiB} MiB)";

        if (GcHeapSummaryText == "--")
        {
            double heapMiB = GC.GetTotalMemory(false) / 1048576.0;
            double wsMiB = Environment.WorkingSet / 1048576.0;
            GcHeapSummaryText = $"Heap: {heapMiB:F2} MiB · Working Set: {wsMiB:F2} MiB";
            GcGenCountsText = $"Gen0: {GC.CollectionCount(0)} · Gen1: {GC.CollectionCount(1)} · Gen2: {GC.CollectionCount(2)}";
        }
    }

    [RelayCommand]
    public void ClearStreamedCache()
    {
        _historyCache.ClearLive();
        RefreshCacheStats();
        StatusMessage = "Streamed history and content cache cleared.";
    }

    [RelayCommand]
    public void ClearStoredQueryCache()
    {
        _historyCache.ClearStored();
        RefreshCacheStats();
        StatusMessage = "Stored query range cache cleared.";
    }

    [RelayCommand]
    public void ClearLogAndApiCache()
    {
        _logCache.Clear();
        _apiCache.Clear();
        RefreshCacheStats();
        StatusMessage = "Logs and Raw API Call zstd chunk caches cleared.";
    }

    [RelayCommand]
    public void ClearMediaCache()
    {
        NoxMediaThumbnailCache.Clear(includeDisk: true);
        RefreshCacheStats();
        StatusMessage = "Stickers & GIFs media cache cleared (memory and disk).";
    }

    [RelayCommand]
    public void ApplyCaches()
    {
        int min = StreamedRetentionMinutes ?? 60;
        if (min is < 1 or > 1440)
        {
            StatusMessage = "Streamed retention must be 1–1440 minutes.";
            return;
        }

        int sLimit = StreamedContentLimitMiB ?? 64;
        int qLimit = StoredQueryLimitMiB ?? 32;
        int qSec = StoredQueryRetentionSeconds ?? 30;
        int lLimit = LogAndApiCacheLimitMiB ?? 128;
        int lChunks = LogAndApiDecodedChunkLimit ?? 32;
        int mLimit = MediaCacheLimitMiB ?? 256;
        int mCount = MediaCacheItemLimit ?? 500;

        _cacheStore.Save(new NoxCacheSettingsStore.Settings
        {
            StreamedRetentionMinutes = min,
            StreamedContentLimitMiB = sLimit,
            StoredQueryLimitMiB = qLimit,
            StoredQueryRetentionSeconds = qSec,
            LogAndApiCacheLimitMiB = lLimit,
            LogAndApiDecodedChunkLimit = lChunks,
            MediaCacheLimitMiB = mLimit,
            MediaCacheItemLimit = mCount,
        });

        _historyCache.Configure(min, sLimit * 1048576L, qLimit * 1048576L, qSec);
        _logCache.Configure(lLimit * 1048576L, lChunks);
        _apiCache.Configure(lLimit * 1048576L / 2, lChunks / 2);
        NoxMediaThumbnailCache.Configure(mLimit, mCount);

        RefreshCacheStats();
        StatusMessage = "NoxAI Cache limits applied and saved.";
    }

    [RelayCommand]
    public void ForceGcCollect()
    {
        long memBefore = GC.GetTotalMemory(false);
        long wsBefore = Environment.WorkingSet;
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        long memAfter = GC.GetTotalMemory(false);
        long wsAfter = Environment.WorkingSet;
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        long freedBytes = Math.Max(0, memBefore - memAfter);
        double freedMiB = freedBytes / 1048576.0;
        double heapMiB = memAfter / 1048576.0;
        double wsMiB = wsAfter / 1048576.0;

        GcLastRunText = $"{DateTime.Now:HH:mm:ss} (Freed {freedMiB:F2} MiB)";
        GcMemoryFreedText = $"{freedMiB:F2} MiB ({memBefore / 1048576.0:F2} MiB → {memAfter / 1048576.0:F2} MiB)";
        GcHeapSummaryText = $"Heap: {heapMiB:F2} MiB · Working Set: {wsMiB:F2} MiB";
        GcGenCountsText = $"Gen0: {gen0After} (+{gen0After - gen0Before}) · Gen1: {gen1After} (+{gen1After - gen1Before}) · Gen2: {gen2After} (+{gen2After - gen2Before})";

        RefreshCacheStats();
    }

    [RelayCommand]
    public async Task LoadEntireLogSpool()
    {
        if (IsPreloading) return;
        var node = _nodeManager.Nodes.FirstOrDefault();
        if (node == null)
        {
            PreloadStatusText = "No node configured to load spool from";
            return;
        }

        IsPreloading = true;
        PreloadStatusText = $"Loading entire log & API spool for {node.AliasOrId}...";

        try
        {
            var client = _nodeManager.GetClient(node);
            int logsLoaded = 0;
            int apisLoaded = 0;

            // 1. Page all historical log chunks
            ulong nextLogChunkId = 0;
            bool hasMoreLogs = true;
            int consecutiveLogErrors = 0;
            while (hasMoreLogs && consecutiveLogErrors < 3)
            {
                try
                {
                    var logResp = await client.QueryLogChunksAsync(new LogChunkQuery
                    {
                        NodeId = node.Id,
                        StartChunkId = nextLogChunkId,
                        MaxChunks = 32
                    });

                    if (logResp.Chunks.Count == 0) break;
                    consecutiveLogErrors = 0;
                    ulong maxId = nextLogChunkId;
                    foreach (var c in logResp.Chunks)
                    {
                        if (c.ChunkId >= maxId) maxId = c.ChunkId;
                        _logCache.PutChunk("noxchat", node.Id, c);
                        logsLoaded += (int)c.RecordCount;
                    }
                    nextLogChunkId = maxId + 1;
                    hasMoreLogs = logResp.HasMore;
                    PreloadStatusText = $"Loading logs: {logsLoaded:N0} records loaded...";
                }
                catch
                {
                    consecutiveLogErrors++;
                    nextLogChunkId += 32;
                }
            }

            // 2. Page all historical API call chunks in batches of 32
            ulong nextApiChunkId = 0;
            bool hasMoreApis = true;
            int consecutiveApiErrors = 0;
            while (hasMoreApis && consecutiveApiErrors < 3)
            {
                try
                {
                    var apiResp = await client.QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                    {
                        NodeId = node.Id,
                        StartChunkId = nextApiChunkId,
                        MaxChunks = 32,
                        SummaryOnly = false
                    });

                    if (apiResp.Chunks.Count == 0) break;
                    consecutiveApiErrors = 0;
                    ulong maxId = nextApiChunkId;
                    foreach (var c in apiResp.Chunks)
                    {
                        if (c.ChunkId >= maxId) maxId = c.ChunkId;
                        _apiCache.PutChunk(node.Id, c);
                        apisLoaded += (int)c.RecordCount;
                    }
                    nextApiChunkId = maxId + 1;
                    hasMoreApis = apiResp.HasMore;
                    PreloadStatusText = $"Loading API calls: {apisLoaded:N0} calls loaded...";
                }
                catch
                {
                    consecutiveApiErrors++;
                    nextApiChunkId += 32;
                }
            }

            _apiCache.FlushPending(node.Id);
            RefreshCacheStats();

            PreloadStatusText = "Spool preload completed";
            PreloadSummaryText = $"Loaded entire spool: {logsLoaded:N0} log records and {apisLoaded:N0} LLM API calls from {node.AliasOrId} into Zstd cache.";
        }
        catch (Exception ex)
        {
            PreloadStatusText = $"Spool load error: {ex.Message}";
        }
        finally
        {
            IsPreloading = false;
        }
    }

    [RelayCommand]
    public async Task ForceLoadLogs()
    {
        if (IsPreloading) return;
        var node = _nodeManager.Nodes.FirstOrDefault();
        if (node == null)
        {
            PreloadStatusText = "No node configured to load logs from";
            return;
        }

        IsPreloading = true;
        PreloadStatusText = $"Loading all historical logs for {node.AliasOrId}...";

        try
        {
            var client = _nodeManager.GetClient(node);
            int logsLoaded = 0;
            ulong nextLogChunkId = 0;
            bool hasMoreLogs = true;
            int consecutiveLogErrors = 0;

            while (hasMoreLogs && consecutiveLogErrors < 3)
            {
                try
                {
                    var logResp = await client.QueryLogChunksAsync(new LogChunkQuery
                    {
                        NodeId = node.Id,
                        StartChunkId = nextLogChunkId,
                        MaxChunks = 32
                    });

                    if (logResp.Chunks.Count == 0) break;
                    consecutiveLogErrors = 0;
                    ulong maxId = nextLogChunkId;
                    foreach (var c in logResp.Chunks)
                    {
                        if (c.ChunkId >= maxId) maxId = c.ChunkId;
                        _logCache.PutChunk("noxchat", node.Id, c);
                        logsLoaded += (int)c.RecordCount;
                    }
                    nextLogChunkId = maxId + 1;
                    hasMoreLogs = logResp.HasMore;
                    PreloadStatusText = $"Loading logs: {logsLoaded:N0} records loaded...";
                }
                catch
                {
                    consecutiveLogErrors++;
                    nextLogChunkId += 32;
                }
            }

            RefreshCacheStats();
            PreloadStatusText = "Log preload completed";
            PreloadSummaryText = $"Loaded {logsLoaded:N0} historical log records from {node.AliasOrId} into Zstd cache.";
        }
        catch (Exception ex)
        {
            PreloadStatusText = $"Log load error: {ex.Message}";
        }
        finally
        {
            IsPreloading = false;
        }
    }

    [RelayCommand]
    public async Task ForceLoadTelemetry()
    {
        if (IsPreloading) return;
        var node = _nodeManager.Nodes.FirstOrDefault();
        if (node == null)
        {
            PreloadStatusText = "No node configured to load telemetry from";
            return;
        }

        IsPreloading = true;
        PreloadStatusText = $"Loading historical telemetry ({SelectedPreloadScope}) for {node.AliasOrId}...";

        try
        {
            var client = _nodeManager.GetClient(node);
            long endNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            long spanHours = SelectedPreloadScope switch
            {
                "1 Hour (1H)" => 1,
                "6 Hours (6H)" => 6,
                "24 Hours (24H)" => 24,
                "7 Days (7D)" => 24 * 7,
                _ => 24 * 30
            };
            long startNano = endNano - (spanHours * 3600L * 1_000_000_000L);

            uint targetPoints = SelectedPreloadBucketingMode switch
            {
                "High Density (5,000 pts)" => 5000,
                "Ultra Density (10,000 pts)" => 10000,
                "Stress Test (50,000 pts)" => 50000,
                "Raw (All Points / No Downsampling)" => 100000,
                _ => 2400
            };

            var metrics = new[]
            {
                "llm.tokens.in",
                "llm.tokens.out",
                "llm.tokens.cached",
                "py.cpu.total_pct",
                "py.mem.rss_bytes",
                "py.runtime.event_loop_lag_ms"
            };

            int pointsLoaded = 0;
            var tasks = metrics.Select(async m =>
            {
                try
                {
                    var resp = await client.QueryTelemetryRangeAsync(new RangeQueryRequest
                    {
                        NodeId = node.Id,
                        MetricName = m,
                        StartTimeUnixNano = startNano,
                        EndTimeUnixNano = endNano,
                        TargetPoints = targetPoints
                    });
                    if (resp?.Points != null)
                    {
                        Interlocked.Add(ref pointsLoaded, resp.Points.Count);
                    }
                }
                catch { }
            });

            await Task.WhenAll(tasks);
            RefreshCacheStats();
            PreloadStatusText = "Telemetry preload completed";
            PreloadSummaryText = $"Loaded {pointsLoaded:N0} telemetry points across {metrics.Length} series from {node.AliasOrId}.";
        }
        catch (Exception ex)
        {
            PreloadStatusText = $"Telemetry load error: {ex.Message}";
        }
        finally
        {
            IsPreloading = false;
        }
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke();
}
