using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed record SfUserRow(
    long UserId,
    string? Username,
    string? FirstName,
    string Lang,
    bool Banned,
    int? WeeklyLimit,
    int Jobs7d,
    int Used,
    int Limit
)
{
    public string Label =>
        !string.IsNullOrWhiteSpace(Username) ? Username :
        !string.IsNullOrWhiteSpace(FirstName) ? FirstName :
        UserId.ToString(CultureInfo.InvariantCulture);

    public string QuotaText => $"{Used} / {Limit}";
    public bool HasOverride => WeeklyLimit.HasValue;
    public string StatusText => Banned ? "BANNED" : "ACTIVE";
    public string OverrideBadge => HasOverride ? $"override ({WeeklyLimit})" : "default";
}

public sealed partial class SfUsersViewModel : ObservableObject
{
    private SfRpc? _rpc;
    private int _refreshInFlight;
    private bool _suppressQuotaInputSync;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private SfUserRow? _selectedUser;
    [ObservableProperty] private string _quotaLimitInput = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<SfUserRow> Users { get; } = new();

    public bool HasSelectedUser => SelectedUser is not null;

    partial void OnSelectedUserChanged(SfUserRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedUser));
        if (!_suppressQuotaInputSync)
            QuotaLimitInput = value?.WeeklyLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public SfUsersViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            await LoadUsersCoreAsync(silent: false, ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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

    public async Task RefreshSilentAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        try
        {
            await LoadUsersCoreAsync(silent: true, ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private async Task LoadUsersCoreAsync(bool silent, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(SearchText))
            payload["query"] = SearchText.Trim();

        var root = await _rpc!.CallAsync("list_users", payload, ct);
        TotalCount = SfJsonHelpers.GetInt(root, "total");

        var incoming = new List<SfUserRow>();
        var incomingById = new Dictionary<long, SfUserRow>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var row = new SfUserRow(
                    UserId: SfJsonHelpers.GetLong(item, "user_id"),
                    Username: SfJsonHelpers.GetStringOrNull(item, "username"),
                    FirstName: SfJsonHelpers.GetStringOrNull(item, "first_name"),
                    Lang: SfJsonHelpers.GetString(item, "lang"),
                    Banned: SfJsonHelpers.GetBool(item, "banned"),
                    WeeklyLimit: SfJsonHelpers.GetNullableInt(item, "weekly_limit"),
                    Jobs7d: SfJsonHelpers.GetInt(item, "jobs_7d"),
                    Used: SfJsonHelpers.GetInt(item, "used"),
                    Limit: SfJsonHelpers.GetInt(item, "limit"));
                incoming.Add(row);
                incomingById[row.UserId] = row;
            }
        }

        long? prevId = SelectedUser?.UserId;

        for (int i = Users.Count - 1; i >= 0; i--)
        {
            if (!incomingById.ContainsKey(Users[i].UserId))
                Users.RemoveAt(i);
        }

        var existingIds = new HashSet<long>();
        for (int i = 0; i < Users.Count; i++)
        {
            var existing = Users[i];
            existingIds.Add(existing.UserId);
            var updated = incomingById[existing.UserId];
            if (existing != updated)
                Users[i] = updated;
        }

        foreach (var row in incoming)
        {
            if (existingIds.Add(row.UserId))
                Users.Add(row);
        }

        if (prevId.HasValue)
        {
            SfUserRow? match = null;
            foreach (var u in Users)
            {
                if (u.UserId == prevId.Value)
                {
                    match = u;
                    break;
                }
            }

            bool prevSuppress = _suppressQuotaInputSync;
            if (silent)
                _suppressQuotaInputSync = true;
            try
            {
                if (match is not null)
                {
                    if (!ReferenceEquals(SelectedUser, match))
                        SelectedUser = match;
                }
                else
                {
                    SelectedUser = null;
                }
            }
            finally
            {
                _suppressQuotaInputSync = prevSuppress;
            }
        }

        ErrorMessage = null;
    }

    public async Task SetQuotaAsync(SfUserRow user, int? weeklyLimit, CancellationToken ct = default)
    {
        if (user is null) return;
        if (weeklyLimit is < 0)
        {
            ErrorMessage = "invalid_quota: weekly_limit must be >= 0";
            return;
        }

        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["user_id"] = user.UserId,
                ["weekly_limit"] = weeklyLimit,
            };
            await _rpc.CallAsync("set_user_quota", payload, ct);
            ErrorMessage = null;
            await LoadAsync(ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
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

    public async Task SetBannedAsync(SfUserRow user, bool banned, CancellationToken ct = default)
    {
        if (user is null) return;
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["user_id"] = user.UserId,
                ["banned"] = banned,
            };
            await _rpc.CallAsync("set_user_banned", payload, ct);
            ErrorMessage = null;
            await LoadAsync(ct);
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
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
    public async Task ApplyQuotaInputAsync()
    {
        if (SelectedUser is null) return;
        if (string.IsNullOrWhiteSpace(QuotaLimitInput))
        {
            await SetQuotaAsync(SelectedUser, null);
            return;
        }

        if (!int.TryParse(QuotaLimitInput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit))
        {
            ErrorMessage = "invalid_quota: enter a whole number or leave empty to reset";
            return;
        }

        await SetQuotaAsync(SelectedUser, limit);
    }

    [RelayCommand]
    public async Task ResetQuotaAsync()
    {
        if (SelectedUser is null) return;
        await SetQuotaAsync(SelectedUser, null);
    }

    [RelayCommand]
    public async Task ToggleBanSelectedAsync()
    {
        if (SelectedUser is null) return;
        await SetBannedAsync(SelectedUser, !SelectedUser.Banned);
    }
}
