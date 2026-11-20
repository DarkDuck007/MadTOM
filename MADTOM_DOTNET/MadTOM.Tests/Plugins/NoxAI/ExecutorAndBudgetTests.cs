using System;
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

/// <summary>Doc Studio executor details (Active Jobs) and per-chat sandbox budgets (Sandbox → Budgets).</summary>
[Collection("Sequential")]
public class ExecutorAndBudgetTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public NoxActiveJobsResponse Jobs { get; set; } = new();
        public int JobCalls { get; private set; }
        public NoxSandboxListVmsResponse Vms { get; set; } = new();
        public int VmCalls { get; private set; }

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxActiveJobsResponse> GetActiveJobsAsync(NoxActiveJobsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            JobCalls++;
            return Call(Jobs);
        }

        public override AsyncUnaryCall<NoxSandboxListVmsResponse> ListSandboxVmsAsync(NoxSandboxListVmsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            VmCalls++;
            return Call(Vms);
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static NoxNodeManager Manager(Client client) =>
        new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);

    private static NoxJobProgressEvent Job(string id, string executor, string status = "running", long chat = -5) => new()
    {
        JobId = id, JobType = executor == "" ? "image_studio" : "doc_studio", Executor = executor, ChatId = chat,
        Status = status, Progress = 0.5, Details = "d", ElapsedMs = 3000
    };

    // ---------------------------------------------------------------- Active Jobs

    [Theory]
    [InlineData("vm", "🖥 Chat VM", "chat -5 sandbox VM · /data/studio/D-1")]
    [InlineData("bwrap", "🔒 Host jail", "host bubblewrap jail")]
    [InlineData("", "", "")]
    [InlineData("other", "other", "")]
    public void ExecutorBadgeAndLocation(string executor, string badge, string location)
    {
        var item = new ActiveJobItemModel { JobId = "D-1", ChatId = -5, Executor = executor };
        Assert.Equal(badge, item.ExecutorBadge);
        Assert.Equal(location, item.ExecutorLocation);
        Assert.Equal(executor != "", item.HasExecutor);
    }

    [Fact]
    public void VmLocationNeedsAChat()
    {
        Assert.Equal("", new ActiveJobItemModel { JobId = "D-1", Executor = "vm" }.ExecutorLocation);
    }

    [Fact]
    public async Task RefreshCarriesExecutorAndDocGenSummary()
    {
        var client = new Client();
        client.Jobs.ActiveJobs.Add(Job("D-1", "vm"));
        client.Jobs.ActiveJobs.Add(Job("D-2", "bwrap"));
        client.Jobs.ActiveJobs.Add(Job("I-1", ""));
        client.Jobs.DocGen = new NoxDocGenSettings
        {
            Executor = "auto", CopyToSandbox = true,
            Sandbox = new NoxDocGenSandboxSettings { KeepJobs = 5, SyncMaxFileBytes = 52428800, ExecMarginS = 15, LockWaitS = 60 }
        };
        await using var manager = Manager(client);
        var vm = new ActiveJobsTabViewModel(manager) { SelectedNode = Node };
        await vm.RefreshJobs();

        Assert.Equal(new[] { "vm", "bwrap", "" }, vm.Jobs.Select(j => j.Executor));
        Assert.Equal(1, vm.VmJobCount);
        Assert.Equal(1, vm.BwrapJobCount);
        Assert.Equal("Doc Studio executor: auto · copy to /data/documents: on · keep 5 jobs · sync ≤ 50 MB · lock wait 60s · exec margin 15s",
            vm.DocGenSummary);
    }

    [Fact]
    public async Task DocGenSummaryWithoutSandboxSettingsAndWhenMissing()
    {
        var client = new Client { Jobs = new NoxActiveJobsResponse { DocGen = new NoxDocGenSettings { Executor = "bwrap" } } };
        await using var manager = Manager(client);
        var vm = new ActiveJobsTabViewModel(manager) { SelectedNode = Node };
        await vm.RefreshJobs();
        Assert.Equal("Doc Studio executor: bwrap · copy to /data/documents: off", vm.DocGenSummary);

        client.Jobs = new NoxActiveJobsResponse();
        await vm.RefreshJobs();
        Assert.Equal("", vm.DocGenSummary);
    }

    [Fact]
    public async Task LiveEventsKeepExecutorCountsAndSelection()
    {
        var client = new Client();
        await using var manager = Manager(client);
        var vm = new ActiveJobsTabViewModel(manager) { SelectedNode = Node };
        await vm.RefreshJobs();

        vm.OnJobEventReceived(Job("D-1", "vm"));
        vm.OnJobEventReceived(Job("D-2", "vm"));
        vm.OnJobEventReceived(Job("D-3", "bwrap", status: "queued"));
        Assert.Equal(2, vm.VmJobCount);
        Assert.Equal(1, vm.BwrapJobCount);

        vm.SelectedJob = vm.Jobs.Single(j => j.JobId == "D-1");
        vm.OnJobEventReceived(Job("D-1", "vm", status: "completed"));
        Assert.Equal(1, vm.VmJobCount); // finished jobs no longer count
        Assert.Equal("D-1", vm.SelectedJob?.JobId);
        Assert.Equal("completed", vm.SelectedJob?.Status);
        Assert.Equal("vm", vm.SelectedJob?.Executor);
    }

    [Fact]
    public async Task RefreshKeepsSelectionById()
    {
        var client = new Client();
        client.Jobs.ActiveJobs.Add(Job("D-1", "vm"));
        await using var manager = Manager(client);
        var vm = new ActiveJobsTabViewModel(manager) { SelectedNode = Node };
        await vm.RefreshJobs();
        vm.SelectedJob = vm.Jobs[0];
        await vm.RefreshJobs();
        Assert.NotNull(vm.SelectedJob);
        Assert.Same(vm.Jobs[0], vm.SelectedJob);
    }

    // ---------------------------------------------------------------- Budgets

    [Theory]
    [InlineData(0, 3600, "ok", 0.0)]
    [InlineData(2879, 3600, "ok", 2879 / 3600.0)]
    [InlineData(2880, 3600, "near", 0.8)]
    [InlineData(3600, 3600, "exceeded", 1.0)]
    [InlineData(5000, 3600, "exceeded", 1.0)]
    [InlineData(100, 0, "ok", 0.0)]
    public void BudgetStateAndFraction(double used, double budget, string state, double fraction)
    {
        var row = new SandboxVmRow { CpuSecondsToday = used, CpuBudgetSeconds = budget };
        Assert.Equal(state, row.BudgetState);
        Assert.Equal(fraction, row.CpuFraction, 6);
    }

    [Fact]
    public void SnapshotQuotaDisplay()
    {
        Assert.Equal("snapshots off", new SandboxVmRow { SnapshotBytes = 5 }.SnapshotQuotaDisplay);
        var row = new SandboxVmRow { SnapshotBytes = 150L << 20, SnapshotQuotaBytes = 2L << 30 };
        Assert.Equal("150 MB / 2 GB", row.SnapshotQuotaDisplay);
    }

    [Theory]
    [InlineData("2026-11-21T00:00:00+09:00", "2026-11-20T18:30:00+09:00", "resets in 5h 30m")]
    [InlineData("2026-11-21T00:00:00+09:00", "2026-11-20T14:30:00Z", "resets in 30m")] // offsets are honoured
    [InlineData("2026-11-21T00:00:00+09:00", "2026-11-20T23:59:30+09:00", "resets in 1m")]
    [InlineData("2026-11-21T00:00:00+09:00", "2026-11-21T00:00:00+09:00", "resets now")]
    [InlineData("", "2026-11-20T18:30:00+09:00", "")]
    [InlineData("garbage", "2026-11-20T18:30:00+09:00", "")]
    public void FormatResetsIn(string resetsAt, string now, string expected)
    {
        var row = new SandboxVmRow { CpuResetsAt = resetsAt };
        Assert.Equal(expected, row.FormatResetsIn(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ProtoFieldsAreCopied()
    {
        var row = new SandboxVmRow(new NoxSandboxVmInfo
        {
            ChatId = 1, Kind = "chat", SnapshotQuotaBytes = 42, CpuResetsAt = "2026-11-21T00:00:00+09:00"
        });
        Assert.Equal(42, row.SnapshotQuotaBytes);
        Assert.Equal("2026-11-21T00:00:00+09:00", row.CpuResetsAt);
        Assert.Equal("", new SandboxVmRow(new NoxSandboxVmInfo { ChatId = 2 }).CpuResetsAt);
    }

    [Fact]
    public async Task BudgetRowsSortedAndSummarised()
    {
        var client = new Client();
        client.Vms.Vms.Add(new NoxSandboxVmInfo { ChatId = 1, Kind = "chat", State = "cold", CpuSecondsToday = 100, CpuBudgetSeconds = 3600 });
        client.Vms.Vms.Add(new NoxSandboxVmInfo { ChatId = 2, Kind = "chat", State = "warm", CpuSecondsToday = 3600, CpuBudgetSeconds = 3600 });
        client.Vms.Vms.Add(new NoxSandboxVmInfo { ChatId = 3, Kind = "chat", State = "hibernated", CpuSecondsToday = 3000, CpuBudgetSeconds = 3600 });
        client.Vms.Vms.Add(new NoxSandboxVmInfo { ChatId = 4, Kind = "chat", State = "cold", CpuSecondsToday = 100, CpuBudgetSeconds = 3600,
            DiskUsedBytes = 1L << 30, DiskQuotaBytes = 2L << 30 });
        client.Vms.Vms.Add(new NoxSandboxVmInfo { ChatId = 0, Kind = "temp", State = "busy" });
        await using var manager = Manager(client);
        var vm = new SandboxTabViewModel(manager);

        var changed = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SelectedNode = Node;
        for (int i = 0; i < 200 && client.VmCalls == 0; i++) await Task.Delay(10);
        await vm.RefreshVmsAsync();

        Assert.Equal(new long[] { 2, 3, 4, 1 }, vm.BudgetRows.Select(r => r.ChatId));
        Assert.Equal(1, vm.ChatsOverBudget);
        Assert.Equal(1, vm.ChatsNearBudget);
        Assert.Equal("4 chats · 1 over budget · 1 near budget · daily CPU budget 3600s per chat", vm.BudgetSummary);
        Assert.Contains(nameof(SandboxTabViewModel.BudgetRows), changed);
        Assert.Contains(nameof(SandboxTabViewModel.BudgetSummary), changed);
        vm.Dispose();
    }

    [Fact]
    public async Task EmptyBudgetSummary()
    {
        await using var manager = Manager(new Client());
        var vm = new SandboxTabViewModel(manager);
        Assert.Empty(vm.BudgetRows);
        Assert.Equal("0 chats · 0 over budget · 0 near budget", vm.BudgetSummary);
        vm.Dispose();
    }
}
