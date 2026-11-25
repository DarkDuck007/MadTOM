using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed partial class SfBroadcastRow : ObservableObject
{
    public string BroadcastId { get; }

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _sent;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private int _blocked;
    [ObservableProperty] private string _createdBy = string.Empty;
    [ObservableProperty] private string _createdAt = string.Empty;
    [ObservableProperty] private string? _finishedAt;
    [ObservableProperty] private string _preview = string.Empty;

    public string StatusText => Status.ToUpperInvariant();
    public string Progress => $"{Sent}/{Total}";
    public double ProgressFraction => Total > 0 ? Math.Clamp((double)Sent / Total, 0.0, 1.0) : 0.0;
    public bool IsActive =>
        string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "sending", StringComparison.OrdinalIgnoreCase);

    public bool IsStatusHealthy => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsStatusAccent => string.Equals(Status, "sending", StringComparison.OrdinalIgnoreCase);
    public bool IsStatusCritical => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase);
    public bool IsStatusWarning => !IsStatusHealthy && !IsStatusAccent && !IsStatusCritical;

    public SfBroadcastRow(
        string broadcastId,
        string status,
        int total,
        int sent,
        int failed,
        int blocked,
        string createdBy,
        string createdAt,
        string? finishedAt,
        string preview)
    {
        BroadcastId = broadcastId;
        _status = status;
        _total = total;
        _sent = sent;
        _failed = failed;
        _blocked = blocked;
        _createdBy = createdBy;
        _createdAt = createdAt;
        _finishedAt = finishedAt;
        _preview = preview;
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsStatusHealthy));
        OnPropertyChanged(nameof(IsStatusAccent));
        OnPropertyChanged(nameof(IsStatusCritical));
        OnPropertyChanged(nameof(IsStatusWarning));
    }

    partial void OnTotalChanged(int value)
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressFraction));
    }

    partial void OnSentChanged(int value)
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressFraction));
    }

    internal void UpdateFrom(SfBroadcastRow other)
    {
        Status = other.Status;
        Total = other.Total;
        Sent = other.Sent;
        Failed = other.Failed;
        Blocked = other.Blocked;
        CreatedBy = other.CreatedBy;
        CreatedAt = other.CreatedAt;
        FinishedAt = other.FinishedAt;
        Preview = other.Preview;
    }
}

public sealed record SfBroadcastErrorRow(
    long UserId,
    string Status,
    string Error,
    string At);

