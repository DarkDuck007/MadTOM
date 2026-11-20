using System;
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

public sealed partial class AutomationChatRow : ObservableObject
{
    public NoxAutomationChatStatus? Proto { get; init; }
    public long ChatId { get; init; }
    public string ChatTitle { get; init; } = string.Empty;
    public int SayStreak { get; init; }
    public int Preprocessors { get; init; }
    public int EnabledPreprocessors { get; init; }
    public int Crons { get; init; }
    public int RunsLastHour { get; init; }
    public int SaysLastHour { get; init; }
    public int FiresToday { get; init; }
    public string NextFireAt { get; init; } = string.Empty;

    public AutomationChatRow()
    {
    }

    public AutomationChatRow(NoxAutomationChatStatus status)
    {
        Proto = status;
        ChatId = status.ChatId;
        ChatTitle = status.ChatTitle ?? string.Empty;
        SayStreak = status.SayStreak;
        Preprocessors = status.Preprocessors;
        EnabledPreprocessors = status.EnabledPreprocessors;
        Crons = status.Crons;
        RunsLastHour = status.RunsLastHour;
        SaysLastHour = status.SaysLastHour;
        FiresToday = status.FiresToday;
        NextFireAt = status.NextFireAt ?? string.Empty;
    }

    public static AutomationChatRow FromProto(NoxAutomationChatStatus status) => new(status);

    public bool IsHighSayStreak => SayStreak >= 4;

    public string ChatDisplay => string.IsNullOrWhiteSpace(ChatTitle)
        ? ChatId.ToString(CultureInfo.InvariantCulture)
        : $"{ChatTitle} ({ChatId})";

    public string PreprocessorsDisplay => $"{EnabledPreprocessors} / {Preprocessors}";
}

public sealed partial class PreprocessorRow : ObservableObject
{
    public NoxAutomationPreprocessor? Proto { get; init; }
    public long Id { get; init; }
    public long ChatId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Triggers { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int ErrorCount { get; init; }
    public string LastError { get; init; } = string.Empty;
    public long Runs { get; init; }

    public PreprocessorRow()
    {
    }

    public PreprocessorRow(NoxAutomationPreprocessor pp)
    {
        Proto = pp;
        Id = pp.Id;
        ChatId = pp.ChatId;
        Name = pp.Name ?? string.Empty;
        Kind = pp.Kind ?? string.Empty;
        Triggers = string.Join(", ", pp.Triggers);
        Enabled = pp.Enabled;
        ErrorCount = pp.ErrorCount;
        LastError = pp.LastError ?? string.Empty;
        Runs = pp.Runs;
    }

    public static PreprocessorRow FromProto(NoxAutomationPreprocessor pp) => new(pp);

    public bool HasErrors => ErrorCount > 0;
    public string EnabledBadge => Enabled ? "ENABLED" : "DISABLED";
}

public sealed partial class CronRow : ObservableObject
{
    public NoxAutomationCron? Proto { get; init; }
    public long Id { get; init; }
    public long ChatId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PreprocessorName { get; init; } = string.Empty;
    public string SpecKind { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string NextFireAt { get; init; } = string.Empty;
    public long Fires { get; init; }
    public bool Enabled { get; init; }
    public int Depth { get; init; }

    public CronRow()
    {
    }

    public CronRow(NoxAutomationCron cron)
    {
        Proto = cron;
        Id = cron.Id;
        ChatId = cron.ChatId;
        Name = cron.Name ?? string.Empty;
        PreprocessorName = cron.PreprocessorName ?? string.Empty;
        SpecKind = cron.SpecKind ?? string.Empty;
        Spec = cron.Spec ?? string.Empty;
        NextFireAt = cron.NextFireAt ?? string.Empty;
        Fires = cron.Fires;
        Enabled = cron.Enabled;
        Depth = cron.Depth;
    }

    public static CronRow FromProto(NoxAutomationCron cron) => new(cron);

    public string EnabledBadge => Enabled ? "ENABLED" : "DISABLED";
}

public sealed partial class AutomationLogRow : ObservableObject
{
    public NoxAutomationLogEntry? Proto { get; init; }
    public long Id { get; init; }
    public string At { get; init; } = string.Empty;
    public string PreprocessorName { get; init; } = string.Empty;
    public string EventKind { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public int DurationMs { get; init; }

    public AutomationLogRow()
    {
    }

    public AutomationLogRow(NoxAutomationLogEntry entry)
    {
        Proto = entry;
        Id = entry.Id;
        At = entry.At ?? string.Empty;
        PreprocessorName = entry.PreprocessorName ?? string.Empty;
        EventKind = entry.EventKind ?? string.Empty;
        Outcome = entry.Outcome ?? string.Empty;
        Detail = entry.Detail ?? string.Empty;
        DurationMs = entry.DurationMs;
    }

    public static AutomationLogRow FromProto(NoxAutomationLogEntry entry) => new(entry);

    public bool IsErrorOutcome => Outcome is "error" or "blocked";
}

public sealed partial class AutomationTabViewModel : ObservableObject, IDisposable
{
    public const int LogsPageSize = 50;

