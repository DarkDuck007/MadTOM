using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Grpc.Net.Client;
using MADTOM.Feed.Engine;
using MADTOM.Identity;
using MADTOM.Ipc;
using MADTOM.PluginContracts;
using MADTOM.Protocol.Video.V1;
using QRCoder;

namespace MADTOM.Plugins.VideoFeedCenter.ViewModels;

public sealed partial class VideoFeedCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPluginHostContext _hostContext;
    private GrpcChannel? _channel;
    private VideoFeedService.VideoFeedServiceClient? _client;
    private FeedEngineHost? _embeddedEngine;

    private CancellationTokenSource _cts = new();
    private Task? _feedWatchTask;
    private Task? _previewWatchTask;
    private CancellationTokenSource? _previewCts;
    private Task? _initialization;
    private bool _disposed;

    [ObservableProperty]
    private string _serverStatus = "Starting video server…";

    public ObservableCollection<FeedItemViewModel> Feeds { get; } = new();

    [ObservableProperty]
    private FeedItemViewModel? _selectedFeed;

    [ObservableProperty]
    private bool _isPairingModalOpen;

    [ObservableProperty]
    private Bitmap? _pairingQrBitmap;

    [ObservableProperty]
    private string _pairingHostText = string.Empty;

    [ObservableProperty]
    private string _pairingQrUri = string.Empty;

    [ObservableProperty]
    private string _pairingPinCode = "----";

    [ObservableProperty]
    private string _pairingExpiresText = string.Empty;

    [ObservableProperty]
    private bool _isAddObsModalOpen;

    [ObservableProperty]
    private string _obsDisplayName = "OBS Stream";

    [ObservableProperty]
    private uint _obsPort = 8554;

    [ObservableProperty]
    private string _obsPassphrase = string.Empty;

    public Action<uint, uint, byte[]>? OnFrameReceived { get; set; }

    public VideoFeedCenterViewModel(IPluginHostContext hostContext)
    {
        _hostContext = hostContext;

    }

    public Task InitializeAsync(CancellationToken ct = default) => _initialization ??= InitializeCoreAsync(ct);

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        try
        {
            _embeddedEngine = new FeedEngineHost();
            await _embeddedEngine.StartAsync(7443, ct);
            _channel = GrpcChannel.ForAddress(_embeddedEngine.LocalAddress);
            _client = new VideoFeedService.VideoFeedServiceClient(_channel);
            await LoadFeedsAsync(ct);
            StartFeedWatcher();
            ServerStatus = "Video server ready • TCP 7443 • Pair an IPCam source to begin";
        }
        catch (Exception ex)
        {
            ServerStatus = $"Video server failed: {ex.Message}";
        }
    }

    private async Task LoadFeedsAsync(CancellationToken ct)
    {
        if (_client == null) return;
        try
        {
            var res = await _client.ListFeedsAsync(new ListFeedsRequest(), cancellationToken: ct).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                Feeds.Clear();
                foreach (var f in res.Feeds)
                {
                    var vm = new FeedItemViewModel(f);
                    Feeds.Add(vm);
                }

                if (SelectedFeed == null && Feeds.Count > 0)
                {
                    SelectFeed(Feeds[0]);
                }
            });
        }
        catch { }
    }

    private void StartFeedWatcher()
    {
        if (_client == null) return;

        _feedWatchTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var call = _client.WatchFeeds(new WatchFeedsRequest(), cancellationToken: _cts.Token);
                    while (await call.ResponseStream.MoveNext(_cts.Token))
                    {
                        var ev = call.ResponseStream.Current;
                        Dispatcher.UIThread.Post(() => HandleFeedEvent(ev));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    await Task.Delay(2000, _cts.Token);
                }
            }
        });
    }

    private void HandleFeedEvent(FeedEvent ev)
    {
        var existing = Feeds.FirstOrDefault(f => string.Equals(f.FeedId, ev.Feed.FeedId, StringComparison.OrdinalIgnoreCase));

        switch (ev.Type)
        {
            case FeedEvent.Types.EventType.Added:
                if (existing == null)
                {
                    var newVm = new FeedItemViewModel(ev.Feed);
                    Feeds.Add(newVm);
                    if (SelectedFeed == null) SelectFeed(newVm);
                }
                else
                {
                    existing.UpdateFromInfo(ev.Feed);
                }
                break;

            case FeedEvent.Types.EventType.Updated:
                existing?.UpdateFromInfo(ev.Feed);
                break;

            case FeedEvent.Types.EventType.Removed:
                if (existing != null)
                {
                    Feeds.Remove(existing);
                    if (SelectedFeed == existing)
                    {
                        SelectFeed(Feeds.FirstOrDefault());
                    }
                }
                break;
        }
    }

    [RelayCommand]
    public void SelectFeed(FeedItemViewModel? feed)
    {
        SelectedFeed = feed;
        RestartPreviewStream();
    }

    private void RestartPreviewStream()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (SelectedFeed == null || _client == null) return;

        _previewCts = new CancellationTokenSource();
        var feedId = SelectedFeed.FeedId;
        var token = _previewCts.Token;

        _previewWatchTask = Task.Run(async () =>
        {
            try
            {
                using var call = _client.WatchPreview(new WatchPreviewRequest
                {
                    FeedId = feedId,
                    MaxWidth = 640,
                    MaxHeight = 360,
                    MaxFps = 30
                }, cancellationToken: token);

                while (await call.ResponseStream.MoveNext(token))
                {
                    var frame = call.ResponseStream.Current;
                    var data = frame.Data.ToByteArray();
                    OnFrameReceived?.Invoke(frame.Width, frame.Height, data);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }

    [RelayCommand]
    public async Task ApplyZoomAsync(double newZoom)
    {
        if (SelectedFeed == null || _client == null) return;

        try
        {
            var res = await _client.SetFeedPropertyAsync(new SetFeedPropertyRequest
            {
                FeedId = SelectedFeed.FeedId,
                PropertyId = "camera.zoom_ratio",
                Value = newZoom
            });

            if (res.Success)
            {
                SelectedFeed.ZoomRatio = res.AppliedValue;
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task OpenPairingModalAsync()
    {
        await InitializeAsync();
        if (_client == null) return;
        try
        {
            var result = await _client.GetPairingQrAsync(new GetPairingQrRequest { ValidDurationSeconds = 300 });
            PairingQrUri = result.QrUri;
            PairingPinCode = result.ComparisonCode;
            PairingHostText = $"{PairingUtils.GetLocalLanIpAddress()}:7443";
            PairingExpiresText = "Valid for 5 minutes • Single-use session token";
            GenerateQrBitmap(PairingQrUri);
            IsPairingModalOpen = true;
        }
        catch (Exception ex) { ServerStatus = $"Pairing unavailable: {ex.Message}"; }
    }

    private void GenerateQrBitmap(string payload)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            byte[] qrBytes = qrCode.GetGraphic(20);
            PairingQrBitmap = new Bitmap(new MemoryStream(qrBytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoFeedCenter] Failed to generate QR bitmap: {ex}");
            PairingQrBitmap = null;
        }
    }

    [RelayCommand]
    public async Task CopyPairingUriAsync()
    {
        if (string.IsNullOrEmpty(PairingQrUri)) return;

        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime switch
            {
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
                Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView => Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView),
                _ => null
            };

            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(PairingQrUri);
            }
        }
        catch { }
    }

    [RelayCommand]
    public void ClosePairingModal()
    {
        IsPairingModalOpen = false;
    }

    [RelayCommand]
    public void OpenAddObsModal()
    {
        ObsDisplayName = "OBS Stream";
        ObsPort = 8554;
        ObsPassphrase = "pass_" + Guid.NewGuid().ToString("N")[..8];
        IsAddObsModalOpen = true;
    }

    [RelayCommand]
    public void CloseAddObsModal()
    {
        IsAddObsModalOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmAddObsAsync()
    {
        if (_client == null) return;

        try
        {
            var feedId = "obs-" + Guid.NewGuid().ToString("N")[..6];
            await _client.CreateFeedAsync(new CreateFeedRequest
            {
                FeedId = feedId,
                DisplayName = ObsDisplayName,
                UnauthenticatedObs = true,
                CustomPassphrase = ObsPassphrase,
                Port = ObsPort
            });

            IsAddObsModalOpen = false;
        }
        catch { }
    }

    [RelayCommand]
    public async Task RemoveSelectedFeedAsync()
    {
        if (SelectedFeed == null || _client == null) return;

        try
        {
            await _client.RemoveFeedAsync(new RemoveFeedRequest
            {
                FeedId = SelectedFeed.FeedId
            });
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialization != null) await _initialization;
        _cts.Cancel();
        _previewCts?.Cancel();

        if (_previewWatchTask != null)
        {
            try { await _previewWatchTask; } catch { }
        }

        if (_feedWatchTask != null)
        {
            try { await _feedWatchTask; } catch { }
        }

        if (_channel != null)
        {
            _channel.Dispose();
        }

        if (_embeddedEngine != null)
        {
            await _embeddedEngine.DisposeAsync();
        }

        _cts.Dispose();
        _previewCts?.Dispose();
    }
}
