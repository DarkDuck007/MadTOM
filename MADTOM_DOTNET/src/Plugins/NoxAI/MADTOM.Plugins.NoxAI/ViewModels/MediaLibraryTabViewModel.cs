using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed record NoxMediaPreviewAsset(IReadOnlyList<Bitmap> Frames, int IntervalMs = 100)
{
    public Bitmap? FirstFrame => Frames.Count > 0 ? Frames[0] : null;
    public bool IsAnimated => Frames.Count > 1;
    public long EstimatedBytes => Frames.Sum(f => (long)f.PixelSize.Width * f.PixelSize.Height * 4);
}

public sealed partial class MediaItemModel : ObservableObject
{
    public string UniqueId { get; init; } = string.Empty;
    public string FileId { get; init; } = string.Empty;
    public string Kind { get; init; } = "sticker";
    public string Format { get; init; } = string.Empty;
    public string SetName { get; init; } = string.Empty;
    public string Emoji { get; init; } = string.Empty;
    public string CaptionText { get; set; } = string.Empty;
    public int PinnedSlot { get; set; }
    public uint UseCount { get; init; }
    public bool IsNsfw { get; set; }
    public bool IsDead { get; set; }

    public bool ExpectsAnimation =>
        Format is "video" or "animated" ||
        Kind is "gif" or "animation";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryBadgeText))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    [NotifyPropertyChangedFor(nameof(ShowInLibraryBadge))]
    [NotifyPropertyChangedFor(nameof(ShowAddToLibraryBadge))]
    private bool _inLibrary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInLibraryBadge))]
    [NotifyPropertyChangedFor(nameof(ShowAddToLibraryBadge))]
    private bool _isUpdatingLibrary;

    [ObservableProperty]
    private string _updatingStatusText = "⟳ Adding...";

    public bool ShowInLibraryBadge => InLibrary && !IsUpdatingLibrary;
    public bool ShowAddToLibraryBadge => !InLibrary && !IsUpdatingLibrary;
    public string LibraryBadgeText => InLibrary ? "✓ In Library" : "+ Library";
    public double CardOpacity => InLibrary ? 1.0 : 0.82;

    [ObservableProperty] private Bitmap? _thumbnail;

    public IReadOnlyList<Bitmap>? Frames { get; private set; }
    public bool IsAnimatedLoaded => Frames is { Count: > 1 };

    public void ApplyPreview(NoxMediaPreviewAsset asset)
    {
        Frames = asset.Frames;
        if (asset.Frames.Count > 0)
        {
            Thumbnail = asset.Frames[0];
        }
    }

    public void AdvanceFrame(int tick)
    {
        if (Frames is { Count: > 1 })
        {
            Thumbnail = Frames[tick % Frames.Count];
        }
    }
}

