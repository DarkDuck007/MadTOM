using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class RawLogsApiTabViewModel
{
    private CancellationTokenSource _historyCts = new();
    private int _historyRevision;
    private CancellationTokenSource? _apiContentCts;
    private HashSet<string>? _apiContentMatches;
    [ObservableProperty] private string _apiSearchStatus = "Press Enter to search retained request and response content.";

    // Discover all sequence ranges without downloading/decompressing log bodies.
    // Register exact ranges: chunk arithmetic is unreliable after probe restarts.
    public async Task LoadLogHistoryAsync() => await LoadLogHistoryCoreAsync();

    private Task<bool>? _logDiscoveryTask;
    private CancellationToken _logDiscoveryToken;

    private Task<bool> LoadLogHistoryCoreAsync()
    {
        lock (_historyLoadGate)
        {
            var token = _historyCts.Token;
            if (_logDiscoveryTask is { IsCompleted: false } && _logDiscoveryToken == token)
                return _logDiscoveryTask;
            _logDiscoveryToken = token;
            return _logDiscoveryTask = DiscoverLogHistoryAsync();
        }
    }

    private async Task<bool> DiscoverLogHistoryAsync()
    {
        var node = SelectedNode;
        if (node == null) return false;
        var token = _historyCts.Token;
        int revision = ++_historyRevision;
        try
        {
            ulong next = 0;
            do
            {
                var response = await _nodeManager.GetClient(node).QueryLogChunksAsync(new LogChunkQuery
                { NodeId = node.Id, StartChunkId = next, MaxChunks = 128, SummaryOnly = true }, deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: token);
                if (token.IsCancellationRequested || SelectedNode != node || revision != _historyRevision) return false;
                if (response.Chunks.Count == 0) break;
                await Task.Run(() =>
                {
                    foreach (var header in response.Chunks)
                    {
                        token.ThrowIfCancellationRequested();
                        _logCache.RegisterRange(node.Id, header);
                        // Older probes ignore SummaryOnly; retain any payload they sent.
                        if (!header.ZstdRecords.IsEmpty) _logCache.PutChunk("noxchat", node.Id, header);
                    }
                }, token);
                if (token.IsCancellationRequested || SelectedNode != node || revision != _historyRevision) return false;
                // Publish bounds once per page, avoiding hundreds of UI notifications.
                UpdateSeqBounds(response.Chunks.Min(c => c.FirstSeq), response.Chunks.Max(c => c.LastSeq));
                CacheRevision++;
                var last = response.Chunks.Max(c => c.ChunkId);
                if (!response.HasMore || last == ulong.MaxValue || last < next) break;
                next = last + 1;
            } while (!token.IsCancellationRequested);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && SelectedNode == node && revision == _historyRevision)
                SpoolStatusSummary = $"Could not discover retained logs: {ex.Message}";
            return false;
        }
        return !token.IsCancellationRequested && SelectedNode == node && revision == _historyRevision;
    }

    private void CancelApiContentSearch()
    {
        _apiContentCts?.Cancel(); _apiContentCts?.Dispose(); _apiContentCts = null;
        _apiContentMatches = null;
        ApiSearchStatus = "Press Enter to search retained request and response content.";
    }
    private bool MatchesCurrentApiFilter(RawApiCallItemModel item) =>
        _apiContentMatches?.Contains(item.CallId) ?? MatchesApiFilter(item, ApiFilter);

    public static bool MatchesApiContent(NoxRawApiCallRecord record, string query) =>
        FindLiteral(NormalizeApiFilter(FormatJsonForDisplay(record.RawRequestJson)), NormalizeApiFilter(query), false).Start >= 0 ||
        FindLiteral(NormalizeApiFilter(FormatJsonForDisplay(record.RawResponseJson)), NormalizeApiFilter(query), false).Start >= 0;

    [RelayCommand]
    public async Task SearchApiContentAsync()
    {
        CancelApiContentSearch();
        var node = SelectedNode;
        var query = ApiFilter;
        if (node == null || string.IsNullOrWhiteSpace(query)) { await RefreshLocalFiltersAsync(); return; }
        _apiContentCts = new();
        var token = _apiContentCts.Token;
        _apiContentMatches = new(StringComparer.Ordinal);
        ++_filterRevision;
        ApiSearchStatus = "Searching retained API request/response content…";
        try
        {
            ulong next = 0;
            int scanned = 0;
            do
            {
                // Small batches bound transport and decoding costs; no new content index.
                var response = await _nodeManager.GetClient(node).QueryApiCallChunksAsync(new NoxApiCallChunkQuery
                { NodeId = node.Id, StartChunkId = next, MaxChunks = 1, SummaryOnly = false }, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (response.Chunks.Count == 0) break;
                var page = await Task.Run(() =>
                {
                    var hits = new List<RawApiCallItemModel>();
                    int count = 0;
                    foreach (var chunk in response.Chunks)
                    foreach (var record in _apiCache.PutChunk(node.Id, chunk))
                    {
                        token.ThrowIfCancellationRequested(); count++;
                        var item = BuildApiCallItemModel(record);
                        if (MatchesApiFilter(item, query) || MatchesApiContent(record, query)) hits.Add(item);
                    }
                    return (hits, count);
                }, token);
                token.ThrowIfCancellationRequested();
                scanned += page.count;
                // Publish a new set so background metadata filtering never reads a mutated set.
                var matches = new HashSet<string>(_apiContentMatches!, StringComparer.Ordinal);
                foreach (var item in page.hits) matches.Add(item.CallId);
                _apiContentMatches = matches;
                AddOrUpdateApiCallsOnUi(page.hits);
                ++_filterRevision;
                await RefreshLocalFiltersAsync();
                token.ThrowIfCancellationRequested();
                ApiSearchStatus = $"{matches.Count:N0} matches · {scanned:N0} API calls scanned…";
                var last = response.Chunks.Max(c => c.ChunkId);
                if (!response.HasMore || last == ulong.MaxValue || last < next) break;
                next = last + 1;
            } while (true);
            ApiSearchStatus = $"Search complete: {_apiContentMatches!.Count:N0} matches in {scanned:N0} retained API calls.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ApiSearchStatus = $"API search incomplete: {ex.Message}";
        }
    }
}
