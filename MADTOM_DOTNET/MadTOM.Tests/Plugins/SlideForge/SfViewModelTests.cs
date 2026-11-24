using System.Text.Json.Nodes;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

/// <summary>
/// View models are driven directly (no Avalonia dispatcher): load methods are awaited, live stream data is applied with
/// Apply* methods. Response shapes follow SlideForge's src/control/rpc.py (tests/test_control_rpc.py).
/// </summary>
public class SfJobsViewModelTests
{
    private static object Row(string id, string status, long user = 7, string fmt = "pptx") => new
    {
        job_id = id, user_id = user, username = $"u{user}", status, output_format = fmt, origin = "guided",
        mode = "generate", title = "Deck " + id, created_at = "2026-11-24T10:00:00+00:00", started_at = (string?)null,
        finished_at = (string?)null, error_code = (string?)null, charged = 1,
    };

    private static (FakeSlideForgeClient fake, SfJobsViewModel vm) Make(int total = 3)
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_jobs"] = p => new
        {
            total, limit = (int?)p?["limit"] ?? 50, offset = (int?)p?["offset"] ?? 0,
            items = new[] { Row("J-00000001", "running"), Row("J-00000002", "completed"), Row("J-00000003", "failed") },
        };
        fake.Handlers["get_job"] = p => new
        {
            job = Row((string)p!["job_id"]!, "waiting_input"),
            spec = new { title = "Solar", output_format = "pptx", language = "fa" },
            events = new[] { new { id = 1, at = "2026-11-24T10:00:01+00:00", type = "note", payload = new { text = "Outline ready" } } },
            questions = new[] { new { id = 4, text = "Palette?", options = new[] { "Blue", "Green" }, answer = (string?)null, answer_source = (string?)null } },
            files = new[] { new { id = 11, role = "output", name = "deck.pptx", kind = "pptx", bytes = 2048 } },
        };
        fake.Handlers["cancel_job"] = _ => new { ok = true };
        fake.Handlers["extend_job"] = _ => new { ok = true };
        return (fake, new SfJobsViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Load_populates_rows_and_total()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(new[] { "J-00000001", "J-00000002", "J-00000003" }, vm.Jobs.Select(j => j.JobId));
        Assert.Equal("u7", vm.Jobs[0].Username);
        Assert.Equal("running", vm.Jobs[0].Status);
        Assert.Equal(vm.PageSize, (int)fake.LastPayload("list_jobs")!["limit"]!);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Filters_are_sent_and_reset_paging()
    {
        var (fake, vm) = Make(total: 120);
        await vm.LoadAsync();
        await vm.NextPageAsync();
        Assert.Equal(vm.PageSize, (int)fake.LastPayload("list_jobs")!["offset"]!);
        vm.StatusFilter = "failed";
        vm.SearchText = "J-0000";
        await vm.LoadAsync();
        var p = fake.LastPayload("list_jobs")!;
        Assert.Equal("failed", (string)p["status"]!);
        Assert.Equal("J-0000", (string)p["query"]!);
        Assert.Equal(0, (int)p["offset"]!);
        vm.StatusFilter = SfJobsViewModel.AllStatuses;
        vm.SearchText = "";
        await vm.LoadAsync();
        var q = fake.LastPayload("list_jobs")!.AsObject();
        Assert.False(q.ContainsKey("status") && q["status"] is not null);
        Assert.False(q.ContainsKey("query") && q["query"] is not null);
    }

    [Fact]
    public async Task Select_loads_detail_with_events_questions_files()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        Assert.Equal("J-00000001", (string)fake.LastPayload("get_job")!["job_id"]!);
        var d = vm.Detail!;
        Assert.Equal("J-00000001", d.JobId);
        Assert.Contains("Solar", d.SpecJson);
        Assert.Equal("note", d.Events.Single().Type);
        Assert.Contains("Outline ready", d.Events.Single().Summary);
        Assert.Equal(new[] { "Blue", "Green" }, d.Questions.Single().Options);
        Assert.Equal("deck.pptx", d.Files.Single().Name);
    }