public sealed partial class StickerPackGroupModel : ObservableObject
{
    [ObservableProperty] private string _setName = string.Empty;
    [ObservableProperty] private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountSummary))]
    private int _stickerCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountSummary))]
    private int _inLibraryCount;

    [ObservableProperty] private string _analyzedAt = string.Empty;
    [ObservableProperty] private bool _isReindexing;
    [ObservableProperty] private bool _isGifGroup;
    [ObservableProperty] private string _iconEmoji = "📦";
    [ObservableProperty] private Bitmap? _thumbnail;

    public IReadOnlyList<Bitmap>? Frames { get; private set; }
    public bool IsAnimatedLoaded => Frames is { Count: > 1 };

    public void ApplyPreview(NoxMediaPreviewAsset asset)
    {
        Frames = asset.Frames;
        if (asset.Frames.Count > 0)
        {
            Thumbnail = asset.Frames[0];
        }
    }

    public void AdvanceFrame(int tick)
    {
        if (Frames is { Count: > 1 })
        {
            Thumbnail = Frames[tick % Frames.Count];
        }
    }

    public string CountSummary => IsGifGroup
        ? $"{StickerCount} GIFs · {InLibraryCount} in Nox's library"
        : $"{InLibraryCount} in Nox's library / {StickerCount} in pack";

    public List<MediaItemModel> Items { get; } = new();

    public IRelayCommand ReindexCommand { get; }

    public StickerPackGroupModel(
        string setName,
        string title,
        int count,
        int inLibraryCount,
        string analyzedAt,
        bool isGifGroup,
        string iconEmoji,
        Func<string, Task> onReindex)
    {
        SetName = setName;
        Title = string.IsNullOrWhiteSpace(title) ? setName : title;
        StickerCount = count;
        InLibraryCount = inLibraryCount;
        AnalyzedAt = analyzedAt;
        IsGifGroup = isGifGroup;
        IconEmoji = string.IsNullOrWhiteSpace(iconEmoji) ? (isGifGroup ? "🎞️" : "📦") : iconEmoji;

        ReindexCommand = new AsyncRelayCommand(async () =>
        {
            if (IsReindexing || IsGifGroup) return;
            IsReindexing = true;
            try
            {
                await onReindex(SetName);
            }
            finally
            {
                IsReindexing = false;
            }
        });
    }

    public void RecalculateCounts()
    {
        StickerCount = Items.Count;
        InLibraryCount = Items.Count(x => x.InLibrary);
    }
}

public static class NoxMediaThumbnailCache
{
    private static readonly object _lruLock = new();
    private static readonly Dictionary<string, (LinkedListNode<string> Node, NoxMediaPreviewAsset Asset)> _lruMap = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> _lruList = new();
    private static long _totalMemoryBytes;
    private static long _maxMemoryBytes = 256 * 1024 * 1024; // 256 MiB default
    private static int _maxItemCount = 500; // 500 items default

    private static readonly ConcurrentDictionary<string, Task<NoxMediaPreviewAsset?>> _inflightFetches = new();
    private static readonly SemaphoreSlim _fetchThrottle = new(16);
    private static readonly string _localDir;

