using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class TipRowModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public string ScopeKind { get; init; } = string.Empty;
    public string ScopeValue { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string State { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public int UseCount { get; init; }
    public double? Score { get; init; }
    public long CreatedChatId { get; init; }

    [ObservableProperty] private string _editText = string.Empty;
    [ObservableProperty] private string _editTrigger = string.Empty;

    public string ScopeLabel => ScopeKind switch
    {
        "chat" => $"Chat {ScopeValue}",
        "persona" => $"Persona {ScopeValue}",
        "global" or "" => "Global",
        _ => string.IsNullOrEmpty(ScopeValue) ? ScopeKind : $"{ScopeKind} {ScopeValue}"
    };

    public string AuthorLabel => Author switch
    {
        "nox" => "Nox",
        "studio" => "Studio",
        _ => string.IsNullOrEmpty(Author) ? "Studio" : Author
    };

    public string StateLabel => State switch
    {
        "pending" => "Pending…",
        "ready" => "Ready",
        "failed" => "Failed",
        _ => State
    };

    public bool IsProposed => string.Equals(Status, "proposed", StringComparison.Ordinal);
    public bool IsRejected => string.Equals(Status, "rejected", StringComparison.Ordinal);
    public bool IsActive => string.Equals(Status, "active", StringComparison.Ordinal);

    public bool IsPending => string.Equals(State, "pending", StringComparison.Ordinal);
    public bool IsReady => string.Equals(State, "ready", StringComparison.Ordinal);
    public bool IsFailed => string.Equals(State, "failed", StringComparison.Ordinal);

    public bool HasTrigger => !string.IsNullOrWhiteSpace(Trigger);
    public string TriggerDisplay => HasTrigger ? $"when: {Trigger}" : string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string? ErrorTooltip => HasError ? Error : null;
    public string StatusDisplay => string.IsNullOrEmpty(Status) ? string.Empty : Status.ToUpperInvariant();
    public string EnableToggleLabel => Enabled ? "Disable" : "Enable";
    public double TextOpacity => Enabled ? 1.0 : 0.55;
    public bool HasScore => Score.HasValue;
    public string ScoreDisplay => Score.HasValue ? Score.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Text)
        ? Avalonia.Media.FlowDirection.RightToLeft
        : Avalonia.Media.FlowDirection.LeftToRight;

    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Text)
        ? Avalonia.Media.TextAlignment.Right
        : Avalonia.Media.TextAlignment.Left;

    public static TipRowModel FromProto(NoxTipItem item, double? score = null)
    {
        string text = item.Text ?? string.Empty;
        string trigger = item.Trigger ?? string.Empty;
        return new TipRowModel
        {
            Id = item.Id ?? string.Empty,
            Text = text,
            Trigger = trigger,
            ScopeKind = item.ScopeKind ?? string.Empty,
            ScopeValue = item.ScopeValue ?? string.Empty,
            Author = item.Author ?? string.Empty,
            Status = item.Status ?? string.Empty,
            Enabled = item.Enabled,
            State = item.State ?? string.Empty,
            Error = item.Error ?? string.Empty,
            UseCount = (int)item.UseCount,
            Score = score,
            CreatedChatId = item.CreatedChatId,
            EditText = text,
            EditTrigger = trigger
        };
    }
}

public sealed partial class TipsTabViewModel : ObservableObject
{
    private static readonly Regex BlankLineSplitRegex = new(@"\n\s*\n", RegexOptions.Compiled);

    private readonly NoxNodeManager _nodeManager;
    private CancellationTokenSource? _pollCts;
    private int _refreshGeneration;
    private bool _syncingFilterScope;
    private bool _syncingFilterStatus;
    private bool _syncingNewScope;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private TipRowModel? _selectedTip;

