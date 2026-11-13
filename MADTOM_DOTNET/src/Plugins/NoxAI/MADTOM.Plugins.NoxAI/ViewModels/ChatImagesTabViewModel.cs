using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>One stored Image Studio image (chat-bound) as shown in the Images tab.</summary>
public sealed partial class ChatImageItemModel : ObservableObject
{
    public string Uuid { get; init; } = string.Empty;
    public string Handle { get; init; } = string.Empty;
    public long ChatId { get; init; }
    public string ChatName { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public long SizeBytes { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public long SourceMessageId { get; init; }
    public string JobId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string LastUsedAt { get; init; } = string.Empty;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;

    internal bool ThumbnailRequested { get; set; }

    public bool IsGenerated => Origin == "generated";
    public string OriginBadge => Origin switch
    {
        "generated" => "✨ generated",
        "telegram" => "📷 telegram",
        _ => string.IsNullOrEmpty(Origin) ? "unknown" : Origin
    };
    public string SizeText => ChatImagesTabViewModel.FormatBytes(SizeBytes);
    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";
    public string CreatedText => ChatImagesTabViewModel.FormatTimestamp(CreatedAt);
    public string LastUsedText => ChatImagesTabViewModel.FormatTimestamp(LastUsedAt);
    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "(no description)" : Description;
    public string MetaLine => $"{DimensionsText} · {SizeText} · {CreatedText}";
    public string Tooltip =>
        $"{Handle}\n{ChatName}\n{OriginBadge} · {MetaLine}" +
        (string.IsNullOrEmpty(JobId) ? string.Empty : $"\njob {JobId}") +
        (SourceMessageId != 0 ? $"\nmessage #{SourceMessageId}" : string.Empty) +
        $"\n\n{DisplayDescription}";

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Description) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
}

/// <summary>Row of image cards (virtualized grid, same approach as the Stickers &amp; GIFs tab).</summary>
public sealed class ChatImageRowModel
{
    public IReadOnlyList<ChatImageItemModel> Items { get; }
    public ChatImageRowModel(IReadOnlyList<ChatImageItemModel> items) => Items = items;
}

/// <summary>Per-chat storage usage entry; ChatId 0 is the synthetic "All chats" entry.</summary>
public sealed class ChatImageUsageModel
{
    public long ChatId { get; init; }
    public string Title { get; init; } = string.Empty;
    public int ImageCount { get; init; }
    public long TotalBytes { get; init; }
    public string Oldest { get; init; } = string.Empty;
    public string Newest { get; init; } = string.Empty;

    public bool IsAllChats => ChatId == 0;
    public string Summary => $"{ImageCount:N0} images · {ChatImagesTabViewModel.FormatBytes(TotalBytes)}";
    public string RangeText => IsAllChats || (string.IsNullOrEmpty(Oldest) && string.IsNullOrEmpty(Newest))
        ? string.Empty
        : $"{ChatImagesTabViewModel.FormatTimestamp(Oldest)} → {ChatImagesTabViewModel.FormatTimestamp(Newest)}";
}

public sealed partial class ChatImagesTabViewModel : ObservableObject
{
    private const int PageSize = 200;
    private const int ThumbnailDimension = 256;
    private const int PreviewDimension = 1024;
    private const int MaxCachedThumbnails = 600;

