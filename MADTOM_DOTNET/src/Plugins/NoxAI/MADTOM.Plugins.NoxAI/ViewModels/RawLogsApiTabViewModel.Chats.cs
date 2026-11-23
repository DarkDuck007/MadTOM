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
    private const int ChatInitialWindow = 150; // newest calls shown when a chat opens
    private const int ChatOlderStep = 100;     // added at the top when scrolling near it
    private const int MaxTraceEventsPerChat = 2000;

    private readonly Dictionary<long, NoxChatItem> _chatRegistry = new();
    private readonly Dictionary<string, long> _chatTitleToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _chatTitleToIdIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChatGroupModel> _chatGroupsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<ChatTraceRowModel>> _chatTraceRows = new();
    private readonly Dictionary<string, RawApiCallItemModel> _chatExtraCalls = new(StringComparer.Ordinal);
    private readonly HashSet<object> _chatHistoryMembers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _chatContentSearchMatchCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chatContentSearchMatchEventKeys = new(StringComparer.Ordinal);
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

    public ObservableCollection<ChatGroupModel> ChatGroups { get; } = new();
    public ObservableCollection<object> ChatHistory { get; } = new();

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

        // Unload uncompressed details from the previous chat's calls so memory stays Zstd-compressed in _apiCache
        if (oldValue != null && !ReferenceEquals(oldValue, newValue))
        {
            UnloadChatGroupDetails(oldValue.Key);
        }

        RebuildChatHistory(resetLimit: true);
        ChatHistoryReset?.Invoke();
        if (newValue != null && newValue.Key != NoChatKey)
        {
            _ = LoadChatTraceForGroupAsync(newValue, _chatCts.Token);
        }
    }

    private void UnloadChatGroupDetails(string groupKey)
    {
        var node = SelectedNode;
        if (node == null) return;
        foreach (var call in AllKnownCalls())
        {
            if (call.IsDetailsLoaded &&
                !ReferenceEquals(call, SelectedApiCall) &&
                ChatKeyOf(call) == groupKey &&
                _apiCache.HasFullRecord(node.Id, call.CallId))
            {
                call.UnloadDetails();
            }
        }
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
        _chatRegistry.Clear(); _chatTitleToId.Clear(); _chatTitleToIdIgnoreCase.Clear(); _chatGroupsByKey.Clear();
        _chatTraceRows.Clear(); _chatExtraCalls.Clear(); _chatHistoryMembers.Clear();
        _chatContentSearchMatchCallIds.Clear(); _chatContentSearchMatchEventKeys.Clear();
        ChatContentSearchQuery = string.Empty;
        ChatContentSearchStatus = string.Empty;
        IsChatContentSearchActive = false;
        _chatTraceSupported = null;
        ++_chatRegistryRevision;
        ChatHistory.Clear(); ChatGroups.Clear();
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
        SyncChatHistory(ordered);

        string trace = _chatTraceSupported == false ? " · probe has no per-chat trace index (only loaded calls shown)" : string.Empty;
        if (filterBySearch)
        {
            ChatHistoryStatus = $"Search matches: {shown.Count:N0} of {allChatCalls.Count:N0} calls" + (eventCount > 0 ? $" · {eventCount:N0} trace events" : string.Empty) + trace;
        }
        else
        {
            ChatHistoryStatus = $"{shown.Count:N0} of {calls.Count:N0} calls" + (eventCount > 0 ? $" · {eventCount:N0} trace events" : string.Empty) + trace;
        }
        _ = LoadChatCallDetailsAsync(node, shown, _chatCts.Token);
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

    /// <summary>Minimal in-place update so live arrivals don't reset scroll position or realized containers.</summary>
    private void SyncChatHistory(List<object> desired)
    {
        var wanted = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        for (int i = ChatHistory.Count - 1; i >= 0; i--)
            if (!wanted.Contains(ChatHistory[i])) { _chatHistoryMembers.Remove(ChatHistory[i]); ChatHistory.RemoveAt(i); }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < ChatHistory.Count && ReferenceEquals(ChatHistory[i], desired[i])) continue;
            if (_chatHistoryMembers.Contains(desired[i]))
            {
                int old = ChatHistory.IndexOf(desired[i]);
                if (old >= 0) { ChatHistory.Move(old, i); continue; }
            }
            ChatHistory.Insert(i, desired[i]);
            _chatHistoryMembers.Add(desired[i]);
        }
    }

    /// <summary>
    /// Prefetches and parses full payloads for the active window (newest first, since the view opens at the
    /// bottom). Records already in the Zstd in-memory cache are decoded and applied in one coalesced batch;
    /// remote fetches are applied in coalesced batches to eliminate scrollbar layout jitter.
    /// </summary>
    private async Task LoadChatCallDetailsAsync(NoxNodeModel node, List<RawApiCallItemModel> calls, CancellationToken token)
    {
        // `calls` is newest-first; skip anything another refresh is already fetching
        var pending = calls.Where(c => !c.IsDetailsLoaded && _chatDetailInflight.TryAdd(c.CallId, 0)).ToList();
        if (pending.Count == 0) return;

        var readyQueue = new System.Collections.Concurrent.ConcurrentQueue<(RawApiCallItemModel Target, RawApiCallItemModel Prepared)>();

        async Task FlushReadyBatchAsync()
        {
            if (readyQueue.IsEmpty) return;
            var batch = new List<(RawApiCallItemModel Target, RawApiCallItemModel Prepared)>();
            while (readyQueue.TryDequeue(out var pair))
            {
                batch.Add(pair);
            }
            if (batch.Count == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var (target, prepared) in batch)
                {
                    if (token.IsCancellationRequested || SelectedNode != node || target.IsDetailsLoaded)
                    {
                        DisposeImages(prepared);
                        continue;
                    }
                    ApplyPreparedDetails(target, prepared);
                }
            }, DispatcherPriority.Background);
        }

        // Fast pass: decode any records already present in client Zstd memory cache (_apiCache) in one batch
        var remotePending = new List<RawApiCallItemModel>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var call in pending)
                {
                    if (token.IsCancellationRequested)
                    {
                        _chatDetailInflight.TryRemove(call.CallId, out _);
                        continue;
                    }
                    var cached = _apiCache.GetRecord(node.Id, call.CallId);
                    if (cached != null && (!string.IsNullOrWhiteSpace(cached.RawRequestJson) || !string.IsNullOrWhiteSpace(cached.RawResponseJson)))
                    {
                        try
                        {
                            var prepared = PrepareDetails(call, cached, token);
                            readyQueue.Enqueue((call, prepared));
                        }
                        finally
                        {
                            _chatDetailInflight.TryRemove(call.CallId, out _);
                        }
                    }
                    else
                    {
                        remotePending.Add(call);
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);

            await FlushReadyBatchAsync().ConfigureAwait(false);
        }
        catch
        {
            foreach (var c in pending) _chatDetailInflight.TryRemove(c.CallId, out _);
            while (readyQueue.TryDequeue(out var leftover)) DisposeImages(leftover.Prepared);
            return;
        }

        if (remotePending.Count == 0 || token.IsCancellationRequested)
        {
            foreach (var c in remotePending) _chatDetailInflight.TryRemove(c.CallId, out _);
            return;
        }

        // Process remote calls in small newest-first waves (8 calls per wave) so each wave updates the UI in a single layout pass
        const int waveSize = 8;
        for (int offset = 0; offset < remotePending.Count; offset += waveSize)
        {
            if (token.IsCancellationRequested)
            {
                for (int j = offset; j < remotePending.Count; j++)
                    _chatDetailInflight.TryRemove(remotePending[j].CallId, out _);
                break;
            }

            var wave = remotePending.Skip(offset).Take(waveSize).ToList();
            var tasks = wave.Select(call => Task.Run(async () =>
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
            }, CancellationToken.None));

            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            await FlushReadyBatchAsync().ConfigureAwait(false);
        }

        while (readyQueue.TryDequeue(out var remaining)) DisposeImages(remaining.Prepared);
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
            RequestJson = FormatJsonForDisplay(record.RawRequestJson),
            ResponseJson = FormatJsonForDisplay(record.RawResponseJson),
        };
        PopulateItemDetails(copy, record);
        token.ThrowIfCancellationRequested();
        return copy;
    }

    internal static void ApplyPreparedDetails(RawApiCallItemModel target, RawApiCallItemModel prepared)
    {
        target.RequestJson = prepared.RequestJson;
        target.ResponseJson = prepared.ResponseJson;
        target.Images.Clear();
        foreach (var img in prepared.Images) target.Images.Add(img);
        target.HasImages = target.Images.Count > 0;
        target.ParsedTurns.Clear();
        foreach (var turn in prepared.ParsedTurns) target.ParsedTurns.Add(turn);
        target.HasParsedTurns = target.ParsedTurns.Count > 0;
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
            bool changed = false;
            var newRecordsToCache = new List<NoxRawApiCallRecord>();
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
                foreach (var record in response.Calls)
                {
                    if (string.IsNullOrEmpty(record.CallId) || _seenApiCallIds.Contains(record.CallId) || _chatExtraCalls.ContainsKey(record.CallId)) continue;
                    var item = BuildApiCallItemModel(record);
                    ResolveChatName(item);
                    _chatExtraCalls[record.CallId] = item;
                    newRecordsToCache.Add(record);
                    changed = true;
                }
                foreach (var ev in response.Events)
                    if (ev.TraceContext?.HasChatId == true && !string.IsNullOrEmpty(ev.EventId))
                        changed |= AddTraceRow(ev.TraceContext.ChatId, ev);
                if (response.NextCursor.Length == 0 || response.NextCursor == cursor) break;
                cursor = response.NextCursor;
            }
            if (newRecordsToCache.Count > 0)
            {
                _apiCache.PutRecords(node.Id, newRecordsToCache);
                _apiCache.FlushPending(node.Id);
            }
            if (changed && SelectedChatGroup?.Key == groupKey)
            {
                if (IsChatContentSearchActive && !string.IsNullOrWhiteSpace(ChatContentSearchQuery))
                {
                    RunLocalChatContentFilter(ChatContentSearchQuery);
                }
                RebuildChatGroups();
                RebuildChatHistory(resetLimit: false);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            if (token.IsCancellationRequested || SelectedNode != node) return false;
            _chatTraceSupported = false;
            if (SelectedChatGroup?.Key == groupKey) RebuildChatHistory(resetLimit: false);
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
