using System.Linq;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

/// Rebuilt contexts (agy bootstrap, openai tiers, delta turns) carry NoxChat's UTC timestamps
/// ("[YYYY/MM/DD HH:MM:SS UTC]") and, in channel discussion groups, a " [member]" tag after the sender.
[Collection("Sequential")]
public class NoxChatRebuildParsingTests
{
    private static RawApiCallItemModel Call(string requestJson) => new()
    {
        CallId = "c1", Caller = "main_agent", Model = "m", TimestampText = "12:00:00.000",
        RequestJson = requestJson, ResponseJson = "{\"content\":\"<reply to=\\\"12\\\">ok</reply>\"}",
    };

    [Fact]
    public void ChatEntries_ParseUtcTimestampedLines_WithMemberTagAndBotLine()
    {
        const string text =
            "Group Chat: 'Channel Comments'\n" +
            "[2026/11/20 12:00:00 UTC] [#10 Alice (@alice)]: First comment\n" +
            "[2026/11/20 12:01:00 UTC] [#11 Nox (You) -> #10]: Welcome!\n" +
            "second line of the welcome\n" +
            "[2026/11/20 12:02:00 UTC] [#12 Sara (@sara) [member] -> #10]: Agreed\n" +
            "[Respond to this merged batch of message IDs: 12. Use reply tags for individual replies.]";

        var entries = NoxChatSessionParser.ParseChatEntries(text);
        var msgs = entries.Where(e => e.Body.Contains("comment") || e.Body.Contains("Welcome") || e.Body.Contains("Agreed")).ToList();

        Assert.Equal(3, msgs.Count);
        Assert.False(msgs[0].IsBot);
        Assert.False(msgs[0].IsMember);
        Assert.True(msgs[1].IsBot);
        Assert.Contains("second line of the welcome", msgs[1].Body);
        Assert.True(msgs[2].IsMember);
        Assert.DoesNotContain("[member]", msgs[2].Sender);
        Assert.Equal("MEMBER", msgs[2].BadgeText);
        Assert.Equal("BOT", msgs[1].BadgeText);
        Assert.DoesNotContain("[#11", msgs[0].Body); // lines are split, not swallowed into one block
    }

    [Fact]
    public void Parse_AgyBootstrap_IsStructured_NotFallbackText()
    {
        const string req = """
        {"messages":[
          {"role":"system","content":"You are Nox.\n\nCURRENT CHAT CONTEXT:\n• Type: Group Chat ('Channel Comments').\n• Channel discussion: this group is the comment section of a channel.\n• Clock: UTC.\n\nSTATEFUL SESSION PROTOCOL:\n- Stay in character."},
          {"role":"user","content":"Group Chat: 'Channel Comments'\n[2026/11/20 12:00:00 UTC] [#10 Alice (@alice)]: First comment\n[2026/11/20 12:01:00 UTC] [#11 Nox (You) -> #10]: Welcome!\n[2026/11/20 12:02:00 UTC] [#12 Sara (@sara) [member] -> #10]: Agreed"}
        ]}
        """;
        var turns = NoxChatSessionParser.Parse(Call(req));

        var user = turns.First(t => t.Role == "user");
        Assert.False(user.ShowFallbackText);
        Assert.True(user.ChatEntries.Count >= 3);

        var sys = turns.First(t => t.Role == "system");
        var ctx = sys.SystemSections.First(s => s.Header.StartsWith("CURRENT CHAT CONTEXT"));
        Assert.Contains("Channel discussion", ctx.Content);
        Assert.DoesNotContain(sys.SystemSections, s => s.Header.Contains("Channel discussion"));
    }

    [Fact]
    public void Parse_OpenAiTiers_And_DeltaTurn_SplitMessages()
    {
        const string tiers =
            "=== ARCHIVED CONVERSATION HISTORY ===\n" +
            "[2026/11/20 10:00:00 UTC] [#101 Bob (@bob)]: Earlier topic\n" +
            "[2026/11/20 10:01:00 UTC] [#102 Nox (You) -> #101]: Noted.\n\n" +
            "=== RECENT MESSAGES & CURRENT TURN ===\n" +
            "[2026/11/20 12:00:00 UTC] [#103 Bob (@bob)]: Ready to ship?";
        var e1 = NoxChatSessionParser.ParseChatEntries(tiers);
        Assert.Contains(e1, e => e.Body.Contains("Earlier topic") && !e.Body.Contains("Noted"));
        Assert.Contains(e1, e => e.IsBot && e.Body.Contains("Noted."));
        Assert.Contains(e1, e => e.Body.Contains("Ready to ship?"));

        const string delta =
            "[CONTEXT DIFF]\n• [RECALLED MEMORY ID: c3d4] Deployed on Ubuntu\n\n" +
            "[NEW MESSAGES]\n" +
            "[2026/11/20 12:05:00 UTC] [#105 Sara (@sara) [member] -> #104]: Thanks!";
        var e2 = NoxChatSessionParser.ParseChatEntries(delta);
        var thanks = Assert.Single(e2, e => e.Body.Contains("Thanks!"));
        Assert.True(thanks.IsMember);
    }

    [Fact]
    public void ChatEntries_EscapedNewlines_StillSplit()
    {
        const string text = "[2026/11/20 12:00:00 UTC] [#10 Alice]: one\\n[2026/11/20 12:01:00 UTC] [#11 Bob]: two";
        var entries = NoxChatSessionParser.ParseChatEntries(text);
        Assert.Contains(entries, e => e.Body.Trim() == "one");
        Assert.Contains(entries, e => e.Body.Trim() == "two");
    }

    [Fact]
    public void IsRtl_SkipsUtcHeaderAndMemberTag()
    {
        Assert.True(NoxChatSessionParser.IsRtl("[2026/11/20 12:00:00 UTC] [#12 Sara [member]]: سلام دوستان"));
        Assert.False(NoxChatSessionParser.IsRtl("[2026/11/20 12:00:00 UTC] [#12 علی [member]]: hello there"));
    }
}
