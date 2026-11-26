using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class MemoryMergeTabTests
{
    /// <summary>In-memory stand-in for NoxChat's list_memory_merges / memory_merge_action.</summary>
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public List<NoxMemoryMergeListRequest> Lists { get; } = new();
        public List<NoxMemoryMergeActionRequest> Actions { get; } = new();
        public List<NoxMemoryMergeProposal> Proposals { get; } = new();
        public bool JobRunning { get; set; }

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(NoxListChatsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxListChatsResponse();
            res.Chats.Add(new NoxChatItem { ChatId = -100, Title = "Japan dudes", LastActiveUnixSec = 20 });
            res.Chats.Add(new NoxChatItem { ChatId = 42, Title = "DM", LastActiveUnixSec = 10 });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxMemoryMergeListResponse> ListMemoryMergesAsync(NoxMemoryMergeListRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Lists.Add(request);
            var res = new NoxMemoryMergeListResponse();
            res.Proposals.AddRange(Proposals.Where(p =>
                (!request.HasChatId || p.ChatId == request.ChatId) && (request.Status == "" || p.Status == request.Status)));
            res.Jobs.Add(new NoxMemoryMergeJob { ChatId = -100, Running = JobRunning, Total = 4, Done = 1, FinishedAt = JobRunning ? "" : "t" });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxMemoryMergeActionResponse> MemoryMergeActionAsync(NoxMemoryMergeActionRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(request);
            var res = new NoxMemoryMergeActionResponse { Ok = true, Message = $"{request.Action} ok" };
            foreach (var id in request.Ids)
            {
                var p = Proposals.First(x => x.Id == id);
                p.Status = request.Action == "apply" ? "applied" : "rejected";
                res.AppliedIds.Add(id);
            }
            return Call(res);
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static NoxMemoryMergeProposal Proposal(long id, string kind, string subject, string proposed, params string[] sources)
    {
        var p = new NoxMemoryMergeProposal
        {
            Id = id, ChatId = -100, EntityKey = "user:" + id, Subject = subject, Kind = kind, Proposed = proposed, Status = "pending"
        };
        foreach (var (s, i) in sources.Select((s, i) => (s, i)))
            p.Sources.Add(new NoxMemoryMergeSource { Id = $"mem-{id}-{i}-0000", Content = s, CreatedAt = "2026-11-20T00:00:00Z" });
        return p;
    }

    private static async Task<(MemoriesRagTabViewModel vm, Client client)> OpenAsync()
    {
        var client = new Client();
        client.Proposals.Add(Proposal(1, "merge", "@fuyunoay", "Lena (@fuyunoay): real name Aida\nINFJ", "Lena is Aida", "Lena is INFJ"));
        client.Proposals.Add(Proposal(2, "merge", "@keiden_001", "Daniel (@keiden_001): Terraria", "a", "b", "c"));
        client.Proposals.Add(Proposal(3, "subject", "@isayuko", "Tadano (@isayuko) moved to Germany.", "Tadano (@isayuko) moved to Germany."));
        client.Proposals[1].Dropped.Add("slept badly");
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new MemoriesRagTabViewModel(manager) { SelectedNode = Node };
        vm.SelectedSubTabIndex = MemoriesRagTabViewModel.MergesSubTabIndex;
        await vm.LoadMergeChatsAsync();
        return (vm, client);
    }

    [Fact]
    public async Task OpeningTheTabListsChatsAndPendingProposals()
    {
        var (vm, client) = await OpenAsync();
        Assert.Equal(new long[] { 0, -100, 42 }, vm.MergeChats.Select(c => c.ChatId));
        Assert.Equal("Japan dudes (#-100)", vm.MergeChats[1].Label);
        Assert.False(vm.CanGenerateMerges); // "All chats" is view only
        Assert.Equal(3, vm.MergeProposals.Count);
        var last = client.Lists.Last();
        Assert.False(last.HasChatId);
        Assert.Equal("pending", last.Status);
        Assert.Equal("2 merges (5 memories) · 1 subject-only", vm.MergeSummary);
        Assert.Contains("1/4 merge proposals written", vm.MergeJobText);

        var lena = vm.MergeProposals[0];
        Assert.Equal("MERGE 2 → 1", lena.KindLabel);
        Assert.False(lena.IsEdited);
        Assert.True(vm.MergeProposals[1].HasDropped);
        Assert.Equal("SUBJECT ONLY", vm.MergeProposals[2].KindLabel);

        vm.SelectedMergeChat = vm.MergeChats[1];
        await vm.RefreshMergesAsync();
        Assert.True(vm.CanGenerateMerges);
        Assert.Equal(-100, client.Lists.Last().ChatId);

        vm.MergeStatusFilter = "all";
        await vm.RefreshMergesAsync();
        Assert.Equal("", client.Lists.Last().Status);
    }

    [Fact]
    public async Task EditedProposalIsAppliedAloneWithItsText()
    {
        var (vm, client) = await OpenAsync();
        var lena = vm.MergeProposals[0];
        lena.EditedText = "Lena (@fuyunoay): real name Aida\n---\nLena: INFJ";
        Assert.True(lena.IsEdited);
        Assert.Equal(2, lena.PartCount);
        Assert.Equal("MERGE 2 → 2", lena.KindLabel);

        vm.CheckMergesCommand.Execute("all");
        Assert.All(vm.MergeProposals, r => Assert.True(r.IsChecked));
        await vm.ApplyCheckedMergesAsync();

        Assert.Equal(2, client.Actions.Count);
        Assert.Equal(new long[] { 1 }, client.Actions[0].Ids);
        Assert.Equal(lena.EditedText, client.Actions[0].EditedText);
        Assert.Equal(new long[] { 2, 3 }, client.Actions[1].Ids);
        Assert.Equal("", client.Actions[1].EditedText);
        Assert.StartsWith("Applied 3 of 3", vm.StatusText);
        Assert.Empty(vm.MergeProposals); // the pending filter no longer shows them
    }

    [Fact]
    public async Task CheckSubjectOnlyAndReject()
    {
        var (vm, client) = await OpenAsync();
        vm.CheckMergesCommand.Execute("subject");
        Assert.Equal(new long[] { 3 }, vm.MergeProposals.Where(r => r.IsChecked).Select(r => r.Id));
        await vm.RejectCheckedMergesAsync();
        var act = client.Actions.Single();
        Assert.Equal("reject", act.Action);
        Assert.Equal(new long[] { 3 }, act.Ids);
        Assert.Equal(2, vm.MergeProposals.Count);
    }

    [Fact]
    public async Task GenerateSendsTheChosenChatAndKeepsEditsOnRefresh()
    {
        var (vm, client) = await OpenAsync();
        vm.SelectedMergeChat = vm.MergeChats.First(c => c.ChatId == -100);
        await vm.RefreshMergesAsync();
        vm.MergeProposals[0].EditedText = "edited";
        await vm.GenerateMergesAsync();
        var gen = client.Actions.Single();
        Assert.Equal("generate", gen.Action);
        Assert.Equal(-100, gen.ChatId);
        Assert.Equal("generate ok", vm.StatusText);
        Assert.Equal("edited", vm.MergeProposals.First(r => r.Id == 1).EditedText);

        vm.ResetSelectedMergeTextCommand.Execute(null);
        Assert.False(vm.SelectedMergeProposal!.IsEdited);
    }

    [Theory]
    [InlineData("a\nb", 1)]
    [InlineData("a\n---\nb", 2)]
    [InlineData("a\r\n  ----  \r\nb\n---\n\n", 2)]
    [InlineData("", 0)]
    public void SplitPartsMatchesNoxChat(string text, int parts) =>
        Assert.Equal(parts, MemoryMergeRowModel.SplitParts(text).Count);
}
