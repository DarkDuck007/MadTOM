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
public class SandboxTabTests
{
    /// <summary>In-memory stand-in for the NoxChat sandbox RPCs.</summary>
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public List<NoxSandboxVmActionRequest> Actions { get; } = new();
        public List<NoxSandboxListFilesRequest> FileLists { get; } = new();
        public List<NoxSandboxReadTextRequest> TextReads { get; } = new();
        public List<NoxSandboxPreviewFileRequest> Previews { get; } = new();
        public List<NoxSandboxDecideLibraryRequest> Decisions { get; } = new();
        public List<NoxSandboxRestoreBackupRequest> Restores { get; } = new();
        public List<NoxSandboxListRunsRequest> RunLists { get; } = new();
        public int RebuildCalls { get; private set; }
        public int VmLists { get; private set; }
        public string? VmError { get; set; }
        public string PreviewB64 { get; set; } = OnePixelPng;
        public int PreviewPages { get; set; } = 3;
        public List<NoxSandboxLibraryRequest> Library { get; } = new();

        // 1x1 transparent PNG
        public const string OnePixelPng =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxSandboxListVmsResponse> ListSandboxVmsAsync(NoxSandboxListVmsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            VmLists++;
            var res = new NoxSandboxListVmsResponse
            {
                Totals = new NoxSandboxTotals { Running = 2, GuestMemMb = 1216, MaxVms = 8, MaxGuestMemMb = 8192 },
                Host = new NoxSandboxHost { StateDirUsedPercent = 41.5 }
            };
            if (VmError != null) { res.Error = VmError; return Call(res); }
            var warm = new NoxSandboxVmInfo { ChatId = -1001, ChatTitle = "Alpha", Kind = "chat", State = "warm", MemMb = 1024, Vcpus = 2,
                DiskUsedBytes = 1 << 20, DiskQuotaBytes = 2L << 30 };
            warm.Sessions.Add(new NoxSandboxSession { Name = "main", Busy = true, Started = "2026-11-20T10:00:00Z" });
            warm.Jobs.Add(new NoxSandboxJob { JobId = "j-1", State = "running", Started = "2026-11-20T10:00:01Z" });
            res.Vms.Add(warm);
            res.Vms.Add(new NoxSandboxVmInfo { ChatId = -1002, ChatTitle = "Beta", Kind = "chat", State = "hibernated", SnapshotBytes = 150 << 20 });
            res.Vms.Add(new NoxSandboxVmInfo { ChatId = -1003, Kind = "chat", State = "busy" });
            res.Vms.Add(new NoxSandboxVmInfo { ChatId = -1004, Kind = "chat", State = "cold" });
            res.Pools.Add("temp", new NoxSandboxPool { Ready = 1, Target = 1 });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxVmActionResponse> SandboxVmActionAsync(NoxSandboxVmActionRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(request);
            return Call(new NoxSandboxVmActionResponse { Ok = true });
        }

        public override AsyncUnaryCall<NoxSandboxListFilesResponse> ListSandboxFilesAsync(NoxSandboxListFilesRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            FileLists.Add(request);
            var res = new NoxSandboxListFilesResponse { AsOf = "2026-11-20T10:00:00Z", FromCache = !request.Refresh };
            res.Entries.Add(new NoxSandboxFileEntry { Path = "/data/notes", Type = "dir", Size = 0, Mtime = 1795150000 });
            res.Entries.Add(new NoxSandboxFileEntry { Path = "/data/notes/a.md", Type = "file", Size = 12, Mtime = 1795150414 });
            res.Entries.Add(new NoxSandboxFileEntry { Path = "/data/outbox/chart.png", Type = "file", Size = 2000, Mtime = 1795150414 });
            res.Entries.Add(new NoxSandboxFileEntry { Path = "/data/blob.bin", Type = "file", Size = 9, Mtime = 1795150414 });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxReadTextResponse> ReadSandboxTextAsync(NoxSandboxReadTextRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            TextReads.Add(request);
            return Call(new NoxSandboxReadTextResponse { Text = "<b>not html</b>\n# note", Truncated = true, Size = 12 });
        }

        public override AsyncUnaryCall<NoxSandboxPreviewFileResponse> PreviewSandboxFileAsync(NoxSandboxPreviewFileRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Previews.Add(request);
            return Call(new NoxSandboxPreviewFileResponse { PngB64 = PreviewB64, Width = 1, Height = 1, Pages = PreviewPages, Kind = "pdf" });
        }

        public override AsyncUnaryCall<NoxSandboxRestoreBackupResponse> RestoreSandboxBackupAsync(NoxSandboxRestoreBackupRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Restores.Add(request);
            return Call(new NoxSandboxRestoreBackupResponse { Ok = true });
        }

        public override AsyncUnaryCall<NoxSandboxListBackupsResponse> ListSandboxBackupsAsync(NoxSandboxListBackupsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxSandboxListBackupsResponse();
            res.Backups.Add(new NoxSandboxBackup { Id = "20261120T030000Z", CreatedAt = "2026-11-20T03:00:00Z", Bytes = 1 << 20 });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxListLibraryRequestsResponse> ListSandboxLibraryRequestsAsync(
            NoxSandboxListLibraryRequestsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxSandboxListLibraryRequestsResponse();
            res.Requests.AddRange(Library.Where(r => request.Status == "" || request.Status == "all" || r.Status == request.Status));
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxDecideLibraryResponse> DecideSandboxLibraryAsync(NoxSandboxDecideLibraryRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Decisions.Add(request);
            var res = new NoxSandboxDecideLibraryResponse { Ok = true };
            res.Ids.AddRange(request.Ids);
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxImageStatusResponse> GetSandboxImageStatusAsync(NoxSandboxImageStatusRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxSandboxImageStatusResponse { CurrentVersion = "2026.11.20-1", Packages = new NoxSandboxPackages(), PendingPackages = new NoxSandboxPackages() };
            res.PendingPackages.Pip.Add("sympy");
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSandboxRebuildImageResponse> RebuildSandboxImageAsync(NoxSandboxRebuildImageRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            RebuildCalls++;
            return Call(new NoxSandboxRebuildImageResponse { Ok = true, Building = true });
        }

        public override AsyncUnaryCall<NoxSandboxListRunsResponse> ListSandboxRunsAsync(NoxSandboxListRunsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            RunLists.Add(request);
            var res = new NoxSandboxListRunsResponse { Total = 120, Limit = request.Limit, Offset = request.Offset };
            for (int i = 0; i < Math.Min(50, Math.Max(0, 120 - request.Offset)); i++)
                res.Runs.Add(new NoxSandboxRun { Id = 1000 - request.Offset - i, ChatId = -1001, Vm = "chat", Kind = "python", CodePreview = "print(1)" });
            return Call(res);
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static (SandboxTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new SandboxTabViewModel(manager) { ConfirmAction = _ => true };
        return (vm, client, manager);
    }

    private static async Task Attach(SandboxTabViewModel vm, Client client)
    {
        vm.SelectedNode = Node;
        for (int i = 0; i < 200 && client.VmLists == 0; i++) await Task.Delay(10);
        await vm.RefreshVmsAsync();
    }

    [Fact]
    public void ExtensionClassification()
    {
        Assert.True(SandboxTabViewModel.IsTextExt("/data/notes/a.MD"));
        Assert.True(SandboxTabViewModel.IsTextExt("/data/x.csv"));
        Assert.False(SandboxTabViewModel.IsTextExt("/data/x.docx"));
        Assert.True(SandboxTabViewModel.IsPreviewExt("/data/x.docx"));
        Assert.True(SandboxTabViewModel.IsPreviewExt("/data/x.PDF"));
        Assert.False(SandboxTabViewModel.IsPreviewExt("/data/x.exe"));
        Assert.False(SandboxTabViewModel.IsTextExt("/data/noext"));
    }

    [Fact]
    public async Task VmList_FiltersHotAndCold()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        Assert.Equal(4, vm.Vms.Count);
        vm.VmFilter = "hot";
        Assert.Equal(new long[] { -1001, -1003 }, vm.FilteredVms.Select(v => v.ChatId).OrderByDescending(x => x).ToArray());
        vm.VmFilter = "cold";
        Assert.Equal(new long[] { -1002, -1004 }, vm.FilteredVms.Select(v => v.ChatId).OrderByDescending(x => x).ToArray());
        vm.VmFilter = "all";
        Assert.Equal(4, vm.FilteredVms.Count());
        var alpha = vm.Vms.Single(v => v.ChatId == -1001);
        Assert.Equal("Alpha", alpha.ChatTitle);
        Assert.NotNull(alpha.Proto);
        Assert.Single(alpha.Proto!.Sessions);
        Assert.Single(alpha.Proto!.Jobs);
    }

    [Fact]
    public async Task VmActions_SendChatIdActionAndSession()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.SelectedVm = vm.Vms.Single(v => v.ChatId == -1001);
        vm.ActionSession = "s2";
        await vm.ResetSessionAsync();
        await vm.RestartVmAsync();
        await vm.HibernateVmAsync();
        await vm.KillVmAsync();
        Assert.Equal(new[] { "reset_session", "restart", "hibernate", "kill" }, client.Actions.Select(a => a.Action).ToArray());
        Assert.All(client.Actions, a => { Assert.True(a.HasChatId); Assert.Equal(-1001, a.ChatId); });
        Assert.Equal("s2", client.Actions[0].Session);
    }

    [Fact]
    public async Task DestructiveActions_RespectDeclinedConfirmation()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.ConfirmAction = _ => false;
        vm.SelectedVm = vm.Vms.Single(v => v.ChatId == -1001);
        await vm.KillVmAsync();
        await vm.RestartVmAsync();
        await vm.RebuildImageAsync();
        await vm.RestoreBackupAsync(new NoxSandboxBackup { Id = "20261120T030000Z" });
        Assert.DoesNotContain(client.Actions, a => a.Action is "kill" or "restart");
        Assert.Equal(0, client.RebuildCalls);
        Assert.Empty(client.Restores);
    }

    [Fact]
    public async Task ErrorFieldIsSurfaced()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.VmError = "sandbox unavailable";
        await Attach(vm, client);
        Assert.Contains("sandbox unavailable", vm.ErrorMessage);
    }

    [Fact]
    public async Task Files_TextIsShownReadOnlyAsPlainText()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.FilesChatId = -1001;
        await vm.RefreshFilesAsync();
        Assert.Equal(-1001, client.FileLists.Last().ChatId);
        var note = vm.Files.Single(f => f.Path == "/data/notes/a.md");
        Assert.Equal(1795150414, note.Mtime);
        await vm.OpenFileAsync(note);
        Assert.Single(client.TextReads);
        Assert.Equal("/data/notes/a.md", client.TextReads[0].Path);
        Assert.Equal("<b>not html</b>\n# note", vm.ViewerText);   // verbatim, no rendering
        Assert.Null(vm.PreviewImage);
        Assert.Contains("truncat", vm.ViewerNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Files_UnsupportedTypeIsNeverFetched()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.FilesChatId = -1001;
        await vm.RefreshFilesAsync();
        await vm.OpenFileAsync(vm.Files.Single(f => f.Path == "/data/blob.bin"));
        await vm.PreviewAsync(vm.Files.Single(f => f.Path == "/data/blob.bin"));
        Assert.Empty(client.TextReads);
        Assert.Empty(client.Previews);
        Assert.False(string.IsNullOrEmpty(vm.ViewerNotice));
    }

    [Fact]
    public async Task Preview_PagesAndOversizeRefusal()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.FilesChatId = -1001;
        await vm.RefreshFilesAsync();
        var chart = vm.Files.Single(f => f.Path == "/data/outbox/chart.png");
        vm.SelectedFile = chart;
        await vm.PreviewAsync(chart);
        Assert.Equal(1, client.Previews.Last().Page);
        Assert.Equal(3, vm.PreviewPages);
        await vm.NextPageAsync();
        Assert.Equal(2, client.Previews.Last().Page);
        await vm.NextPageAsync();
        await vm.NextPageAsync();                       // already on the last page
        Assert.Equal(3, client.Previews.Last().Page);
        await vm.PrevPageAsync();
        Assert.Equal(2, client.Previews.Last().Page);

        client.PreviewB64 = new string('A', SandboxTabViewModel.MaxPreviewBase64Bytes + 4);
        await vm.PreviewAsync(chart);
        Assert.Null(vm.PreviewImage);
        Assert.Contains("16 MB", vm.ViewerNotice);
    }

    [Fact]
    public async Task Library_ApproveAndRejectSelected()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.Library.Add(new NoxSandboxLibraryRequest { Id = 1, ChatId = -1001, Manager = "pip", Package = "sympy", Status = "pending" });
        client.Library.Add(new NoxSandboxLibraryRequest { Id = 2, ChatId = -1002, Manager = "apt", Package = "graphviz", Status = "pending" });
        client.Library.Add(new NoxSandboxLibraryRequest { Id = 3, ChatId = -1002, Manager = "pip", Package = "old", Status = "installed" });
        await Attach(vm, client);
        vm.LibraryStatusFilter = "pending";
        await vm.RefreshLibraryRequestsAsync();
        Assert.Equal(new long[] { 1, 2 }, vm.LibraryRequests.Select(r => r.Id).OrderBy(x => x).ToArray());

        vm.LibraryRequests.Single(r => r.Id == 1).IsSelected = true;
        await vm.ApproveSelectedAsync();
        Assert.True(client.Decisions.Last().Approve);
        Assert.Equal(new long[] { 1 }, client.Decisions.Last().Ids.ToArray());
        Assert.Equal("studio", client.Decisions.Last().DecidedBy);

        foreach (var r in vm.LibraryRequests) r.IsSelected = r.Id == 2;
        vm.DecisionNote = "  not needed ";
        await vm.RejectSelectedAsync();
        Assert.False(client.Decisions.Last().Approve);
        Assert.Equal(new long[] { 2 }, client.Decisions.Last().Ids.ToArray());
        Assert.Equal("not needed", client.Decisions.Last().Note);
    }

    [Fact]
    public async Task Runs_Paging()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        await vm.RefreshRunsAsync();
        Assert.Equal(50, vm.Runs.Count);
        Assert.Equal(0, client.RunLists.Last().Offset);
        await vm.NextRunsPageAsync();
        Assert.Equal(50, vm.RunsOffset);
        Assert.Equal(50, client.RunLists.Last().Offset);
        await vm.PrevRunsPageAsync();
        await vm.PrevRunsPageAsync();                   // never below zero
        Assert.Equal(0, vm.RunsOffset);
        Assert.Equal(0, client.RunLists.Last().Offset);
    }
}
