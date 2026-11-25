using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

public class SfTabRefreshTests
{
    private sealed class AsyncGateSlideForgeClient : SlideForgeControlService.SlideForgeControlServiceClient
    {
        private readonly TaskCompletionSource<SfCallResponse> _gate;
        public int CallCount { get; private set; }

        public AsyncGateSlideForgeClient(TaskCompletionSource<SfCallResponse> gate)
        {
            _gate = gate;
        }

        public override AsyncUnaryCall<SfCallResponse> CallAsync(
            SfCallRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new AsyncUnaryCall<SfCallResponse>(
                _gate.Task,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    [Fact]
    public async Task Users_RefreshSilent_keeps_selected_user_and_never_raises_Reset()
    {
        var fake = new FakeSlideForgeClient();
        int jobs7d = 3;
        bool includeThird = false;
        fake.Handlers["list_users"] = _ =>
        {
            var list = new List<object>
            {
                new { user_id = 7L, username = (string?)"u7", first_name = "A", lang = "fa", banned = 0, weekly_limit = (int?)null, jobs_7d = jobs7d, used = jobs7d, limit = 10 },
                new { user_id = 8L, username = (string?)"u8", first_name = "B", lang = "en", banned = 0, weekly_limit = (int?)25, jobs_7d = 1, used = 1, limit = 25 },
            };
            if (includeThird)
                list.Add(new { user_id = 9L, username = (string?)"u9", first_name = "C", lang = "en", banned = 0, weekly_limit = (int?)null, jobs_7d = 0, used = 0, limit = 10 });
            return new { total = list.Count, items = list };
        };

        var vm = new SfUsersViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadAsync();
        vm.SelectedUser = vm.Users[0];
        vm.QuotaLimitInput = "42"; // user edit buffer

        var actions = new List<NotifyCollectionChangedAction>();
        vm.Users.CollectionChanged += (_, e) => actions.Add(e.Action);

        jobs7d = 5;
        includeThird = true;
        await vm.RefreshSilentAsync();

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Contains(NotifyCollectionChangedAction.Replace, actions);
        Assert.Contains(NotifyCollectionChangedAction.Add, actions);
        Assert.Equal(3, vm.Users.Count);
        Assert.NotNull(vm.SelectedUser);
        Assert.Equal(7L, vm.SelectedUser!.UserId);
        Assert.Equal(5, vm.SelectedUser.Jobs7d);
        Assert.Equal("42", vm.QuotaLimitInput);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Users_RefreshSilent_does_not_set_IsBusy()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_users"] = _ => new
        {
            total = 1,
            items = new[]
            {
                new { user_id = 7L, username = "u7", first_name = "A", lang = "en", banned = 0, weekly_limit = (int?)null, jobs_7d = 1, used = 1, limit = 10 },
            },
        };

        var vm = new SfUsersViewModel(new SfRpc(fake, "node-a"));
        bool sawBusy = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SfUsersViewModel.IsBusy) && vm.IsBusy)
                sawBusy = true;
        };

        await vm.RefreshSilentAsync();

        Assert.False(sawBusy);
        Assert.False(vm.IsBusy);
        Assert.Single(vm.Users);
    }

