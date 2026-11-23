using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Tests.Plugins.SlideForge;

public class SfCertificatePinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sf-pins-" + Guid.NewGuid().ToString("N"));
    private string PinFile => Path.Combine(_dir, "pins.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static X509Certificate2 SelfSigned(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    [Fact]
    public void Fingerprint_is_lowercase_hex_sha256_of_der_like_the_probe_logs()
    {
        using var cert = SelfSigned("tp2");
        var expected = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
        Assert.Equal(expected, SfCertificatePins.Fingerprint(cert));
        Assert.Equal(64, expected.Length);
    }

    [Fact]
    public void First_use_then_match_then_mismatch()
    {
        var pins = new SfCertificatePins(PinFile);
        using var a = SelfSigned("tp2");
        using var b = SelfSigned("tp2");
        Assert.Equal(SfPinResult.FirstUse, pins.Evaluate("tp2:50057", SfCertificatePins.Fingerprint(a)));
        pins.Pin("tp2:50057", SfCertificatePins.Fingerprint(a));
        Assert.Equal(SfPinResult.Match, pins.Evaluate("tp2:50057", SfCertificatePins.Fingerprint(a)));
        Assert.Equal(SfPinResult.Mismatch, pins.Evaluate("tp2:50057", SfCertificatePins.Fingerprint(b)));
        // other endpoints are independent
        Assert.Equal(SfPinResult.FirstUse, pins.Evaluate("other:50057", SfCertificatePins.Fingerprint(b)));
    }

    [Fact]
    public void Comparison_ignores_case_and_colons()
    {
        var pins = new SfCertificatePins(PinFile);
        var fp = new string('a', 64);
        pins.Pin("n", fp);
        var colon = string.Join(":", Enumerable.Range(0, 32).Select(_ => "AA"));
        Assert.Equal(SfPinResult.Match, pins.Evaluate("n", colon));
    }

    [Fact]
    public void Rejects_malformed_fingerprints()
    {
        var pins = new SfCertificatePins(PinFile);
        Assert.Throws<ArgumentException>(() => pins.Pin("n", "not-hex"));
        Assert.Throws<ArgumentException>(() => pins.Pin("n", new string('a', 63)));
        Assert.Equal(SfPinResult.Mismatch, pins.Evaluate("n", "zz"));
    }

    [Fact]
    public void Pins_persist_and_can_be_forgotten()
    {
        new SfCertificatePins(PinFile).Pin("tp2:50057", new string('b', 64));
        var reloaded = new SfCertificatePins(PinFile);
        Assert.Equal(SfPinResult.Match, reloaded.Evaluate("tp2:50057", new string('b', 64)));
        reloaded.Forget("tp2:50057");
        Assert.Equal(SfPinResult.FirstUse, new SfCertificatePins(PinFile).Evaluate("tp2:50057", new string('b', 64)));
    }

    [Fact]
    public void Corrupt_pin_file_fails_closed_not_open()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PinFile, "{not json");
        var pins = new SfCertificatePins(PinFile);
        // A corrupt store must not silently turn every node into "first use".
        Assert.True(pins.LoadError is not null);
        Assert.Equal(SfPinResult.Mismatch, pins.Evaluate("tp2:50057", new string('c', 64)));
    }

    [Fact]
    public void Validator_accepts_match_auto_pins_first_use_only_when_allowed_and_rejects_mismatch()
    {
        var pins = new SfCertificatePins(PinFile);
        using var a = SelfSigned("tp2");
        using var b = SelfSigned("tp2");
        Assert.False(pins.Validate("tp2:50057", a, trustOnFirstUse: false));
        Assert.Equal(SfPinResult.FirstUse, pins.Evaluate("tp2:50057", SfCertificatePins.Fingerprint(a)));
        Assert.True(pins.Validate("tp2:50057", a, trustOnFirstUse: true));
        Assert.True(pins.Validate("tp2:50057", a, trustOnFirstUse: false));
        Assert.False(pins.Validate("tp2:50057", b, trustOnFirstUse: true)); // TOFU never overrides a pin
        Assert.Equal(SfCertificatePins.Fingerprint(b), pins.LastMismatch?.Presented);
        Assert.Equal(SfCertificatePins.Fingerprint(a), pins.LastMismatch?.Pinned);
    }
}

