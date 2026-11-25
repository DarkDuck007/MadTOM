using System.Text.Json.Nodes;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

public class SfBroadcastsViewModelTests
{
    private static object BroadcastJson(
        string id,
        string status = "completed",
        string text = "Hello everyone\nSecond line",
        int total = 100,
        int sent = 80,
        int failed = 2,
        int blocked = 3,
        string createdBy = "madtom",
        string parseMode = "html",
        string? error = null,
        object? texts = null,
        object? audience = null) => new
    {
        broadcast_id = id,
        text,
        texts,
        parse_mode = parseMode,
        audience = audience ?? new { kind = "all" },
        status,
        error,
        created_by = createdBy,
        admin_chat_id = (long?)null,
        admin_message_id = (long?)null,
        total,
        sent,
        failed,
        blocked,
        created_at = "2026-11-25T10:00:00+00:00",
        started_at = "2026-11-25T10:00:01+00:00",
        finished_at = status == "completed" ? "2026-11-25T10:05:00+00:00" : null,
    };

    private static (FakeSlideForgeClient fake, SfBroadcastsViewModel vm) Make()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_broadcasts"] = _ => new
        {
            enabled = true,
            broadcasts = new[]
            {
                BroadcastJson("aa11bb22", status: "sending", total: 100, sent: 40, failed: 1, blocked: 2),
                BroadcastJson("cc33dd44", status: "completed", total: 50, sent: 50, failed: 0, blocked: 0),
            },
        };
        fake.Handlers["get_broadcast"] = p => new
        {
            broadcast_id = (string)p!["broadcast_id"]!,
            text = "<b>Update</b>",
            texts = new { en = "English body", fa = "Farsi body" },
            parse_mode = "html",
            audience = new { kind = "all", active_days = 14, langs = new[] { "en", "fa" }, include_unreachable = true },
            status = "sending",
            error = (string?)null,
            created_by = "madtom",
            total = 100,
            sent = 40,
            failed = 1,
            blocked = 2,
            created_at = "2026-11-25T10:00:00+00:00",
            started_at = "2026-11-25T10:00:01+00:00",
            finished_at = (string?)null,
            deliveries = new { pending = 57, sent = 40, failed = 1, blocked = 2 },
            recent_errors = new[]
            {
                new { user_id = 9001L, status = "failed", error = "Forbidden: bot was blocked", at = "2026-11-25T10:01:00+00:00" },
            },
        };
        fake.Handlers["broadcast_audience"] = _ => new
        {
            count = 1234,
            by_lang = new { en = 900, fa = 334 },
        };
        fake.Handlers["create_broadcast"] = _ => BroadcastJson("ee55ff66", status: "queued", total: 10, sent: 0, failed: 0, blocked: 0);
        fake.Handlers["cancel_broadcast"] = _ => new { ok = true };