    [Fact]
    public async Task Cancel_and_extend_target_selected_job_and_refresh_detail()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        var before = fake.Count("get_job");
        await vm.CancelSelectedAsync();
        Assert.Equal("J-00000001", (string)fake.LastPayload("cancel_job")!["job_id"]!);
        Assert.True(fake.Count("get_job") > before);
        await vm.ExtendSelectedAsync();
        Assert.Equal("J-00000001", (string)fake.LastPayload("extend_job")!["job_id"]!);
    }

    [Fact]
    public async Task Rpc_errors_are_shown_not_thrown()
    {
        var (fake, vm) = Make();
        fake.Errors["list_jobs"] = "bot_unavailable";
        await vm.LoadAsync();
        Assert.Contains("bot_unavailable", vm.ErrorMessage);
        Assert.Empty(vm.Jobs);
        fake.Errors.Clear();
        await vm.LoadAsync();
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Live_job_events_update_status_and_add_new_jobs()
    {
        var (_, vm) = Make();
        await vm.LoadAsync();
        vm.ApplyJobEvent(new SfJobEvent { JobId = "J-00000001", UserId = 7, Kind = "result", Status = "completed", Json = "{}" });
        Assert.Equal("completed", vm.Jobs.Single(j => j.JobId == "J-00000001").Status);
        vm.ApplyJobEvent(new SfJobEvent { JobId = "J-000000aa", UserId = 9, Kind = "status", Status = "queued", Json = "{}" });
        Assert.Equal("J-000000aa", vm.Jobs[0].JobId);
        Assert.Equal(4, vm.TotalCount);
        // with a filter that the new job doesn't match, it is not inserted
        vm.StatusFilter = "failed";
        vm.ApplyJobEvent(new SfJobEvent { JobId = "J-000000bb", UserId = 9, Kind = "status", Status = "queued", Json = "{}" });
        Assert.DoesNotContain(vm.Jobs, j => j.JobId == "J-000000bb");
    }

    [Fact]
    public async Task Live_events_append_to_open_detail_timeline()
    {
        var (_, vm) = Make();
        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        vm.ApplyJobEvent(new SfJobEvent { JobId = "J-00000001", Kind = "note", Status = "running", Json = "{\"text\":\"Rendering\"}" });
        Assert.Equal(2, vm.Detail!.Events.Count);
        Assert.Contains("Rendering", vm.Detail.Events.Last().Summary);
        vm.ApplyJobEvent(new SfJobEvent { JobId = "J-00000002", Kind = "note", Status = "running", Json = "{\"text\":\"x\"}" });
        Assert.Equal(2, vm.Detail.Events.Count);
    }

    [Fact]
    public async Task Select_merges_list_job_files_and_inspects_text_and_downloads_bytes()
    {
        var (fake, vm) = Make();
        fake.Handlers["list_job_files"] = _ => new
        {
            job_id = "J-00000001",
            items = new object[]
            {
                new { id = (long?)1, path = "inputs/brief.txt", name = "brief.txt", role = "input", size = 12, kind = "text", mtime = 1700000000L },
                new { id = (long?)null, path = "output/deck.pptx", name = "deck.pptx", role = "output", size = 4096, kind = "pptx", mtime = 1700000100L },
            },
        };
        fake.Handlers["get_job_file"] = p =>
        {
            var mode = (string)p!["mode"]!;
            if (mode == "text")
                return new { name = "brief.txt", path = "inputs/brief.txt", kind = "text", text = "Hello brief!", truncated = false, binary = false, size = 12 };
            return new { name = "deck.pptx", path = "output/deck.pptx", kind = "pptx", bytes_b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
        };

        await vm.LoadAsync();
        await vm.SelectAsync(vm.Jobs[0]);
        Assert.Equal(2, vm.Detail!.Files.Count);
        Assert.Equal("inputs/brief.txt", vm.Detail.Files[0].Path);
        Assert.Equal("output/deck.pptx", vm.Detail.Files[1].Path);

        await vm.InspectFileAsync(vm.Detail.Files[0]);
        Assert.True(vm.IsFileTextVisible);
        Assert.Equal("Hello brief!", vm.FileViewerText);
        Assert.Equal("inputs/brief.txt", (string)fake.LastPayload("get_job_file")!["path"]!);

        var dl = await vm.FetchJobFileBytesAsync(vm.Detail.Files[1]);
        Assert.NotNull(dl);
        Assert.Equal("deck.pptx", dl.Value.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, dl.Value.Bytes);
    }
}

public class SfSandboxViewModelTests
{
    [Fact]
    public async Task LoadVms_parses_sandbox_stats_totals_caps_and_vm_metrics()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => new
        {
            vms = new[]
            {
                new
                {
                    chat_id = 42L,
                    user_label = "alice",
                    kind = "chat",
                    state = "warm",
                    image_version = "v3",
                    mem_mb = 1536,
                    vcpus = 2,
                    uptime_seconds = 3660,
                    cpu_seconds_today = 900.0,
                    cpu_budget_seconds = 3600.0,
                    disk_used_bytes = 536870912L,
                    disk_quota_bytes = 2147483648L,
                    last_used = "2026-11-24T11:50:00+00:00",
                },
            },
            totals = new
            {
                running = 1,
                max_vms = 8,
                guest_mem_mb = 1536,
                max_guest_mem_mb = 8192,
                total_disk_used_bytes = 536870912L,
                total_cpu_seconds_today = 900.0,
            },
            host = new { state_dir_used_percent = 18.5 },
        };

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();

        Assert.Single(vm.Vms);
        var row = vm.Vms[0];
        Assert.Equal("alice (42)", row.DisplayLabel);
        Assert.True(row.IsHot);
        Assert.Equal("1h 1m", row.UptimeDisplay);
        Assert.Equal(0.25, row.CpuFraction, 3);
        Assert.Equal(0.25, row.DiskUsageFraction, 3);
        Assert.Equal(1, vm.RunningVms);
        Assert.Equal(8, vm.MaxVms);
        Assert.Equal(1536, vm.GuestMemMb);
        Assert.Equal(8192, vm.MaxGuestMemMb);
        Assert.Equal(0.125, vm.RunningVmsFraction, 3);
        Assert.Equal(1536.0 / 8192.0, vm.GuestMemFraction, 3);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task File_browser_lists_navigates_and_reads_text()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["sandbox_stats"] = _ => new
        {
            vms = new[] { new { chat_id = 7L, user_label = "u7", kind = "chat", state = "warm", mem_mb = 1536, vcpus = 2 } },
            totals = new { running = 1, max_vms = 8, guest_mem_mb = 1536, max_guest_mem_mb = 8192 },
        };
        fake.Handlers["sandbox_list_files"] = p =>
        {
            var path = (string)p!["path"]!;
            if (path == "/data")
            {
                return new
                {
                    entries = new[]
                    {
                        new { path = "/data/studio", type = "dir", size = 0L, mtime = 1700000000L },
                        new { path = "/data/notes.md", type = "file", size = 14L, mtime = 1700000005L },
                    },
                    as_of = "2026-11-24T12:00:00Z",
                    from_cache = false,
                };
            }
            return new { entries = new[] { new { path = "/data/studio/out.txt", type = "file", size = 5L, mtime = 1700000010L } }, from_cache = false };
        };
        fake.Handlers["sandbox_read_text"] = _ => new { text = "# Slide notes", binary = false, truncated = false, size = 13L };

        var vm = new SfSandboxViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadVmsAsync();
        vm.SelectedVm = vm.Vms[0];
        await vm.RefreshFilesAsync();
        Assert.Equal(2, vm.Files.Count);

        await vm.OpenFileAsync(vm.Files[1]);
        Assert.True(vm.IsTextViewerVisible);
        Assert.Equal("# Slide notes", vm.ViewerText);

        await vm.OpenFileAsync(vm.Files[0]);
        Assert.Equal("/data/studio", vm.FilesPath);
        Assert.Single(vm.Files);
        Assert.Equal("/data/studio/out.txt", vm.Files[0].Path);

        await vm.NavigateUpAsync();
        Assert.Equal("/data", vm.FilesPath);
    }
}

