using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

[Collection("Sequential")]
public class ChatFilesTabTests
{
    static ChatFilesTabTests()
    {
        EnsureAvaloniaInitialized();
    }

    private static bool _initialized;

    private static string _diag = "";
    private static void EnsureAvaloniaInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            string[] nativeCandidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "../../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/runtimes/linux-x64/native/libSkiaSharp.so"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libSkiaSharp.so")
            };
            string nativePath = "";
            foreach (var np in nativeCandidates)
            {
                if (File.Exists(np))
                {
                    nativePath = Path.GetFullPath(np);
                    break;
                }
            }

            string[] candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "../../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "../../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "../../../../src/Host/MADTOM.Studio/bin/Debug/net10.0/Avalonia.Skia.dll"),
                Path.Combine(AppContext.BaseDirectory, "Avalonia.Skia.dll")
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    var asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(path));
                    var skiaSharpAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SkiaSharp")
                        ?? System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "SkiaSharp.dll"));

                    if (skiaSharpAsm != null && !string.IsNullOrEmpty(nativePath))
                    {
                        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(skiaSharpAsm, (name, a, p) =>
                        {
                            if (name.Contains("SkiaSharp"))
                            {
                                return System.Runtime.InteropServices.NativeLibrary.Load(nativePath);
                            }
                            return IntPtr.Zero;
                        });
                    }

                    var skiaPlatform = asm.GetType("Avalonia.Skia.SkiaPlatform");
                    skiaPlatform?.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _diag += "EX:" + ex.ToString() + "; ";
        }
    }
    private sealed class Client : NoxChatControlService.NoxChatControlServiceClient
    {
        public List<NoxListChatsRequest> ListChatsRequests { get; } = new();
        public List<NoxListChatFilesRequest> ListFilesRequests { get; } = new();
        public List<NoxGetChatFilePreviewRequest> PreviewRequests { get; } = new();
        public List<NoxGetChatFileBytesRequest> BytesRequests { get; } = new();
        public List<NoxSweepChatFilesRequest> SweepRequests { get; } = new();

        public List<NoxChatItem> AllChats { get; } = new();
        public List<NoxChatFile> Files { get; } = new();
        public bool EnableCursorPagination { get; set; } = false;

        private static AsyncUnaryCall<T> Call<T>(T value) =>
            new(Task.FromResult(value), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<NoxListChatsResponse> ListChatsAsync(
            NoxListChatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListChatsRequests.Add(request);
            if (!EnableCursorPagination)
            {
                var res = new NoxListChatsResponse { PaginationSupported = false, NextCursor = "" };
                res.Chats.AddRange(AllChats);
                return Call(res);
            }

            // Paginate in chunks of 2
            int cursorIndex = 0;
            if (!string.IsNullOrEmpty(request.Cursor) && int.TryParse(request.Cursor, out var parsed))
            {
                cursorIndex = parsed;
            }

            var chunk = AllChats.Skip(cursorIndex).Take(2).ToList();
            int nextIndex = cursorIndex + chunk.Count;
            string nextCursor = nextIndex < AllChats.Count ? nextIndex.ToString() : "";

            var pagedRes = new NoxListChatsResponse
            {
                PaginationSupported = true,
                NextCursor = nextCursor
            };
            pagedRes.Chats.AddRange(chunk);
            return Call(pagedRes);
        }

        public override AsyncUnaryCall<NoxWorkspaceStorageStatsResponse> GetWorkspaceStorageStatsAsync(
            NoxWorkspaceStorageStatsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var res = new NoxWorkspaceStorageStatsResponse
            {
                TotalBytes = 5 * 1048576,
                FileCount = 4,
                MaxTotalBytes = 20 * 1048576,
                MaxBytesPerChat = 5 * 1048576
            };

            // Chats with usage (Chat 10 and Chat 20 only)
            res.Chats.Add(new NoxChatFileUsage
            {
                ChatId = 10,
                FileCount = 3,
                TotalBytes = 3 * 1048576,
                ImageCount = 1,
                VideoCount = 1,
                DocumentCount = 1,
                Oldest = "2026-11-01T10:00:00Z",
                Newest = "2026-11-10T12:00:00Z"
            });
            res.Chats.Add(new NoxChatFileUsage
            {
                ChatId = 20,
                FileCount = 1,
                TotalBytes = 2 * 1048576,
                ImageCount = 0,
                SavedFileCount = 1,
                Oldest = "2026-11-05T08:00:00Z",
                Newest = "2026-11-05T08:00:00Z"
            });

            return Call(res);
        }

        public override AsyncUnaryCall<NoxListChatFilesResponse> ListChatFilesAsync(
            NoxListChatFilesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ListFilesRequests.Add(request);
            var res = new NoxListChatFilesResponse();
            var query = Files.AsEnumerable();

            if (request.ChatId != 0)
                query = query.Where(f => f.ChatId == request.ChatId);
            if (!string.IsNullOrEmpty(request.Origin))
                query = query.Where(f => f.Origin == request.Origin);
            if (!string.IsNullOrEmpty(request.KindFilter))
                query = query.Where(f => f.Kind == request.KindFilter);

            res.Files.AddRange(query);
            res.TotalCount = (uint)res.Files.Count;
            return Call(res);
        }

        public override AsyncUnaryCall<NoxGetChatFilePreviewResponse> GetChatFilePreviewAsync(
            NoxGetChatFilePreviewRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            PreviewRequests.Add(request);
            var file = Files.FirstOrDefault(f => f.Uuid == request.Uuid || f.Handle == request.Uuid);
            var res = new NoxGetChatFilePreviewResponse
            {
                File = file ?? new NoxChatFile(),
                MimeType = file?.MimeType ?? "application/octet-stream",
                PageCount = (uint)(file?.PageCount > 0 ? file.PageCount : 1),
                CurrentPage = request.Page > 0 ? request.Page : 1,
                FrameCount = 4
            };

            if (request.Mode == "page")
            {
                byte[] png = CreateTestPngBytes(64, 64);
                res.PreviewBytes = Google.Protobuf.ByteString.CopyFrom(png);
            }
            else if (request.Mode == "anim")
            {
                byte[] anim = CreateTestNoxAnimBytes(3, 120);
                res.PreviewBytes = Google.Protobuf.ByteString.CopyFrom(anim);
            }
            else if (request.Mode == "text")
            {
                res.TextContent = "Sample text content from file";
            }
            else
            {
                byte[] png = CreateTestPngBytes(32, 32);
                res.PreviewBytes = Google.Protobuf.ByteString.CopyFrom(png);
            }

            return Call(res);
        }

        public override AsyncUnaryCall<NoxGetChatFileBytesResponse> GetChatFileBytesAsync(
            NoxGetChatFileBytesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            BytesRequests.Add(request);
            var file = Files.FirstOrDefault(f => f.Uuid == request.Uuid);
            byte[] dummyData = Encoding.UTF8.GetBytes("raw bytes payload");
            return Call(new NoxGetChatFileBytesResponse
            {
                Filename = file?.Filename ?? "download.bin",
                MimeType = file?.MimeType ?? "application/octet-stream",
                FileBytes = Google.Protobuf.ByteString.CopyFrom(dummyData),
                TotalBytes = dummyData.Length
            });
        }

        public override AsyncUnaryCall<NoxSweepChatFilesResponse> SweepChatFilesAsync(
            NoxSweepChatFilesRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            SweepRequests.Add(request);
            uint count = request.Uuids.Count > 0 ? (uint)request.Uuids.Count : 2;
            long freed = count * 1048576L;
            return Call(new NoxSweepChatFilesResponse
            {
                DeletedCount = count,
                FreedBytes = freed
            });
        }
    }

    // 1x1 valid PNG byte array
    private static readonly byte[] Minimal1x1Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static byte[] CreateTestPngBytes(int width, int height)
    {
        return Minimal1x1Png;
    }

    private static byte[] CreateTestNoxAnimBytes(int frameCount, uint intervalMs)
    {
        byte[] framePng = Minimal1x1Png;
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("NOXANIM1"));
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)frameCount);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf, intervalMs);
        ms.Write(buf);
        for (int i = 0; i < frameCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)framePng.Length);
            ms.Write(buf);
            ms.Write(framePng);
        }
        return ms.ToArray();
    }

    private static NoxChatFile MakeFile(string uuid, long chatId, string kind, string store = "image", string origin = "generated", int pageCount = 0, double duration = 0) => new()
    {
        Uuid = uuid,
        Handle = $"{kind.ToUpperInvariant()}-{uuid}",
        ChatId = chatId,
        Kind = kind,
        Store = store,
        Origin = origin,
        Width = 512,
        Height = 512,
        Bytes = 1048576,
        MimeType = kind switch
        {
            "pdf" => "application/pdf",
            "video" => "video/mp4",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "image/png"
        },
        PageCount = pageCount,
        DurationS = duration,
        CreatedAt = "2026-11-01T10:00:00Z",
        LastUsedAt = "2026-11-02T10:00:00Z"
    };

    private static (ChatFilesTabViewModel vm, Client client, NoxNodeManager manager) Create()
    {
        var client = new Client();
        client.AllChats.AddRange(new[]
        {
            new NoxChatItem { ChatId = 10, Title = "General Chat", LastActiveUnixSec = 1700000000 },
            new NoxChatItem { ChatId = 20, Title = "Dev Discussion", LastActiveUnixSec = 1700000100 },
            new NoxChatItem { ChatId = 30, Title = "Empty Project", LastActiveUnixSec = 1700000200 }
        });

        client.Files.AddRange(new[]
        {
            MakeFile("f1", 10, "image", store: "image", origin: "generated"),
            MakeFile("f2", 10, "video", store: "image", origin: "telegram", duration: 12.5),
            MakeFile("f3", 10, "pdf", store: "document", origin: "studio", pageCount: 4),
            MakeFile("f4", 20, "document", store: "file", origin: "job_input")
        });

        var manager = new NoxNodeManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => client);
        var vm = new ChatFilesTabViewModel(manager)
        {
            SelectedNode = new NoxNodeModel("node-1", "test-node", "", "node-1", LogMode: MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff)
        };
        return (vm, client, manager);
    }

    [Fact]
    public async Task Refresh_LoadsAllChats_IncludingEmptyChatsWithZeroFiles()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        // Expect: All chats (index 0), Chat 10 (3 MB), Chat 20 (2 MB), Chat 30 (0 B empty chat)
        Assert.Equal(4, vm.ChatUsages.Count);

        // Index 0: Pinned All chats
        Assert.True(vm.ChatUsages[0].IsAllChats);
        Assert.Equal(0, vm.ChatUsages[0].ChatId);
        Assert.Equal("All chats", vm.ChatUsages[0].Title);
        Assert.Equal(4, vm.ChatUsages[0].FileCount);
        Assert.Equal(5 * 1048576, vm.ChatUsages[0].TotalBytes);

        // Index 1: Chat 10 (sorted by bytes desc: 3 MB)
        Assert.Equal(10, vm.ChatUsages[1].ChatId);
        Assert.Equal(3, vm.ChatUsages[1].FileCount);
        Assert.Equal(3 * 1048576, vm.ChatUsages[1].TotalBytes);

        // Index 2: Chat 20 (2 MB)
        Assert.Equal(20, vm.ChatUsages[2].ChatId);
        Assert.Equal(1, vm.ChatUsages[2].FileCount);

        // Index 3: Chat 30 is an empty chat with 0 files and 0 B
        Assert.Equal(30, vm.ChatUsages[3].ChatId);
        Assert.Equal(0, vm.ChatUsages[3].FileCount);
        Assert.Equal(0, vm.ChatUsages[3].TotalBytes);
        Assert.Equal("0 files · 0 B", vm.ChatUsages[3].Summary);

        // Storage header summary
        Assert.Equal(0.25, vm.StorageUsageFraction, 3);
        Assert.Contains("5 MB / 20 MB", vm.StorageSummary);
    }

    [Fact]
    public async Task CursorPagination_LoadsAllChatsAcrossPages()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        client.EnableCursorPagination = true;

        // Add more chats so that multiple cursor pages are traversed
        client.AllChats.Clear();
        for (int i = 1; i <= 6; i++)
        {
            client.AllChats.Add(new NoxChatItem { ChatId = i, Title = $"Chat #{i}", LastActiveUnixSec = 1700000000 + i });
        }

        await vm.Refresh();

        // 6 chats in chunks of 2 => at least 3 page requests
        Assert.True(client.ListChatsRequests.Count >= 3);
        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains(vm.ChatUsages, c => c.ChatId == i);
        }
    }

    [Fact]
    public async Task ChatSelection_FiltersListChatFiles()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        // Initially All chats is selected
        Assert.Equal(0, client.ListFilesRequests.Last().ChatId);
        Assert.Equal(4, vm.Rows.Sum(r => r.Items.Count));

        // Select Chat 10
        vm.SelectedChatUsage = vm.ChatUsages.First(c => c.ChatId == 10);
        await Task.Yield();

        Assert.Equal(10, client.ListFilesRequests.Last().ChatId);
        Assert.Equal(3, vm.Rows.Sum(r => r.Items.Count));
        Assert.True(vm.SweepChatOlderThanCommand.CanExecute(null));
    }

    [Fact]
    public async Task TypeFilters_And_OriginFilters_SendCorrectParameters()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        // Filter by Videos & GIFs
        vm.SelectedTypeFilter = "Videos & GIFs";
        await Task.Yield();
        Assert.Equal("video", client.ListFilesRequests.Last().KindFilter);

        // Filter by PDFs
        vm.SelectedTypeFilter = "PDFs";
        await Task.Yield();
        Assert.Equal("pdf", client.ListFilesRequests.Last().KindFilter);

        // Filter by Origin "telegram"
        vm.SelectedOriginFilter = "telegram";
        await Task.Yield();
        Assert.Equal("telegram", client.ListFilesRequests.Last().Origin);
    }

    [Fact]
    public async Task SweepButtons_ImmediatelyOpenModal_WithZeroRpcs()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;

        // Calling SweepAllOlderThan must open modal immediately with ZERO RPCs sent
        vm.SweepAllOlderThan();
        Assert.True(vm.IsConfirmOpen);
        Assert.True(vm.IsSweepConfirmation);
        Assert.Empty(client.SweepRequests); // CRITICAL INVARIANT: 0 RPCs

        // Cancelling closes modal with still ZERO RPCs
        vm.CancelConfirm();
        Assert.False(vm.IsConfirmOpen);
        Assert.Empty(client.SweepRequests);

        // Selecting a chat and sweeping also triggers zero RPCs
        vm.SelectedChatUsage = new ChatFileUsageModel { ChatId = 10, Title = "General" };
        vm.SweepChatOlderThan();
        Assert.True(vm.IsConfirmOpen);
        Assert.True(vm.IsSweepConfirmation);
        Assert.Empty(client.SweepRequests);
    }

    [Fact]
    public async Task CalculateButton_TriggersDryRunOnDemand()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        vm.SweepDays = 14;

        // Open sweep dialog (no RPCs)
        vm.SweepAllOlderThan();
        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(client.SweepRequests);

        // Click Calculate button
        await vm.CalculateSweep();

        // Exactly one dry run request was sent!
        var req = Assert.Single(client.SweepRequests);
        Assert.True(req.DryRun);
        Assert.Equal(14, req.OlderThanDays);
        Assert.Contains("Estimated: 2 files", vm.SweepCalculationSummary);
        Assert.True(vm.IsConfirmOpen);
    }

    [Fact]
    public async Task ConfirmSweep_ExecutesRealSweep()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        vm.SweepDays = 30;

        vm.SweepAllOlderThan();
        await vm.ConfirmPending();

        // Real sweep was sent with DryRun = false
        var req = Assert.Single(client.SweepRequests);
        Assert.False(req.DryRun);
        Assert.Equal(30, req.OlderThanDays);
        Assert.Contains("Sweep complete: deleted 2 files", vm.StatusText);
        Assert.False(vm.IsConfirmOpen);
    }

    [Fact]
    public async Task DeleteSelected_SendsUuidsGroupedByChatAfterConfirmation()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        // Select file f1 (Chat 10) and f4 (Chat 20)
        foreach (var item in vm.Rows.SelectMany(r => r.Items).Where(i => i.Uuid is "f1" or "f4"))
        {
            item.IsSelected = true;
        }
        Assert.Equal(2, vm.SelectedCount);

        vm.DeleteSelected();
        Assert.True(vm.IsConfirmOpen);
        Assert.False(vm.IsSweepConfirmation);
        Assert.Empty(client.SweepRequests);

        await vm.ConfirmPending();
        Assert.Equal(2, client.SweepRequests.Count);
        Assert.Contains(client.SweepRequests, r => r.ChatId == 10 && r.Uuids.Contains("f1"));
        Assert.Contains(client.SweepRequests, r => r.ChatId == 20 && r.Uuids.Contains("f4"));
        Assert.Contains("freed 2 MB", vm.StatusText);
    }

    [Fact]
    public async Task MultiPagePdf_Navigation_FetchesPages()
    {
        var (vm, client, manager) = Create();
        await using var _ = manager;
        await vm.Refresh();

        var pdfItem = vm.Rows.SelectMany(r => r.Items).First(i => i.Kind == "pdf");
        await vm.OpenPreview(pdfItem);

        Assert.True(vm.IsPreviewOpen);
        Assert.True(vm.IsMultiPage);
        Assert.Equal(1, vm.PreviewCurrentPage);
        Assert.Equal(4, vm.PreviewPageCount);
        Assert.True(vm.CanNextPage);
        Assert.False(vm.CanPrevPage);
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.False(vm.PrevPageCommand.CanExecute(null));

        // Next page via command
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.PreviewCurrentPage);
        Assert.True(vm.CanPrevPage);
        Assert.True(vm.CanNextPage);
        Assert.True(vm.PrevPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.Equal(2u, client.PreviewRequests.Last().Page);
        Assert.Equal("page", client.PreviewRequests.Last().Mode);

        // Prev page via command
        await vm.PrevPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.PreviewCurrentPage);
        Assert.False(vm.CanPrevPage);
        Assert.True(vm.CanNextPage);
        Assert.False(vm.PrevPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.Equal(1u, client.PreviewRequests.Last().Page);

        // Advance to last page (4)
        vm.PreviewCurrentPage = 4;
        Assert.False(vm.CanNextPage);
        Assert.True(vm.CanPrevPage);
        Assert.False(vm.NextPageCommand.CanExecute(null));
        Assert.True(vm.PrevPageCommand.CanExecute(null));
    }

    [Fact]
    public void Video_NoxAnimDecoding_ParsesFramesAndInterval()
    {
        byte[] validAnim = CreateTestNoxAnimBytes(frameCount: 3, intervalMs: 150);
        Assert.True(ChatFilesTabViewModel.IsNoxAnimBytes(validAnim));

        var asset = ChatFilesTabViewModel.TryDecodeNoxAnim(validAnim);
        Assert.NotNull(asset);
        Assert.Equal(3, asset!.Frames.Count);
        Assert.Equal(150, asset.IntervalMs);

        // Invalid bytes should safely return null
        byte[] invalidBytes = Encoding.UTF8.GetBytes("NOT_A_VALID_NOXANIM_STREAM");
        Assert.False(ChatFilesTabViewModel.IsNoxAnimBytes(invalidBytes));
        Assert.Null(ChatFilesTabViewModel.TryDecodeNoxAnim(invalidBytes));
    }
}