    private readonly NoxNodeManager _nodeManager;
    private readonly SemaphoreSlim _thumbThrottle = new(6);
    private readonly Dictionary<string, Bitmap> _thumbCache = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _chatTitles = new();
    private readonly List<ChatImageItemModel> _items = new();
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
    [ObservableProperty] private string _itemCountSummary = "0 images";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private int _columnCount = 4;
    [ObservableProperty] private string _selectedOriginFilter = "All origins";
    [ObservableProperty] private decimal? _sweepDays = 30;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedChatLabel))]
    [NotifyCanExecuteChangedFor(nameof(SweepChatOlderThanCommand))]
    private ChatImageUsageModel? _selectedChatUsage;

    // Preview overlay
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private ChatImageItemModel? _previewItem;
    [ObservableProperty] private string _previewStatus = string.Empty;

    // Confirmation overlay
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmMessage = string.Empty;
    [ObservableProperty] private string _confirmButtonText = "Delete";

    public ObservableCollection<ChatImageUsageModel> ChatUsages { get; } = new();
    public ObservableCollection<ChatImageRowModel> Rows { get; } = new();
    public IReadOnlyList<string> OriginFilters { get; } = new[] { "All origins", "generated", "telegram" };

    public bool IsChatSelected => SelectedChatUsage is { IsAllChats: false };
    public string SelectedChatLabel => SelectedChatUsage == null || SelectedChatUsage.IsAllChats
        ? "All chats"
        : SelectedChatUsage.Title;

    public ChatImagesTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
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
            ItemCountSummary = "0 images";
            return;
        }
        _needsRefresh = true;
        if (IsTabActive) _ = Refresh();
    }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (value && _needsRefresh && SelectedNode != null) _ = Refresh();
    }

    partial void OnSelectedChatUsageChanged(ChatImageUsageModel? value)
    {
        if (_suppressChatReload || SelectedNode == null) return;
        _ = LoadImagesAsync(append: false);
    }

    partial void OnSelectedOriginFilterChanged(string value)
    {
        if (SelectedNode == null) return;
        _ = LoadImagesAsync(append: false);
    }

    private void CloseOverlays()
    {
        IsPreviewOpen = false;
        PreviewImage = null;
        PreviewItem = null;
        IsConfirmOpen = false;
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
        await LoadImagesAsync(append: false);
    }

    [RelayCommand]
    public Task LoadMore() => LoadImagesAsync(append: true);

    private async Task LoadStatsAsync()
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var client = _nodeManager.GetClient(node);
            var statsTask = client.GetImageStorageStatsAsync(new NoxImageStorageStatsRequest { NodeId = node.Id }).ResponseAsync;
            await LoadChatTitlesAsync(node);
            var stats = await statsTask;
            if (!ReferenceEquals(node, SelectedNode)) return;

            StorageSummary = $"{FormatBytes(stats.TotalBytes)} / {(stats.MaxTotalBytes > 0 ? FormatBytes(stats.MaxTotalBytes) : "∞")} · {stats.ImageCount:N0} images";
            StorageLimitsText = stats.MaxBytesPerChat > 0 ? $"Per-chat limit {FormatBytes(stats.MaxBytesPerChat)}" : string.Empty;
            StorageUsageFraction = stats.MaxTotalBytes > 0 ? Math.Clamp((double)stats.TotalBytes / stats.MaxTotalBytes, 0, 1) : 0;

            long previousChat = SelectedChatUsage?.ChatId ?? 0;
            var usages = new List<ChatImageUsageModel>
            {
                new() { ChatId = 0, Title = "All chats", ImageCount = (int)stats.ImageCount, TotalBytes = stats.TotalBytes }
            };
            usages.AddRange(stats.Chats
                .OrderByDescending(c => c.TotalBytes)
                .Select(c => new ChatImageUsageModel
                {
                    ChatId = c.ChatId,
                    Title = ChatTitle(c.ChatId),
                    ImageCount = (int)c.ImageCount,
                    TotalBytes = c.TotalBytes,
                    Oldest = c.Oldest,
                    Newest = c.Newest
                }));

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

    private async Task LoadChatTitlesAsync(NoxNodeModel node)
    {
        if (_chatTitles.Count > 0) return;
        try
        {
            var res = await _nodeManager.GetClient(node)
                .ListChatsAsync(new NoxListChatsRequest { NodeId = node.Id, Limit = 200 }).ResponseAsync;
            foreach (var c in res.Chats)
            {
                if (!string.IsNullOrWhiteSpace(c.Title)) _chatTitles[c.ChatId] = c.Title;
            }
        }
        catch
        {
            // Titles are cosmetic; fall back to chat ids.
        }
    }

    private string ChatTitle(long chatId) =>
        _chatTitles.TryGetValue(chatId, out var title) ? $"{title} (#{chatId})" : $"Chat #{chatId}";

    private async Task LoadImagesAsync(bool append)
    {
        var node = SelectedNode;
        if (node == null) return;
        int generation = Interlocked.Increment(ref _loadGeneration);
        long chatId = SelectedChatUsage?.ChatId ?? 0;
        string origin = SelectedOriginFilter is "generated" or "telegram" ? SelectedOriginFilter : string.Empty;
        int offset = append ? _items.Count : 0;

        IsLoading = true;
        try
        {
            var res = await _nodeManager.GetClient(node).ListChatImagesAsync(new NoxListChatImagesRequest
            {
                NodeId = node.Id,
                ChatId = chatId,
                Limit = PageSize,
                Offset = (uint)offset,
                Origin = origin
            }).ResponseAsync;
            if (generation != _loadGeneration || !ReferenceEquals(node, SelectedNode)) return;

            if (!append)
            {
                _items.Clear();
                SelectedCount = 0;
            }
            var known = _items.Select(i => i.Uuid).ToHashSet(StringComparer.Ordinal);
            foreach (var img in res.Images)
            {
                if (!known.Add(img.Uuid)) continue;
                var item = ToModel(img);
                if (_thumbCache.TryGetValue(item.Uuid, out var bmp))
                {
                    item.Thumbnail = bmp;
                    item.ThumbnailRequested = true;
                }
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ChatImageItemModel.IsSelected))
                        SelectedCount = _items.Count(i => i.IsSelected);
                };
                _items.Add(item);
            }
            CanLoadMore = res.Images.Count >= PageSize;
            RebuildRows();
            ItemCountSummary = $"{_items.Count:N0} image{(_items.Count == 1 ? "" : "s")}{(CanLoadMore ? "+" : "")}";
            StatusText = _items.Count == 0 ? "No images for this selection." : $"Loaded {_items.Count:N0} images · {SelectedChatLabel}";
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

    private ChatImageItemModel ToModel(NoxChatImage img) => new()
    {
        Uuid = img.Uuid,
        Handle = img.Handle,
        ChatId = img.ChatId,
        ChatName = ChatTitle(img.ChatId),
        Origin = img.Origin,
        Width = img.Width,
        Height = img.Height,
        SizeBytes = img.Bytes,
        MimeType = img.MimeType,
        SourceMessageId = img.SourceMessageId,
        JobId = img.JobId,
        Description = img.Description,
        CreatedAt = img.CreatedAt,
        LastUsedAt = img.LastUsedAt
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
            Rows.Add(new ChatImageRowModel(_items.GetRange(i, Math.Min(cols, _items.Count - i))));
        }
    }

    // ------------------------------------------------------------------
    // Lazy thumbnails
    // ------------------------------------------------------------------

    public void OnItemEnteredViewport(ChatImageItemModel item)
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
                var bmp = await FetchBitmapAsync(node, item.ChatId, item.Uuid, ThumbnailDimension);
                Dispatcher.UIThread.Post(() =>
                {
                    if (bmp == null)
                    {
                        item.ThumbnailRequested = false; // allow retry on next scroll-in
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

    private async Task<Bitmap?> FetchBitmapAsync(NoxNodeModel node, long chatId, string uuid, int maxDimension)
    {
        try
        {
            var res = await _nodeManager.GetClient(node).GetChatImageAsync(new NoxGetChatImageRequest
            {
                NodeId = node.Id,
                ChatId = chatId,
                Uuid = uuid,
                MaxDimension = (uint)maxDimension
            }).ResponseAsync.ConfigureAwait(false);
            if (res.ImageBytes.IsEmpty) return null;
            using var ms = new MemoryStream(res.ImageBytes.ToByteArray(), writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------

    [RelayCommand]
    public async Task OpenPreview(ChatImageItemModel? item)
    {
        var node = SelectedNode;
        if (item == null || node == null) return;
        PreviewItem = item;
        PreviewImage = item.Thumbnail;
        IsPreviewOpen = true;
        IsPreviewLoading = true;
        PreviewStatus = "Loading preview…";
        var bmp = await Task.Run(() => FetchBitmapAsync(node, item.ChatId, item.Uuid, PreviewDimension));
        if (!ReferenceEquals(PreviewItem, item)) return;
        IsPreviewLoading = false;
        if (bmp != null)
        {
            PreviewImage = bmp;
            PreviewStatus = $"Preview {bmp.PixelSize.Width}×{bmp.PixelSize.Height} (original {item.DimensionsText}, {item.SizeText})";
        }
        else
        {
            PreviewStatus = "Could not load a larger preview.";
        }
    }

    [RelayCommand]
    public void ClosePreview()
    {
        IsPreviewOpen = false;
        PreviewItem = null;
        PreviewImage = null;
    }

    [RelayCommand]
    public void DeletePreviewed()
    {
        var item = PreviewItem;
        if (item == null) return;
        ClosePreview();
        AskConfirm("Delete image",
            $"Delete {item.Handle} ({item.SizeText}) from {item.ChatName}? This cannot be undone.",
            "Delete",
            () => DeleteImagesAsync(new[] { item }));
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
            StatusText = "Select one or more images first.";
            return;
        }
        long bytes = selected.Sum(i => i.SizeBytes);
        int chats = selected.Select(i => i.ChatId).Distinct().Count();
        AskConfirm("Delete selected images",
            $"Delete {selected.Count:N0} selected image{(selected.Count == 1 ? "" : "s")} ({FormatBytes(bytes)}) across {chats} chat{(chats == 1 ? "" : "s")}? This cannot be undone.",
            "Delete",
            () => DeleteImagesAsync(selected));
    }

    private async Task DeleteImagesAsync(IReadOnlyCollection<ChatImageItemModel> items)
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
                var req = new NoxSweepChatImagesRequest { NodeId = node.Id, ChatId = group.Key };
                req.Uuids.AddRange(group.Select(i => i.Uuid));
                var res = await client.SweepChatImagesAsync(req).ResponseAsync;
                deleted += (int)res.DeletedCount;
                freed += res.FreedBytes;
            }
            foreach (var i in items) _thumbCache.Remove(i.Uuid);
            await Refresh();
            StatusText = $"Deleted {deleted:N0} image{(deleted == 1 ? "" : "s")}, freed {FormatMegabytes(freed)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed after {deleted} image(s): {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Age-based sweeps (dry-run first, then confirm)
    // ------------------------------------------------------------------

    private bool CanSweepChat() => IsChatSelected;

    [RelayCommand(CanExecute = nameof(CanSweepChat))]
    public Task SweepChatOlderThan() => PrepareSweepAsync(SelectedChatUsage?.ChatId ?? 0);

    [RelayCommand]
    public Task SweepAllOlderThan() => PrepareSweepAsync(0);

    private async Task PrepareSweepAsync(long chatId)
    {
        var node = SelectedNode;
        if (node == null) return;
        double days = (double)(SweepDays ?? 0);
        if (days < 0) days = 0;
        string scope = chatId == 0 ? "ALL chats" : ChatTitle(chatId);
        string age = days > 0 ? $"not used for more than {days.ToString("0.##", CultureInfo.InvariantCulture)} days" : "of ANY age (no age filter)";

        IsBusy = true;
        StatusText = "Calculating sweep (dry run)…";
        try
        {
            var dry = await _nodeManager.GetClient(node).SweepChatImagesAsync(BuildSweepRequest(node, chatId, days, dryRun: true)).ResponseAsync;
            if (dry.DeletedCount == 0)
            {
                StatusText = $"Nothing to sweep: no images in {scope} {age}.";
                return;
            }
            StatusText = string.Empty;
            AskConfirm(chatId == 0 ? "Sweep all chats" : "Sweep chat",
                $"This will delete {dry.DeletedCount:N0} image{(dry.DeletedCount == 1 ? "" : "s")} ({FormatMegabytes(dry.FreedBytes)}) in {scope} {age}.\nImages used by running jobs are protected by the bot. This cannot be undone.",
                $"Delete {dry.DeletedCount:N0}",
                () => ExecuteSweepAsync(chatId, days));
        }
        catch (Exception ex)
        {
            StatusText = $"Sweep dry run failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSweepAsync(long chatId, double days)
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var res = await _nodeManager.GetClient(node).SweepChatImagesAsync(BuildSweepRequest(node, chatId, days, dryRun: false)).ResponseAsync;
            _thumbCache.Clear();
            await Refresh();
            StatusText = $"Sweep complete: deleted {res.DeletedCount:N0} image{(res.DeletedCount == 1 ? "" : "s")}, freed {FormatMegabytes(res.FreedBytes)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Sweep failed: {ex.Message}";
        }
    }

    private static NoxSweepChatImagesRequest BuildSweepRequest(NoxNodeModel node, long chatId, double days, bool dryRun) => new()
    {
        NodeId = node.Id,
        ChatId = chatId,
        OlderThanDays = days,
        KeepRecent = 0,
        DryRun = dryRun
    };

    // ------------------------------------------------------------------
    // Confirmation overlay
    // ------------------------------------------------------------------

    private void AskConfirm(string title, string message, string button, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmButtonText = button;
        _pendingConfirmAction = action;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    public async Task ConfirmPending()
    {
        var action = _pendingConfirmAction;
        _pendingConfirmAction = null;
        IsConfirmOpen = false;
        if (action == null) return;
        IsBusy = true;
        try { await action(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void CancelConfirm()
    {
        _pendingConfirmAction = null;
        IsConfirmOpen = false;
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
