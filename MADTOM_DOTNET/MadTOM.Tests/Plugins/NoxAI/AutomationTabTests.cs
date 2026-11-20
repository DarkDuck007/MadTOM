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
public class AutomationTabTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public int StatusCalls { get; private set; }
        public List<NoxListPreprocessorsRequest> PreprocessorLists { get; } = new();
        public List<NoxPreprocessorActionRequest> PpActions { get; } = new();
        public List<NoxCronActionRequest> CronActions { get; } = new();
        public List<NoxListPreprocessorLogsRequest> LogRequests { get; } = new();
        public List<NoxGetPreprocessorRequest> Gets { get; } = new();
        public string? StatusError { get; set; }
        public string? ActionError { get; set; }

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxAutomationStatusResponse> GetAutomationStatusAsync(NoxAutomationStatusRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            var res = new NoxAutomationStatusResponse { ConfigJson = "{\"preprocess\":{\"max_consecutive_says\":5}}" };
            if (StatusError != null) res.Error = StatusError;
            res.Chats.Add(new NoxAutomationChatStatus { ChatId = -100, ChatTitle = "Group", SayStreak = 4, Preprocessors = 2, EnabledPreprocessors = 1, Crons = 1, NextFireAt = "2026-11-20T07:00:00Z" });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxListPreprocessorsResponse> ListPreprocessorsAsync(NoxListPreprocessorsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            PreprocessorLists.Add(request);
            var res = new NoxListPreprocessorsResponse();
            var a = new NoxAutomationPreprocessor { Id = 1, ChatId = -100, Name = "greeter", Kind = "code", Enabled = true, Runs = 12, ErrorCount = 0 };
            a.Triggers.Add("message");
            var b = new NoxAutomationPreprocessor { Id = 2, ChatId = -100, Name = "cron:tea", Kind = "static", Enabled = false, ErrorCount = 5, LastError = "timeout" };
            b.Triggers.Add("cron");
            res.Preprocessors.Add(a);
            res.Preprocessors.Add(b);
            return Call(res);
        }

        public override AsyncUnaryCall<NoxGetPreprocessorResponse> GetPreprocessorAsync(NoxGetPreprocessorRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Gets.Add(request);
            return Call(new NoxGetPreprocessorResponse
            {
                Preprocessor = new NoxAutomationPreprocessor { Id = request.Id, Name = "greeter" },
                Code = "def handle(event, state):\n    return {\"actions\": []}  # <script>alert(1)</script>",
                StateJson = "{\"n\":3}",
                StaticActionsJson = "",
            });
        }

        public override AsyncUnaryCall<NoxAutomationActionResponse> PreprocessorActionAsync(NoxPreprocessorActionRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            PpActions.Add(request);
            return Call(ActionError == null ? new NoxAutomationActionResponse { Ok = true } : new NoxAutomationActionResponse { Ok = false, Error = ActionError });
        }

        public override AsyncUnaryCall<NoxListPreprocessorLogsResponse> ListPreprocessorLogsAsync(NoxListPreprocessorLogsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            LogRequests.Add(request);
            var res = new NoxListPreprocessorLogsResponse { Total = 120 };
            for (int i = 0; i < Math.Min(50, Math.Max(0, 120 - request.Offset)); i++)
                res.Entries.Add(new NoxAutomationLogEntry { Id = 500 - request.Offset - i, ChatId = -100, PreprocessorName = "greeter", Outcome = i % 2 == 0 ? "say" : "pass", EventKind = "message" });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxListCronsResponse> ListCronsAsync(NoxListCronsRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxListCronsResponse();
            res.Crons.Add(new NoxAutomationCron { Id = 3, ChatId = -100, Name = "tea", PreprocessorName = "cron:tea", SpecKind = "at", Spec = "2026-11-20T07:00:00Z", Enabled = true, Depth = 1 });
            return Call(res);
        }

        public override AsyncUnaryCall<NoxAutomationActionResponse> CronActionAsync(NoxCronActionRequest request,
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            CronActions.Add(request);
            return Call(new NoxAutomationActionResponse { Ok = true });
        }
    }

    private static readonly NoxNodeModel Node =
        new("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff);

    private static (AutomationTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new AutomationTabViewModel(manager) { ConfirmAction = _ => true };
        return (vm, client, manager);
    }

    private static async Task Attach(AutomationTabViewModel vm, Client client)
    {
        vm.SelectedNode = Node;
        for (int i = 0; i < 200 && client.StatusCalls == 0; i++) await Task.Delay(10);
        await vm.RefreshAsync();
    }

    [Fact]
    public async Task Refresh_LoadsStatusPreprocessorsAndCrons()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        var chat = Assert.Single(vm.Chats);
        Assert.Equal(4, chat.SayStreak);
        Assert.Contains("max_consecutive_says", vm.ConfigJson);
        Assert.Equal(2, vm.Preprocessors.Count);
        var greeter = vm.Preprocessors.Single(p => p.Name == "greeter");
        Assert.Equal("message", greeter.Triggers);
        Assert.True(greeter.Enabled);
        var tea = vm.Preprocessors.Single(p => p.Name == "cron:tea");
        Assert.Equal(5, tea.ErrorCount);
        var cron = Assert.Single(vm.Crons);
        Assert.Equal("tea", cron.Name);
        Assert.Equal(1, cron.Depth);
    }

    [Fact]
    public async Task ChatFilter_IsSentAndNullMeansAll()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        Assert.False(client.PreprocessorLists.Last().HasChatId);
        vm.ChatFilter = -100;
        await vm.RefreshAsync();
        Assert.True(client.PreprocessorLists.Last().HasChatId);
        Assert.Equal(-100, client.PreprocessorLists.Last().ChatId);
    }

    [Fact]
    public async Task LoadPreprocessor_ShowsCodeVerbatim()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        var row = vm.Preprocessors.Single(p => p.Name == "greeter");
        await vm.LoadPreprocessorAsync(row);
        Assert.Equal(1, client.Gets.Last().Id);
        Assert.Contains("<script>alert(1)</script>", vm.SelectedCode);
        Assert.Contains("\"n\"", vm.SelectedState);
    }

    [Fact]
    public async Task PreprocessorActions_SendIdAndAction()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.SelectedPreprocessor = vm.Preprocessors.Single(p => p.Name == "greeter");
        await vm.DisablePreprocessorAsync();
        await vm.EnablePreprocessorAsync();
        await vm.ResetErrorsAsync();
        await vm.ResetStateAsync();
        await vm.DeletePreprocessorAsync();
        Assert.Equal(new[] { "disable", "enable", "reset_errors", "reset_state", "delete" }, client.PpActions.Select(a => a.Action).ToArray());
        Assert.All(client.PpActions, a => Assert.Equal(1, a.Id));
    }

    [Fact]
    public async Task DestructiveActions_RespectDeclinedConfirmation()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.ConfirmAction = _ => false;
        vm.SelectedPreprocessor = vm.Preprocessors.Single(p => p.Name == "greeter");
        vm.SelectedCron = vm.Crons.Single();
        await vm.DeletePreprocessorAsync();
        await vm.ResetStateAsync();
        await vm.CancelCronAsync();
        Assert.Empty(client.PpActions);
        Assert.Empty(client.CronActions);
    }

    [Fact]
    public async Task NoSelection_DoesNothing()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.SelectedPreprocessor = null;
        vm.SelectedCron = null;
        await vm.DisablePreprocessorAsync();
        await vm.DisableCronAsync();
        Assert.Empty(client.PpActions);
        Assert.Empty(client.CronActions);
    }

    [Fact]
    public async Task CronActions_SendIdAndAction()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        vm.SelectedCron = vm.Crons.Single();
        await vm.DisableCronAsync();
        await vm.EnableCronAsync();
        await vm.CancelCronAsync();
        Assert.Equal(new[] { "disable", "enable", "cancel" }, client.CronActions.Select(a => a.Action).ToArray());
        Assert.All(client.CronActions, a => Assert.Equal(3, a.Id));
    }

    [Fact]
    public async Task Errors_AreSurfaced()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.StatusError = "automation disabled";
        await Attach(vm, client);
        Assert.Contains("automation disabled", vm.ErrorMessage);

        client.StatusError = null;
        client.ActionError = "unknown preprocessor";
        vm.SelectedPreprocessor = vm.Preprocessors.First();
        await vm.DisablePreprocessorAsync();
        Assert.Contains("unknown preprocessor", vm.ErrorMessage);
    }

    [Fact]
    public async Task Logs_PagingAndSelectedOnly()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await Attach(vm, client);
        await vm.RefreshLogsAsync();
        Assert.Equal(50, vm.Logs.Count);
        Assert.Equal(120, vm.LogsTotal);
        Assert.Equal(0, client.LogRequests.Last().Offset);
        await vm.NextLogsPageAsync();
        await vm.NextLogsPageAsync();
        Assert.Equal(100, vm.LogsOffset);
        await vm.NextLogsPageAsync();                    // beyond the total: stays
        Assert.Equal(100, vm.LogsOffset);
        await vm.PrevLogsPageAsync();
        await vm.PrevLogsPageAsync();
        await vm.PrevLogsPageAsync();                    // never below zero
        Assert.Equal(0, vm.LogsOffset);

        vm.SelectedPreprocessor = vm.Preprocessors.Single(p => p.Name == "greeter");
        vm.LogsForSelectedOnly = true;
        await vm.RefreshLogsAsync();
        Assert.True(client.LogRequests.Last().HasPreprocessorId);
        Assert.Equal(1, client.LogRequests.Last().PreprocessorId);
    }
}
