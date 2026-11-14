using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

/// <summary>One stored workspace file (chat-bound) as shown in the Files tab.</summary>
public sealed partial class ChatFileItemModel : ObservableObject
{
    public string Uuid { get; init; } = string.Empty;
    public string Handle { get; init; } = string.Empty;
    public string Store { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public long ChatId { get; init; }
    public string ChatName { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long SourceBundleBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int PageCount { get; init; }
    public double DurationSec { get; init; }
    public bool HasAudio { get; init; }
    public string Origin { get; init; } = string.Empty;
    public long SourceMessageId { get; init; }
    public string JobId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string LastUsedAt { get; init; } = string.Empty;
    public bool Pinned { get; init; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;

    internal bool ThumbnailRequested { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Filename) ? Filename : (!string.IsNullOrWhiteSpace(Handle) ? Handle : Uuid);

    public string StoreBadge => Store switch
    {
        "image" => "🖼 image",
        "document" => "📄 doc",
        "file" => "📁 file",
        _ => string.IsNullOrEmpty(Store) ? "file" : Store
    };

    public string OriginBadge => Origin switch
    {
        "generated" => "✨ generated",
        "telegram" => "📷 telegram",
        "studio" => "🎨 studio",
        "job_input" => "📥 job_input",
        _ => string.IsNullOrEmpty(Origin) ? "unknown" : Origin
    };

    public string KindBadge => Kind switch
    {
        "video" or "animation" when DurationSec > 0 => $"▶ {DurationSec:0.#}s",
        "video" or "animation" => "▶ video",
        "pdf" when PageCount > 0 => $"📄 {PageCount}p",
        "pptx" or "ppt" or "odp" when PageCount > 0 => $"📊 {PageCount}s",
        _ when Width > 0 && Height > 0 => $"{Width}×{Height}",
        _ => string.Empty
    };

    public string KindIcon => Kind switch
    {
        "image" => "🖼",
        "animation" => "🎞",
        "video" => "🎥",
        "pdf" => "📄",
        "pptx" or "ppt" or "odp" => "📊",
        "docx" or "xlsx" or "csv" => "📝",
        "txt" or "md" or "json" => "📃",
        _ => "📁"
    };

    public string SizeText => ChatFilesTabViewModel.FormatBytes(SizeBytes);
    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";
    public string CreatedText => ChatFilesTabViewModel.FormatTimestamp(CreatedAt);
    public string LastUsedText => ChatFilesTabViewModel.FormatTimestamp(LastUsedAt);
    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "(no description)" : Description;

    public string MetaLine
    {
        get
        {
            if (Kind is "pdf" or "pptx" or "ppt" or "odp" or "docx" || PageCount > 0)
            {
                return (PageCount > 0 ? $"{PageCount} page{(PageCount == 1 ? "" : "s")} · " : "") + $"{SizeText} · {CreatedText}";
            }
            if (Kind is "video" or "animation" || DurationSec > 0)
            {
                return (DurationSec > 0 ? $"{DurationSec:0.#}s · " : "") + (Width > 0 ? $"{DimensionsText} · " : "") + $"{SizeText}";
            }
            if (Width > 0 && Height > 0)
            {
                return $"{DimensionsText} · {SizeText} · {CreatedText}";
            }
            return $"{SizeText} · {CreatedText}";
        }
    }

    public string Tooltip =>
        $"{DisplayName}\n{Handle}\n{ChatName}\n{StoreBadge} · {OriginBadge} · {MetaLine}" +
        (string.IsNullOrEmpty(JobId) ? string.Empty : $"\njob {JobId}") +
        (SourceMessageId != 0 ? $"\nmessage #{SourceMessageId}" : string.Empty) +
        (Pinned ? "\n📌 Pinned" : string.Empty) +
        $"\n\n{DisplayDescription}";

    public bool IsVideoOrAnimation => Kind is "video" or "animation" || Handle.StartsWith("VID-");
    public bool IsMultiPage => Kind is "pdf" or "pptx" or "ppt" or "odp" or "docx" || PageCount > 1;

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Description)
        ? Avalonia.Media.FlowDirection.RightToLeft
        : Avalonia.Media.FlowDirection.LeftToRight;
}

/// <summary>Row of file cards (virtualized grid).</summary>
public sealed class ChatFileRowModel
{
    public IReadOnlyList<ChatFileItemModel> Items { get; }
    public ChatFileRowModel(IReadOnlyList<ChatFileItemModel> items) => Items = items;
}

/// <summary>Per-chat storage usage entry; ChatId 0 is the synthetic "All chats" entry.</summary>
public sealed class ChatFileUsageModel
{
    public long ChatId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public int ImageCount { get; init; }
    public int VideoCount { get; init; }
    public int DocumentCount { get; init; }
    public int SavedFileCount { get; init; }
    public string Oldest { get; init; } = string.Empty;
    public string Newest { get; init; } = string.Empty;
    public long LastActiveUnixSec { get; init; }

    public bool IsAllChats => ChatId == 0;
    public string Summary => TotalBytes == 0 && FileCount == 0
        ? "0 files · 0 B"
        : $"{FileCount:N0} files · {ChatFilesTabViewModel.FormatBytes(TotalBytes)}";

    public string RangeText => IsAllChats || (string.IsNullOrEmpty(Oldest) && string.IsNullOrEmpty(Newest))
        ? string.Empty
        : $"{ChatFilesTabViewModel.FormatTimestamp(Oldest)} → {ChatFilesTabViewModel.FormatTimestamp(Newest)}";
}

public sealed partial class ChatFilesTabViewModel : ObservableObject
{
    private const int PageSize = 200;
    private const int ThumbnailDimension = 256;
    private const int PreviewDimension = 1600;
    private const int MaxCachedThumbnails = 800;

    private readonly NoxNodeManager _nodeManager;
    private readonly SemaphoreSlim _thumbThrottle = new(6);
    private readonly Dictionary<string, Bitmap> _thumbCache = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _chatTitles = new();
    private readonly List<ChatFileItemModel> _items = new();
    private readonly List<Bitmap> _videoFrames = new();
    private readonly DispatcherTimer _animTimer;

    private Func<Task>? _pendingConfirmAction;
    private int _loadGeneration;
    private bool _needsRefresh = true;
    private bool _suppressChatReload;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private bool _isTabActive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Not loaded";
    [ObservableProperty] private string _storageSummary = "—";
    [ObservableProperty] private string _storageLimitsText = string.Empty;
    [ObservableProperty] private double _storageUsageFraction;
    [ObservableProperty] private string _itemCountSummary = "0 files";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _columnCount = 4;
    [ObservableProperty] private string _selectedOriginFilter = "All origins";
    [ObservableProperty] private string _selectedTypeFilter = "All types";
    [ObservableProperty] private decimal? _sweepDays = 30;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedChatLabel))]
    [NotifyCanExecuteChangedFor(nameof(SweepChatOlderThanCommand))]
    private ChatFileUsageModel? _selectedChatUsage;

    // Multi-modal Preview overlay
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private ChatFileItemModel? _previewItem;
    [ObservableProperty] private string _previewStatus = string.Empty;
    [ObservableProperty] private bool _isMultiPage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    [NotifyPropertyChangedFor(nameof(PageStatusText))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _previewCurrentPage = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    [NotifyPropertyChangedFor(nameof(PageStatusText))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _previewPageCount = 1;

    [ObservableProperty] private bool _isVideo;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _isPlaying;
    [ObservableProperty] private int _currentVideoFrameIndex;
    [ObservableProperty] private int _videoFrameMaxIndex;

    public string PlayButtonText => IsPlaying ? "⏸ Pause" : "▶ Play";

    [ObservableProperty] private bool _isTextPreview;
    [ObservableProperty] private string _previewText = string.Empty;

    // Confirmation & Sweep Guardrail overlay
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmMessage = string.Empty;
    [ObservableProperty] private string _confirmButtonText = "Delete";
    [ObservableProperty] private bool _isSweepConfirmation;
    [ObservableProperty] private bool _isSweepCalculating;
    [ObservableProperty] private string _sweepCalculationSummary = string.Empty;
    [ObservableProperty] private long _sweepScopeChatId;
    [ObservableProperty] private double _sweepScopeDays;

    public ObservableCollection<ChatFileUsageModel> ChatUsages { get; } = new();
    public ObservableCollection<ChatFileRowModel> Rows { get; } = new();

    public IReadOnlyList<string> OriginFilters { get; } = new[]
    {
        "All origins",
        "generated",
        "telegram",
        "job_input"
    };

    public IReadOnlyList<string> TypeFilters { get; } = new[]
    {
        "All types",
        "Images",
        "Videos & GIFs",
        "PDFs",
        "Presentations",
        "Documents & Text"
    };

    public bool IsChatSelected => SelectedChatUsage is { IsAllChats: false };
    public string SelectedChatLabel => SelectedChatUsage == null || SelectedChatUsage.IsAllChats
        ? "All chats"
        : SelectedChatUsage.Title;

    public bool CanPrevPage => PreviewCurrentPage > 1;
    public bool CanNextPage => PreviewCurrentPage < PreviewPageCount;
    public string PageStatusText => $"Page {PreviewCurrentPage} of {PreviewPageCount}";

    public ChatFilesTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        _animTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _animTimer.Tick += OnAnimTimerTick;
    }

    private void OnAnimTimerTick(object? sender, EventArgs e)
    {
        if (_videoFrames.Count == 0 || !IsPlaying) return;
        int next = (CurrentVideoFrameIndex + 1) % _videoFrames.Count;
        CurrentVideoFrameIndex = next;
        PreviewImage = _videoFrames[next];
    }

    partial void OnCurrentVideoFrameIndexChanged(int value)
    {
        if (_videoFrames.Count > 0 && value >= 0 && value < _videoFrames.Count)
        {
            PreviewImage = _videoFrames[value];
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        _thumbCache.Clear();
        _chatTitles.Clear();
        _items.Clear();
        Rows.Clear();
        ChatUsages.Clear();
        SelectedCount = 0;
        CloseOverlays();
        if (value == null)
        {
            StatusText = "No node selected";
            StorageSummary = "—";
            StorageLimitsText = string.Empty;
            StorageUsageFraction = 0;
            ItemCountSummary = "0 files";
            return;
        }
        _needsRefresh = true;
        if (IsTabActive) _ = Refresh();
    }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (value && _needsRefresh && SelectedNode != null) _ = Refresh();
    }

    partial void OnSelectedChatUsageChanged(ChatFileUsageModel? value)
    {
        if (_suppressChatReload || SelectedNode == null) return;
        _ = LoadFilesAsync(append: false);
    }

    partial void OnSelectedOriginFilterChanged(string value)
    {
        if (SelectedNode == null) return;
        _ = LoadFilesAsync(append: false);
    }

    partial void OnSelectedTypeFilterChanged(string value)
    {
        if (SelectedNode == null) return;
        _ = LoadFilesAsync(append: false);
    }

    private void CloseOverlays()
    {
        _animTimer.Stop();
        IsPlaying = false;
        _videoFrames.Clear();
        VideoFrameMaxIndex = 0;
        CurrentVideoFrameIndex = 0;
        IsVideo = false;
        IsMultiPage = false;
        IsTextPreview = false;
        PreviewText = string.Empty;
        IsPreviewOpen = false;
        PreviewImage = null;
        PreviewItem = null;
        IsConfirmOpen = false;
        IsSweepConfirmation = false;
        IsSweepCalculating = false;
        _pendingConfirmAction = null;
    }

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    [RelayCommand]
    public async Task Refresh()
    {
        if (SelectedNode == null) return;
        _needsRefresh = false;
        await LoadStatsAsync();
        await LoadFilesAsync(append: false);
    }

    [RelayCommand]
    public Task LoadMore() => LoadFilesAsync(append: true);

    private async Task LoadStatsAsync()
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var client = _nodeManager.GetClient(node);
            var statsTask = client.GetWorkspaceStorageStatsAsync(new NoxWorkspaceStorageStatsRequest { NodeId = node.Id }).ResponseAsync;
            var chatsTask = LoadAllChatsAsync(node);

            await Task.WhenAll(statsTask, chatsTask);
            var stats = await statsTask;
            var allChats = await chatsTask;
            if (!ReferenceEquals(node, SelectedNode)) return;

            StorageSummary = $"{FormatBytes(stats.TotalBytes)} / {(stats.MaxTotalBytes > 0 ? FormatBytes(stats.MaxTotalBytes) : "∞")} · {stats.FileCount:N0} files";
            StorageLimitsText = stats.MaxBytesPerChat > 0 ? $"Per-chat limit {FormatBytes(stats.MaxBytesPerChat)}" : string.Empty;
            StorageUsageFraction = stats.MaxTotalBytes > 0 ? Math.Clamp((double)stats.TotalBytes / stats.MaxTotalBytes, 0, 1) : 0;

            long previousChat = SelectedChatUsage?.ChatId ?? 0;
            var usages = new List<ChatFileUsageModel>
            {
                new()
                {
                    ChatId = 0,
                    Title = "All chats",
                    FileCount = (int)stats.FileCount,
                    TotalBytes = stats.TotalBytes
                }
            };

            var statsChats = stats.Chats.ToDictionary(c => c.ChatId);
            var seenChatIds = new HashSet<long> { 0 };

            // Non-empty chats sorted by TotalBytes descending
            var nonEmptyChats = stats.Chats
                .Where(c => c.TotalBytes > 0 || c.FileCount > 0)
                .OrderByDescending(c => c.TotalBytes)
                .Select(c =>
                {
                    seenChatIds.Add(c.ChatId);
                    return new ChatFileUsageModel
                    {
                        ChatId = c.ChatId,
                        Title = ChatTitle(c.ChatId),
                        FileCount = (int)c.FileCount,
                        TotalBytes = c.TotalBytes,
                        ImageCount = (int)c.ImageCount,
                        VideoCount = (int)c.VideoCount,
                        DocumentCount = (int)c.DocumentCount,
                        SavedFileCount = (int)c.SavedFileCount,
                        Oldest = c.Oldest,
                        Newest = c.Newest
                    };
                });
            usages.AddRange(nonEmptyChats);

            // Empty chats: chats from allChats or statsChats with 0 files / 0 B
            var emptyChats = allChats.Values
                .Where(c => !seenChatIds.Contains(c.ChatId))
                .OrderByDescending(c => c.LastActiveUnixSec)
                .ThenBy(c => c.Title)
                .Select(c =>
                {
                    seenChatIds.Add(c.ChatId);
                    return new ChatFileUsageModel
                    {
                        ChatId = c.ChatId,
                        Title = !string.IsNullOrWhiteSpace(c.Title) ? $"{c.Title} (#{c.ChatId})" : $"Chat #{c.ChatId}",
                        FileCount = 0,
                        TotalBytes = 0,
                        LastActiveUnixSec = c.LastActiveUnixSec
                    };
                })
                .ToList();

            // Also check if any chat in statsChats was 0 files and wasn't in allChats
            foreach (var sc in stats.Chats)
            {
                if (sc.TotalBytes == 0 && sc.FileCount == 0 && seenChatIds.Add(sc.ChatId))
                {
                    emptyChats.Add(new ChatFileUsageModel
                    {
                        ChatId = sc.ChatId,
                        Title = ChatTitle(sc.ChatId),
                        FileCount = 0,
                        TotalBytes = 0
                    });
                }
            }

            usages.AddRange(emptyChats);

            _suppressChatReload = true;
            try
            {
                ChatUsages.Clear();
                foreach (var u in usages) ChatUsages.Add(u);
                SelectedChatUsage = ChatUsages.FirstOrDefault(u => u.ChatId == previousChat) ?? ChatUsages[0];
            }
            finally
            {
                _suppressChatReload = false;
            }
        }
        catch (Exception ex)
        {
            StorageSummary = "Storage stats unavailable";
            StatusText = $"Stats error: {ex.Message}";
        }
    }

    private async Task<Dictionary<long, NoxChatItem>> LoadAllChatsAsync(NoxNodeModel node)
    {
        var chats = new Dictionary<long, NoxChatItem>();
        try
        {
            var client = _nodeManager.GetClient(node);
            string cursor = string.Empty;
            for (int page = 0; page < 100; page++)
            {
                var response = await client.ListChatsAsync(new NoxListChatsRequest
                {
                    NodeId = node.Id,
                    Limit = 200,
                    Cursor = cursor
                }).ResponseAsync;

                foreach (var chat in response.Chats)
                {
                    chats[chat.ChatId] = chat;
                    if (!string.IsNullOrWhiteSpace(chat.Title))
                    {
                        _chatTitles[chat.ChatId] = chat.Title;
                    }
                }

                if (!response.PaginationSupported || string.IsNullOrEmpty(response.NextCursor) || response.NextCursor == cursor)
                    break;
                cursor = response.NextCursor;
            }
        }
        catch
        {
            // Fall back gracefully
        }
        return chats;
    }

    private string ChatTitle(long chatId) =>
        _chatTitles.TryGetValue(chatId, out var title) ? $"{title} (#{chatId})" : $"Chat #{chatId}";

    private static string MapTypeFilter(string filter) => filter switch
    {
        "Images" => "image",
        "Videos & GIFs" => "video",
        "PDFs" => "pdf",
        "Presentations" => "pptx",
        "Documents & Text" => "document",
        _ => string.Empty
    };

    private async Task LoadFilesAsync(bool append)
    {
        var node = SelectedNode;
        if (node == null) return;
        int generation = Interlocked.Increment(ref _loadGeneration);
        long chatId = SelectedChatUsage?.ChatId ?? 0;
        string origin = SelectedOriginFilter is "generated" or "telegram" or "job_input" ? SelectedOriginFilter : string.Empty;
        string kindFilter = MapTypeFilter(SelectedTypeFilter);
        int offset = append ? _items.Count : 0;

        IsLoading = true;
        try
        {
            var res = await _nodeManager.GetClient(node).ListChatFilesAsync(new NoxListChatFilesRequest
            {
                NodeId = node.Id,
                ChatId = chatId,
                Limit = PageSize,
                Offset = (uint)offset,
                Origin = origin,
                KindFilter = kindFilter
            }).ResponseAsync;
            if (generation != _loadGeneration || !ReferenceEquals(node, SelectedNode)) return;

            if (!append)
            {
                _items.Clear();
                SelectedCount = 0;
            }
            var known = _items.Select(i => i.Uuid).ToHashSet(StringComparer.Ordinal);
            foreach (var file in res.Files)
            {
                if (!known.Add(file.Uuid)) continue;
                var item = ToModel(file);
                if (_thumbCache.TryGetValue(item.Uuid, out var bmp))
                {
                    item.Thumbnail = bmp;
                    item.ThumbnailRequested = true;
                }
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ChatFileItemModel.IsSelected))
                        SelectedCount = _items.Count(i => i.IsSelected);
                };
                _items.Add(item);
            }
            CanLoadMore = res.Files.Count >= PageSize;
            RebuildRows();
            ItemCountSummary = $"{_items.Count:N0} file{(_items.Count == 1 ? "" : "s")}{(CanLoadMore ? "+" : "")}";
            StatusText = _items.Count == 0 ? "No files for this selection." : $"Loaded {_items.Count:N0} files · {SelectedChatLabel}";
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration) StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    private ChatFileItemModel ToModel(NoxChatFile f) => new()
    {
        Uuid = f.Uuid,
        Handle = f.Handle,
        Store = f.Store,
        Kind = f.Kind,
        Role = f.Role,
        ChatId = f.ChatId,
        ChatName = ChatTitle(f.ChatId),
        Filename = f.Filename,
        MimeType = f.MimeType,
        SizeBytes = f.Bytes,
        SourceBundleBytes = f.SourceBundleBytes,
        Width = f.Width,
        Height = f.Height,
        PageCount = f.PageCount,
        DurationSec = f.DurationS,
        HasAudio = f.HasAudio,
        Origin = f.Origin,
        SourceMessageId = f.SourceMessageId,
        JobId = f.JobId,
        Description = f.Description,
        CreatedAt = f.CreatedAt,
        LastUsedAt = f.LastUsedAt,
        Pinned = f.Pinned
    };

    public void SetColumnCount(int columns)
    {
        columns = Math.Max(1, columns);
        if (ColumnCount == columns) return;
        ColumnCount = columns;
        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        int cols = Math.Max(1, ColumnCount);
        for (int i = 0; i < _items.Count; i += cols)
        {
            Rows.Add(new ChatFileRowModel(_items.GetRange(i, Math.Min(cols, _items.Count - i))));
        }
    }

    // ------------------------------------------------------------------
    // Lazy thumbnails
    // ------------------------------------------------------------------

    public void OnItemEnteredViewport(ChatFileItemModel item)
    {
        if (item.ThumbnailRequested || SelectedNode == null) return;
        item.ThumbnailRequested = true;
        var node = SelectedNode;
        _ = Task.Run(async () =>
        {
            await _thumbThrottle.WaitAsync();
            try
            {
                if (!ReferenceEquals(node, SelectedNode)) return;
                var bmp = await FetchThumbnailBitmapAsync(node, item.ChatId, item.Uuid);
                Dispatcher.UIThread.Post(() =>
                {
                    if (bmp == null)
                    {
                        item.ThumbnailRequested = false;
                        return;
                    }
                    item.Thumbnail = bmp;
                    if (_thumbCache.Count >= MaxCachedThumbnails) _thumbCache.Clear();
                    _thumbCache[item.Uuid] = bmp;
                });
            }
            finally
            {
                _thumbThrottle.Release();
            }
        });
    }

    private async Task<Bitmap?> FetchThumbnailBitmapAsync(NoxNodeModel node, long chatId, string uuid)
    {
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatFilePreviewAsync(new NoxGetChatFilePreviewRequest
            {
                NodeId = node.Id,
                ChatId = chatId,
                Uuid = uuid,
                Mode = "thumbnail",
                MaxDimension = ThumbnailDimension,
                Page = 1
            }).ResponseAsync.ConfigureAwait(false);

            if (res.PreviewBytes.IsEmpty) return null;
            using var ms = new MemoryStream(res.PreviewBytes.ToByteArray(), writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Multi-modal Preview
    // ------------------------------------------------------------------

    [RelayCommand]
    public async Task OpenPreview(ChatFileItemModel? item)
    {
        var node = SelectedNode;
        if (item == null || node == null) return;

        CloseOverlays();
        PreviewItem = item;
        PreviewImage = item.Thumbnail;
        IsPreviewOpen = true;
        IsPreviewLoading = true;
        PreviewStatus = "Loading preview…";

        if (item.IsMultiPage)
        {
            IsMultiPage = true;
            PreviewCurrentPage = 1;
            PreviewPageCount = Math.Max(1, item.PageCount);
            PrevPageCommand.NotifyCanExecuteChanged();
            NextPageCommand.NotifyCanExecuteChanged();
            await LoadPreviewPageAsync(1);
            return;
        }

        if (item.IsVideoOrAnimation)
        {
            IsVideo = true;
            await LoadVideoPreviewAsync(item);
            return;
        }

        if (item.Kind is "txt" or "md" or "json" or "csv")
        {
            await LoadTextPreviewAsync(item);
            return;
        }

        // Standard image or generic file preview
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatFilePreviewAsync(new NoxGetChatFilePreviewRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid,
                Mode = "thumbnail",
                MaxDimension = PreviewDimension,
                Page = 1
            }).ResponseAsync;

            if (!ReferenceEquals(PreviewItem, item)) return;
            if (!res.PreviewBytes.IsEmpty)
            {
                using var ms = new MemoryStream(res.PreviewBytes.ToByteArray(), writable: false);
                var bmp = new Bitmap(ms);
                PreviewImage = bmp;
                PreviewStatus = $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height} · {item.SizeText}";
            }
            else
            {
                PreviewStatus = $"{item.DisplayName} · {item.SizeText}";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(PreviewItem, item))
                PreviewStatus = $"Preview error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(PreviewItem, item))
                IsPreviewLoading = false;
        }
    }

    [RelayCommand]
    public void ClosePreview()
    {
        CloseOverlays();
    }

    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    public async Task PrevPage()
    {
        if (PreviewCurrentPage <= 1) return;
        PreviewCurrentPage--;
        await LoadPreviewPageAsync(PreviewCurrentPage);
    }

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    public async Task NextPage()
    {
        if (PreviewCurrentPage >= PreviewPageCount) return;
        PreviewCurrentPage++;
        await LoadPreviewPageAsync(PreviewCurrentPage);
    }

    private async Task LoadPreviewPageAsync(int page)
    {
        var item = PreviewItem;
        var node = SelectedNode;
        if (item == null || node == null) return;
        IsPreviewLoading = true;
        PreviewStatus = $"Loading page {page}…";
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatFilePreviewAsync(new NoxGetChatFilePreviewRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid,
                Mode = "page",
                MaxDimension = PreviewDimension,
                Page = (uint)page
            }).ResponseAsync;

            if (!ReferenceEquals(PreviewItem, item)) return;
            if (res.PageCount > 0) PreviewPageCount = (int)res.PageCount;
            PreviewCurrentPage = res.CurrentPage > 0 ? (int)res.CurrentPage : page;
            if (!res.PreviewBytes.IsEmpty)
            {
                using var ms = new MemoryStream(res.PreviewBytes.ToByteArray(), writable: false);
                PreviewImage = new Bitmap(ms);
            }
            PreviewStatus = $"{item.DisplayName} · Page {PreviewCurrentPage} of {PreviewPageCount}";
        }
        catch (Exception ex)
        {
            PreviewStatus = $"Failed to load page {page}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(PreviewItem, item))
            {
                IsPreviewLoading = false;
                PrevPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task LoadVideoPreviewAsync(ChatFileItemModel item)
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatFilePreviewAsync(new NoxGetChatFilePreviewRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid,
                Mode = "anim",
                MaxDimension = 1024,
                Page = 1
            }).ResponseAsync;

            if (!ReferenceEquals(PreviewItem, item)) return;
            var asset = TryDecodeNoxAnim(res.PreviewBytes.ToByteArray());
            if (asset != null && asset.Frames.Count > 0)
            {
                _videoFrames.Clear();
                _videoFrames.AddRange(asset.Frames);
                VideoFrameMaxIndex = _videoFrames.Count - 1;
                CurrentVideoFrameIndex = 0;
                PreviewImage = _videoFrames[0];
                IsPlaying = true;
                _animTimer.Interval = TimeSpan.FromMilliseconds(asset.IntervalMs > 0 ? asset.IntervalMs : 100);
                _animTimer.Start();
                PreviewStatus = $"Video preview: {_videoFrames.Count} frames ({item.DurationSec:0.#}s)";
            }
            else if (!res.PreviewBytes.IsEmpty)
            {
                using var ms = new MemoryStream(res.PreviewBytes.ToByteArray(), writable: false);
                PreviewImage = new Bitmap(ms);
                PreviewStatus = $"Still thumbnail ({item.DurationSec:0.#}s)";
            }
            else
            {
                PreviewStatus = $"Video preview unavailable ({item.DurationSec:0.#}s)";
            }
        }
        catch (Exception ex)
        {
            PreviewStatus = $"Video preview error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(PreviewItem, item))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private async Task LoadTextPreviewAsync(ChatFileItemModel item)
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatFilePreviewAsync(new NoxGetChatFilePreviewRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid,
                Mode = "text",
                MaxDimension = 0,
                Page = 1
            }).ResponseAsync;

            if (!ReferenceEquals(PreviewItem, item)) return;
            if (!string.IsNullOrEmpty(res.TextContent))
            {
                PreviewText = res.TextContent;
                IsTextPreview = true;
                PreviewStatus = $"{item.DisplayName} · {item.SizeText}";
            }
            else
            {
                PreviewStatus = $"{item.DisplayName} · (empty text)";
            }
        }
        catch (Exception ex)
        {
            PreviewStatus = $"Text preview error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(PreviewItem, item))
            {
                IsPreviewLoading = false;
            }
        }
    }

    [RelayCommand]
    public void TogglePlay()
    {
        IsPlaying = !IsPlaying;
        if (IsPlaying)
        {
            _animTimer.Start();
        }
        else
        {
            _animTimer.Stop();
        }
    }

    [RelayCommand]
    public async Task SavePreviewed()
    {
        var item = PreviewItem;
        var node = SelectedNode;
        if (item == null || node == null) return;
        try
        {
            StatusText = $"Downloading {item.DisplayName}…";
            var res = await _nodeManager.GetClient(node).GetChatFileBytesAsync(new NoxGetChatFileBytesRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid
            }).ResponseAsync;

            string filename = !string.IsNullOrWhiteSpace(res.Filename) ? res.Filename : item.DisplayName;
            string defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsDir = Path.Combine(defaultDir, "Downloads");
            string targetDir = Directory.Exists(downloadsDir) ? downloadsDir : Path.GetTempPath();
            string targetPath = Path.Combine(targetDir, filename);

            await File.WriteAllBytesAsync(targetPath, res.FileBytes.ToByteArray());
            StatusText = $"Saved to {targetPath} ({FormatBytes(res.FileBytes.Length)})";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenExternally()
    {
        var item = PreviewItem;
        var node = SelectedNode;
        if (item == null || node == null) return;
        try
        {
            StatusText = $"Opening {item.DisplayName} externally…";
            var res = await _nodeManager.GetClient(node).GetChatFileBytesAsync(new NoxGetChatFileBytesRequest
            {
                NodeId = node.Id,
                ChatId = item.ChatId,
                Uuid = item.Uuid
            }).ResponseAsync;

            string filename = !string.IsNullOrWhiteSpace(res.Filename) ? res.Filename : item.DisplayName;
            string tempDir = Path.Combine(Path.GetTempPath(), "madtom_preview");
            Directory.CreateDirectory(tempDir);
            string targetPath = Path.Combine(tempDir, filename);
            await File.WriteAllBytesAsync(targetPath, res.FileBytes.ToByteArray());

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
            }
            catch { }
            StatusText = $"Opened {filename} in system viewer";
        }
        catch (Exception ex)
        {
            StatusText = $"Open externally failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void DeletePreviewed()
    {
        var item = PreviewItem;
        if (item == null) return;
        ClosePreview();
        AskConfirm("Delete file",
            $"Delete {item.DisplayName} ({item.SizeText}) from {item.ChatName}? This cannot be undone.",
            "Delete",
            () => DeleteFilesAsync(new[] { item }));
    }

    // ------------------------------------------------------------------
    // Selection & deletion
    // ------------------------------------------------------------------

    [RelayCommand]
    public void SelectAll()
    {
        bool select = _items.Any(i => !i.IsSelected);
        foreach (var i in _items) i.IsSelected = select;
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select one or more files first.";
            return;
        }
        long bytes = selected.Sum(i => i.SizeBytes);
        int chats = selected.Select(i => i.ChatId).Distinct().Count();
        AskConfirm("Delete selected files",
            $"Delete {selected.Count:N0} selected file{(selected.Count == 1 ? "" : "s")} ({FormatBytes(bytes)}) across {chats} chat{(chats == 1 ? "" : "s")}? This cannot be undone.",
            "Delete",
            () => DeleteFilesAsync(selected));
    }

    private async Task DeleteFilesAsync(IReadOnlyCollection<ChatFileItemModel> items)
    {
        var node = SelectedNode;
        if (node == null) return;
        int deleted = 0;
        long freed = 0;
        try
        {
            var client = _nodeManager.GetClient(node);
            foreach (var group in items.GroupBy(i => i.ChatId))
            {
                var req = new NoxSweepChatFilesRequest { NodeId = node.Id, ChatId = group.Key };
                req.Uuids.AddRange(group.Select(i => i.Uuid));
                var res = await client.SweepChatFilesAsync(req).ResponseAsync;
                deleted += (int)res.DeletedCount;
                freed += res.FreedBytes;
            }
            foreach (var i in items) _thumbCache.Remove(i.Uuid);
            await Refresh();
            StatusText = $"Deleted {deleted:N0} file{(deleted == 1 ? "" : "s")}, freed {FormatMegabytes(freed)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed after {deleted} file(s): {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Sweeps (Confirmation Guardrails: zero network calls before modal)
    // ------------------------------------------------------------------

    private bool CanSweepChat() => IsChatSelected;

    [RelayCommand(CanExecute = nameof(CanSweepChat))]
    public void SweepChatOlderThan() => OpenSweepConfirm(SelectedChatUsage?.ChatId ?? 0);

    [RelayCommand]
    public void SweepAllOlderThan() => OpenSweepConfirm(0);

    private void OpenSweepConfirm(long chatId)
    {
        var node = SelectedNode;
        if (node == null) return;

        double days = (double)(SweepDays ?? 0);
        if (days < 0) days = 0;
        SweepScopeChatId = chatId;
        SweepScopeDays = days;
        IsSweepConfirmation = true;

        string scope = chatId == 0 ? "ALL chats" : SelectedChatLabel;
        string age = days > 0 ? $"older than {days.ToString("0.##", CultureInfo.InvariantCulture)} days" : "of ANY age (no age filter)";

        ConfirmTitle = chatId == 0 ? "Sweep ALL chats" : $"Sweep chat: {scope}";
        ConfirmMessage = $"Sweep files in {scope} {age}.\nFiles used by active running jobs and pinned files are protected.\n\nClick 'Calculate' to see file count and disk space that will be freed before confirming.";
        ConfirmButtonText = "Execute Sweep";
        SweepCalculationSummary = "Not calculated. Click 'Calculate' to check file count and bytes to be freed.";
        IsSweepCalculating = false;
        IsConfirmOpen = true;
        // INVARIANT: Zero network RPCs executed!
    }

    [RelayCommand]
    public async Task CalculateSweep()
    {
        var node = SelectedNode;
        if (node == null) return;
        IsSweepCalculating = true;
        SweepCalculationSummary = "Calculating sweep (dry run)…";
        try
        {
            var client = _nodeManager.GetClient(node);
            var dry = await client.SweepChatFilesAsync(new NoxSweepChatFilesRequest
            {
                NodeId = node.Id,
                ChatId = SweepScopeChatId,
                OlderThanDays = SweepScopeDays,
                KeepRecent = 0,
                KindFilter = MapTypeFilter(SelectedTypeFilter),
                DryRun = true
            }).ResponseAsync;

            string scope = SweepScopeChatId == 0 ? "ALL chats" : SelectedChatLabel;
            string age = SweepScopeDays > 0 ? $"older than {SweepScopeDays.ToString("0.##", CultureInfo.InvariantCulture)} days" : "of ANY age";
            if (dry.DeletedCount == 0)
            {
                SweepCalculationSummary = $"Nothing to sweep: 0 files in {scope} {age}.";
            }
            else
            {
                SweepCalculationSummary = $"Estimated: {dry.DeletedCount:N0} file{(dry.DeletedCount == 1 ? "" : "s")} ({FormatMegabytes(dry.FreedBytes)}) would be freed.";
            }
        }
        catch (Exception ex)
        {
            SweepCalculationSummary = $"Calculation failed: {ex.Message}";
        }
        finally
        {
            IsSweepCalculating = false;
        }
    }

    // ------------------------------------------------------------------
    // Confirmation overlay
    // ------------------------------------------------------------------

    private void AskConfirm(string title, string message, string button, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmButtonText = button;
        _pendingConfirmAction = action;
        IsSweepConfirmation = false;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    public async Task ConfirmPending()
    {
        var node = SelectedNode;
        bool isSweep = IsSweepConfirmation;
        var action = _pendingConfirmAction;

        _pendingConfirmAction = null;
        IsConfirmOpen = false;
        IsSweepConfirmation = false;

        if (node == null) return;

        if (isSweep)
        {
            IsBusy = true;
            StatusText = "Executing sweep…";
            try
            {
                var client = _nodeManager.GetClient(node);
                var res = await client.SweepChatFilesAsync(new NoxSweepChatFilesRequest
                {
                    NodeId = node.Id,
                    ChatId = SweepScopeChatId,
                    OlderThanDays = SweepScopeDays,
                    KeepRecent = 0,
                    KindFilter = MapTypeFilter(SelectedTypeFilter),
                    DryRun = false
                }).ResponseAsync;

                _thumbCache.Clear();
                await Refresh();
                StatusText = $"Sweep complete: deleted {res.DeletedCount:N0} file{(res.DeletedCount == 1 ? "" : "s")}, freed {FormatMegabytes(res.FreedBytes)}.";
            }
            catch (Exception ex)
            {
                StatusText = $"Sweep failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
            return;
        }

        if (action != null)
        {
            IsBusy = true;
            try { await action(); }
            finally { IsBusy = false; }
        }
    }

    [RelayCommand]
    public void CancelConfirm()
    {
        _pendingConfirmAction = null;
        IsSweepConfirmation = false;
        IsConfirmOpen = false;
    }

    // ------------------------------------------------------------------
    // Video NOXANIM1 Decoding helper
    // ------------------------------------------------------------------

    public static bool IsNoxAnimBytes(ReadOnlySpan<byte> data)
    {
        return data.Length > 16 &&
               data[0] == (byte)'N' && data[1] == (byte)'O' &&
               data[2] == (byte)'X' && data[3] == (byte)'A' &&
               data[4] == (byte)'N' && data[5] == (byte)'I' &&
               data[6] == (byte)'M' && data[7] == (byte)'1';
    }

    public static NoxMediaPreviewAsset? TryDecodeNoxAnim(byte[] bytes)
    {
        try
        {
            if (!IsNoxAnimBytes(bytes)) return null;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
            uint intervalMs = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4));
            if (count == 0 || count > 64) return null;

            int offset = 16;
            var frames = new List<Bitmap>((int)count);
            for (int i = 0; i < count; i++)
            {
                if (offset + 4 > bytes.Length) break;
                uint frameLen = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                offset += 4;
                if (frameLen == 0 || offset + (int)frameLen > bytes.Length) break;

                using var ms = new MemoryStream(bytes, offset, (int)frameLen, writable: false);
                var bmp = new Bitmap(ms);
                if (bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
                {
                    frames.Add(bmp);
                }
                offset += (int)frameLen;
            }

            if (frames.Count > 0)
            {
                return new NoxMediaPreviewAsset(frames, (int)(intervalMs > 0 ? intervalMs : 100));
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------
    // Formatting helpers
    // ------------------------------------------------------------------

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        return (mb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }

    internal static string FormatMegabytes(long bytes) =>
        (bytes / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";

    internal static string FormatTimestamp(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : iso;
    }
}
