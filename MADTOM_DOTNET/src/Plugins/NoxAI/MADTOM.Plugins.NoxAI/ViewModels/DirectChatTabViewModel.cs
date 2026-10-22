using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class ChatMessageItem : ObservableObject
{
    public string Sender { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string TimeFormatted { get; init; } = string.Empty;
    public bool IsUser { get; init; }
    public bool IsThought { get; init; }
    public bool IsMedia { get; init; }
    public string MediaGlyph { get; init; } = string.Empty;

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Text) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Text) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
}

public sealed partial class DirectChatTabViewModel : ObservableObject
{
    private readonly NoxNodeManager _nodeManager;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private string _inputMessage = string.Empty;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _liveThoughtStream = string.Empty;
    [ObservableProperty] private bool _hasActiveThought;

    public Avalonia.Media.FlowDirection LiveThoughtFlowDirection => NoxChatSessionParser.IsRtl(LiveThoughtStream) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment LiveThoughtTextAlignment => NoxChatSessionParser.IsRtl(LiveThoughtStream) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    public DirectChatTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        SelectedNode = _nodeManager.Nodes.FirstOrDefault();

        // Welcome message
        Messages.Add(new ChatMessageItem
        {
            Sender = "NoxAI",
            Text = "Connected to MADTOM Control Center. Ready for direct interaction.",
            TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            IsUser = false
        });
    }

    [RelayCommand]
    public async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputMessage) || IsSending || SelectedNode == null)
            return;

        string textToSend = InputMessage.Trim();
        InputMessage = string.Empty;
        IsSending = true;
        LiveThoughtStream = string.Empty;
        HasActiveThought = false;

        Messages.Add(new ChatMessageItem
        {
            Sender = "MADTOM (Control Center)",
            Text = textToSend,
            TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            IsUser = true
        });

        try
        {
            var client = _nodeManager.GetClient(SelectedNode);
            var req = new NoxDirectMessageRequest
            {
                NodeId = SelectedNode.Id,
                ChatId = 0,
                Text = textToSend
            };

            using var call = client.SendDirectMessage(req);
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var ev = call.ResponseStream.Current;
                if (ev.EventType == "thought")
                {
                    HasActiveThought = true;
                    LiveThoughtStream += ev.Content;
                }
                else if (ev.EventType is "reply" or "message")
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Sender = "Nox",
                        Text = ev.Content,
                        TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        IsUser = false
                    });
                }
                else if (ev.EventType == "sticker")
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Sender = "Nox (Sticker)",
                        Text = ev.Attributes.TryGetValue("file_id", out var fid) ? fid : "Sticker",
                        TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        IsUser = false,
                        IsMedia = true,
                        MediaGlyph = "🎨"
                    });
                }
                else if (ev.EventType == "gif")
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Sender = "Nox (GIF)",
                        Text = ev.Attributes.TryGetValue("file_id", out var fid) ? fid : "GIF",
                        TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        IsUser = false,
                        IsMedia = true,
                        MediaGlyph = "🎞️"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageItem
            {
                Sender = "Error",
                Text = $"Failed to communicate: {ex.Message}",
                TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                IsUser = false
            });
        }
        finally
        {
            IsSending = false;
        }
    }

    public void OnTurnEventReceived(NoxTurnTagEvent turn)
    {
        if (turn.TagName == "thought")
        {
            HasActiveThought = true;
            LiveThoughtStream += turn.Content;
        }
        else if (turn.TagName is "reply" or "message")
        {
            Messages.Add(new ChatMessageItem
            {
                Sender = "Nox",
                Text = turn.Content,
                TimeFormatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                IsUser = false
            });
        }
    }
}
