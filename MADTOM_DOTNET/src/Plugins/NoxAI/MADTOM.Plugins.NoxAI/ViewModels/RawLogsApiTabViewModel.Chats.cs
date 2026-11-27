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
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

/// <summary>One row of the Chats list: a Telegram chat/DM (or a bucket of calls without a chat).</summary>
public sealed partial class ChatGroupModel : ObservableObject
{
    public const long GuestChatIdMin = -9_000_000_000_000_000L;
    public const long GuestChatIdMax = -8_000_000_000_000_000L;

    public string Key { get; init; } = string.Empty;
    public long? ChatId { get; init; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MetaLine))] private string _title = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MetaLine), nameof(IsGuest))] private string _chatType = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CountText))] private int _callCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(LastActiveText))] private long _lastActiveUnixNano;

    public bool IsGuest =>
        ChatType.StartsWith("guest", StringComparison.OrdinalIgnoreCase) ||
        (ChatId.HasValue && ChatId.Value >= GuestChatIdMin && ChatId.Value <= GuestChatIdMax);

    public string CountText => CallCount.ToString("N0", CultureInfo.InvariantCulture);
    public string MetaLine
    {
        get
        {
            string id = ChatId.HasValue ? ChatId.Value.ToString(CultureInfo.InvariantCulture)
                : Key == RawLogsApiTabViewModel.NoChatKey ? "no chat id" : "name match only";
            string type = FormatChatType(ChatType, IsGuest);
            return $"{type} · {id}";
        }
    }

    private static string FormatChatType(string chatType, bool isGuest)
    {
        if (string.IsNullOrEmpty(chatType)) return isGuest ? "guest" : "?";
        if (chatType.Equals("guest_private", StringComparison.OrdinalIgnoreCase)) return "guest · dm";
        if (chatType.Equals("guest_group", StringComparison.OrdinalIgnoreCase)) return "guest · group";
        return chatType;
    }

    public string LastActiveText => LastActiveUnixNano > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(LastActiveUnixNano / 1_000_000).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "—";
}

