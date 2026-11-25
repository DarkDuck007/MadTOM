using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed record QuotaScopeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class QuotaOverrideRow : ObservableObject
{
    public NoxUserQuotaOverride? Proto { get; init; }
    public long UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public long ChatId { get; init; }
    public string ChatTitle { get; init; } = string.Empty;
    public int Quota5h { get; init; }
    public int Quota24h { get; init; }
    public string UpdatedAt { get; init; } = string.Empty;
    public long UpdatedBy { get; init; }

    public QuotaOverrideRow()
    {
    }

    public QuotaOverrideRow(NoxUserQuotaOverride proto)
    {
        Proto = proto;
        UserId = proto.UserId;
        UserName = proto.UserName ?? string.Empty;
        Scope = proto.Scope ?? string.Empty;
        ChatId = proto.ChatId;
        ChatTitle = proto.ChatTitle ?? string.Empty;
        Quota5h = proto.Quota5H;
        Quota24h = proto.Quota24H;
        UpdatedAt = proto.UpdatedAt ?? string.Empty;
        UpdatedBy = proto.UpdatedBy;
    }

    public static QuotaOverrideRow FromProto(NoxUserQuotaOverride proto) => new(proto);

    public string UserDisplay => string.IsNullOrWhiteSpace(UserName)
        ? UserId.ToString(CultureInfo.InvariantCulture)
        : $"{UserName} ({UserId})";

    public string ScopeDisplay => Scope switch
    {
        "all_chats" => "All chats",
        "chat" => string.IsNullOrWhiteSpace(ChatTitle)
            ? $"This chat: {ChatId}"
            : $"This chat: {ChatTitle}",
        "global" => "Global total",
        _ => Scope
    };

    public string Quota5hDisplay => UserQuotasTabViewModel.FormatLimit(Quota5h);
    public string Quota24hDisplay => UserQuotasTabViewModel.FormatLimit(Quota24h);
    public string UpdatedAtDisplay => string.IsNullOrWhiteSpace(UpdatedAt) ? "—" : UpdatedAt;
}

public sealed partial class TopUserQuotaRow : ObservableObject
{
    public NoxUserQuotaTopUser? Proto { get; init; }
    public long UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public int Calls5h { get; init; }
    public int Calls24h { get; init; }
    public int Chats { get; init; }
    public bool Exempt { get; init; }

    public TopUserQuotaRow()
    {
    }

    public TopUserQuotaRow(NoxUserQuotaTopUser proto)
    {
        Proto = proto;
        UserId = proto.UserId;
        UserName = proto.UserName ?? string.Empty;
        Calls5h = proto.Calls5H;
        Calls24h = proto.Calls24H;
        Chats = proto.Chats;
        Exempt = proto.Exempt;
    }

    public static TopUserQuotaRow FromProto(NoxUserQuotaTopUser proto) => new(proto);

    public string UserDisplay => string.IsNullOrWhiteSpace(UserName)
        ? UserId.ToString(CultureInfo.InvariantCulture)
        : $"{UserName} ({UserId})";

    public string ExemptBadge => Exempt ? "EXEMPT" : "—";
}

public sealed partial class UserQuotasTabViewModel : ObservableObject, IDisposable
{
    public const int DefaultTopUsersLimit = 50;

    private readonly NoxNodeManager _nodeManager;
    private Func<Task>? _pendingConfirmAction;
    private bool _nodeExplicitlyCleared;

    public IReadOnlyList<QuotaScopeOption> ScopeOptions { get; } = new[]
    {
        new QuotaScopeOption("all_chats", "All chats"),
        new QuotaScopeOption("chat", "One chat"),
        new QuotaScopeOption("global", "Global total")
    };

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private bool _isTabActive = true;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string StatusText => HasError ? ErrorMessage : StatusMessage;

