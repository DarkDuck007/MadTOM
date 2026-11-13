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
public class ChatImagesTabTests
{
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public List<NoxListChatImagesRequest> ListRequests { get; } = new();
        public List<NoxSweepChatImagesRequest> SweepRequests { get; } = new();
        public List<NoxChatImage> Images { get; } = new();

        private static AsyncUnaryCall<T> Call<T>(T value) => new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(NoxListChatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(new NoxListChatsResponse { Chats = { new NoxChatItem { ChatId = 10, Title = "Cats" } } });

        public override AsyncUnaryCall<NoxImageStorageStatsResponse> GetImageStorageStatsAsync(NoxImageStorageStatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => Call(new NoxImageStorageStatsResponse
            {
                TotalBytes = 3 * 1048576, ImageCount = 3, MaxTotalBytes = 12 * 1048576, MaxBytesPerChat = 4 * 1048576,
                Chats =
                {
                    new NoxChatImageUsage { ChatId = 10, ImageCount = 2, TotalBytes = 2 * 1048576 },
                    new NoxChatImageUsage { ChatId = 20, ImageCount = 1, TotalBytes = 1048576 }
                }
            });

        public override AsyncUnaryCall<NoxListChatImagesResponse> ListChatImagesAsync(NoxListChatImagesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListRequests.Add(request);
            var res = new NoxListChatImagesResponse();
            res.Images.AddRange(Images.Where(i => request.ChatId == 0 || i.ChatId == request.ChatId)
                .Where(i => request.Origin.Length == 0 || i.Origin == request.Origin));
            return Call(res);
        }

        public override AsyncUnaryCall<NoxSweepChatImagesResponse> SweepChatImagesAsync(NoxSweepChatImagesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            SweepRequests.Add(request);
            uint n = request.Uuids.Count > 0 ? (uint)request.Uuids.Count : 2;
            return Call(new NoxSweepChatImagesResponse { DeletedCount = n, FreedBytes = n * 1048576L });
        }
    }

    private static NoxChatImage Img(string uuid, long chat, string origin = "generated") => new()
    {
        Uuid = uuid, Handle = "img_" + uuid, ChatId = chat, Origin = origin, Width = 512, Height = 512,
        Bytes = 1048576, MimeType = "image/png", CreatedAt = "2026-11-01T10:00:00+00:00"
    };

    private static (ChatImagesTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        client.Images.AddRange(new[] { Img("a", 10), Img("b", 10, "telegram"), Img("c", 20) });
        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new ChatImagesTabViewModel(manager)
        {
            SelectedNode = new NoxNodeModel("node", "unused", "", "node", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff)
        };
        return (vm, client, manager);
    }

    [Fact]
    public async Task RefreshLoadsStatsChatBreakdownAndImages()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        Assert.Equal(3, vm.ChatUsages.Count);
        Assert.True(vm.ChatUsages[0].IsAllChats);
        Assert.Equal("Cats (#10)", vm.ChatUsages[1].Title);
        Assert.Equal(0.25, vm.StorageUsageFraction, 3);
        Assert.Equal(3, vm.Rows.Sum(r => r.Items.Count));
        Assert.Equal(0, client.ListRequests.Last().ChatId);

        vm.SelectedChatUsage = vm.ChatUsages.First(c => c.ChatId == 10);
        await Task.Yield();
        Assert.Equal(10, client.ListRequests.Last().ChatId);
        Assert.Equal(2, vm.Rows.Sum(r => r.Items.Count));
        Assert.True(vm.SweepChatOlderThanCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteSelectedSendsUuidsGroupedByChatAfterConfirmation()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();
        foreach (var item in vm.Rows.SelectMany(r => r.Items).Where(i => i.Uuid is "a" or "c")) item.IsSelected = true;
        Assert.Equal(2, vm.SelectedCount);

        vm.DeleteSelected();
        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(client.SweepRequests);

        await vm.ConfirmPending();
        Assert.Equal(2, client.SweepRequests.Count);
        Assert.Contains(client.SweepRequests, r => r.ChatId == 10 && r.Uuids.SequenceEqual(new[] { "a" }));
        Assert.Contains(client.SweepRequests, r => r.ChatId == 20 && r.Uuids.SequenceEqual(new[] { "c" }));
        Assert.Contains("freed 2 MB", vm.StatusText);
    }

    [Fact]
    public async Task SweepAllRunsDryRunBeforeConfirmedSweep()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();
        vm.SweepDays = 14;

        await vm.SweepAllOlderThan();
        var dry = Assert.Single(client.SweepRequests);
        Assert.True(dry.DryRun);
        Assert.Equal(0, dry.ChatId);
        Assert.Equal(14, dry.OlderThanDays);
        Assert.True(vm.IsConfirmOpen);

        vm.CancelConfirm();
        Assert.Single(client.SweepRequests);

        await vm.SweepAllOlderThan();
        await vm.ConfirmPending();
        var real = client.SweepRequests.Last();
        Assert.False(real.DryRun);
        Assert.Equal(14, real.OlderThanDays);
        Assert.Contains("deleted 2 images", vm.StatusText);
    }
}
