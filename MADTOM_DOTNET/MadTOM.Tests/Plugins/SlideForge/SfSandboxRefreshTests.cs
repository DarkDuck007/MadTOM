using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

/// <summary>Tests for Sandbox tab in-place refresh, throttling, overlap guard, and file viewer fallbacks.</summary>
public class SfSandboxRefreshTests
{
    private sealed class AsyncFakeSlideForgeClient : SlideForgeControlService.SlideForgeControlServiceClient
    {
        public int SandboxStatsCallCount => _sandboxStatsCalls;
        private int _sandboxStatsCalls;
        public Func<JsonNode?, Task<object>>? SandboxStatsHandler { get; set; }

        public override AsyncUnaryCall<SfCallResponse> CallAsync(
            SfCallRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var payload = string.IsNullOrEmpty(request.PayloadJson) ? null : JsonNode.Parse(request.PayloadJson);
            if (request.Method == "sandbox_stats")
            {
                Interlocked.Increment(ref _sandboxStatsCalls);
                var task = RunHandlerAsync(payload);
                return new AsyncUnaryCall<SfCallResponse>(task, Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => new Metadata(), () => { });
            }

            return new AsyncUnaryCall<SfCallResponse>(
                Task.FromResult(new SfCallResponse { Ok = true, PayloadJson = "{}" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        private async Task<SfCallResponse> RunHandlerAsync(JsonNode? payload)
        {
            var obj = SandboxStatsHandler is not null ? await SandboxStatsHandler(payload) : new { };
            return new SfCallResponse { Ok = true, PayloadJson = JsonSerializer.Serialize(obj) };
        }
    }

    private static object StatsPayload(params object[] vms) => new
    {
        vms,
        totals = new { running = vms.Length, max_vms = 8, guest_mem_mb = vms.Length * 1024L, max_guest_mem_mb = 8192L },
        caps = new { max_vms = 8, max_guest_mem_mb = 8192L, daily_cpu_seconds_per_chat = 3600.0, chat_vm_disk_gb = 2L },
        host = new { state_dir_used_percent = 22.5 },
    };

    private static object VmObj(long chatId, string label, string state = "warm", double cpu = 100.0, long disk = 1024L, long uptime = 60L) => new
    {
        chat_id = chatId,
        user_label = label,
        kind = "chat",
        state,
        image_version = "v1",
        mem_mb = 1024L,
        vcpus = 2,
        disk_used_bytes = disk,
        disk_quota_bytes = 2147483648L,
        last_used = "2026-11-24T12:00:00Z",
        uptime_seconds = uptime,
        cpu_seconds_today = cpu,
        cpu_budget_seconds = 3600.0,
    };

    [Fact]
    public async Task Refresh_does_not_raise_reset_and_keeps_selected_vm_instance_and_updates_in_place()
    {
        var fake = new FakeSlideForgeClient();
        double cpu = 100.0;
        string state = "warm";
        fake.Handlers["sandbox_stats"] = _ => StatsPayload(
            VmObj(10L, "alice", state, cpu, disk: 2048L, uptime: 120L),
            VmObj(20L, "bob", "cold", 50.0));

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();
        var originalSelected = vm.Vms[0];
        vm.SelectedVm = originalSelected;

        var actions = new List<NotifyCollectionChangedAction>();
        vm.Vms.CollectionChanged += (_, e) => actions.Add(e.Action);

        var changedProps = new List<string?>();
        originalSelected.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        cpu = 450.0;
        state = "busy";
        await vm.LoadVmsAsync();

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Same(originalSelected, vm.SelectedVm);
        Assert.Same(originalSelected, vm.Vms[0]);
        Assert.Equal(10L, vm.SelectedVm!.ChatId);
        Assert.Equal("busy", originalSelected.State);
        Assert.Equal(450.0, originalSelected.CpuSecondsToday);
        Assert.Contains(nameof(SfSandboxVmRow.CpuSecondsToday), changedProps);
        Assert.Contains(nameof(SfSandboxVmRow.State), changedProps);
    }

    [Fact]
    public async Task Refresh_removes_vanished_vm_and_inserts_new_vm_without_reset()
    {
        var fake = new FakeSlideForgeClient();
        object current = StatsPayload(VmObj(10L, "alice"), VmObj(20L, "bob"));
        fake.Handlers["sandbox_stats"] = _ => current;

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();
        var bobInstance = vm.Vms[1];
        vm.SelectedVm = bobInstance;

        var actions = new List<NotifyCollectionChangedAction>();
        vm.Vms.CollectionChanged += (_, e) => actions.Add(e.Action);

        current = StatsPayload(VmObj(20L, "bob", cpu: 250.0), VmObj(30L, "carol"));
        await vm.LoadVmsAsync();

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { 20L, 30L }, vm.Vms.Select(r => r.ChatId).ToArray());
        Assert.Same(bobInstance, vm.Vms[0]);
        Assert.Same(bobInstance, vm.SelectedVm);
        Assert.Equal(250.0, bobInstance.CpuSecondsToday);
    }

    [Fact]
    public async Task Overlapping_silent_refreshes_call_sandbox_stats_once()
    {
        var gate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new AsyncFakeSlideForgeClient
        {
            SandboxStatsHandler = async _ =>
            {
                started.TrySetResult();
                return await gate.Task;
            }
        };

        var vm = new SfSandboxViewModel(new SfRpc(client, "node-a"));
        var first = vm.RefreshSilentAsync();
        await started.Task;

        // Second silent refresh while first is still in flight must be dropped.
        var second = vm.RefreshSilentAsync();
        await second;

        Assert.Equal(1, client.SandboxStatsCallCount);
        Assert.False(vm.IsBusy);

        gate.SetResult(StatsPayload(VmObj(10L, "alice")));
        await first;

        Assert.Equal(1, client.SandboxStatsCallCount);
        Assert.Single(vm.Vms);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Silent_refresh_is_throttled_by_clock_and_manual_refresh_ignores_throttle()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => StatsPayload(VmObj(10L, "alice"));
        fake.Handlers["sandbox_image_status"] = _ => new { current_version = "v1", building = false };

        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"))
        {
            Clock = () => now
        };

        Assert.Equal(10, vm.AutoRefreshIntervalSeconds);

        await vm.RefreshSilentAsync();
        Assert.Equal(1, fake.Count("sandbox_stats"));

        // 5 seconds later (< 10s interval) -> skipped
        now = now.AddSeconds(5);
        await vm.RefreshSilentAsync();
        Assert.Equal(1, fake.Count("sandbox_stats"));

        // Manual refresh ignores throttle
        await vm.LoadVmsAsync();
        Assert.Equal(2, fake.Count("sandbox_stats"));

        // 9 seconds after manual refresh -> silent still skipped
        now = now.AddSeconds(9);
        await vm.RefreshSilentAsync();
        Assert.Equal(2, fake.Count("sandbox_stats"));

        // Advance past 10 seconds -> silent runs
        now = now.AddSeconds(2);
        await vm.RefreshSilentAsync();
        Assert.Equal(3, fake.Count("sandbox_stats"));

        // RefreshAllAsync also ignores throttle
        await vm.RefreshAllAsync();
        Assert.Equal(4, fake.Count("sandbox_stats"));
    }

    [Fact]
    public async Task Selected_vm_file_list_and_viewer_survive_refresh()
    {
        var fake = new FakeSlideForgeClient();
        double cpu = 100.0;
        fake.Handlers["sandbox_stats"] = _ => StatsPayload(VmObj(42L, "alice", cpu: cpu));
        fake.Handlers["sandbox_list_files"] = _ => new
        {
            entries = new[]
            {
                new { path = "/data/notes.md", type = "file", size = 12L, mtime = 1700000000L },
                new { path = "/data/out.txt", type = "file", size = 5L, mtime = 1700000001L },
            },
            as_of = "2026-11-24T12:00:00Z",
            from_cache = false,
        };
        fake.Handlers["sandbox_read_text"] = _ => new { text = "hello world", binary = false, truncated = false, size = 11L };

        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"))
        {
            Clock = () => now
        };

        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];
        await vm.RefreshFilesAsync();
        await vm.OpenFileAsync(vm.Files[0]);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("/data/notes.md", vm.SelectedFile?.Path);
        Assert.Equal("hello world", vm.ViewerText);

        cpu = 300.0;
        now = now.AddSeconds(15);
        await vm.RefreshSilentAsync();

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("/data/notes.md", vm.SelectedFile?.Path);
        Assert.Equal("hello world", vm.ViewerText);
        Assert.Equal(1, fake.Count("sandbox_list_files"));
        Assert.Equal(300.0, vm.SelectedVm!.CpuSecondsToday);
    }

    [Fact]
    public async Task Unknown_extension_file_falls_back_to_sandbox_read_text()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => StatsPayload(VmObj(42L, "alice"));
        bool returnBinary = false;
        fake.Handlers["sandbox_read_text"] = _ => returnBinary
            ? new { text = "", binary = true, truncated = false, size = 4096L }
            : new { text = "plain content", binary = false, truncated = false, size = 13L };

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];