    [Fact]
    public async Task RefreshSilent_skips_overlapping_calls()
    {
        var gate = new TaskCompletionSource<SfCallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AsyncGateSlideForgeClient(gate);
        var vm = new SfUsersViewModel(new SfRpc(client, "node-a"));

        var first = vm.RefreshSilentAsync();
        Assert.False(first.IsCompleted);
        Assert.Equal(1, client.CallCount);

        var second = vm.RefreshSilentAsync();
        Assert.True(second.IsCompleted);
        Assert.Equal(1, client.CallCount);

        gate.SetResult(new SfCallResponse { Ok = true, PayloadJson = "{\"total\":0,\"items\":[]}" });
        await first;
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task RefreshSilent_failure_keeps_existing_data_and_sets_ErrorMessage()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_users"] = _ => new
        {
            total = 1,
            items = new[]
            {
                new { user_id = 7L, username = "u7", first_name = "A", lang = "en", banned = 0, weekly_limit = (int?)null, jobs_7d = 2, used = 2, limit = 10 },
            },
        };

        var vm = new SfUsersViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadAsync();
        vm.SelectedUser = vm.Users[0];

        fake.Errors["list_users"] = "bot_unavailable";
        await vm.RefreshSilentAsync();

        Assert.Equal("bot_unavailable", vm.ErrorMessage);
        Assert.Single(vm.Users);
        Assert.Equal(7L, vm.SelectedUser?.UserId);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Config_RefreshSilent_updates_on_change_skips_if_unchanged_and_keeps_on_error()
    {
        var fake = new FakeSlideForgeClient();
        int maxJobs = 4;
        fake.Handlers["get_config"] = _ => new { max_jobs = maxJobs, lang = "en" };

        var vm = new SfConfigViewModel(new SfRpc(fake, "node-a"));
        bool sawBusy = false;
        int configChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SfConfigViewModel.IsBusy) && vm.IsBusy)
                sawBusy = true;
            if (e.PropertyName == nameof(SfConfigViewModel.ConfigJson))
                configChanges++;
        };

        await vm.RefreshSilentAsync();
        Assert.False(sawBusy);
        Assert.Equal(1, configChanges);
        Assert.Contains("\"max_jobs\": 4", vm.ConfigJson);

        // Unchanged response does not notify again
        await vm.RefreshSilentAsync();
        Assert.Equal(1, configChanges);

        // Changed response updates ConfigJson
        maxJobs = 8;
        await vm.RefreshSilentAsync();
        Assert.Equal(2, configChanges);
        Assert.Contains("\"max_jobs\": 8", vm.ConfigJson);

        // Failure leaves existing ConfigJson intact and sets ErrorMessage
        fake.Errors["get_config"] = "timeout";
        await vm.RefreshSilentAsync();
        Assert.Equal("timeout", vm.ErrorMessage);
        Assert.Contains("\"max_jobs\": 8", vm.ConfigJson);
    }

    [Fact]
    public async Task Guides_RefreshSilent_updates_lists_in_place_and_keeps_editor_and_tip_inputs()
    {
        var fake = new FakeSlideForgeClient();
        long baseBytes = 100;
        string tipText = "Mirror layouts.";
        fake.Handlers["list_guides"] = _ => new
        {
            items = new[]
            {
                new { path = "base.md", bytes = baseBytes },
                new { path = "fragments/web.md", bytes = 42L },
            },
        };
        fake.Handlers["get_guide"] = _ => new { path = "base.md", content = "# Original\n" };
        fake.Handlers["list_tips"] = _ => new
        {
            items = new[]
            {
                new { id = "rtl", text_en = tipText, when = new { language = new[] { "fa" } }, priority = 5 },
            },
        };

        var vm = new SfGuidesViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadAsync();
        await vm.LoadTipsAsync();
        vm.SelectedFile = vm.Files[0];
        await vm.OpenAsync("base.md");
        vm.EditorText = "# User unsaved edits\n";
        vm.SelectedTip = vm.Tips[0];
        vm.TipTextEnInput = "Draft tip edit";

        var fileActions = new List<NotifyCollectionChangedAction>();
        var tipActions = new List<NotifyCollectionChangedAction>();
        vm.Files.CollectionChanged += (_, e) => fileActions.Add(e.Action);
        vm.Tips.CollectionChanged += (_, e) => tipActions.Add(e.Action);

        bool sawBusy = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SfGuidesViewModel.IsBusy) && vm.IsBusy)
                sawBusy = true;
        };

        baseBytes = 250;
        tipText = "Mirror RTL layouts.";
        await vm.RefreshSilentAsync();

        Assert.False(sawBusy);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, fileActions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, tipActions);
        Assert.Equal("# User unsaved edits\n", vm.EditorText);
        Assert.True(vm.IsDirty);
        Assert.Equal("Draft tip edit", vm.TipTextEnInput);
        Assert.Equal(250L, vm.Files[0].Bytes);
        Assert.Equal("base.md", vm.SelectedFile?.Path);
        Assert.Equal("Mirror RTL layouts.", vm.Tips[0].TextEn);
        Assert.Equal("rtl", vm.SelectedTip?.Id);
    }

    [Fact]
    public void Default_preview_spec_is_valid_json_with_four_required_keys()
    {
        var vm = new SfGuidesViewModel(null);
        var node = JsonNode.Parse(vm.PreviewSpecJson) as JsonObject;

        Assert.NotNull(node);
        Assert.Equal("freestyle", (string?)node!["origin"]);
        Assert.Equal("generate", (string?)node["mode"]);
        Assert.Equal("en", (string?)node["language"]);
        Assert.Equal("pptx", (string?)node["output_format"]);
        Assert.Equal("Example deck", (string?)node["title"]);
        Assert.Equal("A short deck about solar energy", (string?)node["prompt"]);
        Assert.False((bool)node["ai_images"]!);
        Assert.True((bool)node["web"]!);
        Assert.IsType<JsonArray>(node["inputs"]);
    }

    [Fact]
    public async Task LoadSpecFromJob_copies_spec_with_unescaped_persian_text()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["get_job"] = _ => new
        {
            job = new { job_id = "1a2b3c4d" },
            spec = new
            {
                origin = "guided",
                mode = "generate",
                language = "fa",
                output_format = "pptx",
                title = "طرح",
            },
        };

        var vm = new SfGuidesViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadSpecFromJobAsync("1a2b3c4d");

        Assert.Equal("1a2b3c4d", (string?)fake.LastPayload("get_job")!["job_id"]);
        Assert.Contains("طرح", vm.PreviewSpecJson);
        Assert.DoesNotContain("\\u0637", vm.PreviewSpecJson, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.ErrorMessage);

        // Reset restores the default template
        vm.ResetPreviewSpec();
        Assert.Equal(SfGuidesViewModel.DefaultPreviewSpecJson, vm.PreviewSpecJson);
    }

    [Fact]
    public async Task PreviewPrompt_maps_bad_request_to_helpful_message_and_keeps_other_errors_raw()
    {
        var fake = new FakeSlideForgeClient();
        fake.Errors["preview_prompt"] = "bad_request";

        var vm = new SfGuidesViewModel(new SfRpc(fake, "node-a"));
        vm.PreviewSpecJson = "{}";
        await vm.PreviewPromptAsync();

        Assert.Equal(
            "The spec is not a valid job spec (needs origin, mode, language, output_format; see the template). Use Reset or Load from job.",
            vm.ErrorMessage);

        fake.Errors["preview_prompt"] = "bot_unavailable";
        await vm.PreviewPromptAsync();

        Assert.Equal("bot_unavailable", vm.ErrorMessage);
    }
}