    // Filters
    [ObservableProperty] private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFilterScopeValue))]
    [NotifyPropertyChangedFor(nameof(FilterScopeValuePlaceholder))]
    private string _filterScopeKind = string.Empty;

    [ObservableProperty] private int _filterScopeIndex;
    [ObservableProperty] private string _filterScopeValue = string.Empty;

    [ObservableProperty] private string _filterStatus = string.Empty;
    [ObservableProperty] private int _filterStatusIndex;
    [ObservableProperty] private bool _onlyAwaitingApproval;

    // Read-only info
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCountLabel))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueue))]
    private int _queueSize;

    [ObservableProperty] private int _proposedCount;
    [ObservableProperty] private string _settingsSummary = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTips))]
    private bool _isBusy;

    [ObservableProperty] private bool _isPollingQueue;

    // Add box
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewTipsCount))]
    [NotifyPropertyChangedFor(nameof(AddButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanAddTips))]
    private string _newTipsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewScopeValue))]
    [NotifyPropertyChangedFor(nameof(NewScopeValuePlaceholder))]
    private string _newScopeKind = "global";

    [ObservableProperty] private int _newScopeIndex;
    [ObservableProperty] private string _newScopeValue = string.Empty;

    // Search
    [ObservableProperty] private string _searchQuery = string.Empty;

    // Test panel
    [ObservableProperty] private string _testText = string.Empty;
    [ObservableProperty] private string _testChatId = string.Empty;
    [ObservableProperty] private string _testPersonaKey = string.Empty;
    [ObservableProperty] private double _testThreshold;

    public ObservableCollection<TipRowModel> Tips { get; } = new();
    public ObservableCollection<TipRowModel> SearchResults { get; } = new();
    public ObservableCollection<TipRowModel> TestShown { get; } = new();
    public ObservableCollection<TipRowModel> TestBelowThreshold { get; } = new();
    public ObservableCollection<string> LastRejected { get; } = new();

    public bool ShowFilterScopeValue => FilterScopeKind is "chat" or "persona";
    public string FilterScopeValuePlaceholder => FilterScopeKind == "persona" ? "persona key" : "chat id";

    public bool ShowNewScopeValue => NewScopeKind is "chat" or "persona";
    public string NewScopeValuePlaceholder => NewScopeKind == "persona" ? "persona key" : "chat id";

    public bool HasQueue => QueueSize > 0;
    public string TotalCountLabel => TotalCount == 1 ? "1 tip" : $"{TotalCount} tips";
    public bool HasLastRejected => LastRejected.Count > 0;

    public int NewTipsCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewTipsText)) return 0;
            string normalized = NewTipsText.Replace("\r\n", "\n");
            return BlankLineSplitRegex.Split(normalized).Count(part => !string.IsNullOrWhiteSpace(part));
        }
    }

    public string AddButtonLabel => NewTipsCount == 1 ? "Add 1 tip" : $"Add {NewTipsCount} tips";
    public bool CanAddTips => !IsBusy && NewTipsCount > 0;

    public TipsTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        LastRejected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLastRejected));
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        StopQueuePolling();
        if (value != null)
        {
            _ = RefreshAsync();
        }
        else
        {
            Interlocked.Increment(ref _refreshGeneration);
            _ = DispatchUiAsync(() =>
            {
                Tips.Clear();
                SearchResults.Clear();
                TestShown.Clear();
                TestBelowThreshold.Clear();
                LastRejected.Clear();
                SelectedTip = null;
                TotalCount = 0;
                QueueSize = 0;
                ProposedCount = 0;
                SettingsSummary = string.Empty;
                StatusText = "No node selected";
            });
        }
    }

    partial void OnFilterScopeKindChanged(string value)
    {
        if (!_syncingFilterScope)
        {
            _syncingFilterScope = true;
            FilterScopeIndex = value switch
            {
                "global" => 1,
                "chat" => 2,
                "persona" => 3,
                _ => 0
            };
            _syncingFilterScope = false;
        }
        if (SelectedNode != null) _ = RefreshAsync();
    }

    partial void OnFilterScopeIndexChanged(int value)
    {
        if (_syncingFilterScope) return;
        _syncingFilterScope = true;
        FilterScopeKind = value switch
        {
            1 => "global",
            2 => "chat",
            3 => "persona",
            _ => string.Empty
        };
        _syncingFilterScope = false;
    }

    partial void OnFilterStatusChanged(string value)
    {
        if (!_syncingFilterStatus)
        {
            _syncingFilterStatus = true;
            FilterStatusIndex = value switch
            {
                "active" => 1,
                "proposed" => 2,
                "rejected" => 3,
                _ => 0
            };
            _syncingFilterStatus = false;
        }
        if (SelectedNode != null) _ = RefreshAsync();
    }

    partial void OnFilterStatusIndexChanged(int value)
    {
        if (_syncingFilterStatus) return;
        _syncingFilterStatus = true;
        FilterStatus = value switch
        {
            1 => "active",
            2 => "proposed",
            3 => "rejected",
            _ => string.Empty
        };
        _syncingFilterStatus = false;
    }

    partial void OnOnlyAwaitingApprovalChanged(bool value)
    {
        if (SelectedNode != null) _ = RefreshAsync();
    }

    partial void OnNewScopeKindChanged(string value)
    {
        if (_syncingNewScope) return;
        _syncingNewScope = true;
        NewScopeIndex = value switch
        {
            "chat" => 1,
            "persona" => 2,
            _ => 0
        };
        _syncingNewScope = false;
    }

    partial void OnNewScopeIndexChanged(int value)
    {
        if (_syncingNewScope) return;
        _syncingNewScope = true;
        NewScopeKind = value switch
        {
            1 => "chat",
            2 => "persona",
            _ => "global"
        };
        _syncingNewScope = false;
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(updateStatus: true, setBusy: true, CancellationToken.None);

    private async Task RefreshCoreAsync(bool updateStatus, bool setBusy, CancellationToken ct)
    {
        var node = SelectedNode;
        if (node == null) return;

        int generation = Interlocked.Increment(ref _refreshGeneration);
        if (setBusy) IsBusy = true;

        try
        {
            var client = _nodeManager.GetClient(node);
            string scopeKind = FilterScopeKind ?? string.Empty;
            string scopeValue = scopeKind is "chat" or "persona"
                ? (FilterScopeValue?.Trim() ?? string.Empty)
                : string.Empty;
            string status = OnlyAwaitingApproval ? "proposed" : (FilterStatus ?? string.Empty);

            var res = await client.ListTipsAsync(new NoxTipListRequest
            {
                NodeId = node.Id,
                ScopeKind = scopeKind,
                ScopeValue = scopeValue,
                Status = status,
                Text = FilterText?.Trim() ?? string.Empty,
                Limit = 500
            }, cancellationToken: ct).ResponseAsync;

            if (generation != _refreshGeneration || !ReferenceEquals(node, SelectedNode)) return;

            var mapped = res.Tips.Select(t => TipRowModel.FromProto(t)).ToList();
            string settingsText = FormatSettingsSummary(res.Settings);

            await DispatchUiAsync(() =>
            {
                if (generation != _refreshGeneration || !ReferenceEquals(node, SelectedNode)) return;

                var prevSelected = SelectedTip;
                bool preserveDirtyEdit = prevSelected != null &&
                    (!string.Equals(prevSelected.EditText, prevSelected.Text, StringComparison.Ordinal) ||
                     !string.Equals(prevSelected.EditTrigger, prevSelected.Trigger, StringComparison.Ordinal));

                Tips.Clear();
                foreach (var row in mapped)
                {
                    Tips.Add(row);
                }

                if (prevSelected != null)
                {
                    var match = Tips.FirstOrDefault(r => r.Id == prevSelected.Id);
                    if (match != null && preserveDirtyEdit)
                    {
                        match.EditText = prevSelected.EditText;
                        match.EditTrigger = prevSelected.EditTrigger;
                    }
                    SelectedTip = match;
                }

                TotalCount = (int)res.TotalCount;
                QueueSize = (int)res.QueueSize;
                ProposedCount = (int)res.ProposedCount;
                SettingsSummary = settingsText;
                if (res.Settings != null && res.Settings.MinRelevance > 0 && TestThreshold <= 0)
                {
                    TestThreshold = res.Settings.MinRelevance;
                }
                if (updateStatus)
                {
                    StatusText = $"Loaded {Tips.Count} tip{(Tips.Count == 1 ? "" : "s")}.";
                }
            });

            EnsureQueuePolling(node);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (RpcException ex)
        {
            if (generation == _refreshGeneration && ReferenceEquals(node, SelectedNode))
            {
                await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
            }
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration && ReferenceEquals(node, SelectedNode))
            {
                await DispatchUiAsync(() => StatusText = ex.Message);
            }
        }
        finally
        {
            if (setBusy) IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task AddTipsAsync()
    {
        var node = SelectedNode;
        if (node == null || string.IsNullOrWhiteSpace(NewTipsText)) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            string scopeKind = string.IsNullOrWhiteSpace(NewScopeKind) ? "global" : NewScopeKind;
            string scopeValue = scopeKind is "chat" or "persona"
                ? (NewScopeValue?.Trim() ?? string.Empty)
                : string.Empty;

            var res = await client.AddTipsAsync(new NoxAddTipsRequest
            {
                NodeId = node.Id,
                RawText = NewTipsText,
                ScopeKind = scopeKind,
                ScopeValue = scopeValue
            }).ResponseAsync;

            await DispatchUiAsync(() =>
            {
                NewTipsText = string.Empty;
                LastRejected.Clear();
                foreach (var r in res.Rejected)
                {
                    string snippet = r.Text ?? string.Empty;
                    if (snippet.Length > 80) snippet = snippet[..80];
                    LastRejected.Add($"{r.Reason}: {snippet}");
                }

                int added = res.Added.Count;
                int rejected = res.Rejected.Count;
                StatusText = rejected > 0
                    ? $"Added {added}, rejected {rejected} · {res.QueueSize} queued for embedding"
                    : $"Added {added} · {res.QueueSize} queued for embedding";
            });

            await RefreshCoreAsync(updateStatus: false, setBusy: false, CancellationToken.None);
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => StatusText = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        var node = SelectedNode;
        if (node == null) return;

        string query = SearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            await DispatchUiAsync(() => SearchResults.Clear());
            return;
        }

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            string scopeKind = FilterScopeKind ?? string.Empty;
            string scopeValue = scopeKind is "chat" or "persona"
                ? (FilterScopeValue?.Trim() ?? string.Empty)
                : string.Empty;

            var res = await client.SearchTipsAsync(new NoxTipSearchRequest
            {
                NodeId = node.Id,
                Query = query,
                ScopeKind = scopeKind,
                ScopeValue = scopeValue,
                Limit = 50
            }).ResponseAsync;

            var rows = res.Results
                .Where(r => r.Tip != null)
                .Select(r => TipRowModel.FromProto(r.Tip, r.Score))
                .ToList();

            await DispatchUiAsync(() =>
            {
                SearchResults.Clear();
                foreach (var row in rows)
                {
                    SearchResults.Add(row);
                }
                StatusText = $"Found {SearchResults.Count} tip{(SearchResults.Count == 1 ? "" : "s")}.";
            });
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => StatusText = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RunTestAsync()
    {
        var node = SelectedNode;
        if (node == null) return;

        long chatId = 0;
        if (!string.IsNullOrWhiteSpace(TestChatId) && !long.TryParse(TestChatId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId))
        {
            await DispatchUiAsync(() => StatusText = "Invalid chat ID (must be an integer).");
            return;
        }

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.TestTipsAsync(new NoxTestTipsRequest
            {
                NodeId = node.Id,
                Text = TestText ?? string.Empty,
                ChatId = chatId,
                PersonaKey = TestPersonaKey?.Trim() ?? string.Empty
            }).ResponseAsync;

            var shown = res.Shown
                .Where(r => r.Tip != null)
                .Select(r => TipRowModel.FromProto(r.Tip, r.Score))
                .ToList();
            var below = res.BelowThreshold
                .Where(r => r.Tip != null)
                .Select(r => TipRowModel.FromProto(r.Tip, r.Score))
                .ToList();

            await DispatchUiAsync(() =>
            {
                TestThreshold = res.Threshold;
                TestShown.Clear();
                foreach (var row in shown) TestShown.Add(row);
                TestBelowThreshold.Clear();
                foreach (var row in below) TestBelowThreshold.Add(row);
                StatusText = $"Test: {TestShown.Count} shown, {TestBelowThreshold.Count} below threshold ({TestThreshold.ToString("F2", CultureInfo.InvariantCulture)}).";
            });
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => StatusText = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task SaveTipAsync(TipRowModel? row)
    {
        if (row == null) return Task.CompletedTask;
        return UpdateTipAsync(
            new NoxUpdateTipRequest
            {
                Id = row.Id,
                SetText = true,
                Text = row.EditText ?? string.Empty,
                SetTrigger = true,
                Trigger = row.EditTrigger ?? string.Empty
            },
            $"Saved tip {row.Id}.");
    }

    [RelayCommand]
    public Task ToggleEnabledAsync(TipRowModel? row)
    {
        if (row == null) return Task.CompletedTask;
        bool nextEnabled = !row.Enabled;
        return UpdateTipAsync(
            new NoxUpdateTipRequest
            {
                Id = row.Id,
                SetEnabled = true,
                Enabled = nextEnabled
            },
            nextEnabled ? $"Enabled tip {row.Id}." : $"Disabled tip {row.Id}.");
    }

    [RelayCommand]
    public Task ApproveAsync(TipRowModel? row)
    {
        if (row == null) return Task.CompletedTask;
        return UpdateTipAsync(
            new NoxUpdateTipRequest
            {
                Id = row.Id,
                Status = "active"
            },
            $"Approved tip {row.Id}.");
    }

    [RelayCommand]
    public Task RejectAsync(TipRowModel? row)
    {
        if (row == null) return Task.CompletedTask;
        return UpdateTipAsync(
            new NoxUpdateTipRequest
            {
                Id = row.Id,
                Status = "rejected"
            },
            $"Rejected tip {row.Id}.");
    }

    [RelayCommand]
    public async Task DeleteAsync(TipRowModel? row)
    {
        var node = SelectedNode;
        if (node == null || row == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var req = new NoxDeleteTipsRequest { NodeId = node.Id };
            req.Ids.Add(row.Id);
            var res = await client.DeleteTipsAsync(req).ResponseAsync;

            if (!res.Success)
            {
                await DispatchUiAsync(() => StatusText = res.Message);
                return;
            }

            await DispatchUiAsync(() =>
            {
                if (SelectedTip?.Id == row.Id) SelectedTip = null;
                StatusText = $"Deleted {res.Affected} tip{(res.Affected == 1 ? "" : "s")}.";
            });

            await RefreshCoreAsync(updateStatus: false, setBusy: false, CancellationToken.None);
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => StatusText = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task DeleteSelectedAsync() => DeleteAsync(SelectedTip);

    private async Task UpdateTipAsync(NoxUpdateTipRequest request, string successStatus)
    {
        var node = SelectedNode;
        if (node == null) return;

        request.NodeId = node.Id;
        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.UpdateTipAsync(request).ResponseAsync;

            if (!res.Success)
            {
                await DispatchUiAsync(() => StatusText = res.Message);
                return;
            }

            await DispatchUiAsync(() => StatusText = successStatus);
            await RefreshCoreAsync(updateStatus: false, setBusy: false, CancellationToken.None);
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => StatusText = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => StatusText = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void EnsureQueuePolling(NoxNodeModel node)
    {
        if (QueueSize <= 0 || !ReferenceEquals(node, SelectedNode))
        {
            StopQueuePolling();
            return;
        }

        if (IsPollingQueue) return;

        StopQueuePolling();
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        IsPollingQueue = true;
        _ = PollQueueLoopAsync(node, cts);
    }

    private void StopQueuePolling()
    {
        var cts = Interlocked.Exchange(ref _pollCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
        }
        IsPollingQueue = false;
    }

    private async Task PollQueueLoopAsync(NoxNodeModel node, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested && ReferenceEquals(node, SelectedNode) && QueueSize > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (ct.IsCancellationRequested || !ReferenceEquals(node, SelectedNode)) break;
                await RefreshCoreAsync(updateStatus: false, setBusy: false, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_pollCts, cts))
            {
                _pollCts = null;
                IsPollingQueue = false;
            }
            cts.Dispose();
        }
    }

    private static string FormatSettingsSummary(NoxTipSettings? settings)
    {
        if (settings == null) return string.Empty;
        return $"min relevance {settings.MinRelevance.ToString("0.00", CultureInfo.InvariantCulture)} · max {settings.MaxTips} tips/turn · max {settings.MaxTipChars} chars";
    }

    private static string FormatRpcError(RpcException ex) =>
        !string.IsNullOrWhiteSpace(ex.Status.Detail) ? ex.Status.Detail : ex.Message;

    private static Task DispatchUiAsync(Action action)
    {
        if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
