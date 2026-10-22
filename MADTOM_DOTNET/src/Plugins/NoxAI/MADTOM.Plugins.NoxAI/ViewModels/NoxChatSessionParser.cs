using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
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

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Body) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Body) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public bool HasHeader => !string.IsNullOrWhiteSpace(Header);

    // Dynamic color coding: Bot messages have distinct emerald/mint styling, distinct from user messages
    public string CardBackgroundHex => IsBot ? "#0D281E" : "#10192A";
    public string BorderBrushHex => IsBot ? "#059669" : "#1E293B";
    public string HeaderForegroundHex => IsBot ? "#34D399" : "#38BDF8";
    public string TextForegroundHex => IsBot ? "#ECFDF5" : "#E2E8F0";
    public string BadgeBackgroundHex => IsBot ? "#064E3B" : "#0C4A6E";
    public string BadgeForegroundHex => IsBot ? "#6EE7B7" : "#7DD3FC";
    public string BadgeText => IsBot ? "BOT" : "USER";
    public bool HasBadge => IsBot;
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

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Text) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Text) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public Avalonia.Media.FlowDirection ThoughtFlowDirection => NoxChatSessionParser.IsRtl(ThoughtText) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment ThoughtTextAlignment => NoxChatSessionParser.IsRtl(ThoughtText) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

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

        // Strip sender prefixes like "[#1234 Alice]: ", "[Alice -> #5678]: ", "Alice: ", "@alice: "
        int startIndex = 0;
        var prefixMatch = Regex.Match(text, @"^\[(?:#\d+\s+)?[^\]:\r\n]+(?:\s+->\s+#\d+)?\]:\s*");
        if (prefixMatch.Success)
        {
            startIndex = prefixMatch.Length;
        }
        else
        {
            var colonMatch = Regex.Match(text, @"^[A-Za-z0-9_.\-@]+\s*:\s*");
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

    private static readonly Regex ThoughtRegex = new(@"<thought>(.*?)</thought>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SectionHeaderRegex = new(
        @"(?m)^(?:YOUR VOICE & STYLE:|YOUR BACKGROUND & PERSPECTIVES:|COMMUNICATION PROTOCOL:|PINNED FAVORITE STICKERS:|PINNED FAVORITE GIFS:|RELEVANT MEDIA CANDIDATES:|PARTICIPANTS' STICKER PREFERENCES:|PARTICIPANTS' PROFILES:|USER PROFILES:|PARTICIPANTS' USER PROFILES:|RELEVANT RECALLED MEMORIES:|HOT CONVERSATION MEMORY:|BACKGROUND TASK RESULTS:|Current Time:|[A-Z0-9 _\-']{3,40}:|#{1,3}\s+.+)",
        RegexOptions.Compiled);

    private static readonly Regex GroupChatRegex = new(
        @"Group\s+Chat:\s*['""]?([^'""\r\n\\]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PrivateChatRegex = new(
        @"Private\s+Chat:\s*['""]?([^'""\r\n\\]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitChatRegex = new(
        @"(?:^|[\r\n])Chat:\s*['""]?([^'""\r\n\\]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SenderLineRegex = new(
        @"\[#\d+\s+([^\]:\r\n]+?)(?:\s+->\s+#\d+)?\]:",
        RegexOptions.Compiled);

    private static readonly Regex ChatEntryHeaderRegex = new(
        @"(?:^|[\r\n]+)(?:(\[#\d+\s+[^\]:\r\n]+?(?:\s+->\s+#\d+)?\]:|\[Current request\]:|\[Incoming[^\]]*\]:|(?:Group|Private)?\s*Chat:\s*['""][^'""]+['""])[ \t]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string ExtractChatName(string? requestJson, string? caller)
    {
        if (string.IsNullOrWhiteSpace(requestJson)) return string.Empty;

        // 1. Check for Group Chat: 'Title'
        var mGroup = GroupChatRegex.Match(requestJson);
        if (mGroup.Success)
        {
            string title = mGroup.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 2. Check for Private Chat: 'Title'
        var mPriv = PrivateChatRegex.Match(requestJson);
        if (mPriv.Success)
        {
            string title = mPriv.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 3. Check for Chat: 'Title'
        var mChat = ExplicitChatRegex.Match(requestJson);
        if (mChat.Success)
        {
            string title = mChat.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(title)) return title;
        }

        // 4. If not found and it is a chat/conversation caller, extract from message turn sender history
        string cLower = caller?.ToLowerInvariant() ?? "";
        bool isChatCaller = cLower.Contains("step") || cLower.Contains("chat") || cLower.Contains("agent");
        if (isChatCaller)
        {
            var matches = SenderLineRegex.Matches(requestJson);
            string? fallbackSender = null;
            foreach (Match match in matches)
            {
                string rawSender = match.Groups[1].Value.Trim();
                string senderName = CleanSenderName(rawSender);
                if (string.Equals(senderName, "Nox", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(senderName, "Bot", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (rawSender.Contains("(You)", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackSender ??= senderName;
                    continue;
                }

                return senderName;
            }

            if (!string.IsNullOrEmpty(fallbackSender))
            {
                return fallbackSender;
            }
        }

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

        var availableImages = new Queue<RawApiCallImageItem>(callItem.Images);

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
                    foreach (var m in messagesArray.EnumerateArray())
                    {
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
                                    if (part.ValueKind == JsonValueKind.Object)
                                    {
                                        string pType = part.TryGetProperty("type", out var pt) ? pt.GetString() ?? "" : "";
                                        if (pType == "text" && part.TryGetProperty("text", out var txtVal))
                                        {
                                            textParts.Add(txtVal.GetString() ?? "");
                                        }
                                        else if (pType == "image_url" && part.TryGetProperty("image_url", out var iu))
                                        {
                                            string url = iu.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                                            var img = TryCreateImageFromDataUrl(url);
                                            if (img != null)
                                            {
                                                inlineImages.Add(img);
                                            }
                                        }
                                    }
                                }
                                textContent = string.Join("\n", textParts);
                            }
                        }

                        if (role == "system")
                        {
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

                            var chatEntries = ParseChatEntries(textContent);
                            foreach (var e in chatEntries)
                            {
                                userTurn.ChatEntries.Add(e);
                            }

                            foreach (var img in inlineImages)
                            {
                                userTurn.MediaAttachments.Add(img);
                            }

                            turns.Add(userTurn);
                        }
                        else if (role == "assistant")
                        {
                            string botContent = textContent;
                            string? thought = null;
                            var thoughtMatch = ThoughtRegex.Match(botContent);
                            if (thoughtMatch.Success)
                            {
                                thought = thoughtMatch.Groups[1].Value.Trim();
                                botContent = botContent.Replace(thoughtMatch.Value, "").Trim();
                            }

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
                            turns.Add(asstTurn);
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Parse Response JSON (current bot completion)
        try
        {
            if (!string.IsNullOrWhiteSpace(callItem.ResponseJson))
            {
                using var respDoc = JsonDocument.Parse(callItem.ResponseJson);
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

                var thoughtMatch = ThoughtRegex.Match(botReply);
                if (thoughtMatch.Success)
                {
                    botThought = thoughtMatch.Groups[1].Value.Trim();
                    botReply = botReply.Replace(thoughtMatch.Value, "").Trim();
                }

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
                while (availableImages.Count > 0)
                {
                    userTurn.MediaAttachments.Add(availableImages.Dequeue());
                }
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
                while (availableImages.Count > 0)
                {
                    mediaTurn.MediaAttachments.Add(availableImages.Dequeue());
                }
                turns.Insert(Math.Min(1, turns.Count), mediaTurn);
            }
        }

        // 4. Fallback if no structured turns were extracted
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
            var rawEntries = ParseChatEntries(callItem.RequestJson);
            foreach (var e in rawEntries)
            {
                rawUserTurn.ChatEntries.Add(e);
            }
            turns.Add(rawUserTurn);

            if (!string.IsNullOrWhiteSpace(callItem.ResponseJson))
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

    public static List<ParsedSystemSectionModel> ParseSystemSections(string systemPrompt)
    {
        var sections = new List<ParsedSystemSectionModel>();
        if (string.IsNullOrWhiteSpace(systemPrompt)) return sections;

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
        else if (hUpper.Contains("PROTOCOL") || hUpper.Contains("COMMUNICATION") || hUpper.Contains("TAG"))
        {
            badgeColor = "#7C3AED";
            icon = "📋";
        }
        else if (hUpper.Contains("PINNED") || hUpper.Contains("FAVORITE"))
        {
            badgeColor = "#EC4899";
            icon = "📌";
        }
        else if (hUpper.Contains("MEDIA") || hUpper.Contains("CANDIDATES") || hUpper.Contains("RAG"))
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
        else if (hUpper.Contains("TIME") || hUpper.Contains("CURRENT"))
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
    {
        var entries = new List<ParsedChatEntryModel>();
        if (string.IsNullOrWhiteSpace(userText)) return entries;

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

                if (string.IsNullOrWhiteSpace(body) && (header.Contains("Chat:", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string sender = string.Empty;
                var sMatch = SenderLineRegex.Match(header);
                if (sMatch.Success)
                {
                    sender = CleanSenderName(sMatch.Groups[1].Value);
                }

                string rawSender = sMatch.Success ? sMatch.Groups[1].Value : string.Empty;
                bool isBot = header.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                             header.Contains("(bot)", StringComparison.OrdinalIgnoreCase) ||
                             rawSender.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                             rawSender.Contains("(bot)", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Nox", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Bot", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Assistant", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(sender, "Minthara", StringComparison.OrdinalIgnoreCase);

                entries.Add(new ParsedChatEntryModel
                {
                    Header = header,
                    Body = body,
                    Sender = sender,
                    IsBot = isBot
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

    private static RawApiCallImageItem? TryCreateImageFromDataUrl(string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:image/")) return null;
        try
        {
            int commaIdx = dataUrl.IndexOf(',');
            if (commaIdx <= 0) return null;

            string meta = dataUrl[..commaIdx];
            string base64 = dataUrl[(commaIdx + 1)..];
            string mime = meta.Contains("png") ? "image/png" : "image/jpeg";

            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            return new RawApiCallImageItem
            {
                Label = "Prompt Inline Image",
                MimeType = mime,
                Preview = bmp
            };
        }
        catch
        {
            return null;
        }
    }
}