    static NoxMediaThumbnailCache()
    {
        _localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MADTOM",
            "nox_thumbnails");
        try { Directory.CreateDirectory(_localDir); } catch { }
    }

    public static void Configure(int maxMiB, int maxCount)
    {
        lock (_lruLock)
        {
            _maxMemoryBytes = (long)Math.Clamp(maxMiB, 16, 4096) * 1024 * 1024;
            _maxItemCount = Math.Clamp(maxCount, 50, 50000);
            EvictIfNeededLocked();
        }
    }

    public static (long memoryBytes, int memoryCount, long diskBytes, int diskCount) GetUsage()
    {
        long memBytes;
        int memCount;
        lock (_lruLock)
        {
            memBytes = _totalMemoryBytes;
            memCount = _lruMap.Count;
        }

        long diskBytes = 0;
        int diskCount = 0;
        try
        {
            if (Directory.Exists(_localDir))
            {
                var dirInfo = new DirectoryInfo(_localDir);
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    diskBytes += file.Length;
                    diskCount++;
                }
            }
        }
        catch { }

        return (memBytes, memCount, diskBytes, diskCount);
    }

    public static void Clear(bool includeDisk = true)
    {
        lock (_lruLock)
        {
            _lruMap.Clear();
            _lruList.Clear();
            _totalMemoryBytes = 0;
        }

        if (includeDisk)
        {
            try
            {
                if (Directory.Exists(_localDir))
                {
                    foreach (var file in Directory.EnumerateFiles(_localDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    public static bool TryGetFromMemory(string fileUniqueId, bool expectsAnimation, out NoxMediaPreviewAsset? asset)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(fileUniqueId, out var entry) && entry.Asset != null)
            {
                if (!expectsAnimation || entry.Asset.IsAnimated)
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                    asset = entry.Asset;
                    return true;
                }
            }
        }
        asset = null;
        return false;
    }

    private static void PutInMemory(string fileUniqueId, NoxMediaPreviewAsset asset)
    {
        lock (_lruLock)
        {
            if (_lruMap.TryGetValue(fileUniqueId, out var existing))
            {
                _totalMemoryBytes -= existing.Asset.EstimatedBytes;
                _lruList.Remove(existing.Node);
            }

            var node = _lruList.AddFirst(fileUniqueId);
            _lruMap[fileUniqueId] = (node, asset);
            _totalMemoryBytes += asset.EstimatedBytes;

            EvictIfNeededLocked();
        }
    }

    private static void EvictIfNeededLocked()
    {
        while (_lruList.Count > _maxItemCount || (_maxMemoryBytes > 0 && _totalMemoryBytes > _maxMemoryBytes))
        {
            var lastNode = _lruList.Last;
            if (lastNode == null) break;

            string key = lastNode.Value;
            _lruList.RemoveLast();

            if (_lruMap.Remove(key, out var removed))
            {
                _totalMemoryBytes -= removed.Asset.EstimatedBytes;
            }
        }
        if (_totalMemoryBytes < 0) _totalMemoryBytes = 0;
    }

    public static Task<NoxMediaPreviewAsset?> FetchPreviewAsync(
        NoxNodeManager nodeManager,
        NoxNodeModel node,
        string fileUniqueId,
        bool expectsAnimation)
    {
        if (string.IsNullOrEmpty(fileUniqueId)) return Task.FromResult<NoxMediaPreviewAsset?>(null);

        if (TryGetFromMemory(fileUniqueId, expectsAnimation, out var cached))
        {
            return Task.FromResult<NoxMediaPreviewAsset?>(cached);
        }

        // Deduplicate concurrent fetches for the same fileUniqueId
        return _inflightFetches.GetOrAdd(fileUniqueId, uid => FetchCoreAsync(nodeManager, node, uid, expectsAnimation));
    }

    private static bool IsNoxAnimBytes(ReadOnlySpan<byte> data)
    {
        return data.Length > 16 &&
               data[0] == (byte)'N' &&
               data[1] == (byte)'O' &&
               data[2] == (byte)'X' &&
               data[3] == (byte)'A' &&
               data[4] == (byte)'N' &&
               data[5] == (byte)'I' &&
               data[6] == (byte)'M' &&
               data[7] == (byte)'1';
    }

    private static NoxMediaPreviewAsset? TryDecodeNoxAnim(byte[] bytes)
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
                if (bmp.PixelSize.Width > 2 && bmp.PixelSize.Height > 2)
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

    private static NoxMediaPreviewAsset? TryDecodeStaticBitmap(byte[] bytes)
    {
        if (bytes.Length <= 200) return null;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new Bitmap(ms);
            if (bmp.PixelSize.Width > 2 && bmp.PixelSize.Height > 2)
            {
                return new NoxMediaPreviewAsset(new[] { bmp }, 100);
            }
            bmp.Dispose();
        }
        catch { }
        return null;
    }

    private static async Task<NoxMediaPreviewAsset?> FetchCoreAsync(
        NoxNodeManager nodeManager,
        NoxNodeModel node,
        string fileUniqueId,
        bool expectsAnimation)
    {
        try
        {
            if (TryGetFromMemory(fileUniqueId, expectsAnimation, out var cached))
            {
                return cached;
            }

            string localAnimPath = Path.Combine(_localDir, $"{fileUniqueId}.noxanim");
            string localJpgPath = Path.Combine(_localDir, $"{fileUniqueId}.jpg");

            // 1. Check local multi-frame .noxanim cache first
            if (File.Exists(localAnimPath))
            {
                try
                {
                    var fi = new FileInfo(localAnimPath);
                    if (fi.Length > 200)
                    {
                        var animAsset = await Task.Run(() => TryDecodeNoxAnim(File.ReadAllBytes(localAnimPath)));
                        if (animAsset != null)
                        {
                            PutInMemory(fileUniqueId, animAsset);
                            return animAsset;
                        }
                    }
                    else
                    {
                        try { File.Delete(localAnimPath); } catch { }
                    }
                }
                catch { }
            }

            // 2. If static sticker, check local .jpg cache (evict any <=200 byte 1x1 corrupt files!)
            if (!expectsAnimation && File.Exists(localJpgPath))
            {
                try
                {
                    var fi = new FileInfo(localJpgPath);
                    if (fi.Length > 200)
                    {
                        var staticAsset = await Task.Run(() => TryDecodeStaticBitmap(File.ReadAllBytes(localJpgPath)));
                        if (staticAsset != null)
                        {
                            PutInMemory(fileUniqueId, staticAsset);
                            return staticAsset;
                        }
                    }
                    try { File.Delete(localJpgPath); } catch { }
                }
                catch { }
            }

            await _fetchThrottle.WaitAsync();
            try
            {
                var client = nodeManager.GetClient(node);
                var resp = await client.GetMediaAssetPreviewAsync(new NoxMediaPreviewRequest
                {
                    NodeId = node.Id,
                    FileUniqueId = fileUniqueId
                });

                if (resp?.ImageBytes != null && !resp.ImageBytes.IsEmpty)
                {
                    byte[] bytes = resp.ImageBytes.ToByteArray();
                    if (bytes.Length > 200)
                    {
                        if (resp.MimeType == "application/x-nox-anim" || IsNoxAnimBytes(bytes))
                        {
                            var animAsset = await Task.Run(() => TryDecodeNoxAnim(bytes));
                            if (animAsset != null)
                            {
                                _ = Task.Run(() =>
                                {
                                    try { File.WriteAllBytes(localAnimPath, bytes); } catch { }
                                });
                                PutInMemory(fileUniqueId, animAsset);
                                return animAsset;
                            }
                        }
                        else
                        {
                            var staticAsset = await Task.Run(() => TryDecodeStaticBitmap(bytes));
                            if (staticAsset != null)
                            {
                                _ = Task.Run(() =>
                                {
                                    try { File.WriteAllBytes(localJpgPath, bytes); } catch { }
                                });
                                PutInMemory(fileUniqueId, staticAsset);
                                return staticAsset;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _fetchThrottle.Release();
            }

            return null;
        }
        finally
        {
            _inflightFetches.TryRemove(fileUniqueId, out _);
        }
    }
}

public sealed partial class MediaLibraryTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;
    private readonly DispatcherTimer _animTimer;
    private int _animTick;
    private CancellationTokenSource? _thumbCts;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private string _selectedLibraryFilter = "All in Pack";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private MediaItemModel? _selectedItem;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isLoading;

    // Telegram-Style Paged View
    [ObservableProperty] private StickerPackGroupModel? _selectedPack;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _pageSize = 40;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private string _pageSummary = "Page 1 of 1";
    [ObservableProperty] private bool _hasPrevPage;
    [ObservableProperty] private bool _hasNextPage;

    // Force Add Sticker Form Properties
    [ObservableProperty] private bool _isForceAddPanelOpen;
    [ObservableProperty] private string _newStickerFileId = string.Empty;
    [ObservableProperty] private string _newStickerSetName = string.Empty;
    [ObservableProperty] private string _newStickerEmoji = "⭐";
    [ObservableProperty] private string _newStickerCaption = string.Empty;
    [ObservableProperty] private string _newStickerKind = "sticker";

    public ObservableCollection<MediaItemModel> AllMediaItems { get; } = new();
    public ObservableCollection<StickerPackGroupModel> PackGroups { get; } = new();
    public ObservableCollection<MediaItemModel> PinnedItems { get; } = new();
    public ObservableCollection<MediaItemModel> CurrentPageItems { get; } = new();

    public MediaLibraryTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        _animTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _animTimer.Tick += OnAnimationTimerTick;
        _animTimer.Start();

        SelectedNode = _nodeManager.Nodes.FirstOrDefault();
    }

    private void OnAnimationTimerTick(object? sender, EventArgs e)
    {
        _animTick++;
        for (int i = 0; i < CurrentPageItems.Count; i++)
        {
            var item = CurrentPageItems[i];
            if (item.IsAnimatedLoaded)
            {
                item.AdvanceFrame(_animTick);
            }
        }

        for (int i = 0; i < PackGroups.Count; i++)
        {
            var pack = PackGroups[i];
            if (pack.IsAnimatedLoaded)
            {
                pack.AdvanceFrame(_animTick);
            }
        }
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value != null)
        {
            _ = RefreshMedia();
        }
        else
        {
            _thumbCts?.Cancel();
            PackGroups.Clear();
            AllMediaItems.Clear();
            PinnedItems.Clear();
            CurrentPageItems.Clear();
            SelectedPack = null;
            StatusText = "No node selected";
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        CurrentPage = 1;
        UpdateCurrentPageItems();
    }

    partial void OnSelectedFilterChanged(string value)
    {
        CurrentPage = 1;
        UpdateCurrentPageItems();
    }

    partial void OnSelectedLibraryFilterChanged(string value)
    {
        CurrentPage = 1;
        UpdateCurrentPageItems();
    }

    partial void OnSelectedPackChanged(StickerPackGroupModel? value)
    {
        CurrentPage = 1;
        UpdateCurrentPageItems();
    }

    [RelayCommand]
    public void ToggleForceAddPanel()
    {
        IsForceAddPanelOpen = !IsForceAddPanelOpen;
    }

    [RelayCommand]
    public void SelectPack(StickerPackGroupModel? pack)
    {
        if (pack == null) return;
        SelectedPack = pack;
    }

    [RelayCommand]
    public void SelectActiveLibraryPack()
    {
        var libItems = AllMediaItems.Where(x => x.InLibrary).ToList();
        var libPack = new StickerPackGroupModel(
            setName: "nox_library",
            title: "Nox's Active Library (All Packs)",
            count: libItems.Count,
            inLibraryCount: libItems.Count,
            analyzedAt: "",
            isGifGroup: true,
            iconEmoji: "📚",
            onReindex: _ => Task.CompletedTask
        );
        foreach (var item in libItems)
        {
            libPack.Items.Add(item);
        }
        SelectedPack = libPack;
    }

    [RelayCommand]
    public void SelectPinnedPack()
    {
        var pinnedPack = new StickerPackGroupModel(
            setName: "pinned",
            title: "Pinned Media Items",
            count: PinnedItems.Count,
            inLibraryCount: PinnedItems.Count(x => x.InLibrary),
            analyzedAt: "",
            isGifGroup: true,
            iconEmoji: "⭐",
            onReindex: _ => Task.CompletedTask
        );
        foreach (var item in PinnedItems)
        {
            pinnedPack.Items.Add(item);
        }
        SelectedPack = pinnedPack;
    }

    [RelayCommand]
    public void SelectGifsPack()
    {
        var gifPack = PackGroups.FirstOrDefault(p => p.IsGifGroup);
        if (gifPack != null)
        {
            SelectedPack = gifPack;
        }
    }

    [RelayCommand]
    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            UpdateCurrentPageItems();
        }
    }

    [RelayCommand]
    public void PrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            UpdateCurrentPageItems();
        }
    }

    [RelayCommand]
    public async Task RefreshMedia()
    {
        if (SelectedNode == null) return;
        IsLoading = true;
        StatusText = "Loading media library...";
        try
        {
            var node = SelectedNode;
            var client = _nodeManager.GetClient(node);

            var res = await Task.Run(async () =>
            {
                return await client.QueryMediaLibraryAsync(new NoxMediaQueryRequest
                {
                    NodeId = node.Id,
                    Kind = "",
                    Limit = 2500
                });
            });

            var (packGroups, allItems, pinnedItems) = await Task.Run(() =>
            {
                var all = new List<MediaItemModel>();
                var pinned = new List<MediaItemModel>();
                var packDict = res.StickerPacks.ToDictionary(p => p.SetName, p => p.Title);

                foreach (var it in res.Items)
                {
                    var m = new MediaItemModel
                    {
                        UniqueId = it.FileUniqueId,
                        FileId = it.FileId,
                        Kind = it.Kind ?? "sticker",
                        Format = it.Format ?? string.Empty,
                        SetName = it.SetName ?? string.Empty,
                        Emoji = it.Emoji ?? string.Empty,
                        CaptionText = it.CaptionText ?? string.Empty,
                        PinnedSlot = it.PinnedSlot,
                        UseCount = it.UseCount,
                        IsNsfw = it.Nsfw,
                        IsDead = it.Dead,
                        InLibrary = it.InLibrary
                    };
                    if (NoxMediaThumbnailCache.TryGetFromMemory(m.UniqueId, m.ExpectsAnimation, out var memAsset) && memAsset != null)
                    {
                        m.ApplyPreview(memAsset);
                    }
                    all.Add(m);
                    if (m.PinnedSlot > 0)
                    {
                        pinned.Add(m);
                    }
                }

                var groups = new List<StickerPackGroupModel>();

                // 1. Group stickers by sticker set
                var stickerGroups = all
                    .Where(it => it.Kind == "sticker")
                    .GroupBy(it => string.IsNullOrWhiteSpace(it.SetName) ? "Other Stickers" : it.SetName)
                    .OrderBy(g => g.Key);

                foreach (var group in stickerGroups)
                {
                    string setName = group.Key;
                    string title = packDict.TryGetValue(setName, out var t) && !string.IsNullOrWhiteSpace(t)
                        ? t
                        : setName;

                    var firstWithEmoji = group.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Emoji));
                    string iconEmoji = firstWithEmoji?.Emoji ?? "📦";

                    var packGroup = new StickerPackGroupModel(
                        setName: setName,
                        title: title,
                        count: group.Count(),
                        inLibraryCount: group.Count(x => x.InLibrary),
                        analyzedAt: "",
                        isGifGroup: false,
                        iconEmoji: iconEmoji,
                        onReindex: ReindexPackAsync
                    );

                    foreach (var item in group)
                    {
                        packGroup.Items.Add(item);
                    }
                    if (packGroup.Items.Count > 0 && packGroup.Items[0].Frames != null)
                    {
                        packGroup.ApplyPreview(new NoxMediaPreviewAsset(packGroup.Items[0].Frames!));
                    }
                    groups.Add(packGroup);
                }

                // 2. Group GIFs and animations
                var gifItems = all.Where(it => it.Kind is "gif" or "animation").ToList();
                if (gifItems.Count > 0)
                {
                    var gifGroup = new StickerPackGroupModel(
                        setName: "gifs",
                        title: "Saved GIFs & Animations",
                        count: gifItems.Count,
                        inLibraryCount: gifItems.Count(x => x.InLibrary),
                        analyzedAt: "",
                        isGifGroup: true,
                        iconEmoji: "🎞️",
                        onReindex: _ => Task.CompletedTask
                    );

                    foreach (var item in gifItems)
                    {
                        gifGroup.Items.Add(item);
                    }
                    if (gifGroup.Items.Count > 0 && gifGroup.Items[0].Frames != null)
                    {
                        gifGroup.ApplyPreview(new NoxMediaPreviewAsset(gifGroup.Items[0].Frames!));
                    }
                    groups.Add(gifGroup);
                }

                return (groups, all, pinned);
            });

            AllMediaItems.Clear();
            foreach (var item in allItems)
            {
                AllMediaItems.Add(item);
            }

            PinnedItems.Clear();
            foreach (var item in pinnedItems)
            {
                PinnedItems.Add(item);
            }

            PackGroups.Clear();
            foreach (var group in packGroups)
            {
                PackGroups.Add(group);
            }

            var previousSetName = SelectedPack?.SetName;
            var target = PackGroups.FirstOrDefault(p => p.SetName == previousSetName) ?? PackGroups.FirstOrDefault();
            CurrentPage = 1;
            if (ReferenceEquals(SelectedPack, target))
            {
                UpdateCurrentPageItems();
            }
            else
            {
                SelectedPack = target;
            }

            // Load pack strip thumbnails in parallel
            LoadThumbnailsForPackGroups();

            int inLibTotal = allItems.Count(x => x.InLibrary);
            StatusText = $"{inLibTotal} in Nox's library / {allItems.Count} total captioned across {PackGroups.Count} packs.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadThumbnailsForPackGroups()
    {
        var node = SelectedNode;
        if (node == null) return;

        var groupsToLoad = PackGroups.Where(g => g.Items.Count > 0 && (g.Thumbnail == null || (g.Items[0].ExpectsAnimation && !g.IsAnimatedLoaded))).ToList();
        if (groupsToLoad.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var tasks = groupsToLoad.Select(async group =>
            {
                var firstItem = group.Items.FirstOrDefault();
                if (firstItem == null) return;

                var asset = await NoxMediaThumbnailCache.FetchPreviewAsync(_nodeManager, node, firstItem.UniqueId, firstItem.ExpectsAnimation);
                if (asset != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        group.ApplyPreview(asset);
                        if (firstItem.Thumbnail == null || (asset.IsAnimated && !firstItem.IsAnimatedLoaded))
                        {
                            firstItem.ApplyPreview(asset);
                        }
                    });
                }
            });
            await Task.WhenAll(tasks);
        });
    }

    private void UpdateCurrentPageItems()
    {
        CurrentPageItems.Clear();
        if (SelectedPack == null)
        {
            TotalPages = 1;
            CurrentPage = 1;
            HasPrevPage = false;
            HasNextPage = false;
            PageSummary = "No pack selected";
            return;
        }

        var query = SearchQuery?.Trim().ToLowerInvariant() ?? string.Empty;
        var filter = SelectedFilter;
        var libFilter = SelectedLibraryFilter;

        var filtered = SelectedPack.Items.Where(it =>
        {
            if (filter == "Stickers" && it.Kind != "sticker") return false;
            if (filter == "GIFs" && it.Kind is not ("gif" or "animation")) return false;

            if (libFilter == "In Nox's Library" && !it.InLibrary) return false;
            if (libFilter == "Not in Library" && it.InLibrary) return false;

            if (string.IsNullOrEmpty(query)) return true;
            return (it.CaptionText?.ToLowerInvariant().Contains(query) ?? false) ||
                   (it.Emoji?.Contains(query) ?? false) ||
                   (it.SetName?.ToLowerInvariant().Contains(query) ?? false);
        }).ToList();

        TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        HasPrevPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
        PageSummary = $"Page {CurrentPage} of {TotalPages} ({filtered.Count} shown)";

        var pagedItems = filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize);
        foreach (var item in pagedItems)
        {
            if ((item.Thumbnail == null || (item.ExpectsAnimation && !item.IsAnimatedLoaded)) &&
                NoxMediaThumbnailCache.TryGetFromMemory(item.UniqueId, item.ExpectsAnimation, out var cached) &&
                cached != null)
            {
                item.ApplyPreview(cached);
            }
            CurrentPageItems.Add(item);
        }

        LoadThumbnailsForCurrentPage();
    }

    private void LoadThumbnailsForCurrentPage()
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        var itemsToLoad = CurrentPageItems
            .Where(x => x.Thumbnail == null || (x.ExpectsAnimation && !x.IsAnimatedLoaded))
            .ToList();
        var node = SelectedNode;
        if (node == null || itemsToLoad.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var tasks = itemsToLoad.Select(async item =>
            {
                var asset = await NoxMediaThumbnailCache.FetchPreviewAsync(_nodeManager, node, item.UniqueId, item.ExpectsAnimation);
                if (asset != null && !ct.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        item.ApplyPreview(asset);
                        var pack = PackGroups.FirstOrDefault(g => g.SetName == item.SetName);
                        if (pack != null && pack.Items.Count > 0 && ReferenceEquals(pack.Items[0], item))
                        {
                            pack.ApplyPreview(asset);
                        }
                    });
                }
            });
            await Task.WhenAll(tasks);
        }, ct);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task ToggleLibraryMembership(MediaItemModel? item)
    {
        if (item == null || SelectedNode == null || item.IsUpdatingLibrary) return;
        bool targetInLibrary = !item.InLibrary;
        item.UpdatingStatusText = targetInLibrary ? "⟳ Adding..." : "⟳ Removing...";
        item.IsUpdatingLibrary = true;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.MutateMediaItemAsync(new NoxMediaMutationRequest
            {
                NodeId = SelectedNode.Id,
                FileUniqueId = item.UniqueId,
                CaptionText = item.CaptionText ?? string.Empty,
                IsNsfw = item.IsNsfw,
                IsDead = item.IsDead,
                InLibrary = targetInLibrary,
                UpdateLibraryMembership = true
            });

            if (res.Success)
            {
                item.InLibrary = targetInLibrary;
                SelectedPack?.RecalculateCounts();
                var matchingGroup = PackGroups.FirstOrDefault(g => g.SetName == item.SetName);
                matchingGroup?.RecalculateCounts();

                int inLibTotal = AllMediaItems.Count(x => x.InLibrary);
                StatusText = targetInLibrary
                    ? $"Added '{item.Emoji}' to Nox's library ({inLibTotal} in library)."
                    : $"Removed '{item.Emoji}' from Nox's library ({inLibTotal} in library).";

                if (SelectedLibraryFilter != "All in Pack" || SelectedPack?.SetName == "nox_library")
                {
                    UpdateCurrentPageItems();
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Library update failed: {ex.Message}";
        }
        finally
        {
            item.IsUpdatingLibrary = false;
        }
    }

    [RelayCommand]
    public async Task ReindexPackAsync(string setName)
    {
        if (SelectedNode == null || string.IsNullOrWhiteSpace(setName)) return;
        StatusText = $"Triggering reindex for '{setName}'...";
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.TriggerStickerPackAnalysisAsync(new NoxAnalyzePackRequest
            {
                NodeId = SelectedNode.Id,
                SetName = setName,
                Reason = "Control Center manual reindex"
            });
            StatusText = res.Success
                ? $"Reindex queued: {res.Message}"
                : $"Reindex failed: {res.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Reindex error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SubmitForceAddSticker()
    {
        if (SelectedNode == null) return;
        if (string.IsNullOrWhiteSpace(NewStickerFileId))
        {
            StatusText = "Error: File ID is required.";
            return;
        }

        StatusText = "Force adding sticker to library...";
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var res = await client.ForceAddStickerAsync(new NoxForceAddStickerRequest
            {
                NodeId = SelectedNode.Id,
                FileId = NewStickerFileId.Trim(),
                SetName = string.IsNullOrWhiteSpace(NewStickerSetName) ? "manual" : NewStickerSetName.Trim(),
                Emoji = string.IsNullOrWhiteSpace(NewStickerEmoji) ? "⭐" : NewStickerEmoji.Trim(),
                CaptionText = string.IsNullOrWhiteSpace(NewStickerCaption) ? "Manual sticker addition" : NewStickerCaption.Trim(),
                Kind = NewStickerKind,
                Scope = 0
            });

            if (res.Success)
            {
                StatusText = $"Successfully added: {res.Message}";
                NewStickerFileId = string.Empty;
                NewStickerCaption = string.Empty;
                IsForceAddPanelOpen = false;
                await RefreshMedia();
            }
            else
            {
                StatusText = $"Add failed: {res.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task TogglePin(MediaItemModel? item)
    {
        if (item == null || SelectedNode == null) return;
        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            int newSlot = item.PinnedSlot > 0 ? 0 : 1;
            await client.ManageMediaPinAsync(new NoxMediaPinRequest
            {
                NodeId = SelectedNode.Id,
                Kind = item.Kind,
                FileUniqueId = item.UniqueId,
                Slot = newSlot
            });
            item.PinnedSlot = newSlot;
            await RefreshMedia();
        }
        catch (Exception ex)
        {
            StatusText = $"Pin toggle failed: {ex.Message}";
        }
    }
}
