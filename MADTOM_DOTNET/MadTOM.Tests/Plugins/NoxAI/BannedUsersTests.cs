using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

/// NoxChat's global abuse bans in the User Quotas tab, and the " [banned]" sender tag in transcripts.
[Collection("Sequential")]
public class BannedUsersTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public int ListUserQuotaCalls { get; private set; }
        public List<NoxBanListRequest> ListRequests { get; } = new();
        public List<NoxBanActionRequest> Actions { get; } = new();
        public List<NoxBan> Bans { get; } = new();
        public string? ListError { get; set; }

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxListUserQuotasResponse> ListUserQuotasAsync(NoxListUserQuotasRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListUserQuotaCalls++;
            return Call(new NoxListUserQuotasResponse { Enabled = true, Defaults = new NoxUserQuotaDefaults() });
        }

        public override AsyncUnaryCall<NoxBanListResponse> ListBansAsync(NoxBanListRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListRequests.Add(request);
            var res = new NoxBanListResponse();
            if (ListError != null) res.Error = ListError;
            else res.Bans.AddRange(Bans.Where(b => request.IncludeInactive || b.Active));
            return Call(res);
        }

        public override AsyncUnaryCall<NoxBanActionResponse> BanActionAsync(NoxBanActionRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(request);
            var ban = Bans.FirstOrDefault(b => b.UserId == request.UserId && b.Active);
            if (ban == null) return Call(new NoxBanActionResponse { Ok = false, Message = "That person is not banned" });
            ban.Active = false;
            ban.LiftedBy = "MADTOM Studio";
            return Call(new NoxBanActionResponse { Ok = true, Message = "Ban lifted" });
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static NoxBan Ban(long id, string name, bool active = true) => new()
    {
        UserId = id, UserName = name, Strikes = 2, Mode = "react", Emoji = "🖕", Reason = "constant insults",
        Active = active, SecondsLeft = active ? 1209600 : 0, BannedUntil = "2026-12-12T12:00:00Z",
        SourceChatId = -1003920939724, Evidence = { 11, 12 },
        History =
        {
            new NoxBanEvent { Action = "ban", Days = 14, Actor = "Nox", Reason = "constant insults", CreatedAt = "2026-11-28T12:00:00Z" },
            new NoxBanEvent { Action = "lift", Actor = "admin", CreatedAt = "2026-11-20T12:00:00Z" },
        },
    };

    private static async Task<(UserQuotasTabViewModel vm, Client client, NoxNodeManager manager)> Create()
    {
        var client = new Client();
        client.Bans.Add(Ban(4242, "Bully"));
        client.Bans.Add(Ban(77, "", active: false));
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new UserQuotasTabViewModel(manager) { ConfirmAction = _ => true };
        vm.SelectedNode = Node;
        for (int i = 0; i < 200 && client.ListUserQuotaCalls == 0; i++) await Task.Delay(10);
        await vm.RefreshAsync();
        return (vm, client, manager);
    }

    [Fact]
    public async Task Refresh_LoadsActiveBans_AndFormatsRows()
    {
        var (vm, client, manager) = await Create();
        await using var _ = manager;

        Assert.True(vm.HasBans);
        var row = Assert.Single(vm.Bans);
        Assert.Equal("Bully (4242)", row.UserDisplay);
        Assert.Equal("strike 2", row.StrikesDisplay);
        Assert.Equal("react 🖕", row.ModeDisplay);
        Assert.Equal("14d 0h", row.RemainingDisplay);
        Assert.Equal("#11, #12", row.EvidenceDisplay);
        Assert.Equal(2, row.History.Count);
        Assert.Equal("2026-11-28T12:00:00Z ban 14d by Nox: constant insults", row.History[0]);
        Assert.Equal("2026-11-20T12:00:00Z lift by admin:", row.History[1]);
        Assert.All(client.ListRequests, r => Assert.False(r.IncludeInactive));
    }

    [Fact]
    public async Task IncludeInactive_ShowsLiftedAndExpired()
    {
        var (vm, client, manager) = await Create();
        await using var _ = manager;

        vm.IncludeInactiveBans = true;
        for (int i = 0; i < 200 && vm.Bans.Count < 2; i++) await Task.Delay(10);
        await vm.RefreshBansAsync();

        Assert.True(client.ListRequests.Last().IncludeInactive);
        Assert.Equal(2, vm.Bans.Count);
        var expired = vm.Bans.Single(b => b.UserId == 77);
        Assert.Equal("77", expired.UserDisplay);
        Assert.Equal("expired", expired.RemainingDisplay);
    }

    [Fact]
    public async Task Lift_SendsActionAndRefreshes()
    {
        var (vm, client, manager) = await Create();
        await using var _ = manager;

        await vm.LiftBanAsync(vm.Bans.Single());

        var action = Assert.Single(client.Actions);
        Assert.Equal("lift", action.Action);
        Assert.Equal(4242, action.UserId);
        Assert.Empty(vm.Bans);
        Assert.False(vm.HasBans);
        Assert.Equal("Nobody is banned.", vm.BansStatus);

        await vm.LiftBanAsync(null);  // no-op
        Assert.Single(client.Actions);
    }

    [Fact]
    public async Task ListError_IsShown_AndKeepsTheList()
    {
        var (vm, client, manager) = await Create();
        await using var _ = manager;

        client.ListError = "bridge down";
        await vm.RefreshBansAsync();

        Assert.Contains("bridge down", vm.BansStatus);
        Assert.Single(vm.Bans);
    }

    [Theory]
    [InlineData(1209600, "14d 0h")]
    [InlineData(90061, "1d 1h")]
    [InlineData(3720, "1h 2m")]
    [InlineData(59, "0h 0m")]
    [InlineData(0, "expired")]
    [InlineData(-5, "expired")]
    public void FormatRemaining(long seconds, string expected) => Assert.Equal(expected, BanRow.FormatRemaining(seconds));

    [Fact]
    public void LiftedRow_SaysWhoLiftedIt()
    {
        var proto = Ban(5, "Ann", active: false);
        proto.LiftedBy = "admin Danial";
        Assert.Equal("lifted by admin Danial", BanRow.FromProto(proto).RemainingDisplay);
        proto.Mode = "silent";
        proto.Emoji = "";
        Assert.Equal("silent", BanRow.FromProto(proto).ModeDisplay);
    }

    [Fact]
    public void Transcript_BannedTag_IsBadgedAndKeptOutOfTheSender()
    {
        const string text =
            "[2026/11/28 12:00:00 UTC] [#40 Bully (@bully) [banned] -> #39]: you're trash\n" +
            "[2026/11/28 12:01:00 UTC] [#41 Sara (@sara) [member] [banned]]: me too\n" +
            "[2026/11/28 12:02:00 UTC] [#42 Ann (@ann)]: ignore them";

        var entries = NoxChatSessionParser.ParseChatEntries(text);

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsBanned);
        Assert.Equal("Bully", entries[0].Sender);
        Assert.Equal("BANNED", entries[0].BadgeText);
        Assert.Equal("you're trash", entries[0].Body);
        Assert.True(entries[1].IsBanned && entries[1].IsMember);
        Assert.Equal("Sara", entries[1].Sender);
        Assert.False(entries[2].IsBanned);
        Assert.False(entries[2].HasBadge);
    }
}