    private readonly NoxNodeManager _nodeManager;
    private Func<Task>? _pendingConfirmAction;
    private bool _syncingChatFilter;
    private bool _nodeExplicitlyCleared;

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

    [ObservableProperty] private long? _chatFilter;
    [ObservableProperty] private string _chatFilterText = string.Empty;

    public ObservableCollection<AutomationChatRow> Chats { get; } = new();
    [ObservableProperty] private string _configJson = string.Empty;

    public ObservableCollection<PreprocessorRow> Preprocessors { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPreprocessor))]
    private PreprocessorRow? _selectedPreprocessor;

    public bool HasSelectedPreprocessor => SelectedPreprocessor != null;

    [ObservableProperty] private string _selectedCode = string.Empty;
    [ObservableProperty] private string _selectedState = string.Empty;
    [ObservableProperty] private string _selectedStaticActions = string.Empty;

    public ObservableCollection<CronRow> Crons { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCron))]
    private CronRow? _selectedCron;

    public bool HasSelectedCron => SelectedCron != null;

    public ObservableCollection<AutomationLogRow> Logs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogsPageLabel))]
    private int _logsOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogsPageLabel))]
    private long _logsTotal;

    [ObservableProperty] private bool _logsForSelectedOnly;

    public string LogsPageLabel => LogsTotal > 0
        ? $"Offset {LogsOffset} · Showing {Logs.Count} of {LogsTotal}"
        : $"Offset {LogsOffset} · {Logs.Count} logs";

    public Func<string, bool>? ConfirmAction { get; set; }

    [ObservableProperty] private bool _requireConfirmation;
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmMessage = string.Empty;

    public AutomationTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value == null)
        {
            _nodeExplicitlyCleared = true;
            _ = DispatchUiAsync(() =>
            {
                Chats.Clear();
                Preprocessors.Clear();
                Crons.Clear();
                Logs.Clear();
                SelectedPreprocessor = null;
                SelectedCron = null;
                SelectedCode = string.Empty;
                SelectedState = string.Empty;
                SelectedStaticActions = string.Empty;
                ConfigJson = string.Empty;
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

    partial void OnChatFilterChanged(long? value)
    {
        if (_syncingChatFilter) return;
        _syncingChatFilter = true;
        ChatFilterText = value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _syncingChatFilter = false;
    }

    partial void OnChatFilterTextChanged(string value)
    {
        if (_syncingChatFilter) return;
        _syncingChatFilter = true;
        if (string.IsNullOrWhiteSpace(value))
        {
            ChatFilter = null;
        }
        else if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            ChatFilter = parsed;
        }
        _syncingChatFilter = false;
    }

    partial void OnSelectedPreprocessorChanged(PreprocessorRow? value)
    {
        if (value == null)
        {
            SelectedCode = string.Empty;
            SelectedState = string.Empty;
            SelectedStaticActions = string.Empty;
        }
        else if (GetActiveNode() != null)
        {
            _ = LoadPreprocessorAsync(value);
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

            var statusReq = new NoxAutomationStatusRequest();
            var ppReq = new NoxListPreprocessorsRequest();
            var cronReq = new NoxListCronsRequest();
            if (ChatFilter.HasValue)
            {
                statusReq.ChatId = ChatFilter.Value;
                ppReq.ChatId = ChatFilter.Value;
                cronReq.ChatId = ChatFilter.Value;
            }

            var statusRes = await client.GetAutomationStatusAsync(statusReq).ResponseAsync;
            var ppRes = await client.ListPreprocessorsAsync(ppReq).ResponseAsync;
            var cronRes = await client.ListCronsAsync(cronReq).ResponseAsync;

            var mappedChats = statusRes.Chats.Select(AutomationChatRow.FromProto).ToList();
            var mappedPps = ppRes.Preprocessors.Select(PreprocessorRow.FromProto).ToList();
            var mappedCrons = cronRes.Crons.Select(CronRow.FromProto).ToList();

            string? err = FirstNonEmpty(statusRes.Error, ppRes.Error, cronRes.Error);

            await DispatchUiAsync(() =>
            {
                long? prevPpId = SelectedPreprocessor?.Id;
                long? prevCronId = SelectedCron?.Id;

                Chats.Clear();
                foreach (var c in mappedChats)
                {
                    Chats.Add(c);
                }

                ConfigJson = statusRes.ConfigJson ?? string.Empty;

                Preprocessors.Clear();
                foreach (var p in mappedPps)
                {
                    Preprocessors.Add(p);
                }

                if (prevPpId.HasValue)
                {
                    SelectedPreprocessor = Preprocessors.FirstOrDefault(p => p.Id == prevPpId.Value);
                }

                Crons.Clear();
                foreach (var cr in mappedCrons)
                {
                    Crons.Add(cr);
                }

                if (prevCronId.HasValue)
                {
                    SelectedCron = Crons.FirstOrDefault(c => c.Id == prevCronId.Value);
                }

                if (!string.IsNullOrWhiteSpace(err))
                {
                    ErrorMessage = err;
                    StatusMessage = err;
                }
                else
                {
                    SetSuccess($"Loaded {Chats.Count} chat{(Chats.Count == 1 ? "" : "s")}, {Preprocessors.Count} preprocessor{(Preprocessors.Count == 1 ? "" : "s")}, {Crons.Count} cron{(Crons.Count == 1 ? "" : "s")}.");
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
    public async Task LoadPreprocessorAsync(PreprocessorRow? row)
    {
        var target = row ?? SelectedPreprocessor;
        if (target == null) return;

        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.GetPreprocessorAsync(new NoxGetPreprocessorRequest
            {
                Id = target.Id
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            await DispatchUiAsync(() =>
            {
                SelectedCode = res.Code ?? string.Empty;
                SelectedState = res.StateJson ?? string.Empty;
                SelectedStaticActions = res.StaticActionsJson ?? string.Empty;
                SetSuccess($"Loaded preprocessor '{target.Name}'.");
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
    public Task EnablePreprocessorAsync()
    {
        var pp = SelectedPreprocessor;
        if (pp == null) return Task.CompletedTask;
        return RunPreprocessorActionCoreAsync(pp.Id, "enable", $"Enabled preprocessor '{pp.Name}'.");
    }

    [RelayCommand]
    public Task DisablePreprocessorAsync()
    {
        var pp = SelectedPreprocessor;
        if (pp == null) return Task.CompletedTask;
        return RunPreprocessorActionCoreAsync(pp.Id, "disable", $"Disabled preprocessor '{pp.Name}'.");
    }

    [RelayCommand]
    public Task DeletePreprocessorAsync()
    {
        var pp = SelectedPreprocessor;
        if (pp == null) return Task.CompletedTask;
        long id = pp.Id;
        string name = pp.Name;
        return ExecuteWithConfirmAsync(
            "Delete Preprocessor",
            $"Delete preprocessor '{name}' (#{id})? Its code and state will be removed.",
            () => RunPreprocessorActionCoreAsync(id, "delete", $"Deleted preprocessor '{name}'."));
    }

    [RelayCommand]
    public Task ResetStateAsync()
    {
        var pp = SelectedPreprocessor;
        if (pp == null) return Task.CompletedTask;
        long id = pp.Id;
        string name = pp.Name;
        return ExecuteWithConfirmAsync(
            "Reset Preprocessor State",
            $"Reset persisted state for preprocessor '{name}' (#{id})?",
            () => RunPreprocessorActionCoreAsync(id, "reset_state", $"Reset state for preprocessor '{name}'."));
    }

    [RelayCommand]
    public Task ResetErrorsAsync()
    {
        var pp = SelectedPreprocessor;
        if (pp == null) return Task.CompletedTask;
        return RunPreprocessorActionCoreAsync(pp.Id, "reset_errors", $"Reset error count for preprocessor '{pp.Name}'.");
    }

    private async Task RunPreprocessorActionCoreAsync(long id, string action, string successMessage)
    {
        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.PreprocessorActionAsync(new NoxPreprocessorActionRequest
            {
                Id = id,
                Action = action
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            if (!res.Ok)
            {
                await SetErrorAsync($"Preprocessor action '{action}' failed.");
                return;
            }

            await RefreshAsync();
            await DispatchUiAsync(() => SetSuccess(successMessage));
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
    public async Task RefreshLogsAsync()
    {
        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var req = new NoxListPreprocessorLogsRequest
            {
                Limit = LogsPageSize,
                Offset = Math.Max(0, LogsOffset)
            };
            if (ChatFilter.HasValue)
            {
                req.ChatId = ChatFilter.Value;
            }
            if (LogsForSelectedOnly && SelectedPreprocessor != null)
            {
                req.PreprocessorId = SelectedPreprocessor.Id;
            }

            var res = await client.ListPreprocessorLogsAsync(req).ResponseAsync;
            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            var mapped = res.Entries.Select(AutomationLogRow.FromProto).ToList();
            await DispatchUiAsync(() =>
            {
                Logs.Clear();
                foreach (var entry in mapped)
                {
                    Logs.Add(entry);
                }
                LogsTotal = res.Total;
                OnPropertyChanged(nameof(LogsPageLabel));
                SetSuccess($"Loaded {Logs.Count} automation log{(Logs.Count == 1 ? "" : "s")}.");
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
    public async Task NextLogsPageAsync()
    {
        int next = Math.Max(0, LogsOffset) + LogsPageSize;
        if (LogsTotal > 0 && next >= LogsTotal)
        {
            return;
        }
        LogsOffset = next;
        await RefreshLogsAsync();
    }

    [RelayCommand]
    public async Task PrevLogsPageAsync()
    {
        int prev = Math.Max(0, LogsOffset - LogsPageSize);
        if (prev == LogsOffset)
        {
            return;
        }
        LogsOffset = prev;
        await RefreshLogsAsync();
    }

    [RelayCommand]
    public Task EnableCronAsync()
    {
        var cron = SelectedCron;
        if (cron == null) return Task.CompletedTask;
        return RunCronActionCoreAsync(cron.Id, "enable", $"Enabled cron '{cron.Name}'.");
    }

    [RelayCommand]
    public Task DisableCronAsync()
    {
        var cron = SelectedCron;
        if (cron == null) return Task.CompletedTask;
        return RunCronActionCoreAsync(cron.Id, "disable", $"Disabled cron '{cron.Name}'.");
    }

    [RelayCommand]
    public Task CancelCronAsync()
    {
        var cron = SelectedCron;
        if (cron == null) return Task.CompletedTask;
        long id = cron.Id;
        string name = cron.Name;
        return ExecuteWithConfirmAsync(
            "Cancel Cron",
            $"Cancel cron '{name}' (#{id})?",
            () => RunCronActionCoreAsync(id, "cancel", $"Cancelled cron '{name}'."));
    }

    private async Task RunCronActionCoreAsync(long id, string action, string successMessage)
    {
        var node = GetActiveNode();
        if (node == null) return;

        IsBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.CronActionAsync(new NoxCronActionRequest
            {
                Id = id,
                Action = action
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await SetErrorAsync(res.Error);
                return;
            }

            if (!res.Ok)
            {
                await SetErrorAsync($"Cron action '{action}' failed.");
                return;
            }

            await RefreshAsync();
            await DispatchUiAsync(() => SetSuccess(successMessage));
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

    public void Dispose()
    {
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
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