public sealed class SfBroadcastDetails
{
    public string BroadcastId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusText => Status.ToUpperInvariant();
    public string Text { get; init; } = string.Empty;
    public string? TextsEn { get; init; }
    public string? TextsFa { get; init; }
    public string ParseMode { get; init; } = "html";
    public string AudienceSummary { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public int Total { get; init; }
    public int Pending { get; init; }
    public int Sent { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public ObservableCollection<SfBroadcastErrorRow> RecentErrors { get; } = new();

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasTextsEn => !string.IsNullOrWhiteSpace(TextsEn);
    public bool HasTextsFa => !string.IsNullOrWhiteSpace(TextsFa);
    public bool HasRecentErrors => RecentErrors.Count > 0;
}

public sealed partial class SfBroadcastsViewModel : ObservableObject, IDisposable
{
    private const int DefaultListLimit = 50;
    private const int AudienceDebounceMs = 400;
    private const int ActivePollIntervalMs = 3000;
    private const int IdlePollIntervalMs = 15000;

    private SfRpc? _rpc;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _autoRefreshCts;
    private CancellationTokenSource? _audienceDebounceCts;
    private CancellationTokenSource? _detailsCts;

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasActiveBroadcasts;

    [ObservableProperty] private SfBroadcastRow? _selectedBroadcast;
    [ObservableProperty] private SfBroadcastDetails? _details;

    // Compose panel
    [ObservableProperty] private string _composeText = string.Empty;
    [ObservableProperty] private string _composeTextEn = string.Empty;
    [ObservableProperty] private string _composeTextFa = string.Empty;
    [ObservableProperty] private int _parseModeIndex;      // 0 = html, 1 = plain
    [ObservableProperty] private int _audienceKindIndex;   // 0 = all, 1 = admins, 2 = users
    [ObservableProperty] private string _userIdsText = string.Empty;
    [ObservableProperty] private string _activeDaysText = string.Empty;
    [ObservableProperty] private bool _langEn;
    [ObservableProperty] private bool _langFa;
    [ObservableProperty] private bool _includeUnreachable;

    // Live audience count
    [ObservableProperty] private int? _audienceCount;
    [ObservableProperty] private string _audienceCountText = "—";

    // Confirmation dialog
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmText = string.Empty;

    public ObservableCollection<SfBroadcastRow> Broadcasts { get; } = new();

    public bool HasDetails => Details is not null;
    public bool CanCancelSelected => SelectedBroadcast is not null && SelectedBroadcast.IsActive;
    public bool IsSpecificUsersAudience => AudienceKindIndex == 2;
    public bool IsAdminsAudience => AudienceKindIndex == 1;
    public string ParseMode => ParseModeIndex == 1 ? "plain" : "html";
    public string AudienceKind => AudienceKindIndex switch
    {
        1 => "admins",
        2 => "users",
        _ => "all",
    };

    public string SendButtonText =>
        AudienceKindIndex == 1
            ? "Send to admins"
            : AudienceCount.HasValue
                ? $"Send to {AudienceCount.Value.ToString("N0", CultureInfo.InvariantCulture)} users"
                : "Send broadcast";

    public SfBroadcastsViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public static string FriendlyError(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Unknown error";

        return code switch
        {
            "bad_html" => "Invalid HTML: Telegram supports only b, i, u, s, a, code, pre, blockquote, tg-spoiler; escape < > & as text",
            "empty_text" => "Message text cannot be empty.",
            "text_too_long" => "Message text exceeds Telegram's maximum length.",
            "bad_audience" => "Invalid audience filter.",
            "bad_language" => "Invalid language code in audience or text overrides.",
            "bad_parse_mode" => "Invalid parse mode; expected html or plain.",
            "disabled" => "Broadcasts are disabled in SlideForge config",
            "not_found" => "Broadcast not found.",
            "bad_request" => "Invalid broadcast request.",
            "unavailable" => "SlideForge service is unavailable.",
            _ => code,
        };
    }

    partial void OnSelectedBroadcastChanged(SfBroadcastRow? value)
    {
        OnPropertyChanged(nameof(CanCancelSelected));
        if (value is null)
        {
            _detailsCts?.Cancel();
            Details = null;
            return;
        }

        if (_rpc is not null)
        {
            _detailsCts?.Cancel();
            _detailsCts?.Dispose();
            var cts = new CancellationTokenSource();
            _detailsCts = cts;
            _ = LoadDetailsAsync(value.BroadcastId, cts.Token);
        }
    }

    partial void OnDetailsChanged(SfBroadcastDetails? value)
    {
        OnPropertyChanged(nameof(HasDetails));
    }

    partial void OnAudienceKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSpecificUsersAudience));
        OnPropertyChanged(nameof(IsAdminsAudience));
        OnPropertyChanged(nameof(AudienceKind));
        OnPropertyChanged(nameof(SendButtonText));
        ScheduleAudienceRefresh();
    }

    partial void OnParseModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ParseMode));
    }

    partial void OnUserIdsTextChanged(string value) => ScheduleAudienceRefresh();
    partial void OnActiveDaysTextChanged(string value) => ScheduleAudienceRefresh();
    partial void OnLangEnChanged(bool value) => ScheduleAudienceRefresh();
    partial void OnLangFaChanged(bool value) => ScheduleAudienceRefresh();
    partial void OnIncludeUnreachableChanged(bool value) => ScheduleAudienceRefresh();

    partial void OnAudienceCountChanged(int? value)
    {
        OnPropertyChanged(nameof(SendButtonText));
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        await _refreshLock.WaitAsync(ct);
        IsBusy = true;
        try
        {
            await LoadCoreAsync(ct);
        }
        finally
        {
            IsBusy = false;
            _refreshLock.Release();
        }
    }

    public async Task RefreshSilentAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
            return;

        if (!await _refreshLock.WaitAsync(0, ct))
            return;

        try
        {
            await LoadCoreAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["limit"] = DefaultListLimit,
                ["offset"] = 0,
            };
            var root = await _rpc!.CallAsync("list_broadcasts", payload, ct);

            Enabled = SfJsonHelpers.GetBool(root, "enabled", fallback: true);

            var incoming = new List<SfBroadcastRow>();
            if (root.TryGetProperty("broadcasts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    incoming.Add(ParseBroadcastRow(item));
                }
            }

            ApplyInPlaceDiff(incoming);
            HasActiveBroadcasts = Broadcasts.Any(b => b.IsActive);
            OnPropertyChanged(nameof(CanCancelSelected));
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void ApplyInPlaceDiff(List<SfBroadcastRow> incoming)
    {
        string? selectedId = SelectedBroadcast?.BroadcastId;
        var incomingIds = new HashSet<string>(incoming.Select(r => r.BroadcastId), StringComparer.Ordinal);

        for (int i = Broadcasts.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Broadcasts[i].BroadcastId))
                Broadcasts.RemoveAt(i);
        }

        var existingById = new Dictionary<string, SfBroadcastRow>(StringComparer.Ordinal);
        foreach (var row in Broadcasts)
            existingById[row.BroadcastId] = row;

        for (int targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            var next = incoming[targetIndex];
            if (existingById.TryGetValue(next.BroadcastId, out var existing))
            {
                existing.UpdateFrom(next);
                int currentIndex = Broadcasts.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                    Broadcasts.Move(currentIndex, targetIndex);
            }
            else
            {
                Broadcasts.Insert(targetIndex, next);
                existingById[next.BroadcastId] = next;
            }
        }

        if (selectedId is not null)
        {
            SfBroadcastRow? match = existingById.TryGetValue(selectedId, out var found) ? found : null;
            if (!ReferenceEquals(SelectedBroadcast, match))
                SelectedBroadcast = match;
        }
    }

    public async Task LoadDetailsAsync(string broadcastId, CancellationToken ct = default)
    {
        if (_rpc is null || string.IsNullOrWhiteSpace(broadcastId))
            return;

        try
        {
            var root = await _rpc.CallAsync(
                "get_broadcast",
                new Dictionary<string, object?> { ["broadcast_id"] = broadcastId },
                ct);

            if (ct.IsCancellationRequested)
                return;

            Details = ParseBroadcastDetails(root);
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        var cts = new CancellationTokenSource();
        _autoRefreshCts = cts;
        _ = RunAutoRefreshLoopAsync(cts.Token);
    }

    public void StopAutoRefresh()
    {
        var cts = Interlocked.Exchange(ref _autoRefreshCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public void OnTabOpened()
    {
        StartAutoRefresh();
        _ = RefreshSilentAsync();
        if (_rpc is not null)
            _ = RefreshAudienceAsync();
    }

    public void OnTabClosed()
    {
        StopAutoRefresh();
    }

    private async Task RunAutoRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int delayMs = HasActiveBroadcasts ? ActivePollIntervalMs : IdlePollIntervalMs;
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    break;
                await RefreshSilentAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep polling loop alive across transient errors
            }
        }
    }

    private void ScheduleAudienceRefresh()
    {
        if (_rpc is null)
            return;

        var prev = Interlocked.Exchange(ref _audienceDebounceCts, null);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            prev.Dispose();
        }

        var cts = new CancellationTokenSource();
        _audienceDebounceCts = cts;
        _ = DebouncedRefreshAudienceAsync(cts.Token);
    }

    private async Task DebouncedRefreshAudienceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AudienceDebounceMs, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                await RefreshAudienceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Errors are surfaced inside RefreshAudienceAsync
        }
    }

    public async Task RefreshAudienceAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
            return;

        if (!TryBuildAudiencePayload(out var audience, out var validationError))
        {
            ErrorMessage = validationError;
            AudienceCount = null;
            AudienceCountText = "—";
            return;
        }

        try
        {
            var root = await _rpc.CallAsync(
                "broadcast_audience",
                new Dictionary<string, object?> { ["audience"] = audience },
                ct);

            if (ct.IsCancellationRequested)
                return;

            int count = SfJsonHelpers.GetInt(root, "count");
            AudienceCount = count;
            AudienceCountText = FormatAudienceCount(count, root);
            ErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
            AudienceCount = null;
            AudienceCountText = "—";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            AudienceCount = null;
            AudienceCountText = "—";
        }
    }

    public Dictionary<string, object?> BuildAudiencePayload()
    {
        if (!TryBuildAudiencePayload(out var audience, out var validationError))
            ErrorMessage = validationError;
        return audience;
    }

    private bool TryBuildAudiencePayload(out Dictionary<string, object?> audience, out string? validationError)
    {
        validationError = null;
        audience = new Dictionary<string, object?>
        {
            ["kind"] = AudienceKind,
        };

        if (AudienceKindIndex == 2)
        {
            var ids = new List<long>();
            if (!string.IsNullOrWhiteSpace(UserIdsText))
            {
                var tokens = UserIdsText.Split(
                    new[] { ',', ';', ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var tok in tokens)
                {
                    if (!long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                    {
                        validationError = $"Invalid user ID: '{tok}'";
                        return false;
                    }
                    ids.Add(id);
                }
            }
            if (ids.Count > 0)
                audience["user_ids"] = ids;
        }

        if (!string.IsNullOrWhiteSpace(ActiveDaysText))
        {
            if (!int.TryParse(ActiveDaysText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days <= 0)
            {
                validationError = "Active days must be a positive whole number.";
                return false;
            }
            audience["active_days"] = days;
        }

        if (LangEn ^ LangFa)
        {
            audience["langs"] = LangEn ? new List<string> { "en" } : new List<string> { "fa" };
        }

        if (IncludeUnreachable)
        {
            audience["include_unreachable"] = true;
        }

        return true;
    }

    public Dictionary<string, object?> BuildCreatePayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["text"] = ComposeText,
            ["audience"] = BuildAudiencePayload(),
            ["parse_mode"] = ParseMode,
        };

        var texts = BuildTextsOverride();
        if (texts is not null)
            payload["texts"] = texts;

        return payload;
    }

    private Dictionary<string, string>? BuildTextsOverride()
    {
        bool hasEn = !string.IsNullOrWhiteSpace(ComposeTextEn);
        bool hasFa = !string.IsNullOrWhiteSpace(ComposeTextFa);
        if (!hasEn && !hasFa)
            return null;

        var texts = new Dictionary<string, string>();
        if (hasEn) texts["en"] = ComposeTextEn;
        if (hasFa) texts["fa"] = ComposeTextFa;
        return texts;
    }

    [RelayCommand]
    public async Task SendTestAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposeText))
        {
            ErrorMessage = FriendlyError("empty_text");
            return;
        }

        IsBusy = true;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["text"] = ComposeText,
                ["audience"] = new Dictionary<string, object?> { ["kind"] = "admins" },
                ["parse_mode"] = ParseMode,
            };
            var texts = BuildTextsOverride();
            if (texts is not null)
                payload["texts"] = texts;

            var res = await _rpc.CallAsync("create_broadcast", payload, ct);
            string? createdId = SfJsonHelpers.GetStringOrNull(res, "broadcast_id");
            ErrorMessage = null;
            IsBusy = false;

            await LoadAsync(ct);
            SelectBroadcastById(createdId);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RequestSendAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ComposeText))
        {
            ErrorMessage = FriendlyError("empty_text");
            return;
        }

        if (!TryBuildAudiencePayload(out _, out var validationError))
        {
            ErrorMessage = validationError;
            return;
        }

        if (AudienceKindIndex == 1)
        {
            await ConfirmSendAsync(ct);
            return;
        }

        if (!AudienceCount.HasValue && _rpc is not null)
            await RefreshAudienceAsync(ct);

        if (!AudienceCount.HasValue)
            return;

        ConfirmText = $"Send to {AudienceCount.Value.ToString("N0", CultureInfo.InvariantCulture)} users?";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    public async Task ConfirmSendAsync(CancellationToken ct = default)
    {
        IsConfirmOpen = false;

        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        if (string.IsNullOrWhiteSpace(ComposeText))
        {
            ErrorMessage = FriendlyError("empty_text");
            return;
        }

        if (!TryBuildAudiencePayload(out var audience, out var validationError))
        {
            ErrorMessage = validationError;
            return;
        }

        IsBusy = true;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["text"] = ComposeText,
                ["audience"] = audience,
                ["parse_mode"] = ParseMode,
            };
            var texts = BuildTextsOverride();
            if (texts is not null)
                payload["texts"] = texts;

            var res = await _rpc.CallAsync("create_broadcast", payload, ct);
            string? createdId = SfJsonHelpers.GetStringOrNull(res, "broadcast_id");

            ComposeText = string.Empty;
            ComposeTextEn = string.Empty;
            ComposeTextFa = string.Empty;
            ErrorMessage = null;
            IsBusy = false;

            await LoadAsync(ct);
            SelectBroadcastById(createdId);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CancelConfirm()
    {
        IsConfirmOpen = false;
    }

    [RelayCommand]
    public async Task CancelBroadcastAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        var selected = SelectedBroadcast;
        if (selected is null || !selected.IsActive)
            return;

        IsBusy = true;
        try
        {
            await _rpc.CallAsync(
                "cancel_broadcast",
                new Dictionary<string, object?> { ["broadcast_id"] = selected.BroadcastId },
                ct);
            ErrorMessage = null;
            IsBusy = false;

            await LoadAsync(ct);
            if (SelectedBroadcast is not null)
                await LoadDetailsAsync(SelectedBroadcast.BroadcastId, ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = FriendlyError(ex.Code);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectBroadcastById(string? broadcastId)
    {
        if (string.IsNullOrWhiteSpace(broadcastId))
            return;

        foreach (var row in Broadcasts)
        {
            if (string.Equals(row.BroadcastId, broadcastId, StringComparison.Ordinal))
            {
                SelectedBroadcast = row;
                return;
            }
        }
    }

    private static SfBroadcastRow ParseBroadcastRow(JsonElement item)
    {
        string id = SfJsonHelpers.GetString(item, "broadcast_id");
        string status = SfJsonHelpers.GetString(item, "status", "draft");
        int total = SfJsonHelpers.GetInt(item, "total");
        int sent = SfJsonHelpers.GetInt(item, "sent");
        int failed = SfJsonHelpers.GetInt(item, "failed");
        int blocked = SfJsonHelpers.GetInt(item, "blocked");
        string createdBy = SfJsonHelpers.GetString(item, "created_by");
        string? rawCreatedAt = SfJsonHelpers.GetStringOrNull(item, "created_at");
        string createdAt = string.IsNullOrWhiteSpace(rawCreatedAt)
            ? string.Empty
            : SfJsonHelpers.FormatTimestamp(rawCreatedAt);
        string? rawFinishedAt = SfJsonHelpers.GetStringOrNull(item, "finished_at");
        string? finishedAt = string.IsNullOrWhiteSpace(rawFinishedAt)
            ? null
            : SfJsonHelpers.FormatTimestamp(rawFinishedAt);
        string text = SfJsonHelpers.GetString(item, "text");

        return new SfBroadcastRow(
            broadcastId: id,
            status: status,
            total: total,
            sent: sent,
            failed: failed,
            blocked: blocked,
            createdBy: createdBy,
            createdAt: createdAt,
            finishedAt: finishedAt,
            preview: MakePreview(text));
    }

    private static SfBroadcastDetails ParseBroadcastDetails(JsonElement root)
    {
        string? textsEn = null;
        string? textsFa = null;
        if (root.TryGetProperty("texts", out var textsEl) && textsEl.ValueKind == JsonValueKind.Object)
        {
            textsEn = SfJsonHelpers.GetStringOrNull(textsEl, "en");
            textsFa = SfJsonHelpers.GetStringOrNull(textsEl, "fa");
        }

        int total = SfJsonHelpers.GetInt(root, "total");
        int sent = SfJsonHelpers.GetInt(root, "sent");
        int failed = SfJsonHelpers.GetInt(root, "failed");
        int blocked = SfJsonHelpers.GetInt(root, "blocked");
        int pending = Math.Max(0, total - sent - failed - blocked);

        if (root.TryGetProperty("deliveries", out var delEl) && delEl.ValueKind == JsonValueKind.Object)
        {
            pending = SfJsonHelpers.GetInt(delEl, "pending", pending);
            sent = SfJsonHelpers.GetInt(delEl, "sent", sent);
            failed = SfJsonHelpers.GetInt(delEl, "failed", failed);
            blocked = SfJsonHelpers.GetInt(delEl, "blocked", blocked);
        }

        string audienceSummary = root.TryGetProperty("audience", out var audEl)
            ? FormatAudienceSummary(audEl)
            : "all";

        string? rawCreatedAt = SfJsonHelpers.GetStringOrNull(root, "created_at");
        string? rawStartedAt = SfJsonHelpers.GetStringOrNull(root, "started_at");
        string? rawFinishedAt = SfJsonHelpers.GetStringOrNull(root, "finished_at");

        var details = new SfBroadcastDetails
        {
            BroadcastId = SfJsonHelpers.GetString(root, "broadcast_id"),
            Status = SfJsonHelpers.GetString(root, "status", "draft"),
            Text = SfJsonHelpers.GetString(root, "text"),
            TextsEn = textsEn,
            TextsFa = textsFa,
            ParseMode = SfJsonHelpers.GetString(root, "parse_mode", "html"),
            AudienceSummary = audienceSummary,
            Error = SfJsonHelpers.GetStringOrNull(root, "error"),
            CreatedBy = SfJsonHelpers.GetString(root, "created_by"),
            CreatedAt = string.IsNullOrWhiteSpace(rawCreatedAt) ? string.Empty : SfJsonHelpers.FormatTimestamp(rawCreatedAt),
            StartedAt = string.IsNullOrWhiteSpace(rawStartedAt) ? null : SfJsonHelpers.FormatTimestamp(rawStartedAt),
            FinishedAt = string.IsNullOrWhiteSpace(rawFinishedAt) ? null : SfJsonHelpers.FormatTimestamp(rawFinishedAt),
            Total = total,
            Pending = pending,
            Sent = sent,
            Failed = failed,
            Blocked = blocked,
        };

        if (root.TryGetProperty("recent_errors", out var errsEl) && errsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var err in errsEl.EnumerateArray())
            {
                string? rawAt = SfJsonHelpers.GetStringOrNull(err, "at");
                details.RecentErrors.Add(new SfBroadcastErrorRow(
                    UserId: SfJsonHelpers.GetLong(err, "user_id"),
                    Status: SfJsonHelpers.GetString(err, "status"),
                    Error: SfJsonHelpers.GetString(err, "error"),
                    At: string.IsNullOrWhiteSpace(rawAt) ? string.Empty : SfJsonHelpers.FormatTimestamp(rawAt)));
            }
        }

        return details;
    }

    private static string MakePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string oneLine = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        return oneLine.Length <= 80 ? oneLine : oneLine[..80];
    }

    private static string FormatAudienceCount(int count, JsonElement root)
    {
        string countStr = count.ToString("N0", CultureInfo.InvariantCulture);
        string noun = count == 1 ? "recipient" : "recipients";

        if (root.TryGetProperty("by_lang", out var byLang) && byLang.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var prop in byLang.EnumerateObject())
            {
                int n = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int v)
                    ? v
                    : 0;
                parts.Add($"{prop.Name} {n.ToString("N0", CultureInfo.InvariantCulture)}");
            }

            if (parts.Count > 0)
                return $"{countStr} {noun} ({string.Join(", ", parts)})";
        }

        return $"{countStr} {noun}";
    }

    private static string FormatAudienceSummary(JsonElement aud)
    {
        if (aud.ValueKind != JsonValueKind.Object)
            return "all";

        var parts = new List<string>();
        string kind = SfJsonHelpers.GetString(aud, "kind", "all");
        parts.Add(kind);

        if (aud.TryGetProperty("user_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            int count = idsEl.GetArrayLength();
            parts.Add($"{count} user(s)");
        }

        int? activeDays = SfJsonHelpers.GetNullableInt(aud, "active_days");
        if (activeDays.HasValue)
            parts.Add($"active {activeDays.Value}d");

        var langs = SfJsonHelpers.GetStringList(aud, "langs");
        if (langs.Count > 0)
            parts.Add($"langs: {string.Join(", ", langs)}");

        if (SfJsonHelpers.GetBool(aud, "include_unreachable"))
            parts.Add("include unreachable");

        return string.Join(" | ", parts);
    }

    public void Dispose()
    {
        StopAutoRefresh();

        var audCts = Interlocked.Exchange(ref _audienceDebounceCts, null);
        if (audCts is not null)
        {
            try { audCts.Cancel(); } catch { }
            audCts.Dispose();
        }

        var detCts = Interlocked.Exchange(ref _detailsCts, null);
        if (detCts is not null)
        {
            try { detCts.Cancel(); } catch { }
            detCts.Dispose();
        }

        _refreshLock.Dispose();
    }
}
