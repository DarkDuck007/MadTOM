using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed class ParsedSystemSectionModel
{
    public string Header { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string BadgeColor { get; init; } = "#A855F7";
    public string SectionIcon { get; init; } = "⚙";

    private bool? _isContentRtl;
    private bool IsContentRtl => _isContentRtl ??= NoxChatSessionParser.IsRtl(Content);

    public Avalonia.Media.FlowDirection FlowDirection => IsContentRtl ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => IsContentRtl ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
}

public sealed class ParsedToolCallModel
{
    public string Name { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
}

public sealed class ParsedChatEntryModel
{
    public string Header { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string FullText => string.IsNullOrEmpty(Header) ? Body : $"{Header} {Body}";
    public string Sender { get; init; } = string.Empty;
    public bool IsBot { get; init; }
    public bool IsMember { get; init; }

    private bool? _isBodyRtl;
    private bool IsBodyRtl => _isBodyRtl ??= NoxChatSessionParser.IsRtl(Body);

    public Avalonia.Media.FlowDirection FlowDirection => IsBodyRtl ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => IsBodyRtl ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public bool HasHeader => !string.IsNullOrWhiteSpace(Header);

    // Dynamic color coding: Bot messages have distinct emerald/mint styling, distinct from user messages
    public string CardBackgroundHex => IsBot ? "#0D281E" : "#10192A";
    public string BorderBrushHex => IsBot ? "#059669" : "#1E293B";
    public string HeaderForegroundHex => IsBot ? "#34D399" : "#38BDF8";
    public string TextForegroundHex => IsBot ? "#ECFDF5" : "#E2E8F0";
    public string BadgeBackgroundHex => IsBot ? "#064E3B" : "#0C4A6E";
    public string BadgeForegroundHex => IsBot ? "#6EE7B7" : "#7DD3FC";
    public string BadgeText => IsBot ? "BOT" : (IsMember ? "MEMBER" : "USER");
    public bool HasBadge => IsBot || IsMember;
}

public sealed partial class ParsedTurnItemModel : ObservableObject
{
    public string Role { get; init; } = "user"; // "system", "user", "bot"
    public string HeaderTitle { get; init; } = string.Empty;
    public string TimestampText { get; init; } = string.Empty;
    public string HeaderBadgeBackground { get; init; } = "#1E293B";
    public string HeaderBadgeForeground { get; init; } = "#38BDF8";
    public string BorderBrushHex { get; init; } = "#334155";
    public string Text { get; init; } = string.Empty;
    public string? ThoughtText { get; init; }
    public bool HasThought => !string.IsNullOrWhiteSpace(ThoughtText);

    private bool? _isTextRtl;
    private bool IsTextRtl => _isTextRtl ??= NoxChatSessionParser.IsRtl(Text);

    public Avalonia.Media.FlowDirection FlowDirection => IsTextRtl ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => IsTextRtl ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    private bool? _isThoughtRtl;
    private bool IsThoughtRtl => _isThoughtRtl ??= NoxChatSessionParser.IsRtl(ThoughtText);

    public Avalonia.Media.FlowDirection ThoughtFlowDirection => IsThoughtRtl ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment ThoughtTextAlignment => IsThoughtRtl ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public ObservableCollection<ParsedSystemSectionModel> SystemSections { get; } = new();
    public bool HasSystemSections => SystemSections.Count > 0;

    public ObservableCollection<ParsedChatEntryModel> ChatEntries { get; } = new();
    public bool HasChatEntries => ChatEntries.Count > 0;
    public bool ShowFallbackText => !HasChatEntries && !HasSystemSections && !string.IsNullOrEmpty(Text);

    public ObservableCollection<RawApiCallImageItem> MediaAttachments { get; } = new();
    public bool HasMediaAttachments => MediaAttachments.Count > 0;

    public ObservableCollection<ParsedToolCallModel> ToolCalls { get; } = new();
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public static class NoxChatSessionParser
{
    /// <summary>
    /// Determines whether the base direction of text should be Right-To-Left (RTL)
    /// based on the Unicode Bidirectional Algorithm (UAX #9) and dominant script analysis.
    /// Handles mixed LTR/RTL, emoji prefixes, username/handle prefixes, and line-level BiDi.
    /// Accurately classifies Persian, Arabic, Hebrew, Urdu, and other RTL scripts.
    /// </summary>
    public static bool IsRtl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Strip sender prefixes like "[2026/11/11 19:00:01 UTC] [#1234 Alice [member]]: ", "[#1234 Alice]: ", "[Alice -> #5678]: ", "Alice: ", "@alice: "
        int startIndex = 0;
        var prefixMatch = IsRtlPrefixRegex.Match(text);
        if (prefixMatch.Success)
        {
            startIndex = prefixMatch.Length;
        }
        else
        {
            var colonMatch = IsRtlColonPrefixRegex.Match(text);
            if (colonMatch.Success && colonMatch.Length < 30)
            {
                startIndex = colonMatch.Length;
            }
        }

        int rtlChars = 0;
        int ltrChars = 0;
        bool? firstStrongIsRtl = null;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];

            // Skip surrogate pairs (emojis like 👨‍💻, 💻, 🇮🇷, 💬)
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                continue;
            }

            // Skip whitespaces, digits, punctuation, and symbols
            if (char.IsWhiteSpace(c) || char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                continue;
            }

            // Arabic / Persian / Urdu: 0x0600 - 0x06FF, 0x0750 - 0x077F, 0x08A0 - 0x08FF, 0xFB50 - 0xFDFF, 0xFE70 - 0xFEFF
            // Hebrew: 0x0590 - 0x05FF, 0xFB1D - 0xFB4F
            // Syriac / Thaana / Samaritan / Mandaic: 0x0700 - 0x074F, 0x0780 - 0x07BF, 0x0800 - 0x083F
            if ((c >= 0x0590 && c <= 0x05FF) ||
                (c >= 0x0600 && c <= 0x06FF) ||
                (c >= 0x0700 && c <= 0x08FF) ||
                (c >= 0xFB1D && c <= 0xFDFF) ||
                (c >= 0xFE70 && c <= 0xFEFF))
            {
                rtlChars++;
                firstStrongIsRtl ??= true;
            }
            else if (char.IsLetter(c))
            {
                ltrChars++;
                firstStrongIsRtl ??= false;
            }
        }

        if (rtlChars > 0)
        {
            // 1. Equal or majority RTL characters
            if (rtlChars >= ltrChars) return true;

            // 2. First strong character after any metadata prefix was RTL
            if (firstStrongIsRtl == true) return true;

            // 3. Significant RTL content in mixed text (at least 3 RTL letters and represents 25%+ of all letters)
            if (rtlChars >= 3 && (double)rtlChars / (rtlChars + ltrChars) >= 0.25) return true;
        }

        return false;
    }

    private static readonly Regex IsRtlPrefixRegex = new(
        @"^(?:\[\d{4}[/-]\d{2}[/-]\d{2}\s+\d{2}:\d{2}:\d{2}(?:\s*(?:UTC|GMT|Z|[+-]\d{2}:?\d{2}))?\]\s*)?\[(?:(?:EDIT\s+)?#\d+\s+)?[^\r\n]+?(?:\s+\[member\])?(?:\s+->\s+#\d+)?\]:\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsRtlColonPrefixRegex = new(
        @"^[A-Za-z0-9_.\-@]+\s*:\s*",
        RegexOptions.Compiled);

    private static readonly Regex UnicodeEscapeRegex = new(
        @"\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled);

    private static readonly Regex PersonaNameRegex = new(
        @"^\s*You are ([^.\r\n]{1,40})\.",
        RegexOptions.Compiled);

    private static readonly Regex ThoughtRegex = new(@"<thought>(.*?)</thought>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BotActionTagRegex = new(
        @"<(?<tag>reply|msg|react|sticker|gif|image|document|remember|forget|hot|caption)\b(?<attrs>[^>]*)>(?<body>.*?)</\k<tag>>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SectionHeaderRegex = new(
        @"(?m)^(?:YOUR IDENTITY:|IDENTITY & ROLE:|YOUR VOICE & STYLE:|YOUR BACKGROUND & PERSPECTIVES:|CONVERSATION GUIDELINES:|TOOL USE PROTOCOL:|COMMUNICATION PROTOCOL:|PINNED FAVORITE STICKERS:|PINNED FAVORITE GIFS:|RELEVANT MEDIA CANDIDATES:|RELEVANT STICKERS:|RELEVANT GIFS:|PARTICIPANTS' STICKER PREFERENCES:|PARTICIPANTS' PROFILES:|USER PROFILES:|PARTICIPANTS' USER PROFILES:|CALLER PROFILE:|RELEVANT RECALLED MEMORIES:|HOT CONVERSATION MEMORY:|BACKGROUND TASK RESULTS:|ACTIVE BACKGROUND JOBS[^\r\n]*:|TIPS FOR THIS MOMENT[^\r\n]*:|STUDIO QUESTIONS WAITING FOR YOUR ANSWER[^\r\n]*:|SANDBOX DISK[^\r\n]*:|Your VM /data[^\r\n]*:|CURRENT CHAT CONTEXT:|STATEFUL SESSION PROTOCOL:|TELEGRAM GUEST MODE ACTIVE:|Current Time:|(?:\A|(?<=\r?\n[ \t]*\r?\n))(?:[A-Z][A-Z0-9 _\-'&/]{2,48}(?:\s*\([^)\r\n]*\))?:|#{1,3}\s+[^\r\n]+))",
        RegexOptions.Compiled);

    private static readonly Regex GroupChatRegex = new(
        @"Group\s+Chat:\s*['""]?([^'""\r\n]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PrivateChatRegex = new(
        @"Private\s+Chat:\s*['""]?([^'""\r\n]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContextGroupChatRegex = new(
        @"Type:\s*Group\s+Chat\s*\(\s*['""]?([^'"")\r\n]+)['""]?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContextPrivateChatRegex = new(
        @"Type:\s*1-on-1\s+Private\s+Chat\s*\(\s*Direct\s+Message\s+with\s+([^)\r\n]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitChatRegex = new(
        @"(?:^|[\r\n])Chat:\s*['""]?([^'""\r\n]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SenderLineRegex = new(
        @"\[(?:EDIT\s+)?#\d+\s+(?<sender>[^\r\n]+?)(?<member>\s+\[member\])?(?:\s+->\s+#\d+)?\]:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ChatEntryHeaderRegex = new(
        @"(?m)^(?:((?:\[\d{4}[/-]\d{2}[/-]\d{2}\s+\d{2}:\d{2}:\d{2}(?:\s*(?:UTC|GMT|Z|[+-]\d{2}:?\d{2}))?\]\s*)?\[(?:EDIT\s+)?#\d+\s+[^\r\n]+?(?:\s+\[member\])?(?:\s+->\s+#\d+)?\]:|\[CONTEXT DIFF\]|\[NEW MESSAGES\]|\[Your previous reply was assigned message ID\(s\):[^\]]*\]|\[Ambient Check:[^\]]*\]|\[Respond to this merged batch of message IDs:[^\]]*\]|\[Replied-to message[^\]]*\]:?|\[Quoted section from replied message\]:?|\[Attached [^\]]+\]|===\s*ARCHIVED CONVERSATION HISTORY\s*===|===\s*LIVE CONTEXT & RECALLED KNOWLEDGE\s*===|===\s*RECENT MESSAGES & CURRENT TURN\s*===|===\s*GUEST SESSION HISTORY\s*===|PINNED FAVORITE STICKERS:|PINNED FAVORITE GIFS:|RELEVANT MEDIA CANDIDATES:|RELEVANT STICKERS:|RELEVANT GIFS:|PARTICIPANTS' STICKER PREFERENCES:|PARTICIPANTS' PROFILES:|USER PROFILES:|PARTICIPANTS' USER PROFILES:|CALLER PROFILE:|RELEVANT RECALLED MEMORIES:|HOT CONVERSATION MEMORY:|BACKGROUND TASK RESULTS:|ACTIVE BACKGROUND JOBS[^\r\n]*:|TIPS FOR THIS MOMENT[^\r\n]*:|STUDIO QUESTIONS WAITING FOR YOUR ANSWER[^\r\n]*:|SANDBOX DISK[^\r\n]*:|Your VM /data[^\r\n]*:|\[Current request[^\]]*\]:|\[Incoming[^\]]*\]:|(?:Group|Private)?\s*Chat:\s*['""][^'""\r\n]+['""])[ \t]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string NormalizeEscapedNewlines(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains('\n') || !text.Contains("\\n", StringComparison.Ordinal))
        {
            return text;
        }
        return text.Replace("\\r\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static string? ExtractPersonaName(string systemText)
    {
        if (string.IsNullOrWhiteSpace(systemText)) return null;
        var m = PersonaNameRegex.Match(systemText);
        if (!m.Success) return null;
        string candidate = m.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(candidate) ||
            candidate.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("an ", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return candidate;
    }

    private static string UnescapeJsonSnippetForChatName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        string s = raw.Replace("\\r\\n", "\n", StringComparison.Ordinal)
                      .Replace("\\n", "\n", StringComparison.Ordinal)
                      .Replace("\\\"", "\"", StringComparison.Ordinal)
                      .Replace("\\'", "'", StringComparison.Ordinal);
        if (s.Contains(@"\u", StringComparison.Ordinal))
        {
            s = UnicodeEscapeRegex.Replace(s, m =>
            {
                int code = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                return ((char)code).ToString();
            });
        }
        return s;
    }

    private static string CleanExtractedChatTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int nl = raw.IndexOf(@"\n", StringComparison.Ordinal);
        if (nl >= 0) raw = raw[..nl];
        int cr = raw.IndexOf(@"\r", StringComparison.Ordinal);
        if (cr >= 0) raw = raw[..cr];
        return raw.Trim(' ', '\t', '\'', '"', '\\', '.');
    }

    public static string ExtractChatName(string? requestJson, string? caller)
    {
        if (string.IsNullOrWhiteSpace(requestJson)) return string.Empty;
        string snippet = UnescapeJsonSnippetForChatName(requestJson);

        // 1. Check for Group Chat: 'Title'
        var mGroup = GroupChatRegex.Match(snippet);
        if (mGroup.Success)
        {
            string title = CleanExtractedChatTitle(mGroup.Groups[1].Value);
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 2. Check for Private Chat: 'Title'
        var mPriv = PrivateChatRegex.Match(snippet);
        if (mPriv.Success)
        {
            string title = CleanExtractedChatTitle(mPriv.Groups[1].Value);
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 3. Check for CURRENT CHAT CONTEXT: Type: Group Chat ('Title') or Direct Message with Title
        var mCtxGroup = ContextGroupChatRegex.Match(snippet);
        if (mCtxGroup.Success)
        {
            string title = CleanExtractedChatTitle(mCtxGroup.Groups[1].Value);
            if (!string.IsNullOrEmpty(title)) return title;
        }

        var mCtxPriv = ContextPrivateChatRegex.Match(snippet);
        if (mCtxPriv.Success)
        {
            string title = CleanExtractedChatTitle(mCtxPriv.Groups[1].Value);
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 4. Check for Chat: 'Title'
        var mChat = ExplicitChatRegex.Match(snippet);
        if (mChat.Success)
        {
            string title = CleanExtractedChatTitle(mChat.Groups[1].Value);
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // No fallback to sender names: a sender is not a chat (agy context diffs carry no chat
        // header, so this used to label group calls with whoever spoke). Chat identity comes from
        // the call's trace chat_id / NoxChat's chat_name instead.
        return string.Empty;
    }

    private static string CleanSenderName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int parenIdx = raw.IndexOf('(');
        if (parenIdx > 0)
        {
            return raw[..parenIdx].Trim();
        }
        return raw.Trim();
    }

    public static List<ParsedTurnItemModel> Parse(RawApiCallItemModel callItem)
    {
        var turns = new List<ParsedTurnItemModel>();
        if (callItem == null) return turns;

        var availableImages = new List<RawApiCallImageItem>(callItem.Images);
        bool requestParsed = false;
        string? botPersonaName = null;

        // 1. Parse Request JSON (messages array or prompt)
        try
        {
            if (!string.IsNullOrWhiteSpace(callItem.RequestJson))
            {
                using var doc = JsonDocument.Parse(callItem.RequestJson);
                var root = doc.RootElement;

                JsonElement messagesArray = default;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    messagesArray = root;
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    messagesArray = msgs;
                }

                if (messagesArray.ValueKind == JsonValueKind.Array)
                {
                    requestParsed = true;
                    var (sentFrom, toolStep) = AgySentSlice(root, messagesArray);
                    int index = -1;
                    foreach (var m in messagesArray.EnumerateArray())
                    {
                        index++;
                        int firstTurn = turns.Count;
                        string role = m.TryGetProperty("role", out var r) ? (r.GetString() ?? "user") : "user";
                        string textContent = string.Empty;
                        var inlineImages = new List<RawApiCallImageItem>();

                        if (m.TryGetProperty("content", out var c))
                        {
                            if (c.ValueKind == JsonValueKind.String)
                            {
                                textContent = c.GetString() ?? string.Empty;
                            }
                            else if (c.ValueKind == JsonValueKind.Array)
                            {
                                var textParts = new List<string>();
                                foreach (var part in c.EnumerateArray())
                                {
                                    if (part.ValueKind == JsonValueKind.String)
                                    {
                                        textParts.Add(part.GetString() ?? "");
                                    }
                                    else if (part.ValueKind == JsonValueKind.Object)
                                    {
                                        string pType = part.TryGetProperty("type", out var pt) ? pt.GetString() ?? "" : "";
                                        if ((pType is "text" or "input_text" or "") && part.TryGetProperty("text", out var txtVal) && txtVal.ValueKind == JsonValueKind.String)
                                        {
                                            textParts.Add(txtVal.GetString() ?? "");
                                        }
                                        else if (pType == "image_url" && part.TryGetProperty("image_url", out var iu))
                                        {
                                            string url = iu.ValueKind == JsonValueKind.String ? iu.GetString() ?? ""
                                                : iu.ValueKind == JsonValueKind.Object && iu.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                                            var img = ResolveInlineImage(url, callItem, availableImages);
                                            if (img != null)
                                            {
                                                inlineImages.Add(img);
                                            }
                                        }
                                    }
                                }
                                textContent = string.Join("\n", textParts);
                            }
                            else if (c.ValueKind == JsonValueKind.Object)
                            {
                                textContent = c.GetRawText();
                            }
                        }

                        // Already sent in an earlier step of this turn: the bridge does not send it again.
                        if (index < sentFrom) continue;

                        if (role is "system" or "developer")
                        {
                            botPersonaName ??= ExtractPersonaName(textContent);
                            var sysTurn = new ParsedTurnItemModel
                            {
                                Role = "system",
                                HeaderTitle = "SYSTEM PROMPT // INSTRUCTIONS & CONTEXT",
                                TimestampText = callItem.TimestampText,
                                HeaderBadgeBackground = "#3B0764",
                                HeaderBadgeForeground = "#C084FC",
                                BorderBrushHex = "#9333EA",
                                Text = textContent
                            };

                            var sections = ParseSystemSections(textContent);
                            foreach (var s in sections)
                            {
                                sysTurn.SystemSections.Add(s);
                            }
                            turns.Add(sysTurn);
                        }
                        else if (role == "user")
                        {
                            var userTurn = new ParsedTurnItemModel
                            {
                                Role = "user",
                                HeaderTitle = "USER MESSAGE",
                                TimestampText = callItem.TimestampText,
                                HeaderBadgeBackground = "#082F49",
                                HeaderBadgeForeground = "#38BDF8",
                                BorderBrushHex = "#0284C7",
                                Text = textContent
                            };

                            var chatEntries = ParseChatEntries(textContent, botPersonaName);
                            foreach (var e in chatEntries)
                            {
                                userTurn.ChatEntries.Add(e);
                            }

                            turns.Add(userTurn);
                        }
                        else if (role is "tool" or "function")
                        {
                            string name = m.TryGetProperty("name", out var toolName) ? toolName.GetString() ?? "" : "";
                            string id = m.TryGetProperty("tool_call_id", out var toolId) ? toolId.GetString() ?? "" : "";
                            turns.Add(new ParsedTurnItemModel
                            {
                                Role = "tool",
                                HeaderTitle = $"TOOL RESULT {name} {id}".Trim(),
                                TimestampText = callItem.TimestampText,
                                Text = textContent,
                                HeaderBadgeBackground = "#422006",
                                HeaderBadgeForeground = "#FBBF24",
                                BorderBrushHex = "#A16207"
                            });
                        }
                        else if (role == "assistant")
                        {
                            var (botContent, thought) = ExtractThoughts(textContent);

                            var asstTurn = new ParsedTurnItemModel
                            {
                                Role = "bot",
                                HeaderTitle = "BOT (HISTORY)",
                                TimestampText = callItem.TimestampText,
                                HeaderBadgeBackground = "#064E3B",
                                HeaderBadgeForeground = "#34D399",
                                BorderBrushHex = "#059669",
                                Text = botContent,
                                ThoughtText = thought
                            };
                            foreach (var botEntry in ParseBotResponseEntries(botContent))
                            {
                                asstTurn.ChatEntries.Add(botEntry);
                            }
                            if (m.TryGetProperty("tool_calls", out var historyTools) && historyTools.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tool in historyTools.EnumerateArray())
                                {
                                    if (!tool.TryGetProperty("function", out var function)) continue;
                                    asstTurn.ToolCalls.Add(new ParsedToolCallModel
                                    {
                                        Name = function.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                                        Arguments = function.TryGetProperty("arguments", out var args) ? args.ToString() : ""
                                    });
                                }
                            }
                            turns.Add(asstTurn);
                        }
                        if (turns.Count > firstTurn)
                            foreach (var image in inlineImages) turns[^1].MediaAttachments.Add(image);
                    }
                    if (sentFrom > 0)
                    {
                        turns.Insert(0, new ParsedTurnItemModel
                        {
                            Role = "note",
                            HeaderTitle = $"TOOL STEP {toolStep} // {sentFrom} EARLIER MESSAGE{(sentFrom == 1 ? "" : "S")} OF THIS TURN NOT RESENT",
                            TimestampText = callItem.TimestampText,
                            HeaderBadgeBackground = "#1E293B",
                            HeaderBadgeForeground = "#94A3B8",
                            BorderBrushHex = "#475569",
                            Text = "The request repeats the whole turn, but the agy bridge sends the model only what follows " +
                                   "its previous reply. The context diff and new messages were sent once, in step 0 of this turn."
                        });
                    }
                }
            }
        }
        catch { }

        // 2. Parse Response JSON (current bot completion)
        bool responseParsed = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(callItem.ResponseJson))
            {
                using var respDoc = JsonDocument.Parse(callItem.ResponseJson);
                responseParsed = true;
                var respRoot = respDoc.RootElement;
                string botReply = string.Empty;
                string? botThought = null;
                var toolCalls = new List<ParsedToolCallModel>();

                if (respRoot.TryGetProperty("content", out var cVal))
                {
                    botReply = cVal.GetString() ?? string.Empty;
                }
                else if (respRoot.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out var msgObj))
                    {
                        if (msgObj.TryGetProperty("content", out var c2))
                        {
                            botReply = c2.GetString() ?? string.Empty;
                        }
                        if (msgObj.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in tc.EnumerateArray())
                            {
                                string name = "function";
                                string args = "";
                                if (item.TryGetProperty("function", out var f))
                                {
                                    if (f.TryGetProperty("name", out var n)) name = n.GetString() ?? "function";
                                    if (f.TryGetProperty("arguments", out var a)) args = a.GetString() ?? "";
                                }
                                toolCalls.Add(new ParsedToolCallModel { Name = name, Arguments = args });
                            }
                        }
                    }
                }

                if (respRoot.TryGetProperty("tool_calls", out var directTc) && directTc.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in directTc.EnumerateArray())
                    {
                        string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                        string args = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";
                        toolCalls.Add(new ParsedToolCallModel { Name = name, Arguments = args });
                    }
                }

                (botReply, botThought) = ExtractThoughts(botReply);

                var botTurn = new ParsedTurnItemModel
                {
                    Role = "bot",
                    HeaderTitle = $"BOT RESPONSE // {callItem.Model} ({callItem.DurationMs}ms · {callItem.CompletionTokens} tok)",
                    TimestampText = callItem.TimestampText,
                    HeaderBadgeBackground = "#064E3B",
                    HeaderBadgeForeground = "#34D399",
                    BorderBrushHex = "#10B981",
                    Text = botReply,
                    ThoughtText = botThought
                };

                foreach (var botEntry in ParseBotResponseEntries(botReply))
                {
                    botTurn.ChatEntries.Add(botEntry);
                }

                foreach (var tc in toolCalls)
                {
                    botTurn.ToolCalls.Add(tc);
                }

                turns.Add(botTurn);
            }
        }
        catch { }

        // 3. Attach any remaining images to the last user message, or to a dedicated turn
        if (availableImages.Count > 0)
        {
            var userTurn = turns.LastOrDefault(t => t.Role == "user");
            if (userTurn != null)
            {
                foreach (var image in availableImages) userTurn.MediaAttachments.Add(image);
            }
            else
            {
                var mediaTurn = new ParsedTurnItemModel
                {
                    Role = "user",
                    HeaderTitle = "ATTACHED MULTIMODAL MEDIA",
                    TimestampText = callItem.TimestampText,
                    HeaderBadgeBackground = "#1E1B4B",
                    HeaderBadgeForeground = "#818CF8",
                    BorderBrushHex = "#4F46E5",
                    Text = "Multimodal images intercepted with this API prompt:"
                };
                foreach (var image in availableImages) mediaTurn.MediaAttachments.Add(image);
                turns.Insert(Math.Min(1, turns.Count), mediaTurn);
            }
        }

        // 4. Fallback if request/response JSON could not be parsed or no structured turns were extracted
        if (!requestParsed && !string.IsNullOrWhiteSpace(callItem.RequestJson))
        {
            var rawUserTurn = new ParsedTurnItemModel
            {
                Role = "user",
                HeaderTitle = $"RAW REQUEST // {callItem.Caller}",
                TimestampText = callItem.TimestampText,
                HeaderBadgeBackground = "#1E293B",
                HeaderBadgeForeground = "#94A3B8",
                BorderBrushHex = "#475569",
                Text = callItem.RequestJson
            };
            var rawEntries = ParseChatEntries(callItem.RequestJson, botPersonaName);
            foreach (var e in rawEntries)
            {
                rawUserTurn.ChatEntries.Add(e);
            }
            turns.Insert(0, rawUserTurn);
        }

        if (!responseParsed && !string.IsNullOrWhiteSpace(callItem.ResponseJson))
        {
            turns.Add(new ParsedTurnItemModel
            {
                Role = "bot",
                HeaderTitle = $"RAW RESPONSE // {callItem.Model}",
                TimestampText = callItem.TimestampText,
                HeaderBadgeBackground = "#064E3B",
                HeaderBadgeForeground = "#34D399",
                BorderBrushHex = "#10B981",
                Text = callItem.ResponseJson
            });
        }

        if (turns.Count == 0)
        {
            var rawUserTurn = new ParsedTurnItemModel
            {
                Role = "user",
                HeaderTitle = $"RAW REQUEST // {callItem.Caller}",
                TimestampText = callItem.TimestampText,
                HeaderBadgeBackground = "#1E293B",
                HeaderBadgeForeground = "#94A3B8",
                BorderBrushHex = "#475569",
                Text = callItem.RequestJson
            };
            turns.Add(rawUserTurn);
        }

        // 5. Ensure ChatName is populated on callItem if detected from unescaped turns
        if (string.IsNullOrEmpty(callItem.ChatName))
        {
            foreach (var t in turns)
            {
                if (t.Role == "user")
                {
                    string found = ExtractChatName(t.Text, callItem.Caller);
                    if (!string.IsNullOrEmpty(found))
                    {
                        callItem.ChatName = found;
                        break;
                    }
                }
            }
        }

        return turns;
    }

    /// <summary>
    /// For agy stateful sessions a tool step (<c>agy_session.tool_step</c> &gt; 0) carries the whole turn again,
    /// but the bridge sends only the messages after the last assistant message (see the bridge's
    /// <c>_slice_active_messages</c>). Returns the index of the first message actually sent (0: all) and the step.
    /// </summary>
    public static (int SentFrom, int ToolStep) AgySentSlice(JsonElement root, JsonElement messages)
    {
        if (root.ValueKind != JsonValueKind.Object || messages.ValueKind != JsonValueKind.Array) return (0, 0);
        if (!TryGetAgySession(root, out var session) &&
            !(root.TryGetProperty("extra_body", out var extra) && TryGetAgySession(extra, out session)))
            return (0, 0);
        if (!session.TryGetProperty("tool_step", out var ts) || ts.ValueKind != JsonValueKind.Number ||
            !ts.TryGetInt32(out int step) || step <= 0)
            return (0, 0);
        int last = -1, count = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("role", out var r) &&
                r.ValueKind == JsonValueKind.String && r.GetString() == "assistant")
                last = count;
            count++;
        }
        return (last >= 0 && last + 1 < count ? last + 1 : 0, step);
    }

    private static bool TryGetAgySession(JsonElement obj, out JsonElement session)
    {
        session = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("agy_session", out session) &&
               session.ValueKind == JsonValueKind.Object;
    }

    public static List<ParsedSystemSectionModel> ParseSystemSections(string systemPrompt)
    {
        var sections = new List<ParsedSystemSectionModel>();
        if (string.IsNullOrWhiteSpace(systemPrompt)) return sections;
        systemPrompt = NormalizeEscapedNewlines(systemPrompt);

        var matches = SectionHeaderRegex.Matches(systemPrompt);
        if (matches.Count == 0)
        {
            // If no regex match, split by double newlines
            var blocks = systemPrompt.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < blocks.Length; i++)
            {
                string b = blocks[i].Trim();
                sections.Add(ClassifySection($"SECTION {i + 1}", b));
            }
            return sections;
        }

        // If there is preamble before first match
        if (matches[0].Index > 0)
        {
            string pre = systemPrompt[..matches[0].Index].Trim();
            if (!string.IsNullOrWhiteSpace(pre))
            {
                sections.Add(ClassifySection("IDENTITY & ROLE", pre));
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int length = (i + 1 < matches.Count) ? matches[i + 1].Index - start : systemPrompt.Length - start;
            string slice = systemPrompt.Substring(start, length).Trim();

            string header = matches[i].Value.Trim().TrimEnd(':').TrimStart('#').Trim();
            string content = slice[matches[i].Value.Length..].Trim();

            sections.Add(ClassifySection(header, content));
        }

        return sections;
    }

    private static (string CleanReply, string? Thought) ExtractThoughts(string text)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, null);
        var matches = ThoughtRegex.Matches(text);
        if (matches.Count == 0) return (text, null);

        var thoughts = new List<string>(matches.Count);
        foreach (Match m in matches)
        {
            string val = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(val)) thoughts.Add(val);
        }
        string cleaned = ThoughtRegex.Replace(text, string.Empty).Trim();
        return (cleaned, thoughts.Count > 0 ? string.Join("\n\n", thoughts) : null);
    }

    public static List<ParsedChatEntryModel> ParseBotResponseEntries(string botReply)
    {
        var entries = new List<ParsedChatEntryModel>();
        if (string.IsNullOrWhiteSpace(botReply)) return entries;

        var matches = BotActionTagRegex.Matches(botReply);
        if (matches.Count == 0) return entries;

        int cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
            {
                string between = botReply[cursor..m.Index].Trim();
                if (!string.IsNullOrWhiteSpace(between))
                {
                    entries.Add(new ParsedChatEntryModel
                    {
                        Header = "[BOT TEXT]",
                        Body = between,
                        Sender = "Nox",
                        IsBot = true
                    });
                }
            }

            string tag = m.Groups["tag"].Value.ToLowerInvariant();
            string attrs = m.Groups["attrs"].Value.Trim();
            string body = m.Groups["body"].Value.Trim();
            string header = string.IsNullOrEmpty(attrs) ? $"<{tag}>" : $"<{tag} {attrs}>";

            entries.Add(new ParsedChatEntryModel
            {
                Header = header,
                Body = body,
                Sender = "Nox",
                IsBot = true
            });

            cursor = m.Index + m.Length;
        }

        if (cursor < botReply.Length)
        {
            string tail = botReply[cursor..].Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                entries.Add(new ParsedChatEntryModel
                {
                    Header = "[BOT TEXT]",
                    Body = tail,
                    Sender = "Nox",
                    IsBot = true
                });
            }
        }

        return entries;
    }

    private static ParsedSystemSectionModel ClassifySection(string header, string content)
    {
        string hUpper = header.ToUpperInvariant();
        string badgeColor = "#A855F7";
        string icon = "⚙️";

        if (hUpper.Contains("IDENTITY") || hUpper.Contains("YOU ARE") || hUpper.Contains("ROLE"))
        {
            badgeColor = "#C084FC";
            icon = "🤖";
        }
        else if (hUpper.Contains("PROFILE") || hUpper.Contains("PARTICIPANT"))
        {
            badgeColor = "#0EA5E9";
            icon = "👤";
        }
        else if (hUpper.Contains("VOICE") || hUpper.Contains("STYLE"))
        {
            badgeColor = "#A855F7";
            icon = "🎭";
        }
        else if (hUpper.Contains("PERSPECTIVE") || hUpper.Contains("BACKGROUND"))
        {
            badgeColor = "#8B5CF6";
            icon = "🧠";
        }
        else if (hUpper.Contains("GUIDELINE") || hUpper.Contains("PROTOCOL") || hUpper.Contains("COMMUNICATION") || hUpper.Contains("TAG") || hUpper.Contains("TOOL USE") || hUpper.Contains("STATEFUL"))
        {
            badgeColor = "#7C3AED";
            icon = "📋";
        }
        else if (hUpper.Contains("TIP"))
        {
            badgeColor = "#10B981";
            icon = "💡";
        }
        else if (hUpper.Contains("SANDBOX") || hUpper.Contains("VM /DATA"))
        {
            badgeColor = "#14B8A6";
            icon = "🖥️";
        }
        else if (hUpper.Contains("STUDIO QUESTION") || hUpper.Contains("ACTIVE BACKGROUND JOB"))
        {
            badgeColor = "#F59E0B";
            icon = "⏳";
        }
        else if (hUpper.Contains("PINNED") || hUpper.Contains("FAVORITE"))
        {
            badgeColor = "#EC4899";
            icon = "📌";
        }
        else if (hUpper.Contains("MEDIA") || hUpper.Contains("CANDIDATES") || hUpper.Contains("STICKER") || hUpper.Contains("GIF") || hUpper.Contains("RAG"))
        {
            badgeColor = "#F43F5E";
            icon = "🖼️";
        }
        else if (hUpper.Contains("PREFERENCE"))
        {
            badgeColor = "#FB7185";
            icon = "⭐";
        }
        else if (hUpper.Contains("MEMORY") || hUpper.Contains("MEMORIES") || hUpper.Contains("RECALLED"))
        {
            badgeColor = "#6366F1";
            icon = "💾";
        }
        else if (hUpper.Contains("HOT") || hUpper.Contains("SCRATCHPAD"))
        {
            badgeColor = "#F97316";
            icon = "🔥";
        }
        else if (hUpper.Contains("TASK") || hUpper.Contains("RESULT"))
        {
            badgeColor = "#EAB308";
            icon = "⚡";
        }
        else if (hUpper.Contains("TIME") || hUpper.Contains("CURRENT") || hUpper.Contains("GUEST MODE"))
        {
            badgeColor = "#06B6D4";
            icon = "⏱️";
        }

        return new ParsedSystemSectionModel
        {
            Header = header,
            Content = content,
            BadgeColor = badgeColor,
            SectionIcon = icon
        };
    }

    public static List<ParsedChatEntryModel> ParseChatEntries(string userText)
        => ParseChatEntries(userText, botPersonaName: null);

    private static List<ParsedChatEntryModel> ParseChatEntries(string userText, string? botPersonaName)
    {
        var entries = new List<ParsedChatEntryModel>();
        if (string.IsNullOrWhiteSpace(userText)) return entries;
        userText = NormalizeEscapedNewlines(userText);

        var matches = ChatEntryHeaderRegex.Matches(userText);
        if (matches.Count > 0)
        {
            if (matches[0].Index > 0)
            {
                string pre = userText[..matches[0].Index].Trim();
                if (!string.IsNullOrWhiteSpace(pre))
                {
                    entries.Add(new ParsedChatEntryModel
                    {
                        Header = "[CONTEXT]",
                        Body = pre
                    });
                }
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                string header = match.Groups[1].Value.Trim();
                int bodyStart = match.Index + match.Length;
                int bodyEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : userText.Length;
                string body = userText[bodyStart..bodyEnd].Trim();

                if (string.IsNullOrWhiteSpace(body) &&
                    (header.Contains("Chat:", StringComparison.OrdinalIgnoreCase) ||
                     header.StartsWith("===", StringComparison.Ordinal) ||
                     string.Equals(header, "[NEW MESSAGES]", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(header, "[CONTEXT DIFF]", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string sender = string.Empty;
                string rawSender = string.Empty;
                bool isMember = false;
                var sMatch = SenderLineRegex.Match(header);
                if (sMatch.Success)
                {
                    rawSender = sMatch.Groups["sender"].Value;
                    sender = CleanSenderName(rawSender);
                    isMember = sMatch.Groups["member"].Success;
                }

                bool isBot = header.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                             header.Contains("(bot)", StringComparison.OrdinalIgnoreCase) ||
                             rawSender.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                             rawSender.Contains("(bot)", StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrEmpty(botPersonaName) && string.Equals(sender, botPersonaName, StringComparison.OrdinalIgnoreCase)) ||
                             string.Equals(sender, "Nox", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Bot", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Assistant", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Minthara", StringComparison.OrdinalIgnoreCase);

                entries.Add(new ParsedChatEntryModel
                {
                    Header = header,
                    Body = body,
                    Sender = sender,
                    IsBot = isBot,
                    IsMember = isMember
                });
            }

            return entries;
        }

        // If no explicit [#1234 ...] headers, split by double newlines (paragraphs)
        var blocks = userText.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (blocks.Length > 1)
        {
            foreach (var b in blocks)
            {
                string trimmed = b.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    entries.Add(new ParsedChatEntryModel
                    {
                        Header = string.Empty,
                        Body = trimmed
                    });
                }
            }
            return entries;
        }

        // Or if multiline with newlines, treat each non-empty line as an individual entry
        if (userText.Contains('\n'))
        {
            var lines = userText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                foreach (var l in lines)
                {
                    string trimmed = l.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        entries.Add(new ParsedChatEntryModel
                        {
                            Header = string.Empty,
                            Body = trimmed
                        });
                    }
                }
                return entries;
            }
        }

        return entries;
    }

    private static RawApiCallImageItem? ResolveInlineImage(string dataUrl, RawApiCallItemModel call,
        List<RawApiCallImageItem> remaining)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            int comma = dataUrl.IndexOf(',');
            if (comma <= 0 || !dataUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            // The probe captures the same bytes separately from the raw JSON. Reuse
            // that bitmap and consume its fallback entry instead of rendering it twice.
            var image = remaining.FirstOrDefault(i => string.Equals(i.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
            if (image != null)
            {
                remaining.Remove(image);
                image.Label = $"Image #{call.Images.IndexOf(image) + 1} (inline)";
                return image;
            }
            using var ms = new MemoryStream(bytes);
            image = new RawApiCallImageItem
            {
                Label = $"Image #{call.Images.Count + 1} (inline)",
                ContentHash = hash,
                MimeType = dataUrl[5..dataUrl.IndexOf(';')],
                Preview = new Bitmap(ms)
            };
            call.Images.Add(image);
            return image;
        }
        catch { return null; }
    }
}