public class SfUsersViewModelTests
{
    private static (FakeSlideForgeClient, SfUsersViewModel) Make()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_users"] = _ => new
        {
            total = 2,
            items = new[]
            {
                new { user_id = 7L, username = (string?)"u7", first_name = "A", lang = "fa", banned = 0, weekly_limit = (int?)null, jobs_7d = 3, used = 3, limit = 10 },
                new { user_id = 8L, username = (string?)null, first_name = "B", lang = "en", banned = 1, weekly_limit = (int?)25, jobs_7d = 0, used = 0, limit = 25 },
            },
        };
        fake.Handlers["set_user_quota"] = _ => new { ok = true };
        fake.Handlers["set_user_banned"] = _ => new { ok = true };
        return (fake, new SfUsersViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Load_and_quota_display()
    {
        var (fake, vm) = Make();
        vm.SearchText = "u";
        await vm.LoadAsync();
        Assert.Equal("u", (string)fake.LastPayload("list_users")!["query"]!);
        Assert.Equal(2, vm.Users.Count);
        Assert.Equal("u7", vm.Users[0].Label);
        Assert.Equal("B", vm.Users[1].Label);            // no username → first name
        Assert.Equal("3 / 10", vm.Users[0].QuotaText);
        Assert.True(vm.Users[1].Banned);
        Assert.True(vm.Users[1].HasOverride);
    }

    [Fact]
    public async Task Set_quota_override_and_reset_to_default()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.SetQuotaAsync(vm.Users[0], 20);
        var p = fake.LastPayload("set_user_quota")!;
        Assert.Equal(7, (long)p["user_id"]!);
        Assert.Equal(20, (int)p["weekly_limit"]!);
        await vm.SetQuotaAsync(vm.Users[1], null);
        var r = fake.LastPayload("set_user_quota")!.AsObject();
        Assert.True(r.ContainsKey("weekly_limit") && r["weekly_limit"] is null);
        Assert.True(fake.Count("list_users") >= 3);      // reloads after each change
    }

