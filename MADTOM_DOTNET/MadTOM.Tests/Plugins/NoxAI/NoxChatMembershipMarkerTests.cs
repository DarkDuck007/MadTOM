using System.Linq;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

/// NoxChat keeps a group's history when Nox leaves or is removed, and marks the boundaries with
/// "[CHAT EVENT <ts>] ..." lines glued (\n) before/after a message line (src/db/membership.py).
[Collection("Sequential")]
public class NoxChatMembershipMarkerTests
{
    private const string Transcript =
        "Group Chat: 'Rough Crowd'\n" +
        "[2026/11/27 22:00:00 UTC] [#12 Bully (@bully) -> #11]: get lost\n" +
        "[CHAT EVENT 2026/11/27 22:00:05 UTC] You were removed from this group here by Bully (@bully).\n" +
        "[CHAT EVENT 2026/11/28 09:30:00 UTC] You were added back to this group by Ann. Everything above is from before you were gone; the new conversation starts here.\n" +
        "[2026/11/28 09:31:00 UTC] [#40 Ann (@ann)]: welcome back Nox\n" +
        "[2026/11/28 09:31:10 UTC] [#41 Nox (You) -> #40]: thanks";

    [Fact]
    public void Markers_AreTheirOwnEventEntries_BetweenMessages()
    {
        var entries = NoxChatSessionParser.ParseChatEntries(Transcript);
        var bodies = entries.Select(e => e.Body).ToList();

        var bully = entries.Single(e => e.Body == "get lost");
        Assert.Equal("Bully", bully.Sender);
        Assert.False(bully.IsEvent);

        var events = entries.Where(e => e.IsEvent).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("[CHAT EVENT 2026/11/27 22:00:05 UTC]", events[0].Header);
        Assert.Equal("You were removed from this group here by Bully (@bully).", events[0].Body);
        Assert.StartsWith("You were added back to this group by Ann.", events[1].Body);
        Assert.All(events, e =>
        {
            Assert.False(e.IsBot);
            Assert.Equal(string.Empty, e.Sender);
            Assert.Equal("EVENT", e.BadgeText);
            Assert.True(e.HasBadge);
        });
        Assert.NotEqual(bully.CardBackgroundHex, events[0].CardBackgroundHex);

        // order kept: message, removed, added, next messages
        int iBully = entries.IndexOf(bully);
        Assert.Same(events[0], entries[iBully + 1]);
        Assert.Same(events[1], entries[iBully + 2]);
        var ann = entries[iBully + 3];
        Assert.Equal("Ann", ann.Sender);
        Assert.Equal("welcome back Nox", ann.Body);
        Assert.True(entries[iBully + 4].IsBot);
    }

    [Fact]
    public void LeftMarker_WithReason_AndWithoutTimestamp()
    {
        const string text =
            "[2026/11/27 22:00:00 UTC] [#5 Nox (You)]: I'm out. Enjoy talking to yourselves.\n" +
            "[CHAT EVENT] You left this group here. Your reason: constant insults";

        var entries = NoxChatSessionParser.ParseChatEntries(text);

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsBot);
        Assert.Equal("I'm out. Enjoy talking to yourselves.", entries[0].Body);
        Assert.True(entries[1].IsEvent);
        Assert.Equal("[CHAT EVENT]", entries[1].Header);
        Assert.Equal("You left this group here. Your reason: constant insults", entries[1].Body);
    }

    [Fact]
    public void MarkerText_InsideAMessageBody_IsNotAnEvent()
    {
        // Only a line that starts with the marker is one; a user quoting it mid-line stays message text.
        const string text = "[2026/11/28 09:31:00 UTC] [#40 Ann (@ann)]: he wrote [CHAT EVENT] lol";

        var entries = NoxChatSessionParser.ParseChatEntries(text);

        Assert.Single(entries);
        Assert.False(entries[0].IsEvent);
        Assert.Equal("he wrote [CHAT EVENT] lol", entries[0].Body);
    }

    [Fact]
    public void OrdinaryEntries_KeepUserAndBotStyling()
    {
        var entries = NoxChatSessionParser.ParseChatEntries(Transcript);
        var user = entries.Single(e => e.Sender == "Ann");
        var bot = entries.Single(e => e.Body == "thanks");

        Assert.Equal("USER", user.BadgeText);
        Assert.False(user.HasBadge);
        Assert.Equal("BOT", bot.BadgeText);
        Assert.Equal("#0D281E", bot.CardBackgroundHex);
        Assert.Equal("#10192A", user.CardBackgroundHex);
    }
}
