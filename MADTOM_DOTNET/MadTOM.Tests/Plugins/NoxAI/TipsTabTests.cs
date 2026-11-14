using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class TipsTabTests
{
    /// <summary>In-memory stand-in for the NoxChat tips RPCs (same rules as the Python service, simplified).</summary>
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public List<NoxTipItem> Tips { get; } = new();
        public List<NoxTipListRequest> ListRequests { get; } = new();
        public List<NoxAddTipsRequest> AddRequests { get; } = new();
        public List<NoxUpdateTipRequest> UpdateRequests { get; } = new();
        public List<NoxDeleteTipsRequest> DeleteRequests { get; } = new();
        public List<NoxTipSearchRequest> SearchRequests { get; } = new();
        public List<NoxTestTipsRequest> TestRequests { get; } = new();
        public uint QueueSize { get; set; }
        public bool FailNextUpdate { get; set; }
        public bool ThrowOnList { get; set; }
        private int _next = 1;

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        private static AsyncUnaryCall<T> Fail<T>(StatusCode code, string detail) =>
            new(Task.FromException<T>(new RpcException(new Status(code, detail))), Task.FromResult(new Metadata()),
                () => new Status(code, detail), () => new Metadata(), () => { });

        public NoxTipItem Seed(string text, string scopeKind = "global", string scopeValue = "", string author = "studio",
            string status = "active", string state = "ready", string trigger = "")
        {
            var tip = new NoxTipItem
            {
                Id = $"T-{_next++:x8}", Text = text, Trigger = trigger, ScopeKind = scopeKind, ScopeValue = scopeValue,
                Author = author, Status = status, Enabled = true, State = state, UseCount = 0,
                CreatedAtUnixSec = 1763000000 + _next
            };
            Tips.Add(tip);
            return tip;
        }

        public override AsyncUnaryCall<NoxTipListResponse> ListTipsAsync(
            NoxTipListRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListRequests.Add(request);
            if (ThrowOnList) return Fail<NoxTipListResponse>(StatusCode.Unavailable, "probe offline");
            IEnumerable<NoxTipItem> q = Tips;
            if (request.ScopeKind != "") q = q.Where(t => t.ScopeKind == request.ScopeKind);
            if (request.ScopeValue != "") q = q.Where(t => t.ScopeValue == request.ScopeValue);
            if (request.Status != "") q = q.Where(t => t.Status == request.Status);
            if (request.Text != "") q = q.Where(t => t.Text.Contains(request.Text, StringComparison.OrdinalIgnoreCase));
            var items = q.ToList();
            var res = new NoxTipListResponse
            {
                TotalCount = (uint)items.Count, QueueSize = QueueSize,
                ProposedCount = (uint)Tips.Count(t => t.Status == "proposed"),
                Settings = new NoxTipSettings { Enabled = true, MinRelevance = 0.45, MaxTips = 3, MaxTipChars = 300, MaxTriggerChars = 200, MaxTipsPerChat = 30 }
            };
            res.Tips.AddRange(items);
            return Call(res);
        }

        public override AsyncUnaryCall<NoxAddTipsResponse> AddTipsAsync(
            NoxAddTipsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            AddRequests.Add(request);
            var res = new NoxAddTipsResponse();
            foreach (var part in Regex.Split(request.RawText, @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (part.Length > 300) { res.Rejected.Add(new NoxTipRejected { Text = part, Reason = "too_long" }); continue; }
                res.Added.Add(Seed(part, request.ScopeKind == "" ? "global" : request.ScopeKind, request.ScopeValue, state: "pending"));
            }
            QueueSize = (uint)res.Added.Count;
            res.QueueSize = QueueSize;
            return Call(res);
        }

        public override AsyncUnaryCall<NoxTipMutationResponse> UpdateTipAsync(
            NoxUpdateTipRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            UpdateRequests.Add(request);
            var tip = Tips.FirstOrDefault(t => t.Id == request.Id);
            if (FailNextUpdate || tip == null)
            {
                FailNextUpdate = false;
                return Call(new NoxTipMutationResponse { Success = false, Message = tip == null ? "not_found" : "too_long" });
            }
            if (request.SetText) tip.Text = request.Text;
            if (request.SetTrigger) tip.Trigger = request.Trigger;
            if (request.SetEnabled) tip.Enabled = request.Enabled;
            if (request.Status != "") tip.Status = request.Status;
            return Call(new NoxTipMutationResponse { Success = true, Tip = tip, Affected = 1 });
        }

        public override AsyncUnaryCall<NoxTipMutationResponse> DeleteTipsAsync(
            NoxDeleteTipsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add(request);
            int removed = Tips.RemoveAll(t => request.Ids.Contains(t.Id));
            return Call(new NoxTipMutationResponse { Success = true, Affected = (uint)removed });
        }

        public override AsyncUnaryCall<NoxTipSearchResponse> SearchTipsAsync(
            NoxTipSearchRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            SearchRequests.Add(request);
            var res = new NoxTipSearchResponse();
            double score = 0.9;
            foreach (var tip in Tips) { res.Results.Add(new NoxTipScored { Tip = tip, Score = score }); score -= 0.2; }
            return Call(res);
        }

        public override AsyncUnaryCall<NoxTestTipsResponse> TestTipsAsync(
            NoxTestTipsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            TestRequests.Add(request);
            var res = new NoxTestTipsResponse { Threshold = 0.45 };
            if (Tips.Count > 0) res.Shown.Add(new NoxTipScored { Tip = Tips[0], Score = 0.61 });
            if (Tips.Count > 1) res.BelowThreshold.Add(new NoxTipScored { Tip = Tips[1], Score = 0.38 });
            return Call(res);
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    /// <summary>The view model without a node, so each test drives the calls itself.</summary>
    private static (TipsTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        return (new TipsTabViewModel(manager), client, manager);
    }

    private static async Task Attach(TipsTabViewModel vm, Client client)
    {
        vm.SelectedNode = Node;                       // starts a refresh on its own
        for (int i = 0; i < 200 && client.ListRequests.Count == 0; i++) await Task.Delay(10);
        await vm.RefreshAsync();
    }

    [Fact]
    public async Task Refresh_FillsListCountersAndLabels()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.Seed("Keep replies short", trigger: "the chat is busy");
        client.Seed("Call the admin boss", scopeKind: "chat", scopeValue: "-100456", author: "nox");
        client.Seed("Minthara never apologises twice", scopeKind: "persona", scopeValue: "minthara", author: "nox", status: "proposed");
        client.Seed("broken", state: "failed");
        await Attach(vm, client);

        Assert.Equal(4, vm.Tips.Count);
        Assert.Equal(4, vm.TotalCount);
        Assert.Equal(1, vm.ProposedCount);
        Assert.Equal(0, vm.QueueSize);
        Assert.False(vm.IsPollingQueue);
        Assert.Contains("0.45", vm.SettingsSummary);
        Assert.Contains("300", vm.SettingsSummary);

        var byText = vm.Tips.ToDictionary(t => t.Text);
        Assert.Equal("Global", byText["Keep replies short"].ScopeLabel);
        Assert.Equal("the chat is busy", byText["Keep replies short"].Trigger);
        Assert.Equal("Studio", byText["Keep replies short"].AuthorLabel);
        Assert.Equal("Chat -100456", byText["Call the admin boss"].ScopeLabel);
        Assert.Equal("Nox", byText["Call the admin boss"].AuthorLabel);
        Assert.Equal("Persona minthara", byText["Minthara never apologises twice"].ScopeLabel);
        Assert.True(byText["Minthara never apologises twice"].IsProposed);
        Assert.False(byText["Keep replies short"].IsProposed);
        Assert.Equal("Failed", byText["broken"].StateLabel);
        Assert.Equal("Ready", byText["Keep replies short"].StateLabel);
    }

    [Fact]
    public async Task Filters_AreSentToTheServer()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.Seed("global one");
        client.Seed("chat one", scopeKind: "chat", scopeValue: "5");
        client.Seed("waiting", status: "proposed", author: "nox");
        await Attach(vm, client);

        vm.FilterScopeKind = "chat";
        vm.FilterScopeValue = "5";
        vm.FilterText = "chat";
        await vm.RefreshAsync();
        var req = client.ListRequests.Last();
        Assert.Equal("chat", req.ScopeKind);
        Assert.Equal("5", req.ScopeValue);
        Assert.Equal("chat", req.Text);
        Assert.Single(vm.Tips);

        vm.FilterScopeKind = "";
        vm.FilterScopeValue = "";
        vm.FilterText = "";
        vm.OnlyAwaitingApproval = true;
        await vm.RefreshAsync();
        Assert.Equal("proposed", client.ListRequests.Last().Status);
        Assert.Equal("waiting", Assert.Single(vm.Tips).Text);
    }

    [Fact]
    public async Task AddTips_SplitsCountsSendsScopeAndReportsRejections()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        Assert.Equal(0, vm.NewTipsCount);
        vm.NewTipsText = "first tip\n\n   \n\nwhen: someone is sad\nbe kind\n\n\n" + new string('x', 400);
        Assert.Equal(3, vm.NewTipsCount);
        Assert.Contains("3", vm.AddButtonLabel);
        vm.NewTipsText = "only one";
        Assert.Equal(1, vm.NewTipsCount);
        Assert.Equal("Add 1 tip", vm.AddButtonLabel);

        vm.NewTipsText = "first tip\n\nwhen: someone is sad\nbe kind\n\n" + new string('x', 400);
        vm.NewScopeKind = "persona";
        vm.NewScopeValue = "minthara";
        await vm.AddTipsAsync();

        var req = Assert.Single(client.AddRequests);
        Assert.Equal("persona", req.ScopeKind);
        Assert.Equal("minthara", req.ScopeValue);
        Assert.Contains("when: someone is sad", req.RawText);
        Assert.Equal("", vm.NewTipsText);
        Assert.Single(vm.LastRejected);
        Assert.StartsWith("too_long", vm.LastRejected[0]);
        Assert.Contains("2", vm.StatusText);
        Assert.Equal(2, vm.Tips.Count);
        Assert.All(vm.Tips, t => Assert.Equal("Pending…", t.StateLabel));

        // the server is still embedding: the tab polls until the queue is empty, with one loop only
        Assert.Equal(2, vm.QueueSize);
        Assert.True(vm.IsPollingQueue);
        client.QueueSize = 0;
        foreach (var tip in client.Tips) tip.State = "ready";
        for (int i = 0; i < 60 && vm.IsPollingQueue; i++) await Task.Delay(100);
        Assert.False(vm.IsPollingQueue);
        Assert.Equal(0, vm.QueueSize);
        Assert.All(vm.Tips, t => Assert.Equal("Ready", t.StateLabel));
    }

    [Fact]
    public async Task RowActions_UpdateApproveRejectToggleDelete()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        var live = client.Seed("Keep replies short");
        var proposed = client.Seed("Never say delve", status: "proposed", author: "nox");
        await Attach(vm, client);

        var row = vm.Tips.Single(t => t.Id == live.Id);
        row.EditText = "Keep replies very short";
        row.EditTrigger = "the chat is busy";
        await vm.SaveTipAsync(row);
        var upd = client.UpdateRequests.Last();
        Assert.True(upd.SetText && upd.SetTrigger);
        Assert.Equal("Keep replies very short", upd.Text);
        Assert.Equal("the chat is busy", upd.Trigger);
        Assert.False(upd.SetEnabled);
        Assert.Equal("", upd.Status);
        Assert.Equal("Keep replies very short", vm.Tips.Single(t => t.Id == live.Id).Text);

        await vm.ToggleEnabledAsync(vm.Tips.Single(t => t.Id == live.Id));
        Assert.True(client.UpdateRequests.Last().SetEnabled);
        Assert.False(client.UpdateRequests.Last().Enabled);
        Assert.False(vm.Tips.Single(t => t.Id == live.Id).Enabled);

        await vm.ApproveAsync(vm.Tips.Single(t => t.Id == proposed.Id));
        Assert.Equal("active", client.UpdateRequests.Last().Status);
        Assert.False(vm.Tips.Single(t => t.Id == proposed.Id).IsProposed);
        Assert.Equal(0, vm.ProposedCount);
        await vm.RejectAsync(vm.Tips.Single(t => t.Id == proposed.Id));
        Assert.Equal("rejected", client.UpdateRequests.Last().Status);

        // a refused update is reported, not thrown
        client.FailNextUpdate = true;
        await vm.SaveTipAsync(vm.Tips.Single(t => t.Id == live.Id));
        Assert.Contains("too_long", vm.StatusText);

        vm.SelectedTip = vm.Tips.Single(t => t.Id == proposed.Id);
        await vm.DeleteSelectedAsync();
        Assert.Equal(new[] { proposed.Id }, client.DeleteRequests.Last().Ids.ToArray());
        Assert.Equal(live.Id, Assert.Single(vm.Tips).Id);
    }

    [Fact]
    public async Task SearchAndTestPanel_ShowScores()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.Seed("Use web search for news");
        client.Seed("Be brief about the weather");
        await Attach(vm, client);

        vm.SearchQuery = "news";
        await vm.SearchAsync();
        Assert.Equal("news", client.SearchRequests.Last().Query);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.Equal(0.9, vm.SearchResults[0].Score!.Value, 3);
        Assert.Equal("Use web search for news", vm.SearchResults[0].Text);

        vm.TestText = "any news today?";
        vm.TestChatId = "-100456";
        vm.TestPersonaKey = "nox";
        await vm.RunTestAsync();
        var req = client.TestRequests.Last();
        Assert.Equal("any news today?", req.Text);
        Assert.Equal(-100456, req.ChatId);
        Assert.Equal("nox", req.PersonaKey);
        Assert.Equal(0.45, vm.TestThreshold, 3);
        Assert.Equal(0.61, Assert.Single(vm.TestShown).Score!.Value, 3);
        Assert.Equal(0.38, Assert.Single(vm.TestBelowThreshold).Score!.Value, 3);

        // a non-numeric chat id is refused locally, without a request
        int sent = client.TestRequests.Count;
        vm.TestChatId = "abc";
        await vm.RunTestAsync();
        Assert.Equal(sent, client.TestRequests.Count);
        Assert.Contains("chat ID", vm.StatusText);
        // an empty chat id means "no chat scope"
        vm.TestChatId = "";
        await vm.RunTestAsync();
        Assert.Equal(0, client.TestRequests.Last().ChatId);
    }

    [Fact]
    public async Task RpcFailure_IsShownInStatusText()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.ThrowOnList = true;
        vm.SelectedNode = Node;
        await vm.RefreshAsync();
        Assert.Contains("probe offline", vm.StatusText);
        Assert.Empty(vm.Tips);
        Assert.False(vm.IsBusy);
    }
}
