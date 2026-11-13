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
    public string Key { get; init; } = string.Empty;
    public long? ChatId { get; init; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MetaLine))] private string _title = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(MetaLine))] private string _chatType = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CountText))] private int _callCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(LastActiveText))] private long _lastActiveUnixNano;

    public string CountText => CallCount.ToString("N0", CultureInfo.InvariantCulture);
    public string MetaLine
    {
        get
        {
            string id = ChatId.HasValue ? ChatId.Value.ToString(CultureInfo.InvariantCulture)
                : Key == RawLogsApiTabViewModel.NoChatKey ? "no chat id" : "name match only";
            string type = string.IsNullOrEmpty(ChatType) ? "?" : ChatType;
            return $"{type} · {id}";
        }
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
    private readonly Dictionary<string, ChatGroupModel> _chatGroupsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, List<ChatTraceRowModel>> _chatTraceRows = new();
    private readonly Dictionary<string, RawApiCallItemModel> _chatExtraCalls = new(StringComparer.Ordinal);
    private readonly HashSet<object> _chatHistoryMembers = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _chatDetailSlots = new(4, 4);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _chatDetailInflight = new(StringComparer.Ordinal);
    private CancellationTokenSource _chatCts = new();
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

    private bool IsChatsViewActive => IsTabActive && SelectedSubViewIndex == 2;

    /// <summary>Raised when a chat is (re)opened: the view jumps to the bottom (newest call).</summary>
    public event Action? ChatHistoryReset;

    partial void OnChatFilterChanged(string value) => ApplyChatGroupFilter();
    partial void OnChatShowTraceEventsChanged(bool value) => RebuildChatHistory(resetLimit: false);
    partial void OnSelectedChatGroupChanged(ChatGroupModel? value)
    {
        if (_suppressChatSelection) return;
        CancelChatLoads();
        RebuildChatHistory(resetLimit: true);
        ChatHistoryReset?.Invoke();
        if (value?.ChatId is long chatId) _ = LoadChatTraceAsync(chatId, _chatCts.Token);
    }

    // ------------------------------------------------------------------ identity

    private static string ChatKey(long chatId) => "id:" + chatId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Chat a call belongs to: trace chat_id first, then an exact, unique registry title match.</summary>
    public string ChatKeyOf(RawApiCallItemModel call)
    {
        if (call.TraceContext?.HasChatId == true) return ChatKey(call.TraceContext.ChatId);
        string name = call.ChatName?.Trim() ?? string.Empty;
        if (name.Length == 0) return NoChatKey;
        return _chatTitleToId.TryGetValue(name, out long id) ? ChatKey(id) : "name:" + name;
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
        _chatRegistry.Clear(); _chatTitleToId.Clear(); _chatGroupsByKey.Clear();
        _chatTraceRows.Clear(); _chatExtraCalls.Clear(); _chatHistoryMembers.Clear();
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

    // ------------------------------------------------------------------ chat list

    [RelayCommand]
    public async Task RefreshChatsAsync()
    {
        await LoadChatRegistryAsync();
        RebuildChatGroups();
        RebuildChatHistory(resetLimit: false);
        if (SelectedChatGroup?.ChatId is long chatId)
            await LoadChatTraceAsync(chatId, _chatCts.Token);
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
            foreach (var group in chats.Values.Where(c => !string.IsNullOrWhiteSpace(c.Title)).GroupBy(c => c.Title.Trim(), StringComparer.Ordinal))
                if (group.Count() == 1) _chatTitleToId[group.Key] = group.First().ChatId; // ambiguous titles never match
            foreach (var call in RecentApiCalls) ResolveChatName(call);
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
            long last = Math.Max(s.Last, reg != null && reg.LastActiveUnixSec > 0 ? reg.LastActiveUnixSec * 1_000_000_000L : 0);
            if (chatId.HasValue && _chatTraceRows.TryGetValue(chatId.Value, out var rows) && rows.Count > 0)
                last = Math.Max(last, rows[^1].TimestampUnixNano);
            if (!_chatGroupsByKey.TryGetValue(key, out var group))
                _chatGroupsByKey[key] = group = new ChatGroupModel { Key = key, ChatId = chatId };
            group.Title = title;
            group.ChatType = reg?.ChatType ?? (key == NoChatKey ? "system" : string.Empty);
            group.CallCount = s.Count;
            group.LastActiveUnixNano = last;
        }
        foreach (string stale in _chatGroupsByKey.Keys.Where(k => !live.Contains(k)).ToList()) _chatGroupsByKey.Remove(stale);
        ChatListStatus = $"{_chatGroupsByKey.Count(g => g.Key != NoChatKey):N0} chats · {RecentApiCalls.Count:N0} API calls loaded" +
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
        var calls = AllKnownCalls().Where(c => ChatKeyOf(c) == group.Key)
            .OrderByDescending(c => c.TimestampUnixNano).ToList();
        var shown = calls.Take(_chatVisibleLimit).ToList();
        ChatHasOlder = calls.Count > shown.Count;

        var entries = new List<(long Ts, object Item)>(shown.Count);
        entries.AddRange(shown.Select(c => (c.TimestampUnixNano, (object)c)));
        int eventCount = 0;
        if (ChatShowTraceEvents && group.ChatId is long chatId && _chatTraceRows.TryGetValue(chatId, out var rows))
        {
            // Only events inside the time span of the visible calls (or all, when every call is shown).
            long floor = ChatHasOlder && shown.Count > 0 ? shown[^1].TimestampUnixNano : long.MinValue;
            foreach (var row in rows.Where(r => r.TimestampUnixNano >= floor)) { entries.Add((row.TimestampUnixNano, row)); eventCount++; }
        }
        // Chronological, like a chat: oldest at the top, newest at the bottom (the view opens at the bottom).
        var ordered = entries.OrderBy(e => e.Ts).Select(e => e.Item).ToList();
        SyncChatHistory(ordered);

        string trace = _chatTraceSupported == false ? " · probe has no per-chat trace index (only loaded calls shown)" : string.Empty;
        ChatHistoryStatus = $"{shown.Count:N0} of {calls.Count:N0} calls" + (eventCount > 0 ? $" · {eventCount:N0} trace events" : string.Empty) + trace;
        _ = LoadChatCallDetailsAsync(node, shown, _chatCts.Token);
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
    /// Prefetches and parses full payloads for the whole window (newest first, since the view opens at the
    /// bottom). Everything except the final property assignment runs on the thread pool.
    /// </summary>
    private async Task LoadChatCallDetailsAsync(NoxNodeModel node, List<RawApiCallItemModel> calls, CancellationToken token)
    {
        // `calls` is newest-first; skip anything another refresh is already fetching
        var pending = calls.Where(c => !c.IsDetailsLoaded && _chatDetailInflight.TryAdd(c.CallId, 0)).ToList();
        if (pending.Count == 0) return;
        var tasks = pending.Select(call => Task.Run(async () =>
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || SelectedNode != node || call.IsDetailsLoaded) { DisposeImages(prepared); return; }
                    ApplyPreparedDetails(call, prepared);
                }, DispatcherPriority.Background); // never ahead of input/render
            }
            catch (OperationCanceledException) { }
            catch (RpcException) { /* expired from the probe spool: the summary row stays visible */ }
            catch (Exception) { }
            finally { _chatDetailSlots.Release(); _chatDetailInflight.TryRemove(call.CallId, out _); }
        }, CancellationToken.None));
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
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

    /// <summary>Pulls the chat's full retained history (metadata only) from the probe's per-chat trace index.</summary>
    private async Task<bool> LoadChatTraceAsync(long chatId, CancellationToken token)
    {
        var node = SelectedNode;
        if (node == null) return false;
        if (_chatTraceSupported == false) return true;
        try
        {
            var client = _nodeManager.GetClient(node);
            string cursor = string.Empty;
            bool changed = false;
            for (int page = 0; page < 25 && !token.IsCancellationRequested; page++)
            {
                var response = await client.QueryChatTraceAsync(new NoxChatTraceRequest
                { NodeId = node.Id, ChatId = chatId, Limit = 200, Cursor = cursor }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                if (token.IsCancellationRequested || SelectedNode != node) return false;
                _chatTraceSupported = true;
                foreach (var record in response.Calls)
                {
                    if (string.IsNullOrEmpty(record.CallId) || _seenApiCallIds.Contains(record.CallId) || _chatExtraCalls.ContainsKey(record.CallId)) continue;
                    var item = BuildApiCallItemModel(record);
                    ResolveChatName(item);
                    _chatExtraCalls[record.CallId] = item;
                    changed = true;
                }
                foreach (var ev in response.Events)
                    if (ev.TraceContext?.HasChatId == true && !string.IsNullOrEmpty(ev.EventId))
                        changed |= AddTraceRow(ev.TraceContext.ChatId, ev);
                if (response.NextCursor.Length == 0 || response.NextCursor == cursor) break;
                cursor = response.NextCursor;
            }
            if (changed && SelectedChatGroup?.ChatId == chatId)
            {
                RebuildChatGroups();
                RebuildChatHistory(resetLimit: false);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            if (token.IsCancellationRequested || SelectedNode != node) return false;
            _chatTraceSupported = false;
            if (SelectedChatGroup?.ChatId == chatId) RebuildChatHistory(resetLimit: false);
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
