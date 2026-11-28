using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed class BanRow
{
    public long UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public int Strikes { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string Emoji { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool Active { get; init; }
    public long SecondsLeft { get; init; }
    public string BannedUntil { get; init; } = string.Empty;
    public long SourceChatId { get; init; }
    public IReadOnlyList<long> Evidence { get; init; } = Array.Empty<long>();
    public string LiftedBy { get; init; } = string.Empty;
    public IReadOnlyList<string> History { get; init; } = Array.Empty<string>();

    public string UserDisplay => string.IsNullOrWhiteSpace(UserName)
        ? UserId.ToString(CultureInfo.InvariantCulture)
        : $"{UserName} ({UserId})";

    public string ModeDisplay =>
        string.Equals(Mode, "react", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Emoji)
            ? $"react {Emoji}"
            : Mode;

    public string RemainingDisplay => Active
        ? FormatRemaining(SecondsLeft)
        : !string.IsNullOrWhiteSpace(LiftedBy)
            ? $"lifted by {LiftedBy}"
            : "expired";

    public string EvidenceDisplay => Evidence == null || Evidence.Count == 0
        ? string.Empty
        : string.Join(", ", Evidence.Select(id => $"#{id}"));

    public string StrikesDisplay => $"strike {Strikes}";

    public static string FormatRemaining(long seconds)
    {
        if (seconds <= 0)
        {
            return "expired";
        }

        if (seconds >= 86400)
        {
            long days = seconds / 86400;
            long hours = (seconds % 86400) / 3600;
            return $"{days}d {hours}h";
        }

        long h = seconds / 3600;
        long m = (seconds % 3600) / 60;
        return $"{h}h {m}m";
    }

    public static BanRow FromProto(NoxBan proto)
    {
        return new BanRow
        {
            UserId = proto.UserId,
            UserName = proto.UserName ?? string.Empty,
            Strikes = proto.Strikes,
            Mode = proto.Mode ?? string.Empty,
            Emoji = proto.Emoji ?? string.Empty,
            Reason = proto.Reason ?? string.Empty,
            Active = proto.Active,
            SecondsLeft = proto.SecondsLeft,
            BannedUntil = proto.BannedUntil ?? string.Empty,
            SourceChatId = proto.SourceChatId,
            Evidence = proto.Evidence.ToList(),
            LiftedBy = proto.LiftedBy ?? string.Empty,
            History = proto.History.Select(FormatHistoryEvent).ToList()
        };
    }

    private static string FormatHistoryEvent(NoxBanEvent e)
    {
        string createdAt = e.CreatedAt ?? string.Empty;
        string action = e.Action ?? string.Empty;
        string daysPart = e.Days > 0 ? $" {e.Days}d" : string.Empty;
        string actor = e.Actor ?? string.Empty;
        string reason = e.Reason ?? string.Empty;
        return $"{createdAt} {action}{daysPart} by {actor}: {reason}".Trim();
    }
}

public sealed partial class UserQuotasTabViewModel
{
    public ObservableCollection<BanRow> Bans { get; } = new();

    [ObservableProperty] private bool _includeInactiveBans;
    [ObservableProperty] private string _bansStatus = string.Empty;

    public bool HasBans => Bans.Count > 0;

    partial void OnIncludeInactiveBansChanged(bool value)
    {
        _ = RefreshBansAsync();
    }

    [RelayCommand]
    public async Task RefreshBansAsync()
    {
        var node = GetActiveNode();
        if (node == null) return;

        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.ListBansAsync(new NoxBanListRequest
            {
                IncludeInactive = IncludeInactiveBans
            }).ResponseAsync;

            if (!string.IsNullOrWhiteSpace(res.Error))
            {
                await DispatchUiAsync(() => BansStatus = res.Error);
                return;
            }

            var mapped = res.Bans.Select(BanRow.FromProto).ToList();

            await DispatchUiAsync(() =>
            {
                Bans.Clear();
                foreach (var ban in mapped)
                {
                    Bans.Add(ban);
                }

                OnPropertyChanged(nameof(HasBans));
                BansStatus = Bans.Count == 0 ? "Nobody is banned." : string.Empty;
            });
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => BansStatus = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => BansStatus = ex.Message);
        }
    }

    [RelayCommand]
    public async Task LiftBanAsync(BanRow? row)
    {
        if (row == null || !row.Active) return;

        long userId = row.UserId;
        string userDisplay = row.UserDisplay;

        await ExecuteWithConfirmAsync(
            "Lift Ban",
            $"Lift the ban on {userDisplay}?",
            () => LiftBanCoreAsync(userId));
    }

    private async Task LiftBanCoreAsync(long userId)
    {
        var node = GetActiveNode();
        if (node == null) return;

        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.BanActionAsync(new NoxBanActionRequest
            {
                Action = "lift",
                UserId = userId,
                Reason = "lifted in Studio"
            }).ResponseAsync;

            if (!res.Ok)
            {
                string msg = !string.IsNullOrWhiteSpace(res.Message) ? res.Message : "Failed to lift ban.";
                await DispatchUiAsync(() => BansStatus = msg);
                return;
            }

            await RefreshBansAsync();
        }
        catch (RpcException ex)
        {
            await DispatchUiAsync(() => BansStatus = FormatRpcError(ex));
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() => BansStatus = ex.Message);
        }
    }
}