        var unknownFile = new SfSandboxFileRow("/data/Makefile", "file", 13L, 1700000000L);
        Assert.False(unknownFile.IsText);
        Assert.False(unknownFile.IsPreview);

        await vm.OpenFileAsync(unknownFile);
        Assert.Equal(1, fake.Count("sandbox_read_text"));
        Assert.True(vm.IsTextViewerVisible);
        Assert.Equal("plain content", vm.ViewerText);

        // Preview command on unknown-extension file also tries sandbox_read_text; binary=true shows Binary file notice
        returnBinary = true;
        var binFile = new SfSandboxFileRow("/data/blob.bin", "file", 4096L, 1700000001L);
        await vm.PreviewFileAsync(binFile);
        Assert.Equal(2, fake.Count("sandbox_read_text"));
        Assert.False(vm.IsTextViewerVisible);
        Assert.Contains("Binary file", vm.ViewerNotice);
    }

    [Fact]
    public async Task Preview_not_supported_maps_to_friendly_notice()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => StatsPayload(VmObj(42L, "alice"));
        fake.Errors["sandbox_preview"] = "not_supported";

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];

        var docFile = new SfSandboxFileRow("/data/report.docx", "file", 1024L, 1700000000L);
        await vm.PreviewFileAsync(docFile);

        Assert.Equal("No page preview available for this file.", vm.ViewerNotice);
        Assert.Null(vm.PreviewImage);
        Assert.Null(vm.ErrorMessage);
    }
}
