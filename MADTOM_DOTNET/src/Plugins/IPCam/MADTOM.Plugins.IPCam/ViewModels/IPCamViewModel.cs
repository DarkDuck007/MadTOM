using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Identity;
using MADTOM.PluginContracts;
using MADTOM.Protocol.Video.V1;
using ZXing;

namespace MADTOM.Plugins.IPCam.ViewModels;

public sealed partial class IPCamViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPluginHostContext _hostContext;
    private readonly ICameraCaptureEngine _cameraEngine;
    private readonly IPCamConfig _config;
    private readonly DeviceCertificate _deviceCert;

    private VideoSourceSessionClient? _sessionClient;
    private Func<bool, Task<PairingResult>>? _manualConfirmCallback;
    private int _qrScanFrameCounter;
    private bool _initialized;
    private bool _disposed;

    // Viewfinder feed event for UI control
    public event Action<uint, uint, byte[]>? ViewfinderFrameReceived;

    // Camera selection
    public ObservableCollection<CameraDeviceInfo> AvailableCameras { get; } = new();

    [ObservableProperty]
    private CameraDeviceInfo? _selectedCamera;

    // Resolution & Profile settings
    public ObservableCollection<string> AvailableResolutions { get; } = new()
    {
        "1920x1080",
        "1280x720",
        "3840x2160"
    };

    [ObservableProperty]
    private string _selectedResolution = "1920x1080";

    public ObservableCollection<uint> AvailableFramerates { get; } = new() { 30, 60 };

    [ObservableProperty]
    private uint _selectedFramerate = 30;

    public ObservableCollection<string> AvailableCodecs { get; } = new() { "HEVC", "AVC" };

    [ObservableProperty]
    private string _selectedCodec = "HEVC";

    [ObservableProperty]
    private double _bitrateMbps = 6.0;

    // Zoom
    [ObservableProperty]
    private float _zoomRatio = 1.0f;

    [ObservableProperty]
    private float _minZoom = 1.0f;

    [ObservableProperty]
    private float _maxZoom = 10.0f;

    // Hub & Connection
    [ObservableProperty]
    private string _hubAddress = "https://127.0.0.1:7443";

    [ObservableProperty]
    private string _displayName = "IPCam Source";

    [ObservableProperty]
    private string _deviceId = string.Empty;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isPaired;

    [ObservableProperty]
    private string _hubFingerprint = string.Empty;

    // Diagnostics
    [ObservableProperty]
    private double _clockOffsetMs;

    [ObservableProperty]
    private double _rttMs;

    [ObservableProperty]
    private string _grantedCodec = "HEVC";

    [ObservableProperty]
    private string _grantedResolution = "1920x1080 @ 30fps";

    // Pairing Modal
    [ObservableProperty]
    private bool _isPairingModalOpen;

    [ObservableProperty]
    private string _pairingQrUriInput = string.Empty;

    [ObservableProperty]
    private string _manualHostInput = string.Empty;

    [ObservableProperty]
    private int _manualPortInput = 7443;

    [ObservableProperty]
    private string _manualComparisonCode = string.Empty;

    [ObservableProperty]
    private string _pairingStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isPairingBusy;

    [ObservableProperty]
    private bool _isManualAwaitingConfirmation;

    [ObservableProperty]
    private bool _isScanningQr;

    public IPCamViewModel(IPluginHostContext hostContext, ICameraCaptureEngine? cameraEngine = null)
    {
        _hostContext = hostContext ?? throw new ArgumentNullException(nameof(hostContext));
        _cameraEngine = cameraEngine ?? CreateDefaultEngine();

        // Load configuration
        _config = IPCamConfig.Load();
        _hubAddress = _config.HubAddress;
        _displayName = _config.DisplayName;
        _deviceId = _config.DeviceId;
        _isPaired = _config.IsPaired;
        _hubFingerprint = _config.HubFingerprintSha256;
        _selectedCodec = _config.PreferredCodec.ToUpperInvariant();
        _selectedFramerate = _config.PreferredFps;
        _selectedResolution = $"{_config.PreferredResolutionWidth}x{_config.PreferredResolutionHeight}";
        _bitrateMbps = _config.PreferredBitrateBps / 1_000_000.0;

        // Device certificate for mutual authentication
        var certDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MADTOM", "ipcam");
        Directory.CreateDirectory(certDir);
        var certPath = Path.Combine(certDir, "device.pfx");
        _deviceCert = DeviceCertificate.LoadOrCreate(certPath, _displayName);

        // Populate cameras
        PopulateCameras();

        _cameraEngine.CamerasChanged += (s, e) => Dispatcher.UIThread.Post(PopulateCameras);
        _cameraEngine.SelectedCameraChanged += (s, cam) => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedCamera != cam) SelectedCamera = cam;
            MinZoom = cam.ZoomLimits.Min;
            MaxZoom = cam.ZoomLimits.Max;
            UpdateResolutionList(cam);
        });

        _cameraEngine.ZoomChanged += (s, z) =>
        {
            Dispatcher.UIThread.Post(() => ZoomRatio = z);
        };

        _cameraEngine.PreviewFrameArrived += OnPreviewFrameReceived;
        if (_cameraEngine is DesktopCameraCaptureEngine desktop)
            desktop.CaptureError += error => Dispatcher.UIThread.Post(() => { StatusText = error; IsStreaming = false; });
    }

    private static ICameraCaptureEngine CreateDefaultEngine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new DesktopCameraCaptureEngine();
        }
        return new SimulatedCameraCaptureEngine();
    }

    private void PopulateCameras()
    {
        AvailableCameras.Clear();
        foreach (var cam in _cameraEngine.AvailableCameras)
        {
            AvailableCameras.Add(cam);
        }

        var matched = AvailableCameras.FirstOrDefault(c => c.Id == _config.SelectedCameraId)
                      ?? AvailableCameras.FirstOrDefault();
        if (matched != null)
        {
            SelectedCamera = matched;
            MinZoom = matched.ZoomLimits.Min;
            MaxZoom = matched.ZoomLimits.Max;
            ZoomRatio = _cameraEngine.CurrentZoomRatio;
            UpdateResolutionList(matched);
        }
    }

    private void UpdateResolutionList(CameraDeviceInfo cam)
    {
        AvailableResolutions.Clear();
        if (cam.SupportedResolutions.Count > 0)
        {
            foreach (var r in cam.SupportedResolutions)
            {
                string resStr = $"{r.Width}x{r.Height}";
                if (!AvailableResolutions.Contains(resStr))
                {
                    AvailableResolutions.Add(resStr);
                }
            }
        }
        else
        {
            AvailableResolutions.Add("1920x1080");
            AvailableResolutions.Add("1280x720");
            AvailableResolutions.Add("640x360");
        }

        if (!AvailableResolutions.Contains(SelectedResolution))
        {
            SelectedResolution = AvailableResolutions.FirstOrDefault() ?? "1920x1080";
        }
    }

    private void OnPreviewFrameReceived(uint width, uint height, byte[] frameData)
    {
        ViewfinderFrameReceived?.Invoke(width, height, frameData);

        // QR barcode scanning from camera frames if scanner is active
        if (IsScanningQr && ++_qrScanFrameCounter % 6 == 0)
        {
            try
            {
                var luminanceSource = new RGBLuminanceSource(frameData, (int)width, (int)height, RGBLuminanceSource.BitmapFormat.BGRA32);
                var reader = new BarcodeReaderGeneric();
                var result = reader.Decode(luminanceSource);
                if (result != null && !string.IsNullOrEmpty(result.Text))
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        PairingQrUriInput = result.Text;
                        IsScanningQr = false;
                        await PairWithQrUriAsync();
                    });
                }
            }
            catch { }
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        try { await _cameraEngine.InitializeAsync(); }
        catch (Exception ex) { StatusText = ex.Message; return; }
        if (_config.AutoStart && _config.IsPaired)
        {
            await ConnectHubAsync();
        }
    }

    [RelayCommand]
    public async Task StartPreviewAsync()
    {
        try
        {
            if (_cameraEngine is DesktopCameraCaptureEngine desktop) await desktop.StartPreviewAsync();
            StatusText = "Preview starting";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    public async Task StopPreviewAsync()
    {
        if (_cameraEngine is DesktopCameraCaptureEngine desktop) await desktop.StopPreviewAsync();
        StatusText = IsStreaming ? "Streaming" : "Camera stopped";
    }

    async partial void OnSelectedCameraChanged(CameraDeviceInfo? value)
    {
        if (value != null)
        {
            _config.SelectedCameraId = value.Id;
            _config.Save();
            try { await _cameraEngine.SelectCameraAsync(value); }
            catch (Exception ex) { StatusText = ex.Message; }
            MinZoom = value.ZoomLimits.Min;
            MaxZoom = value.ZoomLimits.Max;
            UpdateResolutionList(value);
        }
    }

    partial void OnSelectedResolutionChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && value.Contains('x'))
        {
            var parts = value.Split('x');
            if (uint.TryParse(parts[0], out uint w) && uint.TryParse(parts[1], out uint h))
            {
                _config.PreferredResolutionWidth = w;
                _config.PreferredResolutionHeight = h;
                _config.Save();
            }
        }
    }

    partial void OnSelectedFramerateChanged(uint value)
    {
        if (value > 0)
        {
            _config.PreferredFps = value;
            _config.Save();
        }
    }

    partial void OnSelectedCodecChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _config.PreferredCodec = value.ToLowerInvariant();
            _config.Save();
        }
    }

    partial void OnBitrateMbpsChanged(double value)
    {
        if (value > 0)
        {
            _config.PreferredBitrateBps = (uint)(value * 1_000_000);
            _config.Save();
        }
    }

    partial void OnHubAddressChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _config.HubAddress = value.Trim();
            _config.Save();
        }
    }

    [RelayCommand]
    public async Task ConnectHubAsync()
    {
        if (_sessionClient != null)
        {
            await _sessionClient.StopAsync();
            await _sessionClient.DisposeAsync();
        }

        StatusText = "Connecting...";
        string hubAddr = HubAddress.Trim();

        string currentPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                               : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                               : "desktop";

        _sessionClient = new VideoSourceSessionClient(
            _cameraEngine,
            hubAddr,
            DeviceId,
            DisplayName,
            platform: currentPlatform,
            pinnedFingerprint: IsPaired ? HubFingerprint : null,
            clientCert: _deviceCert,
            preferredProfile: new StreamProfile
            {
                CameraId = SelectedCamera?.Id ?? "synthetic",
                Codec = SelectedCodec.ToLowerInvariant(),
                Width = _config.PreferredResolutionWidth,
                Height = _config.PreferredResolutionHeight,
                Fps = SelectedFramerate,
                BitrateBps = _config.PreferredBitrateBps,
                IdrIntervalMs = 2000
            });

        _sessionClient.StateChanged += (s, state) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (state)
                {
                    case SourceConnectionState.Connecting:
                        StatusText = "Connecting to Hub...";
                        IsConnected = false;
                        IsStreaming = false;
                        break;
                    case SourceConnectionState.Connected:
                        StatusText = "Connected to Hub (Standby)";
                        IsConnected = true;
                        IsStreaming = false;
                        break;
                    case SourceConnectionState.Streaming:
                        StatusText = "Streaming to Hub (Live)";
                        IsConnected = true;
                        IsStreaming = true;
                        if (_sessionClient.GrantedProfile != null)
                        {
                            var p = _sessionClient.GrantedProfile;
                            GrantedCodec = p.Codec.ToUpperInvariant();
                            GrantedResolution = $"{p.Width}x{p.Height} @ {p.Fps}fps";
                        }
                        break;
                    case SourceConnectionState.Disconnected:
                        StatusText = string.IsNullOrEmpty(_sessionClient?.LastError) ? "Disconnected" : _sessionClient.LastError;
                        IsConnected = false;
                        IsStreaming = false;
                        break;
                }
            });
        };

        _sessionClient.TimeSyncUpdated += (s, sync) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClockOffsetMs = sync.OffsetMs;
                RttMs = sync.RttMs;
            });
        };

        try
        {
            await _sessionClient.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Connection error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DisconnectHubAsync()
    {
        if (_sessionClient != null)
        {
            await _sessionClient.StopAsync();
            StatusText = "Disconnected";
            IsConnected = false;
            IsStreaming = false;
        }
    }

    [RelayCommand]
    public async Task ApplyZoomAsync(double newZoom)
    {
        float stepped = await _cameraEngine.SetZoomRatioAsync((float)newZoom);
        ZoomRatio = stepped;
    }

    partial void OnZoomRatioChanged(float value)
    {
        if (Math.Abs(_cameraEngine.CurrentZoomRatio - value) > 0.05f)
        {
            _ = _cameraEngine.SetZoomRatioAsync(value);
        }
    }

    // Modal & Pairing Commands
    [RelayCommand]
    public void OpenPairingModal()
    {
        PairingQrUriInput = string.Empty;
        ManualHostInput = PairingUtils.GetLocalLanIpAddress();
        ManualPortInput = 7443;
        ManualComparisonCode = string.Empty;
        PairingStatusMessage = string.Empty;
        IsManualAwaitingConfirmation = false;
        IsPairingBusy = false;
        IsScanningQr = false;
        IsPairingModalOpen = true;
    }

    [RelayCommand]
    public void ClosePairingModal()
    {
        IsScanningQr = false;
        IsPairingModalOpen = false;
    }

    [RelayCommand]
    public void ToggleQrScanner()
    {
        IsScanningQr = !IsScanningQr;
        PairingStatusMessage = IsScanningQr
            ? "Point camera at Hub QR code to scan automatically..."
            : string.Empty;
    }

    [RelayCommand]
    public async Task PairWithQrUriAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingQrUriInput))
        {
            PairingStatusMessage = "Please enter or paste a valid madtom://pair URI";
            return;
        }

        if (!PairingUtils.TryParseQrUri(PairingQrUriInput.Trim(), out var details))
        {
            PairingStatusMessage = "Invalid QR code payload. Expected scheme madtom://pair?...";
            return;
        }

        IsPairingBusy = true;
        PairingStatusMessage = $"Connecting to Hub at {details.Host}:{details.Port}...";

        try
        {
            var channel = PairingClient.CreateSecureChannel(details.Host, details.Port, details.HubFingerprintSha256, _deviceCert);
            var pairingClient = new PairingClient(_deviceCert, DisplayName, "desktop");

            var result = await pairingClient.PairWithQrAsync(channel, details);
            if (result.Success)
            {
                _config.IsPaired = true;
                _config.HubAddress = $"https://{details.Host}:{details.Port}";
                _config.HubFingerprintSha256 = details.HubFingerprintSha256;
                _config.TrustId = result.TrustId;
                _config.Save();

                IsPaired = true;
                HubAddress = _config.HubAddress;
                HubFingerprint = _config.HubFingerprintSha256;
                PairingStatusMessage = "Paired successfully! Connecting to Hub...";
                await Task.Delay(800);
                IsPairingModalOpen = false;
                await ConnectHubAsync();
            }
            else
            {
                PairingStatusMessage = $"Pairing failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            PairingStatusMessage = $"Pairing error: {ex.Message}";
        }
        finally
        {
            IsPairingBusy = false;
        }
    }

    [RelayCommand]
    public async Task BeginManualPairingAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualHostInput))
        {
            PairingStatusMessage = "Please enter a valid Hub IP address or hostname";
            return;
        }

        IsPairingBusy = true;
        PairingStatusMessage = "Initiating manual pairing exchange...";

        try
        {
            string host = ManualHostInput.Trim();
            int port = ManualPortInput > 0 ? ManualPortInput : 7443;
            var channel = PairingClient.CreateSecureChannel(host, port, pinnedFingerprint: null, _deviceCert);
            var pairingClient = new PairingClient(_deviceCert, DisplayName, "desktop");

            string sessionId = Guid.NewGuid().ToString("N")[..8];
            var (comparisonCode, confirmFunc) = await pairingClient.BeginManualPairingAsync(channel, sessionId);

            _manualConfirmCallback = confirmFunc;
            ManualComparisonCode = comparisonCode;
            IsManualAwaitingConfirmation = true;
            PairingStatusMessage = $"Verify that the 4-digit comparison code matches on the Hub screen: {comparisonCode}";
        }
        catch (Exception ex)
        {
            PairingStatusMessage = $"Error starting manual pairing: {ex.Message}";
        }
        finally
        {
            IsPairingBusy = false;
        }
    }

    [RelayCommand]
    public async Task ConfirmManualPairingAsync(bool matches)
    {
        if (_manualConfirmCallback == null) return;

        IsPairingBusy = true;
        PairingStatusMessage = matches ? "Confirming pairing..." : "Aborting pairing...";

        try
        {
            var result = await _manualConfirmCallback(matches);
            if (result.Success && matches)
            {
                _config.IsPaired = true;
                _config.HubAddress = $"https://{ManualHostInput.Trim()}:{ManualPortInput}";
                _config.HubFingerprintSha256 = result.HubFingerprintSha256;
                _config.TrustId = result.TrustId;
                _config.Save();

                IsPaired = true;
                HubAddress = _config.HubAddress;
                HubFingerprint = _config.HubFingerprintSha256;
                PairingStatusMessage = "Paired successfully! Connecting to Hub...";
                await Task.Delay(800);
                IsPairingModalOpen = false;
                await ConnectHubAsync();
            }
            else
            {
                PairingStatusMessage = matches ? $"Pairing rejected: {result.Message}" : "Pairing cancelled by user.";
                IsManualAwaitingConfirmation = false;
            }
        }
        catch (Exception ex)
        {
            PairingStatusMessage = $"Confirmation error: {ex.Message}";
        }
        finally
        {
            IsPairingBusy = false;
        }
    }

    [RelayCommand]
    public void Unpair()
    {
        _config.IsPaired = false;
        _config.HubFingerprintSha256 = string.Empty;
        _config.TrustId = string.Empty;
        _config.Save();

        IsPaired = false;
        HubFingerprint = string.Empty;
        StatusText = "Unpaired";
        _ = DisconnectHubAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cameraEngine.PreviewFrameArrived -= OnPreviewFrameReceived;

        if (_sessionClient != null)
        {
            await _sessionClient.DisposeAsync();
            _sessionClient = null;
        }
        _deviceCert.Dispose();
        await _cameraEngine.DisposeAsync();
    }
}
