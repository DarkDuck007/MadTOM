using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

public class NoxInlineImageTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static RawApiCallImageItem Image(string text) => new()
    {
        Label = "Image #1", MimeType = "image/png",
        ContentHash = Convert.ToHexString(SHA256.HashData(Bytes(text)))
    };
    private static object Part(string text) => new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(Bytes(text)) } };

    [Theory]
    [InlineData("user")]
    [InlineData("tool")]
    [InlineData("assistant")]
    [InlineData("system")]
    public void CapturedAndInlineCopies_AreOneAttachmentInTheCorrectTurn(string role)
    {
        var item = new RawApiCallItemModel { RequestJson = JsonSerializer.Serialize(new { messages = new[] { new { role, content = new[] { Part("pixels") } } } }) };
        var image = Image("pixels");
        item.Images.Add(image);
        var turns = NoxChatSessionParser.Parse(item);
        var attachment = Assert.Single(turns.SelectMany(t => t.MediaAttachments));
        Assert.Same(image, attachment);
        Assert.Equal("Image #1 (inline)", attachment.Label);
        Assert.Single(item.Images);
        // Re-parsing the same retained record must not add another copy.
        Assert.Single(NoxChatSessionParser.Parse(item).SelectMany(t => t.MediaAttachments));
    }

    [Fact]
    public void ImagesMatchTheirBytes_RatherThanCapturedArrayPosition()
    {
        var item = new RawApiCallItemModel { RequestJson = JsonSerializer.Serialize(new { messages = new[] {
            new { role = "user", content = new[] { Part("a") } },
            new { role = "tool", content = new[] { Part("b") } }
        } }) };
        var b = Image("b"); var a = Image("a");
        item.Images.Add(b); item.Images.Add(a);
        var turns = NoxChatSessionParser.Parse(item);
        Assert.Same(a, Assert.Single(turns[0].MediaAttachments));
        Assert.Same(b, Assert.Single(turns[1].MediaAttachments));
        Assert.Equal("Image #2 (inline)", a.Label);
        Assert.Equal("Image #1 (inline)", b.Label);
    }

    [Fact]
    public void UnmatchedCapturedImages_AreStillDisplayed()
    {
        var item = new RawApiCallItemModel { RequestJson = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = new[] { Part("inline") } } } }) };
        item.Images.Add(Image("inline")); item.Images.Add(Image("external"));
        Assert.Equal(2, NoxChatSessionParser.Parse(item).SelectMany(t => t.MediaAttachments).Count());
    }
}
