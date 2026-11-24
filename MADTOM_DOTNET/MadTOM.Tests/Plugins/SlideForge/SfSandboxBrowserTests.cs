using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

/// <summary>Edge cases of the Sandbox tab (VM file browser, host totals, older bots without sandbox_stats).</summary>
public class SfSandboxBrowserTests
{
    private static object OneVm(string state = "warm") => new
    {
        vms = new[] { new { chat_id = 42L, user_label = "alice", state, mem_mb = 1536, vcpus = 2 } },
        totals = new { running = 1, max_vms = 8, guest_mem_mb = 1536, max_guest_mem_mb = 8192 },
        caps = new { max_vms = 8, max_guest_mem_mb = 8192, daily_cpu_seconds_per_chat = 3600, chat_vm_disk_gb = 2 },
    };

    private static (FakeSlideForgeClient, SfSandboxViewModel) Make()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => OneVm();
        fake.Handlers["sandbox_list_files"] = p => new
        {
            entries = new[] { new { path = (string)p!["path"]! + "/x.txt", type = "file", size = 3L, mtime = 1L } },
            as_of = "2026-11-24T12:00:00+00:00",
            from_cache = false,
        };
        return (fake, new SfSandboxViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Navigating_up_never_leaves_data()
    {
        var (fake, vm) = Make();
        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];

        vm.FilesPath = "/data/studio/J-1";
        await vm.NavigateUpAsync();
        Assert.Equal("/data/studio", vm.FilesPath);
        await vm.NavigateUpAsync();
        Assert.Equal("/data", vm.FilesPath);
        await vm.NavigateUpAsync();
        Assert.Equal("/data", vm.FilesPath);
        Assert.Equal("/data", (string)fake.LastPayload("sandbox_list_files")!["path"]!);
        Assert.Equal(42L, (long)fake.LastPayload("sandbox_list_files")!["chat_id"]!);
        Assert.Single(vm.Files);
    }

    [Fact]
    public async Task Opening_a_directory_lists_it()
    {
        var (fake, vm) = Make();
        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];
        await vm.OpenFileAsync(new SfSandboxFileRow("/data/studio", "dir", 0, 0));
        Assert.Equal("/data/studio", vm.FilesPath);
        Assert.Equal("/data/studio", (string)fake.LastPayload("sandbox_list_files")!["path"]!);
    }

    [Fact]
    public async Task Host_totals_show_caps_and_fractions()
    {
        var (_, vm) = Make();
        await vm.LoadVmsAsync();
        Assert.Equal("1 / 8 VMs", vm.RunningVmsText);
        Assert.Equal("1536 / 8192 MiB", vm.GuestMemText);
        Assert.Equal(0.125, vm.RunningVmsFraction, 3);
        Assert.Equal(1536.0 / 8192, vm.GuestMemFraction, 3);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Older_bot_without_sandbox_stats_falls_back_to_the_vm_list()
    {
        var (fake, vm) = Make();
        fake.Errors["sandbox_stats"] = "unknown_method";
        fake.Handlers["sandbox_list_vms"] = _ => new { vms = new[] { new { chat_id = 7L, state = "cold" } } };
        await vm.LoadVmsAsync();
        Assert.Single(vm.Vms);
        Assert.Equal(7L, vm.Vms[0].ChatId);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task No_vm_selected_means_no_file_calls()
    {
        var (fake, vm) = Make();
        await vm.RefreshFilesAsync();
        await vm.NavigateUpAsync();
        Assert.Equal(0, fake.Count("sandbox_list_files"));
    }
}
