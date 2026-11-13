using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed class LogSearchResult
{
    public required NoxLogSearchHit Hit { get; init; }
    public string Header => $"{DateTimeOffset.FromUnixTimeMilliseconds(Hit.TimestampUnixNano / 1_000_000):yyyy-MM-dd HH:mm:ss.fff} UTC · {Hit.Unit} · priority {Hit.Priority} · #{Hit.Seq}";
    public string Before => Hit.Snippet[..Math.Min((int)Hit.MatchStart, Hit.Snippet.Length)];
    public string Match => Hit.Snippet.Substring(Math.Min((int)Hit.MatchStart, Hit.Snippet.Length), Math.Min((int)Hit.MatchLength, Hit.Snippet.Length - Math.Min((int)Hit.MatchStart, Hit.Snippet.Length)));
    public string After => Hit.Snippet[Math.Min((int)(Hit.MatchStart + Hit.MatchLength), Hit.Snippet.Length)..];
    public Avalonia.Media.FlowDirection Direction => NoxChatSessionParser.IsRtl(Hit.Snippet) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
}

public sealed partial class RawLogsApiTabViewModel
{
    [ObservableProperty] private string _apiFilter = string.Empty;
    [ObservableProperty] private string _logQuery = string.Empty;
    [ObservableProperty] private string _logUnitFilter = string.Empty;
    [ObservableProperty] private bool _logCaseSensitive;
    [ObservableProperty] private string _logTimeRange = "15 minutes";
    [ObservableProperty] private string _logPriority = "Any";
    [ObservableProperty] private string _searchStatus = "Press Enter to search retained logs. Times are UTC.";
    [ObservableProperty] private bool _showLogResults;
    [ObservableProperty] private bool _isLogSearching;
    [ObservableProperty] private bool _hasMoreLogResults;
    [ObservableProperty] private LogSearchResult? _selectedLogResult;
    public string[] LogTimeRanges { get; } = ["15 minutes", "1 hour", "24 hours", "All retained logs"];
    public string[] LogPriorities { get; } = ["Any", "0", "1", "2", "3", "4", "5", "6", "7"];
    public ObservableCollection<RawApiCallItemModel> FilteredApiCalls { get; } = new();
    public ObservableCollection<LogSearchResult> LogSearchResults { get; } = new();
    public string ApiFilterScope => $"Filtering {RecentApiCalls.Count:N0} loaded API calls · UTC";
    public event Action<ulong>? LogNavigationRequested;
    private DispatcherTimer? _filterTimer;
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private string _nextSearchCursor = string.Empty;
    private NoxSearchLogsRequest? _searchRequest;
    private bool _remoteResults;
    private int _filterRevision;

