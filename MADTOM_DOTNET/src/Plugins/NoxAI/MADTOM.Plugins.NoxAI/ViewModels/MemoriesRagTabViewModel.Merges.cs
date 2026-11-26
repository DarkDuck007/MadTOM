using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

/// <summary>A chat in the merge-proposals chat picker (ChatId 0 = all chats, view only).</summary>
public sealed record MergeChatOption(long ChatId, string Label)
{
    public override string ToString() => Label;
}

public sealed class MergeSourceModel
{
    public string Id { get; init; } = string.Empty;
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;
    public string Content { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string CreatedDate => CreatedAt.Length >= 10 ? CreatedAt[..10] : CreatedAt;
    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Content) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;
}

/// <summary>One proposal from NoxChat's memory_merge_proposals (src/memory/consolidation.py).</summary>
public sealed partial class MemoryMergeRowModel : ObservableObject
{
    public long Id { get; init; }
    public long ChatId { get; init; }
    public string ChatLabel { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string EntityKey { get; init; } = string.Empty;
    public string Kind { get; init; } = "merge";
    public string Status { get; init; } = "pending";
    public string Error { get; init; } = string.Empty;
    public string ResultMemoryId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string Proposed { get; init; } = string.Empty;
    public IReadOnlyList<MergeSourceModel> Sources { get; init; } = Array.Empty<MergeSourceModel>();
    public IReadOnlyList<string> Dropped { get; init; } = Array.Empty<string>();

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    [NotifyPropertyChangedFor(nameof(PartsDisplay))]
    private string _editedText = string.Empty;

    public bool IsPending => Status == "pending";
    public bool IsSubjectOnly => Kind == "subject";
    public bool IsEdited => !string.Equals(Normalize(EditedText), Normalize(Proposed), StringComparison.Ordinal);
    public bool HasDropped => Dropped.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public int PartCount => SplitParts(EditedText).Count;

    public string KindLabel => IsSubjectOnly ? "SUBJECT ONLY" : $"MERGE {Sources.Count} → {Math.Max(1, PartCount)}";
    public string StatusLabel => Status.ToUpperInvariant();
    public string Title => string.IsNullOrWhiteSpace(Subject) ? EntityKey : Subject;
    public string PartsDisplay => IsSubjectOnly
        ? "Text stays as it is; the memory only gets its subject, so later facts about this person merge into it."
        : $"{PartCount} memor{(PartCount == 1 ? "y" : "ies")} after applying (separate memories with a line holding only ---)";
    public string ResultDisplay => string.IsNullOrWhiteSpace(ResultMemoryId) ? string.Empty : $"Result: {ResultMemoryId}";

    public Avalonia.Media.FlowDirection FlowDirection => NoxChatSessionParser.IsRtl(Proposed) ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
    public Avalonia.Media.TextAlignment TextAlignment => NoxChatSessionParser.IsRtl(Proposed) ? Avalonia.Media.TextAlignment.Right : Avalonia.Media.TextAlignment.Left;

    private static string Normalize(string? text) =>
        string.Join("\n", (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Same rule as NoxChat's consolidation.split_parts: blocks separated by a line of three or more dashes.</summary>
    public static IReadOnlyList<string> SplitParts(string? text) =>
        Regex.Split((text ?? string.Empty).Replace("\r\n", "\n"), @"(?m)^\s*-{3,}\s*$")
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .ToList();

    public static MemoryMergeRowModel FromProto(NoxMemoryMergeProposal p, string chatLabel) => new()
    {
        Id = p.Id,
        ChatId = p.ChatId,
        ChatLabel = chatLabel,
        Subject = p.Subject,
        EntityKey = p.EntityKey,
        Kind = string.IsNullOrEmpty(p.Kind) ? "merge" : p.Kind,
        Status = string.IsNullOrEmpty(p.Status) ? "pending" : p.Status,
        Error = p.Error,
        ResultMemoryId = p.ResultMemoryId,
        CreatedAt = p.CreatedAt,
        Proposed = p.Proposed,
        EditedText = p.Proposed,
        Sources = p.Sources.Select(s => new MergeSourceModel { Id = s.Id, Content = s.Content, CreatedAt = s.CreatedAt }).ToList(),
        Dropped = p.Dropped.ToList(),
    };
}

public sealed partial class MemoriesRagTabViewModel
{
    public const int MergesSubTabIndex = 2;
    private static readonly TimeSpan MergePollInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<long, string> _mergeChatTitles = new();
    private bool _mergePolling;

    public ObservableCollection<MergeChatOption> MergeChats { get; } = new();
    public ObservableCollection<MemoryMergeRowModel> MergeProposals { get; } = new();
    public IReadOnlyList<string> MergeStatusFilters { get; } = new[] { "pending", "applied", "rejected", "stale", "failed", "all" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateMerges))]
    private MergeChatOption? _selectedMergeChat;

    [ObservableProperty] private string _mergeStatusFilter = "pending";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMergeProposal))]
    private MemoryMergeRowModel? _selectedMergeProposal;

    [ObservableProperty] private string _mergeJobText = string.Empty;
    [ObservableProperty] private string _mergeSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateMerges))]
    private bool _isMergeBusy;

    public bool HasSelectedMergeProposal => SelectedMergeProposal != null;
    public bool CanGenerateMerges => !IsMergeBusy && SelectedMergeChat is { ChatId: not 0 };

    partial void OnSelectedMergeChatChanged(MergeChatOption? value) => _ = RefreshMergesAsync();
    partial void OnMergeStatusFilterChanged(string value) => _ = RefreshMergesAsync();

    private void ClearMerges()
    {
        MergeProposals.Clear();
        MergeChats.Clear();
        _mergeChatTitles.Clear();
        SelectedMergeProposal = null;
        MergeJobText = string.Empty;
        MergeSummary = string.Empty;
    }

    private string MergeChatLabel(long chatId) =>
        _mergeChatTitles.TryGetValue(chatId, out var t) && !string.IsNullOrWhiteSpace(t) ? $"{t} (#{chatId})" : $"Chat #{chatId}";

    [RelayCommand]
    public async Task LoadMergeChatsAsync()
    {
        var node = SelectedNode;
        if (node == null) return;
        var keep = SelectedMergeChat?.ChatId ?? 0;
        var items = new List<NoxChatItem>();
        try
        {
            var client = _nodeManager.GetClient(node);
            string cursor = string.Empty;
            for (int page = 0; page < 50; page++)
            {
                var res = await client.ListChatsAsync(new NoxListChatsRequest { NodeId = node.Id, Limit = 200, Cursor = cursor }).ResponseAsync;
                items.AddRange(res.Chats);
                if (!res.PaginationSupported || string.IsNullOrEmpty(res.NextCursor) || res.NextCursor == cursor) break;
                cursor = res.NextCursor;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not list chats: {ex.Message}";
        }

        MergeChats.Clear();
        _mergeChatTitles.Clear();
        MergeChats.Add(new MergeChatOption(0, "All chats (view only)"));
        foreach (var c in items.GroupBy(c => c.ChatId).Select(g => g.First()).OrderByDescending(c => c.LastActiveUnixSec))
        {
            _mergeChatTitles[c.ChatId] = c.Title;
            MergeChats.Add(new MergeChatOption(c.ChatId, MergeChatLabel(c.ChatId)));
        }
        var target = MergeChats.FirstOrDefault(c => c.ChatId == keep) ?? MergeChats[0];
        if (Equals(SelectedMergeChat, target)) await RefreshMergesAsync();
        else SelectedMergeChat = target;
    }

    [RelayCommand]
    public async Task RefreshMergesAsync()
    {
        var node = SelectedNode;
        if (node == null) return;
        try
        {
            var client = _nodeManager.GetClient(node);
            var req = new NoxMemoryMergeListRequest { NodeId = node.Id, Status = MergeStatusFilter == "all" ? string.Empty : MergeStatusFilter };
            if (SelectedMergeChat is { ChatId: not 0 } chat) req.ChatId = chat.ChatId;
            var res = await client.ListMemoryMergesAsync(req).ResponseAsync;
            if (!string.IsNullOrEmpty(res.Error))
            {
                MergeSummary = $"Error: {res.Error}";
                return;
            }

            var keepId = SelectedMergeProposal?.Id;
            var edits = MergeProposals.Where(r => r.IsEdited).ToDictionary(r => r.Id, r => r.EditedText);
            var checkedIds = MergeProposals.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet();
            MergeProposals.Clear();
            foreach (var p in res.Proposals)
            {
                var row = MemoryMergeRowModel.FromProto(p, MergeChatLabel(p.ChatId));
                if (row.IsPending && edits.TryGetValue(row.Id, out var edited)) row.EditedText = edited;
                row.IsChecked = row.IsPending && checkedIds.Contains(row.Id);
                MergeProposals.Add(row);
            }
            SelectedMergeProposal = MergeProposals.FirstOrDefault(r => r.Id == keepId) ?? MergeProposals.FirstOrDefault();

            int merges = MergeProposals.Count(r => !r.IsSubjectOnly), subjects = MergeProposals.Count - merges;
            int sources = MergeProposals.Where(r => !r.IsSubjectOnly).Sum(r => r.Sources.Count);
            MergeSummary = MergeProposals.Count == 0
                ? $"No {(MergeStatusFilter == "all" ? "" : MergeStatusFilter + " ")}proposals."
                : $"{merges} merge{(merges == 1 ? "" : "s")} ({sources} memories) · {subjects} subject-only";

            var running = res.Jobs.Where(j => j.Running).ToList();
            MergeJobText = running.Count > 0
                ? string.Join(" · ", running.Select(j => $"{MergeChatLabel(j.ChatId)}: writing proposals {j.Done}/{j.Total}{(j.Failed > 0 ? $", {j.Failed} failed" : "")}"))
                : string.Join(" · ", res.Jobs.Where(j => !string.IsNullOrEmpty(j.FinishedAt)).Select(j =>
                    $"{MergeChatLabel(j.ChatId)}: {j.Done - j.Failed}/{j.Total} merge proposals written{(j.Failed > 0 ? $", {j.Failed} failed" : "")}"));
            if (running.Count > 0) _ = PollMergeJobsAsync();
        }
        catch (Exception ex)
        {
            MergeSummary = $"Error loading proposals: {ex.Message}";
        }
    }

    private async Task PollMergeJobsAsync()
    {
        if (_mergePolling) return;
        _mergePolling = true;
        try
        {
            await Task.Delay(MergePollInterval);
        }
        finally
        {
            _mergePolling = false;
        }
        if (SelectedSubTabIndex == MergesSubTabIndex) await RefreshMergesAsync();
    }

    [RelayCommand]
    public async Task GenerateMergesAsync()
    {
        var node = SelectedNode;
        if (node == null || SelectedMergeChat is not { ChatId: not 0 } chat) return;
        IsMergeBusy = true;
        try
        {
            var client = _nodeManager.GetClient(node);
            var res = await client.MemoryMergeActionAsync(new NoxMemoryMergeActionRequest
            {
                NodeId = node.Id, Action = "generate", ChatId = chat.ChatId
            }).ResponseAsync;
            StatusText = res.Message;
            if (res.Ok) MergeStatusFilter = "pending";
            await RefreshMergesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Generating proposals failed: {ex.Message}";
        }
        finally
        {
            IsMergeBusy = false;
        }
    }

    private async Task<NoxMemoryMergeActionResponse?> MergeActionAsync(string action, IEnumerable<long> ids, string editedText = "")
    {
        var node = SelectedNode;
        if (node == null) return null;
        var req = new NoxMemoryMergeActionRequest { NodeId = node.Id, Action = action, EditedText = editedText };
        req.Ids.AddRange(ids);
        if (req.Ids.Count == 0) return null;
        return await _nodeManager.GetClient(node).MemoryMergeActionAsync(req).ResponseAsync;
    }

    /// <summary>Apply proposals; one with operator edits goes alone so NoxChat receives its edited text.</summary>
    private async Task ApplyRowsAsync(IReadOnlyList<MemoryMergeRowModel> rows)
    {
        var pending = rows.Where(r => r.IsPending).ToList();
        if (pending.Count == 0) return;
        IsMergeBusy = true;
        int applied = 0;
        var errors = new List<string>();
        try
        {
            foreach (var row in pending.Where(r => r.IsEdited))
            {
                var res = await MergeActionAsync("apply", new[] { row.Id }, row.EditedText);
                if (res == null) continue;
                applied += res.AppliedIds.Count;
                errors.AddRange(res.Errors);
            }
            var plain = pending.Where(r => !r.IsEdited).Select(r => r.Id).ToList();
            if (plain.Count > 0)
            {
                var res = await MergeActionAsync("apply", plain);
                if (res != null)
                {
                    applied += res.AppliedIds.Count;
                    errors.AddRange(res.Errors);
                }
            }
            StatusText = $"Applied {applied} of {pending.Count} proposal{(pending.Count == 1 ? "" : "s")}"
                         + (errors.Count > 0 ? $" · {string.Join("; ", errors.Take(3))}" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsMergeBusy = false;
        }
        await RefreshMergesAsync();
    }

    private async Task RejectRowsAsync(IReadOnlyList<MemoryMergeRowModel> rows)
    {
        var ids = rows.Where(r => r.IsPending || r.Status == "failed").Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        IsMergeBusy = true;
        try
        {
            var res = await MergeActionAsync("reject", ids);
            if (res != null) StatusText = res.Message + (res.Errors.Count > 0 ? $" · {string.Join("; ", res.Errors.Take(3))}" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"Reject failed: {ex.Message}";
        }
        finally
        {
            IsMergeBusy = false;
        }
        await RefreshMergesAsync();
    }

    [RelayCommand]
    public Task ApplySelectedMergeAsync() =>
        SelectedMergeProposal is { } row ? ApplyRowsAsync(new[] { row }) : Task.CompletedTask;

    [RelayCommand]
    public Task RejectSelectedMergeAsync() =>
        SelectedMergeProposal is { } row ? RejectRowsAsync(new[] { row }) : Task.CompletedTask;

    [RelayCommand]
    public Task ApplyCheckedMergesAsync() => ApplyRowsAsync(MergeProposals.Where(r => r.IsChecked).ToList());

    [RelayCommand]
    public Task RejectCheckedMergesAsync() => RejectRowsAsync(MergeProposals.Where(r => r.IsChecked).ToList());

    [RelayCommand]
    public void ResetSelectedMergeText()
    {
        if (SelectedMergeProposal is { } row) row.EditedText = row.Proposed;
    }

    [RelayCommand]
    public void CheckMerges(string? which)
    {
        foreach (var row in MergeProposals)
        {
            row.IsChecked = row.IsPending && which switch
            {
                "subject" => row.IsSubjectOnly,
                "merge" => !row.IsSubjectOnly,
                "none" => false,
                _ => true,
            };
        }
    }
}