    // Header: enabled + config defaults
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledBadge))]
    private bool _rateLimitingEnabled;

    public string EnabledBadge => RateLimitingEnabled ? "ENABLED" : "DISABLED";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultUserLimit5hDisplay))]
    private int _defaultUserLimit5h;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultUserLimit24hDisplay))]
    private int _defaultUserLimit24h;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultGlobalLimit5hDisplay))]
    private int _defaultGlobalLimit5h;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultGlobalLimit24hDisplay))]
    private int _defaultGlobalLimit24h;

    public string DefaultUserLimit5hDisplay => FormatLimit(DefaultUserLimit5h);
    public string DefaultUserLimit24hDisplay => FormatLimit(DefaultUserLimit24h);
    public string DefaultGlobalLimit5hDisplay => FormatLimit(DefaultGlobalLimit5h);
    public string DefaultGlobalLimit24hDisplay => FormatLimit(DefaultGlobalLimit24h);

    // Grids
    public ObservableCollection<QuotaOverrideRow> Overrides { get; } = new();
    public ObservableCollection<TopUserQuotaRow> TopUsers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTopUser))]
    private TopUserQuotaRow? _selectedTopUser;

    public bool HasSelectedTopUser => SelectedTopUser != null;

    // Selected user detail panel (from GetUserQuota chat_id=0)
    [ObservableProperty] private bool _hasUserDetail;
    [ObservableProperty] private string _detailUserHeader = string.Empty;
    [ObservableProperty] private bool _detailExempt;
    [ObservableProperty] private string _detailGlobalSource = "—";
    [ObservableProperty] private string _detailGlobal5hUsage = "—";
    [ObservableProperty] private string _detailGlobal5hReset = "—";
    [ObservableProperty] private string _detailGlobal24hUsage = "—";
    [ObservableProperty] private string _detailGlobal24hReset = "—";
    public ObservableCollection<QuotaOverrideRow> SelectedUserOverrides { get; } = new();

    // Editor
    [ObservableProperty] private string _editorUserIdText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorChatIdEnabled))]
    private QuotaScopeOption _selectedEditorScope;

    public bool IsEditorChatIdEnabled => SelectedEditorScope?.Value == "chat";

    [ObservableProperty] private string _editorChatIdText = string.Empty;
    [ObservableProperty] private string _editorQuota5hText = "0";
    [ObservableProperty] private string _editorQuota24hText = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorError))]
    private string _editorErrorMessage = string.Empty;

    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorErrorMessage);

    // Confirmation support (same pattern as AutomationTabViewModel)
    public Func<string, bool>? ConfirmAction { get; set; }

    [ObservableProperty] private bool _requireConfirmation;
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmMessage = string.Empty;

    public UserQuotasTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        _selectedEditorScope = ScopeOptions[0];
    }

    public static string FormatLimit(int limit) =>
        limit <= 0 ? "Unlimited" : limit.ToString(CultureInfo.InvariantCulture);

    public static string FormatTierWindow(int count, int limit, int? remaining, double? pct)
    {
        if (limit <= 0)
        {
            return $"{count} / Unlimited";
        }

        string remPart = remaining.HasValue ? $" ({remaining.Value} left" : " (";
        string pctPart = pct.HasValue
            ? $"{(remaining.HasValue ? ", " : "")}{pct.Value.ToString("0.#", CultureInfo.InvariantCulture)}%)"
            : remaining.HasValue ? ")" : string.Empty;

        return $"{count} / {limit}{(remaining.HasValue || pct.HasValue ? remPart + pctPart : string.Empty)}";
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value == null)
        {
            _nodeExplicitlyCleared = true;
            _ = DispatchUiAsync(() =>
            {
                Overrides.Clear();
                TopUsers.Clear();
                SelectedTopUser = null;
                ClearUserDetail();
                RateLimitingEnabled = false;
                DefaultUserLimit5h = 0;
                DefaultUserLimit24h = 0;
                DefaultGlobalLimit5h = 0;
                DefaultGlobalLimit24h = 0;
                StatusMessage = "No node selected";
                ErrorMessage = string.Empty;
            });
        }
        else
        {
            _nodeExplicitlyCleared = false;
            if (IsTabActive)
            {
                _ = RefreshAsync();
            }
        }
    }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (value && GetActiveNode() != null)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnSelectedTopUserChanged(TopUserQuotaRow? value)
    {
        if (value == null)
        {
            ClearUserDetail();
        }
        else if (GetActiveNode() != null)
        {
            _ = LoadUserDetailAsync(value);
        }
    }

    [RelayCommand]
    public async Task ConfirmPendingAsync()
    {
        var action = _pendingConfirmAction;
        _pendingConfirmAction = null;
        IsConfirmOpen = false;
        if (action != null)
        {
            await action();
        }
    }

    [RelayCommand]
    public void CancelConfirm()
    {
        _pendingConfirmAction = null;
        IsConfirmOpen = false;
    }

    private async Task ExecuteWithConfirmAsync(string title, string prompt, Func<Task> action)
    {
        if (ConfirmAction != null)
        {
            if (!ConfirmAction(prompt)) return;
            await action();
            return;
        }

        if (RequireConfirmation)
        {
            ConfirmTitle = title;
            ConfirmMessage = prompt;
            _pendingConfirmAction = action;
            IsConfirmOpen = true;
            return;
        }

        await action();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.ListUserQuotasAsync(new NoxListUserQuotasRequest
            {
                Limit = DefaultTopUsersLimit
            }).ResponseAsync;

            var mappedOverrides = res.Overrides.Select(QuotaOverrideRow.FromProto).ToList();
            var mappedTopUsers = res.TopUsers.Select(TopUserQuotaRow.FromProto).ToList();

            await DispatchUiAsync(() =>
            {
                long? prevUserId = SelectedTopUser?.UserId;

                RateLimitingEnabled = res.Enabled;
                DefaultUserLimit5h = res.Defaults?.UserLimit5H ?? 0;
                DefaultUserLimit24h = res.Defaults?.UserLimit24H ?? 0;
                DefaultGlobalLimit5h = res.Defaults?.UserGlobalLimit5H ?? 0;
                DefaultGlobalLimit24h = res.Defaults?.UserGlobalLimit24H ?? 0;

                Overrides.Clear();
                foreach (var ov in mappedOverrides)
                {
                    Overrides.Add(ov);
                }

                TopUsers.Clear();
                foreach (var u in mappedTopUsers)
                {
                    TopUsers.Add(u);
                }

                if (prevUserId.HasValue)
                {
                    SelectedTopUser = TopUsers.FirstOrDefault(u => u.UserId == prevUserId.Value);
                }

                if (!string.IsNullOrWhiteSpace(res.Error))
                {
                    ErrorMessage = res.Error;
                    StatusMessage = res.Error;
                }
                else
                {
                    SetSuccess($"Loaded {Overrides.Count} override{(Overrides.Count == 1 ? "" : "s")}, {TopUsers.Count} top user{(TopUsers.Count == 1 ? "" : "s")}.");
                }
            });
        }
        catch (RpcException ex)
        {
            await SetErrorAsync(FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LoadUserDetailAsync(TopUserQuotaRow? row)
    {
        var target = row ?? SelectedTopUser;
        if (target == null) return;

        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.GetUserQuotaAsync(new NoxGetUserQuotaRequest
            {
                UserId = target.UserId,
                ChatId = 0
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            var mappedOverrides = res.Overrides.Select(QuotaOverrideRow.FromProto).ToList();

            await DispatchUiAsync(() =>
            {
                HasUserDetail = true;
                string name = !string.IsNullOrWhiteSpace(res.UserName) ? res.UserName : target.UserName;
                DetailUserHeader = string.IsNullOrWhiteSpace(name)
                    ? res.UserId.ToString(CultureInfo.InvariantCulture)
                    : $"{name} ({res.UserId})";
                DetailExempt = res.Exempt;

                var g = res.Global;
                if (g != null)
                {
                    DetailGlobalSource = string.IsNullOrWhiteSpace(g.Source) ? "—" : g.Source;
                    DetailGlobal5hUsage = FormatTierWindow(
                        g.Count5H,
                        g.Limit5H,
                        g.HasRemaining5H ? g.Remaining5H : null,
                        g.HasPct5H ? g.Pct5H : null);
                    DetailGlobal5hReset = string.IsNullOrWhiteSpace(g.Reset5H) ? "—" : g.Reset5H;

                    DetailGlobal24hUsage = FormatTierWindow(
                        g.Count24H,
                        g.Limit24H,
                        g.HasRemaining24H ? g.Remaining24H : null,
                        g.HasPct24H ? g.Pct24H : null);
                    DetailGlobal24hReset = string.IsNullOrWhiteSpace(g.Reset24H) ? "—" : g.Reset24H;
                }
                else
                {
                    DetailGlobalSource = "—";
                    DetailGlobal5hUsage = "—";
                    DetailGlobal5hReset = "—";
                    DetailGlobal24hUsage = "—";
                    DetailGlobal24hReset = "—";
                }

                SelectedUserOverrides.Clear();
                foreach (var ov in mappedOverrides)
                {
                    SelectedUserOverrides.Add(ov);
                }

                SetSuccess($"Loaded quota detail for {DetailUserHeader}.");
            });
        }
        catch (RpcException ex)
        {
            await SetErrorAsync(FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void EditOverride(QuotaOverrideRow? row)
    {
        if (row == null) return;

        EditorUserIdText = row.UserId.ToString(CultureInfo.InvariantCulture);
        SelectedEditorScope = ScopeOptions.FirstOrDefault(o => o.Value == row.Scope) ?? ScopeOptions[0];
        EditorChatIdText = row.Scope == "chat"
            ? row.ChatId.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        EditorQuota5hText = row.Quota5h.ToString(CultureInfo.InvariantCulture);
        EditorQuota24hText = row.Quota24h.ToString(CultureInfo.InvariantCulture);
        EditorErrorMessage = string.Empty;
    }

    [RelayCommand]
    public void ClearEditor()
    {
        EditorUserIdText = string.Empty;
        SelectedEditorScope = ScopeOptions[0];
        EditorChatIdText = string.Empty;
        EditorQuota5hText = "0";
        EditorQuota24hText = "0";
        EditorErrorMessage = string.Empty;
    }

    public bool TryValidateEditor(out NoxSetUserQuotaRequest request, out string error)
    {
        request = new NoxSetUserQuotaRequest();
        error = string.Empty;

        if (!long.TryParse(EditorUserIdText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) || userId <= 0)
        {
            error = "User ID must be a positive integer.";
            return false;
        }

        string scope = SelectedEditorScope?.Value ?? string.Empty;
        if (scope is not ("all_chats" or "chat" or "global"))
        {
            error = "Scope must be All chats, One chat, or Global total.";
            return false;
        }

        long chatId = 0;
        if (scope == "chat")
        {
            if (!long.TryParse(EditorChatIdText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId) || chatId == 0)
            {
                error = "Chat ID is required and must be a non-zero integer for 'One chat' scope.";
                return false;
            }
        }

        if (!int.TryParse(EditorQuota5hText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int quota5h) || quota5h < 0)
        {
            error = "5h quota must be an integer >= 0 (0 = Unlimited).";
            return false;
        }

        if (!int.TryParse(EditorQuota24hText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int quota24h) || quota24h < 0)
        {
            error = "24h quota must be an integer >= 0 (0 = Unlimited).";
            return false;
        }

        if (quota24h > 0 && quota5h > quota24h)
        {
            error = "5h quota cannot exceed 24h quota when 24h quota > 0.";
            return false;
        }

        request = new NoxSetUserQuotaRequest
        {
            UserId = userId,
            Scope = scope,
            ChatId = chatId,
            Quota5H = quota5h,
            Quota24H = quota24h,
            Reset = false
        };
        return true;
    }

    [RelayCommand]
    public async Task SaveEditorAsync()
    {
        if (!TryValidateEditor(out var req, out var validationError))
        {
            await DispatchUiAsync(() => EditorErrorMessage = validationError);
            return;
        }

        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            await DispatchUiAsync(() => EditorErrorMessage = string.Empty);

            var client = _nodeManager.GetClient(node);
            var res = await client.SetUserQuotaAsync(req).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await DispatchUiAsync(() =>
                {
                    EditorErrorMessage = res.Error;
                    ErrorMessage = res.Error;
                    StatusMessage = res.Error;
                });
                return;
            }

            if (!res.Ok)
            {
                const string msg = "Failed to save user quota override.";
                await DispatchUiAsync(() =>
                {
                    EditorErrorMessage = msg;
                    ErrorMessage = msg;
                    StatusMessage = msg;
                });
                return;
            }

            await RefreshAsync();
            if (SelectedTopUser != null && SelectedTopUser.UserId == req.UserId)
            {
                await LoadUserDetailAsync(SelectedTopUser);
            }
            await DispatchUiAsync(() => SetSuccess($"Saved quota override for user {req.UserId} ({req.Scope})."));
        }
        catch (RpcException ex)
        {
            string msg = FormatRpcError(ex);
            await DispatchUiAsync(() =>
            {
                EditorErrorMessage = msg;
                ErrorMessage = msg;
                StatusMessage = msg;
            });
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() =>
            {
                EditorErrorMessage = ex.Message;
                ErrorMessage = ex.Message;
                StatusMessage = ex.Message;
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task RemoveOverrideAsync(QuotaOverrideRow? row)
    {
        if (row == null) return Task.CompletedTask;

        long userId = row.UserId;
        string scope = row.Scope;
        long chatId = row.ChatId;
        string label = $"{row.UserDisplay} [{row.ScopeDisplay}]";

        return ExecuteWithConfirmAsync(
            "Remove Quota Override",
            $"Remove quota override for {label}?",
            () => RemoveOverrideCoreAsync(userId, scope, chatId, label));
    }

    private async Task RemoveOverrideCoreAsync(long userId, string scope, long chatId, string label)
    {
        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.SetUserQuotaAsync(new NoxSetUserQuotaRequest
            {
                UserId = userId,
                Scope = scope,
                ChatId = chatId,
                Quota5H = 0,
                Quota24H = 0,
                Reset = true
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            if (!res.Ok)
            {
                await SetErrorAsync($"Failed to remove override for {label}.");
                return;
            }

            await RefreshAsync();
            if (SelectedTopUser != null && SelectedTopUser.UserId == userId)
            {
                await LoadUserDetailAsync(SelectedTopUser);
            }
            await DispatchUiAsync(() => SetSuccess($"Removed quota override for {label}."));
        }
        catch (RpcException ex)
        {
            await SetErrorAsync(FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await SetErrorAsync(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearUserDetail()
    {
        HasUserDetail = false;
        DetailUserHeader = string.Empty;
        DetailExempt = false;
        DetailGlobalSource = "—";
        DetailGlobal5hUsage = "—";
        DetailGlobal5hReset = "—";
        DetailGlobal24hUsage = "—";
        DetailGlobal24hReset = "—";
        SelectedUserOverrides.Clear();
    }

    public void Dispose()
    {
    }

    private NoxNodeModel? GetActiveNode()
    {
        if (SelectedNode != null) return SelectedNode;
        if (_nodeExplicitlyCleared) return null;
        return _nodeManager.Nodes.FirstOrDefault();
    }

    private void SetSuccess(string message)
    {
        ErrorMessage = string.Empty;
        StatusMessage = message;
    }

    private Task SetErrorAsync(string error)
    {
        return DispatchUiAsync(() =>
        {
            ErrorMessage = error;
            StatusMessage = error;
        });
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
