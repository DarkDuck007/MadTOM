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
public class UserQuotasTabTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public int ListCalls { get; private set; }
        public List<NoxGetUserQuotaRequest> GetRequests { get; } = new();
        public List<NoxSetUserQuotaRequest> SetRequests { get; } = new();
        public string? ListError { get; set; }
        public string? SetError { get; set; }

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxListUserQuotasResponse> ListUserQuotasAsync(NoxListUserQuotasRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            var res = new NoxListUserQuotasResponse
            {
                Enabled = true,
                Defaults = new NoxUserQuotaDefaults
                {
                    UserLimit5H = 20,
                    UserLimit24H = 0,
                    UserGlobalLimit5H = 50,
                    UserGlobalLimit24H = 150
                }
            };
            if (ListError != null) res.Error = ListError;
            res.Overrides.Add(new NoxUserQuotaOverride
            {
                UserId = 1001,
                UserName = "Alice",
                Scope = "all_chats",
                ChatId = 0,
                Quota5H = 5,
                Quota24H = 20,
                UpdatedAt = "2026-11-25T10:00:00Z",
                UpdatedBy = 7
            });
            res.Overrides.Add(new NoxUserQuotaOverride
            {
                UserId = 1002,
                UserName = "Bob",
                Scope = "chat",
                ChatId = -100999,
                ChatTitle = "Dev Room",
                Quota5H = 0,
                Quota24H = 0,
                UpdatedAt = ""
            });
            res.Overrides.Add(new NoxUserQuotaOverride
            {
                UserId = 1003,
                Scope = "global",
                Quota5H = 10,
                Quota24H = 40
            });
            res.TopUsers.Add(new NoxUserQuotaTopUser
            {
                UserId = 1001,
                UserName = "Alice",
                Calls5H = 12,
                Calls24H = 34,
                Chats = 3,
                Exempt = false
            });
            res.TopUsers.Add(new NoxUserQuotaTopUser
            {
                UserId = 7,
                UserName = "Admin",
                Calls5H = 50,
                Calls24H = 200,
                Chats = 5,
                Exempt = true
            });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxGetUserQuotaResponse> GetUserQuotaAsync(NoxGetUserQuotaRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            GetRequests.Add(request);
            var res = new NoxGetUserQuotaResponse
            {
                UserId = request.UserId,
                UserName = request.UserId == 1001 ? "Alice" : "Admin",
                ChatId = request.ChatId,
                Exempt = request.UserId == 7,
                User = new NoxUserQuotaTier
                {
                    Source = "default",
                    Count5H = 0,
                    Limit5H = 0
                },
                Global = new NoxUserQuotaTier
                {
                    Source = "override",
                    Count5H = 12,
                    Limit5H = 50,
                    Remaining5H = 38,
                    Pct5H = 24.0,
                    Reset5H = "2026-11-25T15:00:00Z",
                    Count24H = 34,
                    Limit24H = 150,
                    Remaining24H = 116,
                    Pct24H = 22.67,
                    Reset24H = "2026-11-26T00:00:00Z"
                }
            };
            res.Overrides.Add(new NoxUserQuotaOverride
            {
                UserId = request.UserId,
                Scope = "global",
                Quota5H = 50,
                Quota24H = 150
            });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSetUserQuotaResponse> SetUserQuotaAsync(NoxSetUserQuotaRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            SetRequests.Add(request);
            return Call(SetError == null
                ? new NoxSetUserQuotaResponse { Ok = true, Removed = request.Reset }
                : new NoxSetUserQuotaResponse { Ok = false, Error = SetError });
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static (UserQuotasTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new UserQuotasTabViewModel(manager) { ConfirmAction = _ => true };
        return (vm, client, manager);
    }

    private static async Task Attach(UserQuotasTabViewModel vm, Client client)
    {
        vm.SelectedNode = Node;
        for (int i = 0; i < 200 && client.ListCalls == 0; i++) await Task.Delay(10);
        await vm.RefreshAsync();
    }

    [Fact]
    public async Task Refresh_ParsesDefaultsOverridesAndTopUsers()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        Assert.True(vm.RateLimitingEnabled);
        Assert.Equal("ENABLED", vm.EnabledBadge);
        Assert.Equal("20", vm.DefaultUserLimit5hDisplay);
        Assert.Equal("Unlimited", vm.DefaultUserLimit24hDisplay);
        Assert.Equal("50", vm.DefaultGlobalLimit5hDisplay);
        Assert.Equal("150", vm.DefaultGlobalLimit24hDisplay);

        Assert.Equal(3, vm.Overrides.Count);
        Assert.Equal("Alice (1001)", vm.Overrides[0].UserDisplay);
        Assert.Equal("All chats", vm.Overrides[0].ScopeDisplay);
        Assert.Equal("5", vm.Overrides[0].Quota5hDisplay);
        Assert.Equal("20", vm.Overrides[0].Quota24hDisplay);

        Assert.Equal("This chat: Dev Room", vm.Overrides[1].ScopeDisplay);
        Assert.Equal("Unlimited", vm.Overrides[1].Quota5hDisplay);
        Assert.Equal("Unlimited", vm.Overrides[1].Quota24hDisplay);
        Assert.Equal("—", vm.Overrides[1].UpdatedAtDisplay);

        Assert.Equal("1003", vm.Overrides[2].UserDisplay);
        Assert.Equal("Global total", vm.Overrides[2].ScopeDisplay);

        Assert.Equal(2, vm.TopUsers.Count);
        Assert.Equal("EXEMPT", vm.TopUsers[1].ExemptBadge);
    }

    [Fact]
    public async Task SelectTopUser_LoadsGlobalDetailAndOverrides()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        var alice = vm.TopUsers.Single(u => u.UserId == 1001);
        await vm.LoadUserDetailAsync(alice);

        var req = Assert.Single(client.GetRequests);
        Assert.Equal(1001, req.UserId);
        Assert.Equal(0, req.ChatId);

        Assert.True(vm.HasUserDetail);
        Assert.Equal("Alice (1001)", vm.DetailUserHeader);
        Assert.Equal("override", vm.DetailGlobalSource);
        Assert.Contains("12 / 50", vm.DetailGlobal5hUsage);
        Assert.Contains("38 left", vm.DetailGlobal5hUsage);
        Assert.Equal("2026-11-25T15:00:00Z", vm.DetailGlobal5hReset);
        Assert.Contains("34 / 150", vm.DetailGlobal24hUsage);
        Assert.Single(vm.SelectedUserOverrides);
    }

    [Fact]
    public async Task ScopeSelection_EnablesChatIdOnlyForOneChat()
    {
        var (vm, _, manager) = Create();
        await using var _ = manager;

        vm.SelectedEditorScope = vm.ScopeOptions.Single(s => s.Value == "all_chats");
        Assert.False(vm.IsEditorChatIdEnabled);

        vm.SelectedEditorScope = vm.ScopeOptions.Single(s => s.Value == "chat");
        Assert.True(vm.IsEditorChatIdEnabled);

        vm.SelectedEditorScope = vm.ScopeOptions.Single(s => s.Value == "global");
        Assert.False(vm.IsEditorChatIdEnabled);
    }

    [Fact]
    public async Task EditorValidation_BlocksInvalidInputsWithoutCallingRpc()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        // Missing user id
        vm.EditorUserIdText = "";
        await vm.SaveEditorAsync();
        Assert.True(vm.HasEditorError);
        Assert.Empty(client.SetRequests);

        // Scope "chat" requires chat id
        vm.EditorUserIdText = "1001";
        vm.SelectedEditorScope = vm.ScopeOptions.Single(s => s.Value == "chat");
        vm.EditorChatIdText = "";
        vm.EditorQuota5hText = "10";
        vm.EditorQuota24hText = "20";
        await vm.SaveEditorAsync();
        Assert.Contains("Chat ID", vm.EditorErrorMessage);
        Assert.Empty(client.SetRequests);

        // 5h cannot exceed 24h when 24h > 0
        vm.EditorChatIdText = "-100123";
        vm.EditorQuota5hText = "30";
        vm.EditorQuota24hText = "20";
        await vm.SaveEditorAsync();
        Assert.Contains("5h quota cannot exceed 24h quota", vm.EditorErrorMessage);
        Assert.Empty(client.SetRequests);

        // Negative quota
        vm.EditorQuota5hText = "-1";
        vm.EditorQuota24hText = "0";
        await vm.SaveEditorAsync();
        Assert.True(vm.HasEditorError);
        Assert.Empty(client.SetRequests);
    }

    [Fact]
    public async Task SaveEditor_SendsSetUserQuotaAndSurfacesServerError()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        vm.EditOverride(vm.Overrides[1]); // Bob, chat -100999
        Assert.Equal("1002", vm.EditorUserIdText);
        Assert.True(vm.IsEditorChatIdEnabled);
        Assert.Equal("-100999", vm.EditorChatIdText);

        vm.EditorQuota5hText = "15";
        vm.EditorQuota24hText = "60";
        await vm.SaveEditorAsync();

        var sent = Assert.Single(client.SetRequests);
        Assert.Equal(1002, sent.UserId);
        Assert.Equal("chat", sent.Scope);
        Assert.Equal(-100999, sent.ChatId);
        Assert.Equal(15, sent.Quota5H);
        Assert.Equal(60, sent.Quota24H);
        Assert.False(sent.Reset);
        Assert.False(vm.HasEditorError);

        // Server-side error surfaced inline
        client.SetError = "5h quota exceeds 24h quota";
        await vm.SaveEditorAsync();
        Assert.Contains("5h quota exceeds 24h quota", vm.EditorErrorMessage);
    }

    [Fact]
    public async Task RemoveOverride_RespectsConfirmationAndSendsResetTrue()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);

        var target = vm.Overrides[1];
        vm.ConfirmAction = _ => false;
        await vm.RemoveOverrideAsync(target);
        Assert.Empty(client.SetRequests);

        vm.ConfirmAction = _ => true;
        await vm.RemoveOverrideAsync(target);
        var sent = Assert.Single(client.SetRequests);
        Assert.Equal(1002, sent.UserId);
        Assert.Equal("chat", sent.Scope);
        Assert.Equal(-100999, sent.ChatId);
        Assert.True(sent.Reset);
    }
}