    [Fact]
    public async Task Negative_quota_is_rejected_locally()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.SetQuotaAsync(vm.Users[0], -1);
        Assert.Equal(0, fake.Count("set_user_quota"));
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Ban_toggle()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.SetBannedAsync(vm.Users[0], true);
        Assert.True((bool)fake.LastPayload("set_user_banned")!["banned"]!);
        Assert.Equal(7, (long)fake.LastPayload("set_user_banned")!["user_id"]!);
    }
}

public class SfGuidesViewModelTests
{
    private static (FakeSlideForgeClient, SfGuidesViewModel) Make()
    {
        var fake = new FakeSlideForgeClient();
        var files = new Dictionary<string, string> { ["base.md"] = "# Base\n", ["fragments/web.md"] = "Cite.\n" };
        fake.Handlers["list_guides"] = _ => new { items = files.Keys.Select(k => new { path = k, bytes = files[k].Length }).ToArray() };
        fake.Handlers["get_guide"] = p => new { path = (string)p!["path"]!, content = files[(string)p["path"]!] };
        fake.Handlers["put_guide"] = p => { files[(string)p!["path"]!] = (string)p["content"]!; return new { ok = true }; };
        var tips = new List<JsonObject> { new() { ["id"] = "rtl", ["text_en"] = "Mirror layouts.", ["when"] = new JsonObject { ["language"] = new JsonArray("fa") }, ["priority"] = 5 } };
        fake.Handlers["list_tips"] = _ => new { items = tips.Select(t => t.DeepClone()).ToArray() };
        fake.Handlers["upsert_tip"] = p => { var t = p!["tip"]!.AsObject(); tips.RemoveAll(x => (string)x["id"]! == (string)t["id"]!); tips.Add((JsonObject)t.DeepClone()); return new { ok = true }; };
        fake.Handlers["delete_tip"] = p => { tips.RemoveAll(x => (string)x["id"]! == (string)p!["id"]!); return new { ok = true }; };
        fake.Handlers["preview_prompt"] = p => new { prompt = "BRIEF for " + p!["spec"]!["title"] };
        return (fake, new SfGuidesViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Open_edit_save_cycle_tracks_dirty_state()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        Assert.Contains(vm.Files, f => f.Path == "fragments/web.md");
        await vm.OpenAsync("fragments/web.md");
        Assert.Equal("Cite.\n", vm.EditorText);
        Assert.False(vm.IsDirty);
        vm.EditorText = "Cite sources.\n";
        Assert.True(vm.IsDirty);
        await vm.SaveAsync();
        Assert.Equal("Cite sources.\n", (string)fake.LastPayload("put_guide")!["content"]!);
        Assert.Equal("fragments/web.md", (string)fake.LastPayload("put_guide")!["path"]!);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Save_error_keeps_edits_and_shows_code()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        await vm.OpenAsync("base.md");
        vm.EditorText = new string('x', 10);
        fake.Errors["put_guide"] = "too_large";
        await vm.SaveAsync();
        Assert.True(vm.IsDirty);
        Assert.Contains("too_large", vm.ErrorMessage);
        Assert.Equal(new string('x', 10), vm.EditorText);
    }

    [Fact]
    public async Task Tips_crud()
    {
        var (fake, vm) = Make();
        await vm.LoadTipsAsync();
        Assert.Equal("rtl", vm.Tips.Single().Id);
        Assert.Contains("language", vm.Tips.Single().WhenJson);
        await vm.UpsertTipAsync(new SfTip { Id = "short", TextEn = "Keep it short.", WhenJson = "{\"format\":[\"pptx\"]}", Priority = 3 });
        var sent = fake.LastPayload("upsert_tip")!["tip"]!;
        Assert.Equal("short", (string)sent["id"]!);
        Assert.Equal("pptx", (string)sent["when"]!["format"]![0]!);   // when is sent as an object, not a string
        Assert.Equal(3, (int)sent["priority"]!);
        Assert.Equal(2, vm.Tips.Count);
        await vm.DeleteTipAsync("rtl");
        Assert.Equal("short", vm.Tips.Single().Id);
    }

    [Fact]
    public async Task Invalid_when_json_is_rejected_locally()
    {
        var (fake, vm) = Make();
        await vm.LoadTipsAsync();
        await vm.UpsertTipAsync(new SfTip { Id = "x", TextEn = "t", WhenJson = "{not json", Priority = 1 });
        Assert.Equal(0, fake.Count("upsert_tip"));
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Preview_prompt_sends_spec_object()
    {
        var (fake, vm) = Make();
        vm.PreviewSpecJson = "{\"title\":\"Solar\",\"output_format\":\"pptx\"}";
        await vm.PreviewPromptAsync();
        Assert.Equal("BRIEF for Solar", vm.PromptPreview);
        Assert.Equal("Solar", (string)fake.LastPayload("preview_prompt")!["spec"]!["title"]!);
    }
}

public class SfOverviewViewModelTests
{
    [Fact]
    public async Task Overview_load()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["get_overview"] = _ => new
        {
            jobs_by_status = new Dictionary<string, int> { ["completed"] = 3, ["running"] = 1 },
            jobs_by_format_7d = new Dictionary<string, int> { ["pptx"] = 3, ["docx"] = 1 },
            jobs_7d = 4, failure_rate_7d = 0.25, avg_duration_s_7d = 200.0, active = 1, users_total = 2,
            top_users_7d = new[] { new { user_id = 7L, username = "u7", jobs = 3 } },
        };
        var vm = new SfOverviewViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadAsync();
        Assert.Equal(4, vm.Jobs7d);
        Assert.Equal(0.25, vm.FailureRate7d, 3);
        Assert.Equal(3, vm.JobsByStatus["completed"]);
        Assert.Equal(3, vm.JobsByFormat7d["pptx"]);
        Assert.Equal("u7", vm.TopUsers.Single().Label);
    }

    [Fact]
    public void Telemetry_updates_live_values_and_bounded_history()
    {
        var vm = new SfOverviewViewModel(new SfRpc(new FakeSlideForgeClient(), "tp2"));
        for (var i = 0; i < SfOverviewViewModel.HistoryCapacity + 50; i++)
        {
            var s = new SfTelemetrySample { TimestampUnixNano = 1_700_000_000_000_000_000L + i * 1_000_000_000L };
            s.Metrics["jobs_active"] = i % 3;
            s.Metrics["jobs_queued"] = 2;
            s.Metrics["jobs_waiting_input"] = 1;
            s.Metrics["bridge_ok"] = 1;
            s.Metrics["sandbox_ok"] = 0;
            s.Metrics["quota_used_7d_total"] = 17;
            vm.ApplyTelemetry(s);
        }
        Assert.Equal(2, vm.JobsQueued);
        Assert.Equal(1, vm.JobsWaitingInput);
        Assert.True(vm.BridgeOk);
        Assert.False(vm.SandboxOk);
        Assert.Equal(17, vm.QuotaUsed7dTotal);
        Assert.Equal(SfOverviewViewModel.HistoryCapacity, vm.ActiveJobsHistory.Count);
    }

    [Fact]
    public void Missing_metrics_do_not_reset_known_values()
    {
        var vm = new SfOverviewViewModel(new SfRpc(new FakeSlideForgeClient(), "tp2"));
        var a = new SfTelemetrySample();
        a.Metrics["jobs_queued"] = 5;
        vm.ApplyTelemetry(a);
        vm.ApplyTelemetry(new SfTelemetrySample());
        Assert.Equal(5, vm.JobsQueued);
    }
}
