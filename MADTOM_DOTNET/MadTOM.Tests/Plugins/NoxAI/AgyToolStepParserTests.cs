using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

/// <summary>
/// agy tool steps re-carry the whole turn (context diff included) in their request, but the bridge sends the
/// model only what follows the previous assistant reply. The chat view must not show the diff once per step.
/// </summary>
public class AgyToolStepParserTests
{
    private const string Diff = "[CONTEXT DIFF]\n• [G4] = sleeping kitten\n• [NEW MEMORY ID: m1] [user_profile] Dina writes a novel\n\n[NEW MESSAGES]\n[#19968 Dina]: voice note";

    private static string Request(string session, bool nested = false, string? userContent = null)
    {
        string user = userContent ?? JsonSerializer.Serialize(Diff);
        string messages = $$$"""
            [{"role": "user", "content": {{{user}}}},
             {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "function": {"name": "transcribe_audio", "arguments": "{\"message_id\": 19968}"}}]},
             {"role": "tool", "tool_call_id": "c1", "name": "transcribe_audio", "content": "{\"status\": \"queued\"}"}]
            """;
        return nested
            ? $$$"""{"model": "m", "messages": {{{messages}}}, "extra_body": {"agy_session": {{{session}}}}}"""
            : $$$"""{"model": "m", "messages": {{{messages}}}, "agy_session": {{{session}}}}""";
    }

    private static RawApiCallItemModel Call(string request) => new() { CallId = "c", Model = "m", RequestJson = request };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolStepShowsOnlyWhatTheBridgeSent(bool nested)
    {
        var turns = NoxChatSessionParser.Parse(Call(Request("""{"chat_id": 1, "tool_step": 1, "history_mode": "agy"}""", nested)));

        Assert.Equal(new[] { "note", "tool" }, turns.Select(t => t.Role));
        Assert.Equal("TOOL STEP 1 // 2 EARLIER MESSAGES OF THIS TURN NOT RESENT", turns[0].HeaderTitle);
        Assert.DoesNotContain(turns, t => t.Text.Contains("CONTEXT DIFF") || t.Text.Contains("NEW MEMORY"));
        Assert.StartsWith("TOOL RESULT transcribe_audio c1", turns[1].HeaderTitle);
    }

    [Fact]
    public void StepZeroShowsTheWholeTurn()
    {
        var turns = NoxChatSessionParser.Parse(Call(Request("""{"chat_id": 1, "tool_step": 0}""")));
        Assert.Equal(new[] { "user", "bot", "tool" }, turns.Select(t => t.Role));
        Assert.Contains("CONTEXT DIFF", turns[0].Text);
    }

    [Theory]
    [InlineData("""{"model": "m", "messages": [{"role": "user", "content": "x"}], "agy_session": {"tool_step": 2}}""", 0, 2)] // no assistant yet
    [InlineData("""{"model": "m", "messages": [{"role": "user", "content": "x"}, {"role": "assistant", "content": "y"}], "agy_session": {"tool_step": 1}}""", 0, 1)] // nothing after it
    [InlineData("""{"model": "m", "messages": [{"role": "user", "content": "x"}, {"role": "assistant", "content": "y"}, {"role": "user", "content": "z"}], "agy_session": {"tool_step": "1"}}""", 0, 0)] // not a number
    [InlineData("""{"model": "m", "messages": [{"role": "user", "content": "x"}, {"role": "assistant", "content": "y"}, {"role": "user", "content": "z"}]}""", 0, 0)] // no session
    [InlineData("""{"model": "m", "messages": [{"role": "assistant", "content": "a"}, {"role": "tool", "content": "t"}, {"role": "assistant", "content": "b"}, {"role": "user", "content": "z"}], "agy_session": {"tool_step": 3}}""", 3, 3)] // last assistant wins
    public void SentSliceMirrorsTheBridge(string request, int sentFrom, int step)
    {
        using var doc = JsonDocument.Parse(request);
        var root = doc.RootElement;
        Assert.Equal((sentFrom, step), NoxChatSessionParser.AgySentSlice(root, root.GetProperty("messages")));
    }

    [Fact]
    public void ImagesOfSkippedMessagesAreNotReattached()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("fake image bytes");
        string url = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        string userContent = $$$"""[{"type": "text", "text": {{{JsonSerializer.Serialize(Diff)}}}}, {"type": "image_url", "image_url": {"url": "{{{url}}}"}}]""";
        var call = Call(Request("""{"tool_step": 1}""", userContent: userContent));
        // The probe also captures the bytes separately; the parser matches them by hash.
        call.Images.Add(new RawApiCallImageItem { Label = "probe", ContentHash = Convert.ToHexString(SHA256.HashData(bytes)), MimeType = "image/jpeg" });

        var turns = NoxChatSessionParser.Parse(call);

        Assert.Equal(new[] { "note", "tool" }, turns.Select(t => t.Role));
        Assert.All(turns, t => Assert.Empty(t.MediaAttachments));
    }
}