    private void InitializeSearch()
    {
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _filterTimer.Tick += async (_, _) => { _filterTimer.Stop(); await RefreshLocalFiltersAsync(); };
        RecentApiCalls.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ApiFilterScope)); ScheduleFilters(); };
    }
    private void ScheduleFilters() { ++_filterRevision; _filterTimer?.Stop(); _filterTimer?.Start(); }
    partial void OnApiFilterChanged(string value) { CancelApiContentSearch(); ScheduleFilters(); }
    partial void OnLogQueryChanged(string value) => SearchFiltersChanged();
    partial void OnLogUnitFilterChanged(string value) => SearchFiltersChanged();
    partial void OnLogCaseSensitiveChanged(bool value) => SearchFiltersChanged();
    partial void OnLogTimeRangeChanged(string value) => SearchFiltersChanged();
    partial void OnLogPriorityChanged(string value) => SearchFiltersChanged();
    private void SearchFiltersChanged()
    {
        CancelLogSearch(); _remoteResults = false; ShowLogResults = LogQuery.Length > 0 || LogUnitFilter.Length > 0 || LogPriority != "Any";
        ScheduleFilters();
    }
    private void CancelLogSearch()
    {
        ++_filterRevision;
        ++_searchGeneration; _searchCts?.Cancel(); _searchCts?.Dispose(); _searchCts = null;
        IsLogSearching = false; HasMoreLogResults = false; _nextSearchCursor = string.Empty; _searchRequest = null;
    }
    private void ResetSearch()
    {
        ++_filterRevision; CancelLogSearch(); _remoteResults = false; LogSearchResults.Clear(); SelectedLogResult = null;
        ShowLogResults = false; SearchStatus = "Press Enter to search retained logs. Times are UTC.";
    }
    [RelayCommand]
    public void ClearLogSearch() { ResetSearch(); LogQuery = string.Empty; LogUnitFilter = string.Empty; LogPriority = "Any"; IsFollowMode = true; }

    // One-rune invariant uppercase, matching Go unicode.ToUpper; preserve original UTF-16 offsets.
    public static (int Start, int Length) FindLiteral(string text, string query, bool sensitive)
    {
        if (query.Length == 0) return (0, 0);
        var source = text.EnumerateRunes().ToArray();
        var needle = query.EnumerateRunes().Select(r => sensitive ? r : Rune.ToUpperInvariant(r)).ToArray();
        // KMP keeps long messages/queries from making local filtering quadratic.
        var prefix = new int[needle.Length];
        for (int i = 1, j = 0; i < needle.Length; i++)
        {
            while (j > 0 && needle[i] != needle[j]) j = prefix[j - 1];
            if (needle[i] == needle[j]) j++;
            prefix[i] = j;
        }
        for (int i = 0, j = 0; i < source.Length; i++)
        {
            var r = sensitive ? source[i] : Rune.ToUpperInvariant(source[i]);
            while (j > 0 && r != needle[j]) j = prefix[j - 1];
            if (r == needle[j]) j++;
            if (j == needle.Length)
            {
                int startRune = i - j + 1;
                return (source.Take(startRune).Sum(x => x.Utf16SequenceLength), source.Skip(startRune).Take(j).Sum(x => x.Utf16SequenceLength));
            }
        }
        return (-1, 0);
    }
    public static string NormalizeApiFilter(string text) => text.Replace('ي', 'ی').Replace('ك', 'ک').Replace('\u200c', ' ').Replace("ـ", "");
    public static bool MatchesApiFilter(RawApiCallItemModel item, string query)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(item.TimestampUnixNano / 1_000_000).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return FindLiteral(NormalizeApiFilter($"{item.ChatName}\n{date}\n{item.TimestampText}\n{item.Caller}\n{item.Model}\n{item.CallId}"), NormalizeApiFilter(query), false).Start >= 0;
    }
    public async Task RefreshLocalFiltersAsync()
    {
        int revision = _filterRevision;
        var calls = RecentApiCalls.ToArray();
        var apiQuery = ApiFilter;
        var contentMatches = _apiContentMatches;
        var desired = await Task.Run(() => calls.Where(x => contentMatches != null ? contentMatches.Contains(x.CallId) : MatchesApiFilter(x, apiQuery)).ToList());
        if (revision != _filterRevision) return;
        var retained = desired.ToHashSet();
        for (int i = FilteredApiCalls.Count - 1; i >= 0; i--) if (!retained.Contains(FilteredApiCalls[i])) FilteredApiCalls.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < FilteredApiCalls.Count && ReferenceEquals(FilteredApiCalls[i], desired[i])) continue;
            int old = FilteredApiCalls.IndexOf(desired[i]);
            if (old >= 0) FilteredApiCalls.Move(old, i); else FilteredApiCalls.Insert(i, desired[i]);
        }
        if (SelectedApiCall != null && !retained.Contains(SelectedApiCall))
        {
            SelectedApiCall = FilteredApiCalls.FirstOrDefault();
        }
        else if (SelectedApiCall == null && FilteredApiCalls.Count > 0 && !IsScrolledDown)
        {
            SelectedApiCall = FilteredApiCalls[0];
        }
        if (_remoteResults || IsLogSearching) return;
        var loaded = _logCache.SnapshotDecodedRecords(NodeId, 5000);
        long start = GetSearchStart();
        var unit = LogUnitFilter; var priority = LogPriority; var query = LogQuery; var sensitive = LogCaseSensitive;
        var matches = await Task.Run(() => {
        var results = new List<LogSearchResult>();
        foreach (var (chunkId, record) in loaded)
        {
            if (record.TimestampUnixNano < start || (unit.Length > 0 && record.Unit != unit) || (priority != "Any" && record.Priority.ToString(CultureInfo.InvariantCulture) != priority)) continue;
            var match = FindLiteral(record.Message, query, sensitive);
            if (match.Start < 0) continue;
            // Bound display text independently of matching the full message; avoid splitting surrogate pairs.
            int lo = Math.Max(0, match.Start - 100); if (lo > 0 && char.IsLowSurrogate(record.Message[lo])) lo--;
            int hi = Math.Min(record.Message.Length, Math.Max(lo + 1000, match.Start + Math.Min(match.Length, 1000)));
            if (hi < record.Message.Length && char.IsLowSurrogate(record.Message[hi])) hi--;
            results.Add(new() { Hit = new() { ChunkId = chunkId, Seq = record.Seq, TimestampUnixNano = record.TimestampUnixNano, Unit = record.Unit, Priority = record.Priority,
                Snippet = record.Message[lo..hi], MatchStart = (uint)(match.Start - lo), MatchLength = (uint)Math.Min(match.Length, hi - match.Start), MessageTruncated = lo > 0 || hi < record.Message.Length } });
            if (results.Count == 200) break;
        }
        return results;
        });
        if (revision != _filterRevision || _remoteResults || IsLogSearching) return;
        LogSearchResults.Clear();
        foreach (var result in matches) LogSearchResults.Add(result);
        SearchStatus = $"Filtering {loaded.Count:N0} decoded logs · showing up to 200 matches. Press Enter to search retained logs. UTC.";
    }
    private long GetSearchStart() => LogTimeRange switch
    {
        "15 minutes" => DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds() * 1_000_000,
        "1 hour" => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() * 1_000_000,
        "24 hours" => DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds() * 1_000_000,
        _ => 0
    };
    [RelayCommand]
    public async Task SearchLogsAsync()
    {
        CancelLogSearch(); LogSearchResults.Clear(); SelectedLogResult = null;
        var req = new NoxSearchLogsRequest { NodeId = NodeId, Query = LogQuery, CaseSensitive = LogCaseSensitive, Unit = LogUnitFilter, StartTimeNano = GetSearchStart(), Limit = 100 };
        if (LogPriority != "Any" && uint.TryParse(LogPriority, out var priority)) req.Priorities.Add(priority);
        _searchRequest = req; await FetchSearchPageAsync();
    }
    [RelayCommand]
    public async Task MoreLogResultsAsync()
    {
        if (!IsLogSearching && HasMoreLogResults && _searchRequest != null) await FetchSearchPageAsync();
    }
    private async Task FetchSearchPageAsync()
    {
        var node = SelectedNode;
        if (node == null || _searchRequest == null) return;
        var generation = _searchGeneration;
        _searchCts?.Dispose(); _searchCts = new CancellationTokenSource(); var token = _searchCts.Token;
        var request = _searchRequest.Clone(); request.Cursor = _nextSearchCursor;
        IsLogSearching = true; ShowLogResults = true; _remoteResults = true; HasMoreLogResults = false;
        SearchStatus = "Searching retained logs…";
        try
        {
            var result = await _nodeManager.GetClient(node).SearchLogsAsync(request, cancellationToken: token);
            if (generation != _searchGeneration) return;
            foreach (var hit in result.Hits) LogSearchResults.Add(new() { Hit = hit });
            _nextSearchCursor = result.NextCursor; HasMoreLogResults = result.NextCursor.Length > 0 && LogSearchResults.Count < 2000;
            SearchStatus = $"{LogSearchResults.Count:N0} matches · {result.ScannedRecords:N0} records scanned this page · {result.TookMs} ms · {result.StopReason}" +
                (HasMoreLogResults ? " · More history remains; load next page." : result.NextCursor.Length > 0 ? " · Display limit reached (2,000). Narrow the filters." : result.SnapshotComplete ? " · Search complete." : " · Results incomplete.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) SearchStatus = ex is RpcException { StatusCode: StatusCode.Unimplemented } ? "This probe does not support retained-log search. Edit the query to filter loaded logs." : $"Search failed: {ex.Message}";
        }
        finally { if (generation == _searchGeneration) IsLogSearching = false; }
    }
    [RelayCommand]
    public async Task OpenLogResultAsync()
    {
        var node = SelectedNode; var selected = SelectedLogResult; var generation = _searchGeneration;
        if (node == null || selected == null) return;
        try
        {
            // Fetch again: a cache hit alone cannot prove that the record is still retained.
            var response = await _nodeManager.GetClient(node).QueryLogChunksAsync(new LogChunkQuery { NodeId = node.Id, StartChunkId = selected.Hit.ChunkId, MaxChunks = 1 });
            if (generation != _searchGeneration) return;
            var chunk = response.Chunks.FirstOrDefault(c => c.ChunkId == selected.Hit.ChunkId);
            if (chunk == null) { SearchStatus = "This log has expired from the probe spool."; return; }
            _logCache.PutChunk("noxchat", node.Id, chunk);
            var raw = MadTOM.Services.ZstdCodec.Decode(chunk.ZstdRecords.Span, checked((int)chunk.RawSize));
            var record = LogRecordList.Parser.ParseFrom(raw).Records.FirstOrDefault(r => r.Seq == selected.Hit.Seq);
            if (record == null || record.TimestampUnixNano != selected.Hit.TimestampUnixNano || record.Unit != selected.Hit.Unit || !record.Message.Contains(selected.Hit.Snippet, StringComparison.Ordinal))
            { SearchStatus = "This log has expired or changed. Search again."; return; }
            UpdateSeqBounds(chunk.FirstSeq, chunk.LastSeq); CacheRevision++; ShowLogResults = false; IsFollowMode = false;
            LogNavigationRequested?.Invoke(record.Seq);
        }
        catch (Exception ex) { if (generation == _searchGeneration) SearchStatus = $"Cannot open log: {ex.Message}"; }
    }
}