public class SfRpcTests
{
    private static readonly string[] ProbeAllowlist =
    {
        "get_overview", "list_jobs", "get_job", "cancel_job", "extend_job", "get_job_file", "get_job_transcript",
        "list_users", "get_user", "set_user_quota", "set_user_banned",
        "list_guides", "get_guide", "put_guide", "list_tips", "upsert_tip", "delete_tip", "preview_prompt",
        "get_config",
        "sandbox_list_vms", "sandbox_vm_action", "sandbox_list_files", "sandbox_read_text", "sandbox_preview",
        "sandbox_backups", "sandbox_restore_backup", "sandbox_library_requests", "sandbox_decide_library",
        "sandbox_image_status", "sandbox_rebuild_image", "sandbox_runs",
    };

    [Fact]
    public void Client_method_list_matches_probe_allowlist()
    {
        Assert.Equal(ProbeAllowlist.OrderBy(x => x), SfRpc.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task Call_sends_snake_case_json_node_id_and_timeout()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["list_jobs"] = _ => new { total = 0, items = Array.Empty<object>(), limit = 50, offset = 0 };
        var rpc = new SfRpc(fake, "tp2");
        var res = await rpc.CallAsync("list_jobs", new { Limit = 50, UserId = (long?)7, Status = (string?)null });
        Assert.Equal(0, res.GetProperty("total").GetInt32());
        var call = fake.Calls.Single();
        Assert.Equal("tp2", call.NodeId);
        Assert.Equal(50, (int)call.Payload!["limit"]!);
        Assert.Equal(7, (long)call.Payload!["user_id"]!);
        Assert.True(call.TimeoutMs is > 0 and <= 120000);
    }

    [Fact]
    public async Task Explicit_nulls_are_sent_for_quota_reset()
    {
        var fake = new FakeSlideForgeClient();
        var rpc = new SfRpc(fake, "tp2");
        await rpc.CallAsync("set_user_quota", new Dictionary<string, object?> { ["user_id"] = 7L, ["weekly_limit"] = null });
        var p = fake.LastPayload("set_user_quota")!.AsObject();
        Assert.True(p.ContainsKey("weekly_limit"));
        Assert.Null(p["weekly_limit"]);
    }

    [Fact]
    public async Task Errors_surface_as_SfRpcException_with_code()
    {
        var fake = new FakeSlideForgeClient();
        fake.Errors["get_job"] = "not_found";
        var rpc = new SfRpc(fake, "tp2");
        var ex = await Assert.ThrowsAsync<SfRpcException>(() => rpc.CallAsync("get_job", new { job_id = "J-1" }));
        Assert.Equal("not_found", ex.Code);
        fake.Offline = true;
        var off = await Assert.ThrowsAsync<SfRpcException>(() => rpc.CallAsync("get_overview"));
        Assert.Equal("unavailable", off.Code);
    }

    [Fact]
    public async Task Unknown_methods_are_refused_locally()
    {
        var fake = new FakeSlideForgeClient();
        var rpc = new SfRpc(fake, "tp2");
        var ex = await Assert.ThrowsAsync<SfRpcException>(() => rpc.CallAsync("drop_tables"));
        Assert.Equal("method_not_allowed", ex.Code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Empty_payload_json_is_an_empty_object()
    {
        var fake = new FakeSlideForgeClient();
        fake.Handlers["get_overview"] = _ => new { };
        var rpc = new SfRpc(fake, "tp2");
        var res = await rpc.CallAsync("get_overview");
        Assert.Equal(JsonValueKind.Object, res.ValueKind);
        Assert.Equal("{}", fake.Calls.Single().Payload!.ToJsonString());
    }
}
