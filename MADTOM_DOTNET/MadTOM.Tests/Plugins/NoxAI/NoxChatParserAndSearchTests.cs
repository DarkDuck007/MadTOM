using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using MadTOM.Services;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class NoxChatParserAndSearchTests
{
    [Fact]
    public void ChatParser_Differentiates_SystemPromptSections_UserMessage_BotResponse_And_Media()
    {
        string requestJson = @"[
          {
            ""role"": ""system"",
            ""content"": ""You are Nox.\nRole: AI Assistant\n\nYOUR VOICE & STYLE:\n- concise, witty\n- dry humor\n\nCOMMUNICATION PROTOCOL:\nAlways respond in Markdown.\n\nPINNED FAVORITE STICKERS:\n• [pin_1]: happy duck sticker\n\nRELEVANT RECALLED MEMORIES:\n• [ID: 1] User lives in Tokyo\n\nCurrent Time: 2026-10-21 03:00:00 UTC""
          },
          {
            ""role"": ""user"",
            ""content"": ""Hey Nox, can you check this photo and tell me what you see?""
          }
        ]";

        string responseJson = @"{
          ""content"": ""<thought>User is asking about a photo. Need to reply concisely.</thought>I see a cute duck!""
        }";

        var item = new RawApiCallItemModel
        {
            CallId = "call_test_123",
            Caller = "main_agent",
            Model = "gpt-4o",
            TimestampText = "03:00:01.000",
            DurationMs = 450,
            PromptTokens = 120,
            CompletionTokens = 35,
            RequestJson = requestJson,
            ResponseJson = responseJson
        };

        item.Images.Add(new RawApiCallImageItem
        {
            Label = "User Photo #1",
            MimeType = "image/jpeg"
        });

        var turns = NoxChatSessionParser.Parse(item);

        Assert.Equal(3, turns.Count);

        // 1. System Prompt Turn
        var sysTurn = turns[0];
        Assert.Equal("system", sysTurn.Role);
        Assert.True(sysTurn.HasSystemSections);
        Assert.True(sysTurn.SystemSections.Count >= 5);
        Assert.Contains(sysTurn.SystemSections, s => s.Header.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase) || s.Header.Contains("VOICE", StringComparison.OrdinalIgnoreCase) || s.Header.Contains("ROLE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sysTurn.SystemSections, s => s.Header.Contains("COMMUNICATION", StringComparison.OrdinalIgnoreCase) || s.Header.Contains("PROTOCOL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sysTurn.SystemSections, s => s.Header.Contains("PINNED", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sysTurn.SystemSections, s => s.Header.Contains("MEMORY", StringComparison.OrdinalIgnoreCase) || s.Header.Contains("RECALLED", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sysTurn.SystemSections, s => s.Header.Contains("TIME", StringComparison.OrdinalIgnoreCase) || s.Header.Contains("CURRENT", StringComparison.OrdinalIgnoreCase));

        // 2. User Message Turn with attached media
        var userTurn = turns[1];
        Assert.Equal("user", userTurn.Role);
        Assert.Contains("Hey Nox", userTurn.Text);
        Assert.True(userTurn.HasMediaAttachments);
        Assert.Single(userTurn.MediaAttachments);
        Assert.Equal("User Photo #1", userTurn.MediaAttachments[0].Label);

        // 3. Bot Response Turn with Thought extraction
        var botTurn = turns[2];
        Assert.Equal("bot", botTurn.Role);
        Assert.True(botTurn.HasThought);
        Assert.Equal("User is asking about a photo. Need to reply concisely.", botTurn.ThoughtText);
        Assert.Equal("I see a cute duck!", botTurn.Text);
    }

    [Fact]
    public void ChatParser_Extracts_ToolCalls_From_ResponseJson()
    {
        string requestJson = @"[
          {""role"": ""user"", ""content"": ""What is the weather?""}
        ]";

        string responseJson = @"{
          ""content"": """",
          ""tool_calls"": [
            {
              ""name"": ""get_weather"",
              ""arguments"": ""{\""city\"": \""Tokyo\""}""
            }
          ]
        }";

        var item = new RawApiCallItemModel
        {
            CallId = "call_tool_1",
            Model = "gemini-2.5-flash",
            RequestJson = requestJson,
            ResponseJson = responseJson
        };

        var turns = NoxChatSessionParser.Parse(item);
        Assert.Equal(2, turns.Count);

        var botTurn = turns[1];
        Assert.True(botTurn.HasToolCalls);
        Assert.Single(botTurn.ToolCalls);
        Assert.Equal("get_weather", botTurn.ToolCalls[0].Name);
        Assert.Contains("Tokyo", botTurn.ToolCalls[0].Arguments);
    }

    [Fact]
    public async Task TelemetryTabViewModel_TimeFrame_Switching_Updates_Nanoseconds_Bounds()
    {
        await using var manager = new NoxNodeManager();
        var historyCache = new TelemetryHistoryCache();
        var vm = new NoxTelemetryTabViewModel(manager, historyCache);

        // Default range is 15M
        Assert.Equal("15M", vm.SelectedTimeRange);

        // Feed a live sample with nanosecond timestamp
        long sampleNano = 1776735000_000_000_000L;
        var sample = new NoxTelemetrySample
        {
            TimestampUnixNano = sampleNano
        };
        sample.Metrics["llm.tokens.in"] = 100;
        sample.Metrics["llm.tokens.out"] = 50;
        sample.Metrics["py.cpu.total_pct"] = 12.5;

        vm.OnTelemetrySampleReceived(sample);

        // For 15M (900 seconds = 900_000_000_000 ns)
        Assert.Equal(sampleNano, vm.WindowEnd);
        Assert.Equal(sampleNano - 900_000_000_000L, vm.WindowStart);

        // Switch to 5M (300 seconds = 300_000_000_000 ns)
        vm.SelectedTimeRange = "5M";
        Assert.Equal(300_000_000_000L, vm.WindowEnd - vm.WindowStart);

        // Switch to 1H (3600 seconds = 3_600_000_000_000 ns)
        vm.SelectedTimeRange = "1H";
        Assert.Equal(3_600_000_000_000L, vm.WindowEnd - vm.WindowStart);

        // Switch to 24H (86400 seconds = 86_400_000_000_000 ns)
        vm.SelectedTimeRange = "24H";
        Assert.Equal(86_400_000_000_000L, vm.WindowEnd - vm.WindowStart);
    }

    [Fact]
    public async Task MediaLibrary_RealTimeTextSearch_Matches_SetName_SetUrl_And_Definition()
    {
        await using var manager = new NoxNodeManager();
        var vm = new MediaLibraryTabViewModel(manager);

        var item1 = new MediaItemModel
        {
            UniqueId = "u1",
            SetName = "duck_stickers",
            CaptionText = "happy quacking yellow duck",
            Emoji = "🦆",
            Kind = "sticker",
            InLibrary = true
        };

        var item2 = new MediaItemModel
        {
            UniqueId = "u2",
            SetName = "cool_cats",
            CaptionText = "cat wearing sunglasses chilling",
            Emoji = "😎",
            Kind = "sticker",
            InLibrary = false
        };

        var item3 = new MediaItemModel
        {
            UniqueId = "u3",
            SetName = "pepe_reactions",
            CaptionText = "sad frog crying at computer",
            Emoji = "🐸",
            Kind = "sticker",
            InLibrary = true
        };

        vm.AllMediaItems.Add(item1);
        vm.AllMediaItems.Add(item2);
        vm.AllMediaItems.Add(item3);

        var pack = new StickerPackGroupModel("duck_stickers", "Duck Stickers", 1, 1, "", false, "🦆", _ => Task.CompletedTask);
        pack.Items.Add(item1);
        vm.PackGroups.Add(pack);
        vm.SelectedPack = pack;

        // 1. Search by sticker set name
        vm.SearchQuery = "cool_cats";
        Assert.Contains(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u2"));

        // 2. Search by Telegram set URL
        vm.SearchQuery = "https://t.me/addstickers/pepe_reactions";
        Assert.Contains(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u3"));

        // 3. Search by sticker definition (caption keyword)
        vm.SearchQuery = "sunglasses";
        Assert.Contains(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u2"));

        // 4. Search by emoji
        vm.SearchQuery = "🐸";
        Assert.Contains(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u3"));

        // 5. Clear search returns to pack items
        vm.ClearSearch();
        Assert.Contains(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u1"));
        Assert.DoesNotContain(vm.Rows, r => r.Items.Any(it => it.UniqueId == "u2"));
    }

    [Fact]
    public void ChatParser_Extracts_GroupChat_Name()
    {
        string req1 = @"[
          {""role"": ""system"", ""content"": ""You are Nox.""},
          {""role"": ""user"", ""content"": ""Group Chat: 'Japan dudes'\n[#190152 ShUu (@AmIr_ShUu)]: hello""}
        ]";
        string chat1 = NoxChatSessionParser.ExtractChatName(req1, "stream_step");
        Assert.Equal("Japan dudes", chat1);

        string req2 = @"[
          {""role"": ""system"", ""content"": ""You are Nox.""},
          {""role"": ""user"", ""content"": ""Group Chat: 'برنامه نویسی'\n[#12304 Danial (You)]: سلام""}
        ]";
        string chat2 = NoxChatSessionParser.ExtractChatName(req2, "stream_step");
        Assert.Equal("برنامه نویسی", chat2);
    }

    [Fact]
    public void ChatParser_Extracts_PrivateChat_Name_And_Fallback()
    {
        // Explicit Private Chat header
        string req1 = @"[
          {""role"": ""system"", ""content"": ""You are Nox.""},
          {""role"": ""user"", ""content"": ""Private Chat: 'Nicki🦦'\n[#555 Nicki🦦 (@nicki)]: hey""}
        ]";
        string chat1 = NoxChatSessionParser.ExtractChatName(req1, "stream_step");
        Assert.Equal("Nicki🦦", chat1);

        // Fallback from sender line in private chat
        string req2 = @"[
          {""role"": ""system"", ""content"": ""You are Nox.""},
          {""role"": ""user"", ""content"": ""[#3849 Danial (You)]: عکس سگه ولی آخرت مود بود...""}
        ]";
        string chat2 = NoxChatSessionParser.ExtractChatName(req2, "stream_step");
        Assert.Equal("Danial", chat2);

        // Fallback with other user talking to bot
        string req3 = @"[
          {""role"": ""system"", ""content"": ""You are Nox.""},
          {""role"": ""user"", ""content"": ""[#100 Diba (@diba)]: سلام نکس""}
        ]";
        string chat3 = NoxChatSessionParser.ExtractChatName(req3, "stream_step");
        Assert.Equal("Diba", chat3);
    }

    [Fact]
    public void ChatParser_Leaves_NonChat_Calls_Empty()
    {
        string stickerReq = @"[
          {""role"": ""system"", ""content"": ""You are an expert visual and media analyst specializing in Telegram stickers.""},
          {""role"": ""user"", ""content"": ""Examine these 6 animated stickers with indices: [19, 20].""}
        ]";
        string chat = NoxChatSessionParser.ExtractChatName(stickerReq, "sticker_analyst");
        Assert.Equal(string.Empty, chat);

        var model = new RawApiCallItemModel
        {
            Caller = "sticker_analyst",
            RequestJson = stickerReq,
            ChatName = chat
        };
        Assert.False(model.HasChatName);
        Assert.Equal(string.Empty, model.ChatName);
    }

    [Fact]
    public void RawApiCallItemModel_HasChatName_Populates_And_Notifies()
    {
        var model = new RawApiCallItemModel
        {
            Caller = "stream_step",
            ChatName = "Japan dudes"
        };
        Assert.True(model.HasChatName);
        Assert.Equal("Japan dudes", model.ChatName);

        model.ChatName = string.Empty;
        Assert.False(model.HasChatName);
    }

    [Fact]
    public void MemoryItemModel_ScoreDisplay_FormatsCorrectly()
    {
        var mem1 = new MemoryItemModel
        {
            Id = "m1",
            Content = "Test memory",
            Score = 0.8542
        };
        Assert.True(mem1.HasScore);
        Assert.Equal("Match: 0.85", mem1.ScoreDisplay);

        var mem2 = new MemoryItemModel
        {
            Id = "m2",
            Content = "No score memory",
            Score = 0.0
        };
        Assert.False(mem2.HasScore);
        Assert.Equal(string.Empty, mem2.ScoreDisplay);
    }

    [Fact]
    public void ChatParser_Detects_Rtl_And_Mixed_Languages_Correctly()
    {
        // 1. Pure Persian
        Assert.True(NoxChatSessionParser.IsRtl("سلام دنیا!"));

        // 2. Persian with embedded English words
        Assert.True(NoxChatSessionParser.IsRtl("این یک تست با کلمه Model و GPT است."));

        // 3. Persian prefixed with numbers and symbols
        Assert.True(NoxChatSessionParser.IsRtl("123 - [اطلاعیه]: پیام جدید رسید"));

        // 4. Pure English
        Assert.False(NoxChatSessionParser.IsRtl("Hello world, this is a test!"));

        // 5. English with embedded Persian words
        Assert.False(NoxChatSessionParser.IsRtl("The word 'سلام' means hello in Persian."));

        // 6. Symbols and digits only
        Assert.False(NoxChatSessionParser.IsRtl("12345 67890 !?#@"));

        // 7. Empty or whitespace
        Assert.False(NoxChatSessionParser.IsRtl(""));
        Assert.False(NoxChatSessionParser.IsRtl(null));
    }

    [Fact]
    public void ParsedTurnItemModel_Sets_FlowDirection_And_TextAlignment_Based_On_Rtl()
    {
        var rtlTurn = new ParsedTurnItemModel
        {
            Text = "سلام نوکس، چطوری؟",
            ThoughtText = "باید به کاربر به زبان فارسی پاسخ دهم."
        };

        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlTurn.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlTurn.TextAlignment);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlTurn.ThoughtFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlTurn.ThoughtTextAlignment);

        var ltrTurn = new ParsedTurnItemModel
        {
            Text = "Hey Nox, how is the server running?",
            ThoughtText = "Checking memory and CPU telemetry..."
        };

        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, ltrTurn.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, ltrTurn.TextAlignment);
        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, ltrTurn.ThoughtFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, ltrTurn.ThoughtTextAlignment);

        var rtlSection = new ParsedSystemSectionModel
        {
            Header = "قوانین",
            Content = "پاسخ‌ها باید همیشه کوتاه و مختصر باشند."
        };
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlSection.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlSection.TextAlignment);
    }

    [Fact]
    public void ChatParser_Parses_ParticipantsProfiles_Section_Correctly()
    {
        string prompt = "IDENTITY & ROLE:\nYou are Nox.\n\nPARTICIPANTS' PROFILES:\n• Alice (@alice, ID: 12345): Software engineer from Berlin.\n• Bob (@bob, ID: 67890): Musician.\n\nCOMMUNICATION PROTOCOL:\nBe concise.";

        var sections = NoxChatSessionParser.ParseSystemSections(prompt);

        Assert.Equal(3, sections.Count);
        var profSection = sections.FirstOrDefault(s => s.Header.Contains("PROFILES", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(profSection);
        Assert.Equal("PARTICIPANTS' PROFILES", profSection.Header);
        Assert.Contains("Alice (@alice, ID: 12345)", profSection.Content);
        Assert.Equal("#0EA5E9", profSection.BadgeColor);
        Assert.Equal("👤", profSection.SectionIcon);
    }

    [Fact]
    public void IsRtl_Handles_Emojis_MetadataPrefixes_And_Mixed_Languages_Accurately()
    {
        // 1. Emoji prefixes (surrogates)
        Assert.True(NoxChatSessionParser.IsRtl("💻 برنامه‌نویس پایتون و لینوکس"));
        Assert.True(NoxChatSessionParser.IsRtl("✨ سلام چطوری؟"));
        Assert.True(NoxChatSessionParser.IsRtl("👨‍💻 طراح سایت و عاشق هوش مصنوعی"));

        // 2. Sender prefixes in chat history
        Assert.True(NoxChatSessionParser.IsRtl("[#1234 Alice]: سلام دوستان، وقتتون بخیر"));
        Assert.True(NoxChatSessionParser.IsRtl("Danial: سلام به همه"));

        // 3. Latin prefixes or names in bio
        Assert.True(NoxChatSessionParser.IsRtl("Danial - توسعه‌دهنده نرم‌افزار"));
        Assert.True(NoxChatSessionParser.IsRtl("@myhandle | کانال رسمی و شخصی"));

        // 4. Pure English
        Assert.False(NoxChatSessionParser.IsRtl("Hello world, this is a test!"));
        Assert.False(NoxChatSessionParser.IsRtl("Senior software engineer based in Tokyo."));

        // 5. English with small embedded Persian mention
        Assert.False(NoxChatSessionParser.IsRtl("The Persian word for hello is 'سلام'."));
    }

    [Fact]
    public void UserProfileItemModel_FlowDirection_And_TextAlignment_Reflect_Bio_Language()
    {
        var rtlProfile = new UserProfileItemModel
        {
            FullName = "علی رضایی",
            Bio = "💻 علاقه‌مند به هوش مصنوعی و یادگیری ماشین. ساکن تهران.",
            PhotoDescription = "مرد جوان خندان در پارک با کت مشکی"
        };

        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlProfile.BioFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlProfile.BioTextAlignment);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlProfile.PhotoDescriptionFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlProfile.PhotoDescriptionTextAlignment);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, rtlProfile.TitleFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, rtlProfile.TitleTextAlignment);

        var ltrProfile = new UserProfileItemModel
        {
            FullName = "John Doe",
            Bio = "Software developer from London. Coffee lover.",
            PhotoDescription = "Young man wearing glasses indoors."
        };

        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, ltrProfile.BioFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, ltrProfile.BioTextAlignment);
        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, ltrProfile.PhotoDescriptionFlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, ltrProfile.PhotoDescriptionTextAlignment);
    }

    [Fact]
    public async Task MemoriesRagTabViewModel_Queries_And_Searches_UserProfiles_Successfully()
    {
        await using var manager = new NoxNodeManager();
        var vm = new MemoriesRagTabViewModel(manager);
        if (vm.SelectedNode != null)
        {
            await vm.RefreshUserProfiles();
            Assert.False(vm.StatusText.StartsWith("Failed", StringComparison.OrdinalIgnoreCase), vm.StatusText);

            vm.ProfileSearchQuery = "test";
            await vm.SearchUserProfiles();
            Assert.False(vm.StatusText.StartsWith("Profile search failed", StringComparison.OrdinalIgnoreCase), vm.StatusText);
        }
    }

    [Fact]
    public void NoxChatSessionParser_ParsesUserTurn_Into_DynamicBiDiChatEntries()
    {
        string rawUserText =
            "Group Chat: 'تیم فنی MADTOM'\n" +
            "[#1001 Danial]: سلام به همه همکاران گرامی. جلسه ساعت پنج شروع میشه.\n" +
            "[#1002 John]: Sounds good! I will upload the build logs before the meeting.\n" +
            "[#1003 سارا -> #1001]: ممنون، فایل گزارش رو آماده کردم.\n" +
            "[#1004 Alice]: Don't forget to push your latest commits to Git.";

        var entries = NoxChatSessionParser.ParseChatEntries(rawUserText);

        Assert.NotEmpty(entries);
        Assert.True(entries.Count >= 4);

        // Entry 1: Danial (Persian) -> RTL
        var danialEntry = entries.FirstOrDefault(e => e.Sender.Contains("Danial"));
        Assert.NotNull(danialEntry);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, danialEntry.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, danialEntry.TextAlignment);

        // Entry 2: John (English) -> LTR
        var johnEntry = entries.FirstOrDefault(e => e.Sender.Contains("John"));
        Assert.NotNull(johnEntry);
        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, johnEntry.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, johnEntry.TextAlignment);

        // Entry 3: Sara (Persian) -> RTL
        var saraEntry = entries.FirstOrDefault(e => e.Sender.Contains("سارا"));
        Assert.NotNull(saraEntry);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, saraEntry.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, saraEntry.TextAlignment);

        // Entry 4: Alice (English) -> LTR
        var aliceEntry = entries.FirstOrDefault(e => e.Sender.Contains("Alice"));
        Assert.NotNull(aliceEntry);
        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, aliceEntry.FlowDirection);
        Assert.Equal(Avalonia.Media.TextAlignment.Left, aliceEntry.TextAlignment);
    }

    [Fact]
    public void NoxChatSessionParser_ParsesUserTurn_WithoutHeaders_Into_DynamicBiDiEntries()
    {
        string prompt = "Hello world, this is a plain prompt.\n\nسلام دوستان، این یک متن تست به زبان فارسی است.";
        var entries = NoxChatSessionParser.ParseChatEntries(prompt);

        Assert.Equal(2, entries.Count);
        Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, entries[0].FlowDirection);
        Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, entries[1].FlowDirection);
    }

    [Fact]
    public void RawApiCallItemModel_PreservesChatName_And_FormatsFullDateTime()
    {
        var record = new MADTOM.Plugins.NoxAI.Proto.V1.NoxRawApiCallRecord
        {
            CallId = "call_test123",
            Caller = "stream_chat",
            Model = "gpt-4o",
            TimestampUnixNano = 1729632400000000000L,
            ChatName = "Project Alpha Devs"
        };

        var item = new RawApiCallItemModel
        {
            CallId = record.CallId,
            Caller = record.Caller,
            Model = record.Model,
            ChatName = record.ChatName,
            TimestampText = DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampUnixNano / 1_000_000).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
        };

        Assert.Equal("Project Alpha Devs", item.ChatName);
        Assert.True(item.HasChatName);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}$", item.TimestampText);
    }

    [Fact]
    public void NoxChatSessionParser_Differentiates_BotEntries_From_UserEntries_With_DistinctColorHex()
    {
        string prompt = @"Group Chat: 'Anime Club'
[#1001 Alice]: Hey bot, tell me a joke
[#1002 Nox (You) -> #1001]: Why did the chicken cross the road?
[#1003 Bob -> #1002]: Haha good one
[#1004 Minthara (You) -> #1003]: Silence, fool.";

        var entries = NoxChatSessionParser.ParseChatEntries(prompt);

        Assert.Equal(4, entries.Count);

        // Entry 1: Alice (User)
        Assert.False(entries[0].IsBot);
        Assert.Equal("#10192A", entries[0].CardBackgroundHex);
        Assert.Equal("#1E293B", entries[0].BorderBrushHex);
        Assert.Equal("#38BDF8", entries[0].HeaderForegroundHex);
        Assert.Equal("#E2E8F0", entries[0].TextForegroundHex);
        Assert.False(entries[0].HasBadge);

        // Entry 2: Nox (Bot)
        Assert.True(entries[1].IsBot);
        Assert.Equal("#0D281E", entries[1].CardBackgroundHex);
        Assert.Equal("#059669", entries[1].BorderBrushHex);
        Assert.Equal("#34D399", entries[1].HeaderForegroundHex);
        Assert.Equal("#ECFDF5", entries[1].TextForegroundHex);
        Assert.True(entries[1].HasBadge);
        Assert.Equal("BOT", entries[1].BadgeText);

        // Entry 3: Bob (User)
        Assert.False(entries[2].IsBot);
        Assert.Equal("#10192A", entries[2].CardBackgroundHex);
        Assert.False(entries[2].HasBadge);

        // Entry 4: Minthara (Bot persona)
        Assert.True(entries[3].IsBot);
        Assert.Equal("#0D281E", entries[3].CardBackgroundHex);
        Assert.True(entries[3].HasBadge);
        Assert.Equal("BOT", entries[3].BadgeText);
    }

    [Fact]
    public void MemoriesRagTabViewModel_Open_And_Close_AvatarLightbox()
    {
        var manager = new NoxNodeManager();
        var vm = new MemoriesRagTabViewModel(manager);

        var profile = new UserProfileItemModel
        {
            UserId = 12345,
            Username = "testuser",
            FullName = "Test User Full Name"
        };

        // Open with null profile and no selected user -> does nothing
        vm.SelectedUserProfile = null;
        vm.OpenAvatarLightbox(null);
        Assert.False(vm.IsAvatarLightboxOpen);

        // Open with profile
        vm.OpenAvatarLightbox(profile);
        Assert.True(vm.IsAvatarLightboxOpen);
        Assert.Equal("Test User Full Name", vm.LightboxTitle);
        Assert.Equal("@testuser", vm.LightboxSubtitle);

        // Close lightbox
        vm.CloseAvatarLightbox();
        Assert.False(vm.IsAvatarLightboxOpen);
    }
}
