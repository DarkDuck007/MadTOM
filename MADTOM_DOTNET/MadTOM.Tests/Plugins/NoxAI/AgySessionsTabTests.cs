using System;
using System.Linq;
using System.Threading.Tasks;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

public class AgySessionsTabTests
{
    private static NoxAgySessionsResponse Sample() => new()
    {
        HistoryMode = "agy",
        SinceUnix = 1_764_089_000,
        Settings = new NoxAgySessionSettings
        {
            RebaseGrowthTokens = "auto", RebaseGrowthMinTokens = 10000, RebaseGrowthMaxTokens = 24000,
            ColdRebuildAfterSeconds = 900, MaxSessionTurns = 0, MaxContextTokens = 65536, WorkingDialogueTurns = 70,
        },
        Rebuilds = new NoxAgyRebuildCounts { New = 2, Growth = 5, Cold = 4, Expired = 1 },
        InputTokens = new NoxAgyInputTokensByKind
        {
            Bootstrap = new NoxAgyInputTokens { Uncached = 54000, Cached = 0, Calls = 3 },
            Warm = new NoxAgyInputTokens { Uncached = 9000, Cached = 51000, Calls = 4 },
        },
        WarmCacheHit = 0.5,
        Sessions =
        {
            new NoxAgySession
            {
                ChatId = -100123, ChatTitle = "", Epoch = 3, Turns = 6, Tokens = 25000, BootstrapTokens = 18000,
                RebuildAtTokens = 32000, GrowthPerTurn = 1043.5, CacheHit = 0.876, IdleSeconds = 1250,
                StartedReason = "cold", NextTurnRebuilds = "cold",
            },
            new NoxAgySession
            {
                ChatId = 42, ChatTitle = "Alice", Epoch = 1, Turns = 2, Tokens = 19000, BootstrapTokens = 18000,
                RebuildAtTokens = 18000, IdleSeconds = 30, StartedReason = "new",
            },
        },
    };

    [Fact]
    public async Task ApplyResponse_fills_summaries_reasons_and_rows()
    {
        await using var manager = new NoxNodeManager();
        using var vm = new AgySessionsTabViewModel(manager);
        vm.ApplyResponse(Sample(), DateTimeOffset.FromUnixTimeSeconds(1_764_092_000));

        Assert.Equal(new[] { "new", "growth", "cold", "max_turns", "expired", "reset" },
            vm.RebuildReasons.Select(r => r.Code).ToArray());
        Assert.Equal(new long[] { 2, 5, 4, 0, 1, 0 }, vm.RebuildReasons.Select(r => r.Count).ToArray());
        Assert.StartsWith("12 rebuilds since", vm.RebuildSummary);
        Assert.Contains("growth 5", vm.RebuildSummary);
        Assert.DoesNotContain("max turns", vm.RebuildSummary);
        // hit share from the totals (51k of 60k), not the moving average
        Assert.Contains("9.0k uncached · 51.0k cached (85% hit)", vm.TokenSummary);
        Assert.Contains("54.0k uncached over 3 calls", vm.TokenSummary);
        Assert.Contains("idle 900 s", vm.SettingsSummary);
        Assert.Contains("turn limit off", vm.SettingsSummary);
        Assert.Contains("70 messages", vm.SettingsSummary);
        Assert.Equal("", vm.ErrorText);

        Assert.Equal(2, vm.Sessions.Count);
        var a = vm.Sessions[0];
        Assert.Equal("-100123", a.ChatTitle);
        Assert.Equal(0.5, a.FillFraction, 3);
        Assert.Equal("25.0k / 32.0k", a.SizeText);
        Assert.Equal("88%", a.CacheHitText);
        Assert.Equal("1.0k/turn", a.GrowthText);
        Assert.Equal("20 min", a.IdleText);
        Assert.True(a.WillRebuild);
        Assert.Equal("Idle (cache expired)", a.NextTurnText);
        var b = vm.Sessions[1];
        Assert.Equal("Alice", b.ChatTitle);
        Assert.Equal(0, b.FillFraction);   // rebuild point not above the bootstrap
        Assert.False(b.WillRebuild);
        Assert.Equal("", b.NextTurnText);
        Assert.Equal("First turn since NoxChat start", b.StartedReasonText);
        Assert.Equal("30 s", b.IdleText);
    }

    [Fact]
    public async Task ApplyResponse_shows_the_probe_error()
    {
        await using var manager = new NoxNodeManager();
        using var vm = new AgySessionsTabViewModel(manager);
        vm.ApplyResponse(new NoxAgySessionsResponse { Error = "decode agy_sessions reply: boom" }, DateTimeOffset.Now);
        Assert.Equal("decode agy_sessions reply: boom", vm.ErrorText);
        Assert.Empty(vm.Sessions);
        Assert.Equal(6, vm.RebuildReasons.Count);
    }

    [Fact]
    public void Formatting_helpers()
    {
        Assert.Equal("950", AgySessionsTabViewModel.FormatTokens(950L));
        Assert.Equal("18.0k", AgySessionsTabViewModel.FormatTokens(18000L));
        Assert.Equal("2.1 h", AgySessionsTabViewModel.FormatIdleDuration(7560));
        Assert.Equal("Token ceiling", AgySessionsTabViewModel.FormatReason("growth"));
        Assert.Equal("weird", AgySessionsTabViewModel.FormatReason("weird"));
    }
}