        return (fake, new SfBroadcastsViewModel(new SfRpc(fake, "node-a")));
    }

    [Fact]
    public async Task Load_parses_rows_and_inplace_diff_keeps_selection_and_object_identity()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();

        Assert.True(vm.Enabled);
        Assert.Equal(2, vm.Broadcasts.Count);
        var firstRow = vm.Broadcasts[0];
        Assert.Equal("aa11bb22", firstRow.BroadcastId);
        Assert.Equal("SENDING", firstRow.StatusText);
        Assert.Equal("40/100", firstRow.Progress);
        Assert.Equal(0.4, firstRow.ProgressFraction, 3);
        Assert.Equal("Hello everyone Second line", firstRow.Preview);
        Assert.True(firstRow.IsActive);

        vm.SelectedBroadcast = firstRow;

        // Second load updates progress in place and reorders without clearing
        fake.Handlers["list_broadcasts"] = _ => new
        {
            enabled = true,
            broadcasts = new[]
            {
                BroadcastJson("cc33dd44", status: "completed", total: 50, sent: 50, failed: 0, blocked: 0),
                BroadcastJson("aa11bb22", status: "completed", total: 100, sent: 95, failed: 2, blocked: 3),
            },
        };

        await vm.LoadAsync();

        Assert.Equal(2, vm.Broadcasts.Count);
        Assert.Same(firstRow, vm.Broadcasts[1]);
        Assert.Same(firstRow, vm.SelectedBroadcast);
        Assert.Equal("95/100", firstRow.Progress);
        Assert.Equal("COMPLETED", firstRow.StatusText);
        Assert.False(firstRow.IsActive);
    }

    [Fact]
    public async Task Active_detection_tracks_queued_and_sending_broadcasts()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        Assert.True(vm.HasActiveBroadcasts);

        fake.Handlers["list_broadcasts"] = _ => new
        {
            enabled = true,
            broadcasts = new[]
            {
                BroadcastJson("cc33dd44", status: "completed"),
                BroadcastJson("dd55ee66", status: "failed"),
            },
        };
        await vm.LoadAsync();
        Assert.False(vm.HasActiveBroadcasts);

        fake.Handlers["list_broadcasts"] = _ => new
        {
            enabled = true,
            broadcasts = new[]
            {
                BroadcastJson("ff77aa88", status: "queued"),
            },
        };
        await vm.LoadAsync();
        Assert.True(vm.HasActiveBroadcasts);
    }

    [Fact]
    public async Task Preview_truncates_to_80_chars_on_one_line()
    {
        var fake = new FakeSlideForgeClient();
        string longText = "Line1\r\n" + new string('x', 120);
        fake.Handlers["list_broadcasts"] = _ => new
        {
            enabled = true,
            broadcasts = new[] { BroadcastJson("11223344", text: longText) },
        };
        var vm = new SfBroadcastsViewModel(new SfRpc(fake, "node-a"));
        await vm.LoadAsync();

        Assert.Equal(80, vm.Broadcasts[0].Preview.Length);
        Assert.DoesNotContain('\n', vm.Broadcasts[0].Preview);
        Assert.DoesNotContain('\r', vm.Broadcasts[0].Preview);
    }

    [Fact]
    public void BuildAudiencePayload_all_kind_omits_unset_keys()
    {
        var (_, vm) = Make();
        vm.AudienceKindIndex = 0;
        vm.ActiveDaysText = "";
        vm.LangEn = false;
        vm.LangFa = false;
        vm.IncludeUnreachable = false;

        var aud = vm.BuildAudiencePayload();
        Assert.Equal("all", aud["kind"]);
        Assert.False(aud.ContainsKey("active_days"));
        Assert.False(aud.ContainsKey("langs"));
        Assert.False(aud.ContainsKey("user_ids"));
        Assert.False(aud.ContainsKey("include_unreachable"));

        // Both languages checked also means no language filter
        vm.LangEn = true;
        vm.LangFa = true;
        var audBoth = vm.BuildAudiencePayload();
        Assert.False(audBoth.ContainsKey("langs"));
    }

    [Fact]
    public void BuildAudiencePayload_admins_and_specific_users_with_all_filters()
    {
        var (_, vm) = Make();
        vm.AudienceKindIndex = 1;
        var admins = vm.BuildAudiencePayload();
        Assert.Equal("admins", admins["kind"]);

        vm.AudienceKindIndex = 2;
        vm.UserIdsText = "101, 202 303";
        vm.ActiveDaysText = "30";
        vm.LangEn = false;
        vm.LangFa = true;
        vm.IncludeUnreachable = true;

        var users = vm.BuildAudiencePayload();
        Assert.Equal("users", users["kind"]);
        Assert.Equal(new List<long> { 101, 202, 303 }, users["user_ids"]);
        Assert.Equal(30, users["active_days"]);
        Assert.Equal(new List<string> { "fa" }, users["langs"]);
        Assert.Equal(true, users["include_unreachable"]);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void BuildAudiencePayload_invalid_user_ids_sets_error_message()
    {
        var (_, vm) = Make();
        vm.AudienceKindIndex = 2;
        vm.UserIdsText = "101, notanumber";

        _ = vm.BuildAudiencePayload();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("notanumber", vm.ErrorMessage);
    }

    [Fact]
    public void BuildCreatePayload_includes_texts_only_when_overrides_non_empty_and_sets_parse_mode()
    {
        var (_, vm) = Make();
        vm.ComposeText = "Default message";
        vm.ParseModeIndex = 0;

        var p1 = vm.BuildCreatePayload();
        Assert.Equal("Default message", p1["text"]);
        Assert.Equal("html", p1["parse_mode"]);
        Assert.False(p1.ContainsKey("texts"));

        vm.ParseModeIndex = 1;
        vm.ComposeTextEn = "English only";
        vm.ComposeTextFa = "   ";

        var p2 = vm.BuildCreatePayload();
        Assert.Equal("plain", p2["parse_mode"]);
        Assert.True(p2.ContainsKey("texts"));
        var texts = Assert.IsType<Dictionary<string, string>>(p2["texts"]);
        Assert.Equal("English only", texts["en"]);
        Assert.False(texts.ContainsKey("fa"));
    }

    [Fact]
    public async Task SendTest_sends_admins_audience_without_confirmation_and_keeps_compose()
    {
        var (fake, vm) = Make();
        vm.ComposeText = "<b>Preview</b>";
        vm.AudienceKindIndex = 0;

        await vm.SendTestAsync();

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal(1, fake.Count("create_broadcast"));
        var sent = fake.LastPayload("create_broadcast")!;
        Assert.Equal("admins", (string)sent["audience"]!["kind"]!);
        Assert.Equal("<b>Preview</b>", (string)sent["text"]!);
        Assert.Equal("<b>Preview</b>", vm.ComposeText);
    }

    [Fact]
    public async Task Confirm_flow_opens_dialog_then_sends_once_clears_compose_and_reloads()
    {
        var (fake, vm) = Make();
        await vm.RefreshAudienceAsync();
        vm.ComposeText = "Big release";
        vm.ComposeTextEn = "Big release EN";
        vm.AudienceKindIndex = 0;

        await vm.RequestSendAsync();

        Assert.True(vm.IsConfirmOpen);
        Assert.Equal("Send to 1,234 users?", vm.ConfirmText);
        Assert.Equal(0, fake.Count("create_broadcast"));

        await vm.ConfirmSendAsync();

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal(1, fake.Count("create_broadcast"));
        Assert.Equal(string.Empty, vm.ComposeText);
        Assert.Equal(string.Empty, vm.ComposeTextEn);
        Assert.True(fake.Count("list_broadcasts") >= 1);
    }

    [Fact]
    public async Task RequestSend_for_admins_sends_directly_without_dialog()
    {
        var (fake, vm) = Make();
        vm.ComposeText = "Admin notice";
        vm.AudienceKindIndex = 1;

        await vm.RequestSendAsync();

        Assert.False(vm.IsConfirmOpen);
        Assert.Equal(1, fake.Count("create_broadcast"));
        Assert.Equal("admins", (string)fake.LastPayload("create_broadcast")!["audience"]!["kind"]!);
    }

    [Fact]
    public async Task Error_mapping_covers_known_codes_and_rpc_failures()
    {
        Assert.Equal(
            "Invalid HTML: Telegram supports only b, i, u, s, a, code, pre, blockquote, tg-spoiler; escape < > & as text",
            SfBroadcastsViewModel.FriendlyError("bad_html"));
        Assert.Equal(
            "Broadcasts are disabled in SlideForge config",
            SfBroadcastsViewModel.FriendlyError("disabled"));
        Assert.Equal("custom_unknown_code", SfBroadcastsViewModel.FriendlyError("custom_unknown_code"));

        var (fake, vm) = Make();
        fake.Errors["create_broadcast"] = "bad_html";
        vm.ComposeText = "<script>bad</script>";
        await vm.SendTestAsync();

        Assert.Equal(SfBroadcastsViewModel.FriendlyError("bad_html"), vm.ErrorMessage);
    }

    [Fact]
    public async Task CancelBroadcast_calls_cancel_rpc_and_reloads()
    {
        var (fake, vm) = Make();
        await vm.LoadAsync();
        vm.SelectedBroadcast = vm.Broadcasts[0]; // status: sending
        Assert.True(vm.CanCancelSelected);

        int loadsBefore = fake.Count("list_broadcasts");
        await vm.CancelBroadcastAsync();

        Assert.Equal(1, fake.Count("cancel_broadcast"));
        Assert.Equal("aa11bb22", (string)fake.LastPayload("cancel_broadcast")!["broadcast_id"]!);
        Assert.True(fake.Count("list_broadcasts") > loadsBefore);
    }

    [Fact]
    public async Task LoadDetails_populates_deliveries_recent_errors_and_metadata()
    {
        var (fake, vm) = Make();
        await vm.LoadDetailsAsync("aa11bb22");

        Assert.True(vm.HasDetails);
        var d = vm.Details!;
        Assert.Equal("aa11bb22", d.BroadcastId);
        Assert.Equal("<b>Update</b>", d.Text);
        Assert.Equal("English body", d.TextsEn);
        Assert.Equal("Farsi body", d.TextsFa);
        Assert.Equal("html", d.ParseMode);
        Assert.Contains("all", d.AudienceSummary);
        Assert.Contains("active 14d", d.AudienceSummary);
        Assert.Equal(57, d.Pending);
        Assert.Equal(40, d.Sent);
        Assert.Equal(1, d.Failed);
        Assert.Equal(2, d.Blocked);
        Assert.Single(d.RecentErrors);
        Assert.Equal(9001L, d.RecentErrors[0].UserId);
        Assert.Equal("Forbidden: bot was blocked", d.RecentErrors[0].Error);
    }

    [Fact]
    public async Task RefreshAudience_formats_count_and_by_lang_text()
    {
        var (fake, vm) = Make();
        await vm.RefreshAudienceAsync();

        Assert.Equal(1234, vm.AudienceCount);
        Assert.Equal("1,234 recipients (en 900, fa 334)", vm.AudienceCountText);
        Assert.Equal("Send to 1,234 users", vm.SendButtonText);
    }
}