/// <summary>A non-call trace event (tool result, job event, …) shown inline in a chat's history.</summary>
public sealed class ChatTraceRowModel
{
    public string Key { get; init; } = string.Empty;
    public long TimestampUnixNano { get; init; }
    public string TimestampText { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// "Chats" sub-view: the Raw API Calls &amp; Payloads view grouped per chat. Chat identity comes from
/// the call's trace context (chat_id), then from the probe's chat registry by exact title. Prompt text
/// is never used to guess a chat: agy context-diff turns carry no chat header.
/// </summary>
public sealed partial class RawLogsApiTabViewModel
{
    public const string NoChatKey = "none";
    public const int ChatViewCacheSize = 8;
    private const int ChatInitialWindow = 150; // newest calls shown when a chat opens
    private const int ChatOlderStep = 100;     // added at the top when scrolling near it
    private const int MaxTraceEventsPerChat = 2000;
    private const int ChatFirstDetailWaveSize = 12;
    private const int ChatSubsequentDetailWaveSize = 16;

    private sealed record CachedCallDetails(
        string RequestJson,
        string ResponseJson,
        string ChatName,
        IReadOnlyList<ParsedTurnItemModel> ParsedTurns,
        IReadOnlyList<RawApiCallImageItem> Images);

    private sealed class ChatViewCacheEntry
    {
        public string CacheKey { get; init; } = string.Empty;
        public Dictionary<string, CachedCallDetails> Calls { get; } = new(StringComparer.Ordinal);
        public bool TraceLoaded { get; set; }
        public bool IsActive { get; set; }
    }

    private readonly Dictionary<long, NoxChatItem> _chatRegistry = new();
    private readonly Dictionary<string, long> _chatTitleToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _chatTitleToIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChatGroupModel> _chatGroupsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<ChatTraceRowModel>> _chatTraceRows = new();
    private readonly Dictionary<string, RawApiCallItemModel> _chatExtraCalls = new(StringComparer.Ordinal);
    private readonly HashSet<object> _chatHistoryMembers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _chatContentSearchMatchCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chatContentSearchMatchEventKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<ChatViewCacheEntry>> _chatViewCacheMap = new(StringComparer.Ordinal);
    private readonly LinkedList<ChatViewCacheEntry> _chatViewCacheLru = new();
    private readonly SemaphoreSlim _chatDetailSlots = new(4, 4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _chatDetailInflight = new(StringComparer.Ordinal);
    private CancellationTokenSource _chatCts = new();
    private CancellationTokenSource _chatSearchCts = new();
    private DispatcherTimer? _chatRefreshTimer;
    private bool _chatRefreshPending;
    private bool? _chatTraceSupported;
    private bool _suppressChatSelection;
    private int _chatVisibleLimit = ChatInitialWindow;
    private int _chatRegistryRevision;

    private readonly BulkObservableCollection<object> _chatHistory = new();
    public ObservableCollection<ChatGroupModel> ChatGroups { get; } = new();
    public ObservableCollection<object> ChatHistory => _chatHistory;
    public int ChatViewCacheCount => _chatViewCacheMap.Count;

    [ObservableProperty] private string _chatFilter = string.Empty;
    [ObservableProperty] private ChatGroupModel? _selectedChatGroup;
    [ObservableProperty] private string _chatListStatus = "Open this tab to load chats.";
    [ObservableProperty] private string _chatHistoryStatus = "Select a chat.";
    [ObservableProperty] private bool _chatHasOlder;
    [ObservableProperty] private bool _chatShowTraceEvents = true;
    [ObservableProperty] private string _chatContentSearchQuery = string.Empty;
    [ObservableProperty] private string _chatContentSearchStatus = string.Empty;
    [ObservableProperty] private bool _isChatContentSearching;
    [ObservableProperty] private bool _isChatContentSearchActive;

    private bool IsChatsViewActive => IsTabActive && SelectedSubViewIndex == 2;

    /// <summary>Raised when a chat is (re)opened: the view jumps to the bottom (newest call).</summary>
    public event Action? ChatHistoryReset;

    partial void OnChatFilterChanged(string value) => ApplyChatGroupFilter();
    partial void OnChatShowTraceEventsChanged(bool value) => RebuildChatHistory(resetLimit: false);
    partial void OnChatContentSearchQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CancelChatContentSearch();
            _chatContentSearchMatchCallIds.Clear();
            _chatContentSearchMatchEventKeys.Clear();
            IsChatContentSearchActive = false;
            ChatContentSearchStatus = string.Empty;
            RebuildChatHistory(resetLimit: false);
        }
        else
        {
            // Immediate fast local filter across cached records & metadata
            RunLocalChatContentFilter(value);
            RebuildChatHistory(resetLimit: false);
        }
    }

    partial void OnSelectedChatGroupChanged(ChatGroupModel? oldValue, ChatGroupModel? newValue)
    {
        if (_suppressChatSelection) return;
        CancelChatLoads();
        CancelChatContentSearch();
        _chatContentSearchMatchCallIds.Clear();
        _chatContentSearchMatchEventKeys.Clear();
        IsChatContentSearchActive = !string.IsNullOrWhiteSpace(ChatContentSearchQuery);
        if (IsChatContentSearchActive)
        {
            RunLocalChatContentFilter(ChatContentSearchQuery);
        }
        else
        {
            ChatContentSearchStatus = string.Empty;
        }

        // Snapshot parsed turns & decoded bitmaps into the bounded LRU chat view cache, then detach
        // uncompressed details from the previous chat's call models so switching back is instant.
        if (oldValue != null && !ReferenceEquals(oldValue, newValue))
        {
            CacheAndUnloadChatGroupDetails(oldValue.Key);
        }

        bool traceAlreadyLoaded = false;
        if (newValue != null && SelectedNode != null &&
            _chatViewCacheMap.TryGetValue(ChatViewCacheKey(SelectedNode.Id, newValue.Key), out var cachedNode))
        {
            _chatViewCacheLru.Remove(cachedNode);
            _chatViewCacheLru.AddFirst(cachedNode);
            cachedNode.Value.IsActive = true;
            traceAlreadyLoaded = cachedNode.Value.TraceLoaded;
        }

        RebuildChatHistory(resetLimit: true);
        ChatHistoryReset?.Invoke();
        if (newValue != null && newValue.Key != NoChatKey && !traceAlreadyLoaded)
        {
            _ = LoadChatTraceForGroupAsync(newValue, _chatCts.Token);
        }
    }

    private static string ChatViewCacheKey(string nodeId, string groupKey) => nodeId + "\0" + groupKey;

    private ChatViewCacheEntry GetOrCreateChatViewCacheEntry(string nodeId, string groupKey)
    {
        string key = ChatViewCacheKey(nodeId, groupKey);
        if (_chatViewCacheMap.TryGetValue(key, out var existing))
        {
            _chatViewCacheLru.Remove(existing);
            _chatViewCacheLru.AddFirst(existing);
            return existing.Value;
        }

        var entry = new ChatViewCacheEntry { CacheKey = key };
        var nodeRef = _chatViewCacheLru.AddFirst(entry);
        _chatViewCacheMap[key] = nodeRef;
        EvictChatViewCacheIfNeeded();
        return entry;
    }

    private void EvictChatViewCacheIfNeeded()
    {
        while (_chatViewCacheLru.Count > ChatViewCacheSize)
        {
            var tail = _chatViewCacheLru.Last;
            if (tail == null) break;
            _chatViewCacheLru.RemoveLast();
            _chatViewCacheMap.Remove(tail.Value.CacheKey);
            if (!tail.Value.IsActive)
            {
                DisposeCachedEntryImages(tail.Value);
            }
        }
    }

    private void DisposeCachedEntryImages(ChatViewCacheEntry entry)
    {
        HashSet<Bitmap>? liveBitmaps = null;
        if (SelectedApiCall is { Images.Count: > 0 } selectedCall)
        {
            liveBitmaps = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
            foreach (var img in selectedCall.Images)
            {
                if (img.Preview != null) liveBitmaps.Add(img.Preview);
            }
        }

        foreach (var cached in entry.Calls.Values)
        {
            foreach (var img in cached.Images)
            {
                if (img.Preview != null && (liveBitmaps == null || !liveBitmaps.Contains(img.Preview)))
                {
                    img.Preview.Dispose();
                }
            }
        }
        entry.Calls.Clear();
    }

    private void ClearChatViewCache()
    {
        foreach (var entry in _chatViewCacheLru)
        {
            if (!entry.IsActive)
            {
                DisposeCachedEntryImages(entry);
            }
            else
            {
                entry.Calls.Clear();
            }
        }
        _chatViewCacheLru.Clear();
        _chatViewCacheMap.Clear();
    }

    private void CacheAndUnloadChatGroupDetails(string groupKey)
    {
        var node = SelectedNode;
        if (node == null) return;

        ChatViewCacheEntry? entry = null;
        foreach (var call in AllKnownCalls())
        {
            if (call.IsDetailsLoaded &&
                !ReferenceEquals(call, SelectedApiCall) &&
                ChatKeyOf(call) == groupKey &&
                _apiCache.HasFullRecord(node.Id, call.CallId))
            {
                entry ??= GetOrCreateChatViewCacheEntry(node.Id, groupKey);
                if (entry.Calls.TryGetValue(call.CallId, out var prev))
                {
                    // Dispose any old bitmaps that are no longer referenced by the updated call images
                    var keep = new HashSet<Bitmap>(call.Images.Where(i => i.Preview != null).Select(i => i.Preview!), ReferenceEqualityComparer.Instance);
                    foreach (var oldImg in prev.Images)
                    {
                        if (oldImg.Preview != null && !keep.Contains(oldImg.Preview))
                            oldImg.Preview.Dispose();
                    }
                }

                entry.Calls[call.CallId] = new CachedCallDetails(
                    call.RequestJson,
                    call.ResponseJson,
                    call.ChatName,
                    call.ParsedTurns.ToArray(),
                    call.Images.ToArray());

                call.DetachDetailsWithoutDisposing();
            }
        }

        if (entry != null)
        {
            entry.IsActive = false;
        }
        else if (_chatViewCacheMap.TryGetValue(ChatViewCacheKey(node.Id, groupKey), out var existing))
        {
            existing.Value.IsActive = false;
        }
    }

    private bool TryRestoreCachedCallDetails(string nodeId, string groupKey, IReadOnlyList<RawApiCallItemModel> shown)
    {
        if (!_chatViewCacheMap.TryGetValue(ChatViewCacheKey(nodeId, groupKey), out var cachedNode))
            return false;

        var entry = cachedNode.Value;
        entry.IsActive = true;
        bool restoredAny = false;
        foreach (var call in shown)
        {
            if (call.IsDetailsLoaded) continue;
            if (entry.Calls.TryGetValue(call.CallId, out var cached))
            {
                call.RequestJson = cached.RequestJson;
                call.ResponseJson = cached.ResponseJson;
                call.ReplaceImages(cached.Images);
                call.ReplaceParsedTurns(cached.ParsedTurns);
                if (string.IsNullOrEmpty(call.ChatName) && !string.IsNullOrEmpty(cached.ChatName))
                    call.ChatName = cached.ChatName;
                call.IsDetailsLoaded = true;
                restoredAny = true;
            }
        }
        return restoredAny;
    }

    // ------------------------------------------------------------------ identity

    private static string ChatKey(long chatId) => "id:" + chatId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Chat a call belongs to: trace chat_id first, then an exact, unique registry title match.</summary>
    public string ChatKeyOf(RawApiCallItemModel call)
    {
        if (call.TraceContext?.HasChatId == true) return ChatKey(call.TraceContext.ChatId);
        string name = call.ChatName?.Trim() ?? string.Empty;
        if (name.Length == 0) return NoChatKey;
        if (_chatTitleToId.TryGetValue(name, out long id)) return ChatKey(id);
        if (_chatTitleToIdIgnoreCase.TryGetValue(name, out long idIgnoreCase)) return ChatKey(idIgnoreCase);
        return "name:" + name;
    }

    /// <summary>Authoritative display name for a call (registry title by chat_id when known).</summary>
    private void ResolveChatName(RawApiCallItemModel call)
    {
        if (call.TraceContext?.HasChatId == true && _chatRegistry.TryGetValue(call.TraceContext.ChatId, out var chat) &&
            !string.IsNullOrWhiteSpace(chat.Title) && call.ChatName != chat.Title)
            call.ChatName = chat.Title;
    }

    // ------------------------------------------------------------------ activation / live updates

    private void OnChatsViewActivityChanged()
    {
        if (!IsChatsViewActive) { CancelChatLoads(); return; }
        _ = RefreshChatsAsync();
        RebuildChatGroups();
        RebuildChatHistory(resetLimit: false);
        ChatHistoryReset?.Invoke();
    }

    /// <summary>Called after new API call summaries were added to <see cref="RecentApiCalls"/>.</summary>
    private void OnApiCallsAddedForChats(IReadOnlyList<RawApiCallItemModel> added)
    {
        foreach (var call in added) ResolveChatName(call);
        if (!IsChatsViewActive) return;
        // Coalesce bursts (spool loading delivers thousands of calls) into one refresh per second.
        _chatRefreshPending = true;
        _chatRefreshTimer ??= CreateChatRefreshTimer();
        if (!_chatRefreshTimer.IsEnabled) _chatRefreshTimer.Start();
    }

    private DispatcherTimer CreateChatRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_chatRefreshPending || !IsChatsViewActive) return;
            _chatRefreshPending = false;
            RebuildChatGroups();
            RebuildChatHistory(resetLimit: false);
        };
        return timer;
    }

    private void IngestChatTraceEvent(NoxChatTraceEvent trace)
    {
        if (trace.TraceContext?.HasChatId != true || string.IsNullOrEmpty(trace.EventId)) return;
        AddTraceRow(trace.TraceContext.ChatId, trace);
        if (IsChatsViewActive && SelectedChatGroup?.ChatId == trace.TraceContext.ChatId)
            OnApiCallsAddedForChats(Array.Empty<RawApiCallItemModel>());
    }

    private bool AddTraceRow(long chatId, NoxChatTraceEvent trace)
    {
        if (!_chatTraceRows.TryGetValue(chatId, out var rows)) _chatTraceRows[chatId] = rows = new();
        string key = "event:" + trace.EventId;
        if (rows.Any(r => r.Key == key)) return false;
        string content = trace.Content ?? string.Empty;
        if (trace.Attributes.Count > 0)
            content += (content.Length > 0 ? "  " : string.Empty) + string.Join("  ", trace.Attributes.Select(a => $"{a.Key}={a.Value}"));
        rows.Add(new ChatTraceRowModel
        {
            Key = key, TimestampUnixNano = trace.TimestampUnixNano, Kind = trace.Kind,
            TimestampText = FormatLocalTime(trace.TimestampUnixNano),
            Content = content.Length > 4000 ? content[..4000] + " …" : content,
        });
        if (rows.Count > MaxTraceEventsPerChat) rows.RemoveRange(0, rows.Count - MaxTraceEventsPerChat);
        return true;
    }

    [RelayCommand]
    private Task OpenCallInRawAsync(RawApiCallItemModel? call) =>
        call == null ? Task.CompletedTask : OpenChatCallInRawAsync(call.CallId);

    private void ResetChatsState()
    {
        CancelChatLoads();
        CancelChatContentSearch();
        ClearChatViewCache();
        _chatRegistry.Clear(); _chatTitleToId.Clear(); _chatTitleToIdIgnoreCase.Clear(); _chatGroupsByKey.Clear();
        _chatTraceRows.Clear(); _chatExtraCalls.Clear(); _chatHistoryMembers.Clear();
        _chatContentSearchMatchCallIds.Clear(); _chatContentSearchMatchEventKeys.Clear();
        ChatContentSearchQuery = string.Empty;
        ChatContentSearchStatus = string.Empty;
        IsChatContentSearchActive = false;
        _chatTraceSupported = null;
        ++_chatRegistryRevision;
        _chatHistory.ReplaceAll(Array.Empty<object>()); ChatGroups.Clear();
        SelectedChatGroup = null;
        ChatListStatus = "Open this tab to load chats.";
        ChatHistoryStatus = "Select a chat.";
    }

    private void CancelChatLoads()
    {
        _chatCts.Cancel(); _chatCts.Dispose(); _chatCts = new();
    }

    private void CancelChatContentSearch()
    {
        _chatSearchCts.Cancel(); _chatSearchCts.Dispose(); _chatSearchCts = new();
        IsChatContentSearching = false;
    }

    // ------------------------------------------------------------------ chat list

    [RelayCommand]
    public async Task RefreshChatsAsync()
    {
        await LoadChatRegistryAsync();
        RebuildChatGroups();
        RebuildChatHistory(resetLimit: false);
        if (SelectedChatGroup != null && SelectedChatGroup.Key != NoChatKey)
            await LoadChatTraceForGroupAsync(SelectedChatGroup, _chatCts.Token);
    }

    private async Task LoadChatRegistryAsync() => await LoadChatRegistryCoreAsync();

    private async Task<bool> LoadChatRegistryCoreAsync()
    {
        var node = SelectedNode;
        if (node == null) return false;
        var token = _historyCts.Token;
        int revision = ++_chatRegistryRevision;
        ChatListStatus = "Loading chat list…";
        try
        {
            var client = _nodeManager.GetClient(node);
            var chats = new Dictionary<long, NoxChatItem>();
            string cursor = string.Empty;
            for (int page = 0; page < 50; page++)
            {
                var response = await client.ListChatsAsync(new NoxListChatsRequest { NodeId = node.Id, Limit = 200, Cursor = cursor },
                    deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                if (token.IsCancellationRequested || SelectedNode != node || revision != _chatRegistryRevision) return false;
                foreach (var chat in response.Chats) chats[chat.ChatId] = chat;
                if (!response.PaginationSupported || response.NextCursor.Length == 0 || response.NextCursor == cursor) break;
                cursor = response.NextCursor;
            }
            if (SelectedNode != node || revision != _chatRegistryRevision) return false;
            _chatRegistry.Clear();
            foreach (var pair in chats) _chatRegistry[pair.Key] = pair.Value;
            _chatTitleToId.Clear();
            _chatTitleToIdIgnoreCase.Clear();
            foreach (var group in chats.Values.Where(c => !string.IsNullOrWhiteSpace(c.Title)).GroupBy(c => c.Title.Trim(), StringComparer.Ordinal))
                if (group.Count() == 1) _chatTitleToId[group.Key] = group.First().ChatId; // ambiguous titles never match
            foreach (var group in chats.Values.Where(c => !string.IsNullOrWhiteSpace(c.Title)).GroupBy(c => c.Title.Trim(), StringComparer.OrdinalIgnoreCase))
                if (group.Count() == 1) _chatTitleToIdIgnoreCase[group.Key] = group.First().ChatId;
            foreach (var call in AllKnownCalls()) ResolveChatName(call);
            RebuildChatGroups();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (SelectedNode == node && revision == _chatRegistryRevision)
                ChatListStatus = $"Chat list unavailable ({ex.Message}); showing chats seen in loaded API calls.";
            return false;
        }
    }

    private IEnumerable<RawApiCallItemModel> AllKnownCalls() =>
        _chatExtraCalls.Count == 0 ? RecentApiCalls : RecentApiCalls.Concat(_chatExtraCalls.Values.Where(c => !_seenApiCallIds.Contains(c.CallId)));

    public void RebuildChatGroups()
    {
        var stats = new Dictionary<string, (int Count, long Last, string Name)>(StringComparer.Ordinal);
        foreach (var call in AllKnownCalls())
        {
            string key = ChatKeyOf(call);
            stats.TryGetValue(key, out var s);
            stats[key] = (s.Count + 1, Math.Max(s.Last, call.TimestampUnixNano), s.Name ?? call.ChatName);
        }
        var keys = new HashSet<string>(stats.Keys, StringComparer.Ordinal);
        foreach (long id in _chatRegistry.Keys) keys.Add(ChatKey(id));
        foreach (long id in _chatTraceRows.Keys) keys.Add(ChatKey(id));

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            live.Add(key);
            stats.TryGetValue(key, out var s);
            long? chatId = key.StartsWith("id:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
            NoxChatItem? reg = chatId.HasValue && _chatRegistry.TryGetValue(chatId.Value, out var r) ? r : null;
            string title = reg != null && !string.IsNullOrWhiteSpace(reg.Title) ? reg.Title
                : key == NoChatKey ? "(no chat — background calls)"
                : key.StartsWith("name:", StringComparison.Ordinal) ? key[5..] + "  (unverified)"
                : !string.IsNullOrWhiteSpace(s.Name) ? s.Name : "Chat " + chatId;

            // Count reflects loaded calls, probe's retained WAL per-chat call index, or SQLite bot message count
            int callCount = s.Count;
            if (reg != null)
            {
                if (reg.ApiCallCount > callCount) callCount = (int)reg.ApiCallCount;
                else if (callCount == 0 && reg.BotMessageCount > 0) callCount = (int)reg.BotMessageCount;
            }

            // Timestamp at the top must reflect the final message/call sent by the agent (never passive group message arrivals)
            long last = s.Last;
            if (reg != null && reg.LastAgentCallUnixNano > last)
            {
                last = reg.LastAgentCallUnixNano;
            }
            if (chatId.HasValue && _chatTraceRows.TryGetValue(chatId.Value, out var rows) && rows.Count > 0)
            {
                last = Math.Max(last, rows[^1].TimestampUnixNano);
            }
            // Only fall back to SQLite LastActiveUnixSec when no nanosecond agent call/trace timestamp is known
            if (last == 0 && reg != null && reg.LastActiveUnixSec > 0)
            {
                last = reg.LastActiveUnixSec * 1_000_000_000L;
            }

            if (!_chatGroupsByKey.TryGetValue(key, out var group))
                _chatGroupsByKey[key] = group = new ChatGroupModel { Key = key, ChatId = chatId };
            group.Title = title;
            group.ChatType = reg?.ChatType ?? (key == NoChatKey ? "system" : string.Empty);
            group.CallCount = callCount;
            group.LastActiveUnixNano = last;
        }
        foreach (string stale in _chatGroupsByKey.Keys.Where(k => !live.Contains(k)).ToList()) _chatGroupsByKey.Remove(stale);
        int totalKnownCalls = AllKnownCalls().Count();
        ChatListStatus = $"{_chatGroupsByKey.Count(g => g.Key != NoChatKey):N0} chats · {totalKnownCalls:N0} API calls indexed" +
            (_chatRegistry.Count == 0 ? " · chat list not loaded" : string.Empty);
        ApplyChatGroupFilter();
    }

    private void ApplyChatGroupFilter()
    {
        lock (ChatGroups)
        {
            string q = ChatFilter.Trim();
            var desired = _chatGroupsByKey.Values
                .Where(g => q.Length == 0 || g.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || g.MetaLine.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.Key == NoChatKey) // background calls last
                .ThenByDescending(g => g.LastActiveUnixNano)
                .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = SelectedChatGroup;
            var wanted = new HashSet<ChatGroupModel>(desired);
            // Reordering makes the ListBox drop/re-set its selection; that must not reload the inspected chat.
            _suppressChatSelection = true;
            try
            {
                for (int i = ChatGroups.Count - 1; i >= 0; i--)
                    if (!wanted.Contains(ChatGroups[i])) ChatGroups.RemoveAt(i);
                for (int i = 0; i < desired.Count; i++)
                {
                    if (i < ChatGroups.Count && ReferenceEquals(ChatGroups[i], desired[i])) continue;
                    int old = ChatGroups.IndexOf(desired[i]);
                    if (old >= 0) ChatGroups.Move(old, i); else ChatGroups.Insert(i, desired[i]);
                }
                if (selected != null && wanted.Contains(selected)) SelectedChatGroup = selected;
            }
            finally { _suppressChatSelection = false; }
            if (selected != null && !wanted.Contains(selected)) SelectedChatGroup = null; // filtered out: normal change
        }
    }

    // ------------------------------------------------------------------ history of the selected chat

    /// <summary>Raised right before older calls are prepended, so the view can keep its scroll position.</summary>
    public event Action? ChatOlderPrepending;

    [RelayCommand]
    private void LoadOlderChat() => LoadOlderChatHistory();

    /// <summary>Extends the window upwards; returns false when everything is already shown.</summary>
    public bool LoadOlderChatHistory()
    {
        if (!ChatHasOlder) return false;
        ChatOlderPrepending?.Invoke();
        _chatVisibleLimit += ChatOlderStep;
        RebuildChatHistory(resetLimit: false);
        return true;
    }

    private void RebuildChatHistory(bool resetLimit)
    {
        if (resetLimit) _chatVisibleLimit = ChatInitialWindow;
        var group = SelectedChatGroup;
        var node = SelectedNode;
        if (group == null || node == null)
        {
            ChatHistory.Clear(); _chatHistoryMembers.Clear(); ChatHasOlder = false;
            ChatHistoryStatus = "Select a chat.";
            return;
        }
        var allChatCalls = AllKnownCalls().Where(c => ChatKeyOf(c) == group.Key)
            .OrderByDescending(c => c.TimestampUnixNano).ToList();

        bool filterBySearch = IsChatContentSearchActive && !string.IsNullOrWhiteSpace(ChatContentSearchQuery);
        var calls = filterBySearch
            ? allChatCalls.Where(c => _chatContentSearchMatchCallIds.Contains(c.CallId)).ToList()
            : allChatCalls;

        var shown = calls.Take(_chatVisibleLimit).ToList();
        ChatHasOlder = calls.Count > shown.Count;

        // Restore cached parsed turns & bitmaps on cache hit before detaching/loading
        TryRestoreCachedCallDetails(node.Id, group.Key, shown);

        // Unload details of calls in this chat that fell outside the visible window so memory remains Zstd-compressed
        var shownSet = new HashSet<RawApiCallItemModel>(shown, ReferenceEqualityComparer.Instance);
        foreach (var c in allChatCalls)
        {
            if (c.IsDetailsLoaded && !shownSet.Contains(c) && !ReferenceEquals(c, SelectedApiCall) &&
                _apiCache.HasFullRecord(node.Id, c.CallId))
            {
                c.UnloadDetails();
            }
        }

        var entries = new List<(long Ts, object Item)>(shown.Count);
        entries.AddRange(shown.Select(c => (c.TimestampUnixNano, (object)c)));
        int eventCount = 0;
        if (ChatShowTraceEvents && group.ChatId is long chatId && _chatTraceRows.TryGetValue(chatId, out var rows))
        {
            // Only events inside the time span of the visible calls (or all, when every call is shown).
            long floor = !filterBySearch && ChatHasOlder && shown.Count > 0 ? shown[^1].TimestampUnixNano : long.MinValue;
            foreach (var row in rows.Where(r => r.TimestampUnixNano >= floor))
            {
                if (filterBySearch && !_chatContentSearchMatchEventKeys.Contains(row.Key)) continue;
                entries.Add((row.TimestampUnixNano, row));
                eventCount++;
            }
        }
        // Chronological, like a chat: oldest at the top, newest at the bottom (the view opens at the bottom).
        var ordered = entries.OrderBy(e => e.Ts).Select(e => e.Item).ToList();
        SyncChatHistory(ordered, reset: resetLimit);

        string trace = _chatTraceSupported == false ? " · probe has no per-chat trace index (only loaded calls shown)" : string.Empty;
        if (filterBySearch)
        {
            ChatHistoryStatus = $"Search matches: {shown.Count:N0} of {allChatCalls.Count:N0} calls" + (eventCount > 0 ? $" · {eventCount:N0} trace events" : string.Empty) + trace;
        }
        else
        {
            ChatHistoryStatus = $"{shown.Count:N0} of {calls.Count:N0} calls" + (eventCount > 0 ? $" · {eventCount:N0} trace events" : string.Empty) + trace;
        }
        _ = LoadChatCallDetailsAsync(node, group.Key, shown, _chatCts.Token);
    }

    // ------------------------------------------------------------------ chat content search

    private void RunLocalChatContentFilter(string rawQuery)
    {
        _chatContentSearchMatchCallIds.Clear();
        _chatContentSearchMatchEventKeys.Clear();
        string q = NormalizeApiFilter(rawQuery);
        if (string.IsNullOrEmpty(q))
        {
            IsChatContentSearchActive = false;
            ChatContentSearchStatus = string.Empty;
            return;
        }

        IsChatContentSearchActive = true;
        var group = SelectedChatGroup;
        var node = SelectedNode;
        if (group == null || node == null) return;

        int uncachedCount = 0;
        foreach (var call in AllKnownCalls().Where(c => ChatKeyOf(c) == group.Key))
        {
            if (MatchesChatCallQuery(node.Id, call, q, out bool hadPayload))
            {
                _chatContentSearchMatchCallIds.Add(call.CallId);
            }
            else if (!hadPayload)
            {
                uncachedCount++;
            }
        }

        if (group.ChatId is long chatId && _chatTraceRows.TryGetValue(chatId, out var rows))
        {
            foreach (var row in rows)
            {
                if (FindLiteral(NormalizeApiFilter(row.Kind), q, false).Start >= 0 ||
                    FindLiteral(NormalizeApiFilter(row.Content), q, false).Start >= 0)
                {
                    _chatContentSearchMatchEventKeys.Add(row.Key);
                }
            }
        }

        int totalMatches = _chatContentSearchMatchCallIds.Count + _chatContentSearchMatchEventKeys.Count;
        ChatContentSearchStatus = uncachedCount > 0
            ? $"{totalMatches:N0} match(es) in cached memory ({uncachedCount:N0} uncached calls on probe — press Enter to search all)"
            : $"{totalMatches:N0} match(es) for \"{rawQuery.Trim()}\"";
    }

    private bool MatchesChatCallQuery(string nodeId, RawApiCallItemModel call, string normalizedQuery, out bool hadPayload)
    {
        hadPayload = false;
        if (MatchesApiFilter(call, normalizedQuery))
        {
            hadPayload = true;
            return true;
        }

        if (call.IsDetailsLoaded && (!string.IsNullOrEmpty(call.RequestJson) || !string.IsNullOrEmpty(call.ResponseJson)))
        {
            hadPayload = true;
            if (FindLiteral(NormalizeApiFilter(call.RequestJson), normalizedQuery, false).Start >= 0 ||
                FindLiteral(NormalizeApiFilter(call.ResponseJson), normalizedQuery, false).Start >= 0)
                return true;
            return false;
        }

        var record = _apiCache.GetRecord(nodeId, call.CallId);
        if (record != null && (!string.IsNullOrEmpty(record.RawRequestJson) || !string.IsNullOrEmpty(record.RawResponseJson)))
        {
            hadPayload = true;
            string reqText = FormatJsonForDisplay(record.RawRequestJson);
            string respText = FormatJsonForDisplay(record.RawResponseJson);
            if (FindLiteral(NormalizeApiFilter(reqText), normalizedQuery, false).Start >= 0 ||
                FindLiteral(NormalizeApiFilter(respText), normalizedQuery, false).Start >= 0)
                return true;
        }

        return false;
    }

    [RelayCommand]
    public async Task SearchChatHistoryAsync()
    {
        var group = SelectedChatGroup;
        var node = SelectedNode;
        string rawQuery = ChatContentSearchQuery?.Trim() ?? string.Empty;
        if (group == null || node == null || string.IsNullOrEmpty(rawQuery))
        {
            ClearChatHistorySearch();
            return;
        }

        CancelChatContentSearch();
        var token = _chatSearchCts.Token;
        IsChatContentSearching = true;
        IsChatContentSearchActive = true;

        try
        {
            // Ensure all retained trace summaries for this chat are loaded first
            if (group.Key != NoChatKey)
            {
                await LoadChatTraceForGroupAsync(group, token);
            }
            if (token.IsCancellationRequested || SelectedNode != node || SelectedChatGroup != group) return;

            string q = NormalizeApiFilter(rawQuery);
            var chatCalls = AllKnownCalls().Where(c => ChatKeyOf(c) == group.Key)
                .OrderByDescending(c => c.TimestampUnixNano).ToList();

            _chatContentSearchMatchCallIds.Clear();
            _chatContentSearchMatchEventKeys.Clear();

            if (group.ChatId is long chatId && _chatTraceRows.TryGetValue(chatId, out var rows))
            {
                foreach (var row in rows)
                {
                    if (FindLiteral(NormalizeApiFilter(row.Kind), q, false).Start >= 0 ||
                        FindLiteral(NormalizeApiFilter(row.Content), q, false).Start >= 0)
                    {
                        _chatContentSearchMatchEventKeys.Add(row.Key);
                    }
                }
            }

            int scanned = 0;
            var client = _nodeManager.GetClient(node);
            foreach (var call in chatCalls)
            {
                token.ThrowIfCancellationRequested();
                scanned++;
                if (MatchesChatCallQuery(node.Id, call, q, out bool hadPayload))
                {
                    _chatContentSearchMatchCallIds.Add(call.CallId);
                    continue;
                }

                if (!hadPayload)
                {
                    try
                    {
                        var fullRec = await Task.Run(async () =>
                            await client.GetApiCallDetailAsync(
                                new NoxApiCallDetailRequest { NodeId = node.Id, CallId = call.CallId },
                                deadline: DateTime.UtcNow.AddSeconds(10),
                                cancellationToken: token).ResponseAsync.ConfigureAwait(false), token);
                        if (fullRec != null && !string.IsNullOrEmpty(fullRec.CallId))
                        {
                            _apiCache.PutFullRecord(node.Id, fullRec);
                            string reqText = FormatJsonForDisplay(fullRec.RawRequestJson);
                            string respText = FormatJsonForDisplay(fullRec.RawResponseJson);
                            if (FindLiteral(NormalizeApiFilter(reqText), q, false).Start >= 0 ||
                                FindLiteral(NormalizeApiFilter(respText), q, false).Start >= 0 ||
                                FindLiteral(NormalizeApiFilter(fullRec.ErrorMessage), q, false).Start >= 0)
                            {
                                _chatContentSearchMatchCallIds.Add(call.CallId);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                if (scanned % 8 == 0)
                {
                    int currentMatches = _chatContentSearchMatchCallIds.Count + _chatContentSearchMatchEventKeys.Count;
                    ChatContentSearchStatus = $"Searching {scanned:N0}/{chatCalls.Count:N0} calls ({currentMatches:N0} matches)…";
                    RebuildChatHistory(resetLimit: false);
                }
            }

            int finalMatches = _chatContentSearchMatchCallIds.Count + _chatContentSearchMatchEventKeys.Count;
            ChatContentSearchStatus = $"Found {finalMatches:N0} match(es) across {chatCalls.Count:N0} calls for \"{rawQuery}\".";
            RebuildChatHistory(resetLimit: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsChatContentSearching = false;
            }
        }
    }

    [RelayCommand]
    public void ClearChatHistorySearch()
    {
        CancelChatContentSearch();
        ChatContentSearchQuery = string.Empty;
        _chatContentSearchMatchCallIds.Clear();
        _chatContentSearchMatchEventKeys.Clear();
        IsChatContentSearchActive = false;
        ChatContentSearchStatus = string.Empty;
        RebuildChatHistory(resetLimit: false);
    }

    /// <summary>
    /// Batched update of ChatHistory so switching chats fires 1 Reset, prepending older items fires 1
    /// range Add at index 0, and appending live items fires 1 range Add at the end.
    /// </summary>
    private void SyncChatHistory(List<object> desired, bool reset = false)
    {
        if (reset || _chatHistory.Count == 0 || desired.Count == 0)
        {
            _chatHistoryMembers.Clear();
            foreach (var item in desired) _chatHistoryMembers.Add(item);
            _chatHistory.ReplaceAll(desired);
            return;
        }

        // Fast path 1: pure prepend at index 0 (LoadOlderChatHistory) — existing items are a contiguous suffix of desired
        if (desired.Count > _chatHistory.Count)
        {
            int prependCount = desired.Count - _chatHistory.Count;
            bool isPurePrepend = true;
            for (int i = 0; i < _chatHistory.Count; i++)
            {
                if (!ReferenceEquals(_chatHistory[i], desired[prependCount + i]))
                {
                    isPurePrepend = false;
                    break;
                }
            }
            if (isPurePrepend)
            {
                var prefix = desired.GetRange(0, prependCount);
                foreach (var item in prefix) _chatHistoryMembers.Add(item);
                _chatHistory.InsertRange(0, prefix);
                return;
            }

            // Fast path 2: pure append at the end (live arrivals) — existing items are a contiguous prefix of desired
            bool isPureAppend = true;
            for (int i = 0; i < _chatHistory.Count; i++)
            {
                if (!ReferenceEquals(_chatHistory[i], desired[i]))
                {
                    isPureAppend = false;
                    break;
                }
            }
            if (isPureAppend)
            {
                var suffix = desired.GetRange(_chatHistory.Count, desired.Count - _chatHistory.Count);
                foreach (var item in suffix) _chatHistoryMembers.Add(item);
                _chatHistory.AddRange(suffix);
                return;
            }
        }

        // Check if already identical
        if (desired.Count == _chatHistory.Count)
        {
            bool identical = true;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(_chatHistory[i], desired[i])) { identical = false; break; }
            }
            if (identical) return;
        }

        // If the diff is large (e.g. switching chats without resetLimit or toggling search/trace), bulk replace in 1 pass
        var wanted = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        int changes = 0;
        for (int i = 0; i < _chatHistory.Count; i++)
            if (!wanted.Contains(_chatHistory[i])) changes++;
        for (int i = 0; i < desired.Count; i++)
            if (!_chatHistoryMembers.Contains(desired[i])) changes++;
        if (changes > 16)
        {
            _chatHistoryMembers.Clear();
            foreach (var item in desired) _chatHistoryMembers.Add(item);
            _chatHistory.ReplaceAll(desired);
            return;
        }

        for (int i = _chatHistory.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_chatHistory[i])) { _chatHistoryMembers.Remove(_chatHistory[i]); _chatHistory.RemoveAt(i); }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < _chatHistory.Count && ReferenceEquals(_chatHistory[i], desired[i])) continue;
            if (_chatHistoryMembers.Contains(desired[i]))
            {
                int old = _chatHistory.IndexOf(desired[i]);
                if (old >= 0) { _chatHistory.Move(old, i); continue; }
            }
            _chatHistory.Insert(i, desired[i]);
            _chatHistoryMembers.Add(desired[i]);
        }
    }

    private static Task RunOnUiAsync(Action action, DispatcherPriority priority = default)
    {
        if (Dispatcher.UIThread.CheckAccess() || Avalonia.Application.Current == null)
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action, priority).GetTask();
    }

    /// <summary>
    /// Prefetches and parses full payloads bottom-up (newest first, matching the bottom-anchored chat view).
    /// Wave 0 decodes/fetches the newest calls visible at the bottom immediately with bounded parallelism;
    /// older calls follow in coalesced waves off the UI thread. Stale or canceled results are discarded.
    /// </summary>
    private async Task LoadChatCallDetailsAsync(NoxNodeModel node, string groupKey, List<RawApiCallItemModel> calls, CancellationToken token)
    {
        // `calls` is newest-first; skip anything already loaded or being fetched by another refresh
        var pending = calls.Where(c => !c.IsDetailsLoaded && _chatDetailInflight.TryAdd(c.CallId, 0)).ToList();
        if (pending.Count == 0) return;

        var readyQueue = new System.Collections.Concurrent.ConcurrentQueue<(RawApiCallItemModel Target, RawApiCallItemModel Prepared)>();

        async Task FlushReadyBatchAsync(DispatcherPriority priority)
        {
            if (readyQueue.IsEmpty) return;
            var batch = new List<(RawApiCallItemModel Target, RawApiCallItemModel Prepared)>();
            while (readyQueue.TryDequeue(out var pair))
            {
                batch.Add(pair);
            }
            if (batch.Count == 0) return;

            await RunOnUiAsync(() =>
            {
                bool stale = token.IsCancellationRequested || SelectedNode != node || SelectedChatGroup?.Key != groupKey;
                foreach (var (target, prepared) in batch)
                {
                    if (stale || target.IsDetailsLoaded)
                    {
                        DisposeImages(prepared);
                        continue;
                    }
                    ApplyPreparedDetails(target, prepared);
                }
            }, priority).ConfigureAwait(false);
        }

        int offset = 0;
        int waveIndex = 0;
        try
        {
            while (offset < pending.Count)
            {
                if (token.IsCancellationRequested)
                {
                    for (int j = offset; j < pending.Count; j++)
                        _chatDetailInflight.TryRemove(pending[j].CallId, out _);
                    break;
                }

                int waveSize = waveIndex == 0 ? ChatFirstDetailWaveSize : ChatSubsequentDetailWaveSize;
                int count = Math.Min(waveSize, pending.Count - offset);
                var wave = pending.GetRange(offset, count);
                offset += count;

                var cachedWave = new List<(RawApiCallItemModel Call, NoxRawApiCallRecord Record)>(count);
                var remoteWave = new List<RawApiCallItemModel>(count);

                foreach (var call in wave)
                {
                    var cached = _apiCache.GetRecord(node.Id, call.CallId);
                    if (cached != null && (!string.IsNullOrWhiteSpace(cached.RawRequestJson) || !string.IsNullOrWhiteSpace(cached.RawResponseJson)))
                    {
                        cachedWave.Add((call, cached));
                    }
                    else
                    {
                        remoteWave.Add(call);
                    }
                }

                Task cachedTask = cachedWave.Count == 0
                    ? Task.CompletedTask
                    : Task.Run(() =>
                    {
                        var parallelOptions = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)),
                            CancellationToken = CancellationToken.None
                        };
                        Parallel.ForEach(cachedWave, parallelOptions, item =>
                        {
                            try
                            {
                                if (token.IsCancellationRequested || item.Call.IsDetailsLoaded) return;
                                var prepared = PrepareDetails(item.Call, item.Record, token);
                                readyQueue.Enqueue((item.Call, prepared));
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception) { }
                            finally
                            {
                                _chatDetailInflight.TryRemove(item.Call.CallId, out _);
                            }
                        });
                    }, CancellationToken.None);

                Task remoteTask = remoteWave.Count == 0
                    ? Task.CompletedTask
                    : Task.WhenAll(remoteWave.Select(call => Task.Run(async () =>
                    {
                        try { await _chatDetailSlots.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { _chatDetailInflight.TryRemove(call.CallId, out _); return; }
                        try
                        {
                            if (token.IsCancellationRequested || call.IsDetailsLoaded) return;
                            var record = _apiCache.GetRecord(node.Id, call.CallId);
                            if (record == null || (string.IsNullOrWhiteSpace(record.RawRequestJson) && string.IsNullOrWhiteSpace(record.RawResponseJson)))
                            {
                                record = await _nodeManager.GetClient(node).GetApiCallDetailAsync(
                                    new NoxApiCallDetailRequest { NodeId = node.Id, CallId = call.CallId }, cancellationToken: token).ResponseAsync.ConfigureAwait(false);
                                if (record == null || record.CallId != call.CallId) return;
                                _apiCache.PutFullRecord(node.Id, record);
                            }
                            var prepared = PrepareDetails(call, record, token);
                            readyQueue.Enqueue((call, prepared));
                        }
                        catch (OperationCanceledException) { }
                        catch (RpcException) { /* expired from the probe spool: the summary row stays visible */ }
                        catch (Exception) { }
                        finally { _chatDetailSlots.Release(); _chatDetailInflight.TryRemove(call.CallId, out _); }
                    }, CancellationToken.None)));

                try { await Task.WhenAll(cachedTask, remoteTask).ConfigureAwait(false); } catch { }
                var priority = waveIndex == 0 ? DispatcherPriority.Normal : DispatcherPriority.Background;
                await FlushReadyBatchAsync(priority).ConfigureAwait(false);
                waveIndex++;
            }
        }
        finally
        {
            for (int j = offset; j < pending.Count; j++)
                _chatDetailInflight.TryRemove(pending[j].CallId, out _);
            while (readyQueue.TryDequeue(out var remaining)) DisposeImages(remaining.Prepared);
        }
    }

    /// <summary>Formats JSON, decodes images and parses turns on a detached copy (safe off the UI thread).</summary>
    internal static RawApiCallItemModel PrepareDetails(RawApiCallItemModel call, NoxRawApiCallRecord record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var copy = new RawApiCallItemModel
        {
            CallId = call.CallId, Caller = call.Caller, Model = call.Model, ChatName = call.ChatName,
            TimestampText = call.TimestampText, TimestampUnixNano = call.TimestampUnixNano, DurationMs = call.DurationMs,
            PromptTokens = call.PromptTokens, CompletionTokens = call.CompletionTokens, CachedTokens = call.CachedTokens,
            BaseUrl = call.BaseUrl, Temperature = call.Temperature, ErrorMessage = call.ErrorMessage,
            TraceContext = call.TraceContext,
        };
        PopulateItemDetails(copy, record);
        token.ThrowIfCancellationRequested();
        return copy;
    }

    internal static void ApplyPreparedDetails(RawApiCallItemModel target, RawApiCallItemModel prepared)
    {
        target.RequestJson = prepared.RequestJson;
        target.ResponseJson = prepared.ResponseJson;
        target.ReplaceImages(prepared.Images.ToArray());
        target.ReplaceParsedTurns(prepared.ParsedTurns.ToArray());
        if (string.IsNullOrEmpty(target.ChatName) && !string.IsNullOrEmpty(prepared.ChatName)) target.ChatName = prepared.ChatName;
        target.IsDetailsLoaded = true;
    }

    private static void DisposeImages(RawApiCallItemModel prepared)
    {
        foreach (var img in prepared.Images) img.Preview?.Dispose();
    }

    private Task<bool> LoadChatTraceForGroupAsync(ChatGroupModel group, CancellationToken token)
    {
        string rawTitle = group.Key.StartsWith("name:", StringComparison.Ordinal) ? group.Key[5..] : group.Title;
        return LoadChatTraceCoreAsync(group.Key, group.ChatId, rawTitle, token);
    }

    /// <summary>Pulls the chat's full retained history (metadata only) from the probe's per-chat trace index.</summary>
    private Task<bool> LoadChatTraceAsync(long chatId, CancellationToken token)
    {
        _chatRegistry.TryGetValue(chatId, out var reg);
        return LoadChatTraceCoreAsync(ChatKey(chatId), chatId, reg?.Title ?? string.Empty, token);
    }

    private async Task<bool> LoadChatTraceCoreAsync(string groupKey, long? chatId, string chatTitle, CancellationToken token)
    {
        var node = SelectedNode;
        if (node == null) return false;
        if (_chatTraceSupported == false) return true;
        try
        {
            var client = _nodeManager.GetClient(node);
            string cursor = string.Empty;
            bool pendingUiFlush = false;
            for (int page = 0; page < 50 && !token.IsCancellationRequested; page++)
            {
                var req = new NoxChatTraceRequest
                {
                    NodeId = node.Id,
                    Limit = 200,
                    Cursor = cursor,
                    ChatName = chatTitle ?? string.Empty
                };
                if (chatId.HasValue) req.ChatId = chatId.Value;

                var response = await client.QueryChatTraceAsync(req, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                if (token.IsCancellationRequested || SelectedNode != node) return false;
                _chatTraceSupported = true;

                // Build call summary models and cache records off the UI thread
                List<RawApiCallItemModel>? builtModels = null;
                if (response.Calls.Count > 0)
                {
                    var unseen = new List<NoxRawApiCallRecord>(response.Calls.Count);
                    foreach (var record in response.Calls)
                    {
                        if (string.IsNullOrEmpty(record.CallId) || _seenApiCallIds.Contains(record.CallId) || _chatExtraCalls.ContainsKey(record.CallId))
                            continue;
                        unseen.Add(record);
                    }
                    if (unseen.Count > 0)
                    {
                        builtModels = await Task.Run(() =>
                        {
                            var models = new List<RawApiCallItemModel>(unseen.Count);
                            foreach (var record in unseen)
                            {
                                token.ThrowIfCancellationRequested();
                                models.Add(BuildApiCallItemModel(record));
                            }
                            _apiCache.PutRecords(node.Id, unseen);
                            _apiCache.FlushPending(node.Id);
                            return models;
                        }, token);
                    }
                }

                if (token.IsCancellationRequested || SelectedNode != node) return false;

                bool pageChanged = false;
                await RunOnUiAsync(() =>
                {
                    if (token.IsCancellationRequested || SelectedNode != node) return;
                    if (builtModels != null)
                    {
                        foreach (var item in builtModels)
                        {
                            if (_seenApiCallIds.Contains(item.CallId) || _chatExtraCalls.ContainsKey(item.CallId)) continue;
                            ResolveChatName(item);
                            _chatExtraCalls[item.CallId] = item;
                            pageChanged = true;
                        }
                    }
                    foreach (var ev in response.Events)
                        if (ev.TraceContext?.HasChatId == true && !string.IsNullOrEmpty(ev.EventId))
                            pageChanged |= AddTraceRow(ev.TraceContext.ChatId, ev);

                    if (pageChanged) pendingUiFlush = true;

                    // Publish page 0 immediately so the user sees history without waiting for remaining pages;
                    // coalesce subsequent pages every 4 pages.
                    if (pendingUiFlush && (page == 0 || page % 4 == 3) && SelectedChatGroup?.Key == groupKey)
                    {
                        pendingUiFlush = false;
                        if (IsChatContentSearchActive && !string.IsNullOrWhiteSpace(ChatContentSearchQuery))
                        {
                            RunLocalChatContentFilter(ChatContentSearchQuery);
                        }
                        RebuildChatGroups();
                        RebuildChatHistory(resetLimit: false);
                    }
                });

                if (response.NextCursor.Length == 0 || response.NextCursor == cursor) break;
                cursor = response.NextCursor;
            }

            if (token.IsCancellationRequested || SelectedNode != node) return false;

            await RunOnUiAsync(() =>
            {
                if (token.IsCancellationRequested || SelectedNode != node) return;
                var cacheEntry = GetOrCreateChatViewCacheEntry(node.Id, groupKey);
                cacheEntry.TraceLoaded = true;
                cacheEntry.IsActive = SelectedChatGroup?.Key == groupKey;

                if (pendingUiFlush && SelectedChatGroup?.Key == groupKey)
                {
                    if (IsChatContentSearchActive && !string.IsNullOrWhiteSpace(ChatContentSearchQuery))
                    {
                        RunLocalChatContentFilter(ChatContentSearchQuery);
                    }
                    RebuildChatGroups();
                    RebuildChatHistory(resetLimit: false);
                }
            });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            if (token.IsCancellationRequested || SelectedNode != node) return false;
            _chatTraceSupported = false;
            await RunOnUiAsync(() =>
            {
                if (!token.IsCancellationRequested && SelectedNode == node && SelectedChatGroup?.Key == groupKey)
                    RebuildChatHistory(resetLimit: false);
            });
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (RpcException) { return false; }
        return !token.IsCancellationRequested && SelectedNode == node;
    }

    private static string FormatLocalTime(long unixNano) => unixNano > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(unixNano / 1_000_000).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        : string.Empty;
}
