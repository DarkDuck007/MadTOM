#if ANDROID
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using MADTOM.Identity;
using MADTOM.Plugins.IPCam;

namespace MADTOM.Plugins.IPCam.Android;

[Activity(Exported = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize, Label = "MADTOM IPCam", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar")]
public class MainActivity : Activity, TextureView.ISurfaceTextureListener
{
    private const int RequestPermissionsCode = 100;

    private VideoSourceSessionClient? _sessionClient;
    private AndroidCameraCaptureEngine? _cameraEngine;
    private IPCamConfig _config = new();
    private DeviceCertificate? _deviceCert;

    private TextureView? _textureView;
    private Spinner? _cameraSpinner;
    private SeekBar? _zoomSeekBar;
    private TextView? _zoomTextView;
    private TextView? _statusTextView;
    private TextView? _hubInfoTextView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _config = IPCamConfig.Load();
        var certDir = System.IO.Path.Combine(FilesDir?.AbsolutePath ?? "/data/data/com.madtom.ipcam/files", "ipcam");
        Directory.CreateDirectory(certDir);
        _deviceCert = DeviceCertificate.LoadOrCreate(System.IO.Path.Combine(certDir, "device.pfx"), _config.DisplayName);

        var root = new ScrollView(this);
        var layout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        layout.SetPadding(40, 50, 40, 50);

        // Header
        var title = new TextView(this)
        {
            Text = "MADTOM IPCam Source",
            TextSize = 22,
            Typeface = Typeface.DefaultBold
        };
        layout.AddView(title);

        var subtitle = new TextView(this)
        {
            Text = "Hardware Camera Capture & Streaming Client",
            TextSize = 13
        };
        subtitle.SetPadding(0, 0, 0, 30);
        layout.AddView(subtitle);

        // Viewfinder
        _textureView = new TextureView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 600)
        };
        _textureView.SurfaceTextureListener = this;
        layout.AddView(_textureView);

        // Zoom Bar
        var zoomLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        zoomLayout.SetPadding(0, 20, 0, 20);

        _zoomTextView = new TextView(this)
        {
            Text = "Zoom: 1.0x",
            TextSize = 14,
            Typeface = Typeface.DefaultBold
        };
        _zoomTextView.SetPadding(0, 0, 20, 0);
        zoomLayout.AddView(_zoomTextView);

        _zoomSeekBar = new SeekBar(this)
        {
            Max = 100,
            Progress = 10,
            LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f)
        };
        _zoomSeekBar.ProgressChanged += (s, e) =>
        {
            float ratio = 1.0f + (e.Progress / 100.0f) * 9.0f;
            _zoomTextView.Text = $"Zoom: {ratio:F1}x";
            _ = _cameraEngine?.SetZoomRatioAsync(ratio);
        };
        zoomLayout.AddView(_zoomSeekBar);
        layout.AddView(zoomLayout);

        // Camera Selector
        var camLabel = new TextView(this) { Text = "Selected Camera Sensor:" };
        layout.AddView(camLabel);

        _cameraSpinner = new Spinner(this);
        _cameraSpinner.ItemSelected += async (s, e) =>
        {
            if (_cameraEngine != null && e.Position >= 0 && e.Position < _cameraEngine.AvailableCameras.Count)
            {
                var selected = _cameraEngine.AvailableCameras[e.Position];
                try { await _cameraEngine.SelectCameraAsync(selected); }
                catch (Exception ex) { _statusTextView!.Text = ex.Message; return; }
                _config.SelectedCameraId = selected.Id;
                _config.Save();
            }
        };
        layout.AddView(_cameraSpinner);

        // Hub Connection Info
        _hubInfoTextView = new TextView(this)
        {
            Text = $"Hub: {_config.HubAddress} {(_config.IsPaired ? "(Paired)" : "(Unpaired)")}",
            TextSize = 13
        };
        _hubInfoTextView.SetPadding(0, 20, 0, 10);
        layout.AddView(_hubInfoTextView);

        // Status
        _statusTextView = new TextView(this)
        {
            Text = "Status: Standby",
            TextSize = 14,
            Typeface = Typeface.DefaultBold
        };
        _statusTextView.SetPadding(0, 0, 0, 20);
        layout.AddView(_statusTextView);

        // Buttons
        var pairButton = new Button(this) { Text = "Scan / Enter QR Pairing URI" };
        pairButton.Click += (s, e) => ShowPairingDialog();
        layout.AddView(pairButton);

        var startButton = new Button(this) { Text = "▶ Connect and stream" };
        startButton.Click += (s, e) => CheckAndStartService();
        layout.AddView(startButton);

        var stopButton = new Button(this) { Text = "■ Disconnect" };
        stopButton.Click += async (s, e) => { if (_sessionClient != null) await _sessionClient.StopAsync(); };
        layout.AddView(stopButton);

        root.AddView(layout);
        SetContentView(root);

        CheckAndRequestPermissions();
    }

    private void CheckAndRequestPermissions()
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            RequestPermissions(
                new[]
                {
                    global::Android.Manifest.Permission.Camera,
                    global::Android.Manifest.Permission.ForegroundService,
                    global::Android.Manifest.Permission.Internet
                },
                RequestPermissionsCode);
        }
        else
        {
            InitializeCamera();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == RequestPermissionsCode && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            InitializeCamera();
        }
    }

    private async void InitializeCamera()
    {
        try
        {
            _cameraEngine = new AndroidCameraCaptureEngine(this);
            await _cameraEngine.InitializeAsync();
            if (_textureView?.IsAvailable == true && _textureView.SurfaceTexture != null)
            {
                _textureView.SurfaceTexture.SetDefaultBufferSize(1280, 720);
                _cameraEngine.SetPreviewSurface(new Surface(_textureView.SurfaceTexture));
            }

            var names = _cameraEngine.AvailableCameras.Select(c => $"{c.DisplayName} ({c.Facing})").ToArray();
            var adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, names);
            adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _cameraSpinner!.Adapter = adapter;

            var matchIdx = _cameraEngine.AvailableCameras.ToList().FindIndex(c => c.Id == _config.SelectedCameraId);
            if (matchIdx >= 0) _cameraSpinner.SetSelection(matchIdx);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Camera init error: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        surface.SetDefaultBufferSize(1280, 720);
        _cameraEngine?.SetPreviewSurface(new Surface(surface));
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        _cameraEngine?.SetPreviewSurface(null);
        return true;
    }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    private void ShowPairingDialog()
    {
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Pair with MADTOM Hub");

        var dialogLayout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        dialogLayout.SetPadding(40, 20, 40, 20);

        var prompt = new TextView(this) { Text = "Paste the madtom://pair URI displayed on the Hub:" };
        dialogLayout.AddView(prompt);

        var uriInput = new EditText(this)
        {
            Hint = "madtom://pair?host=192.168.1.X&port=7443&..."
        };
        dialogLayout.AddView(uriInput);

        builder.SetView(dialogLayout);
        builder.SetPositiveButton("Pair", async (s, e) =>
        {
            string uriText = uriInput.Text?.Trim() ?? string.Empty;
            if (PairingUtils.TryParseQrUri(uriText, out var details))
            {
                try
                {
                    var channel = PairingClient.CreateSecureChannel(details.Host, details.Port, details.HubFingerprintSha256, _deviceCert);
                    var client = new PairingClient(_deviceCert!, _config.DisplayName, "android");
                    var res = await client.PairWithQrAsync(channel, details);
                    if (res.Success)
                    {
                        _config.IsPaired = true;
                        _config.HubAddress = $"https://{details.Host}:{details.Port}";
                        _config.HubFingerprintSha256 = details.HubFingerprintSha256;
                        _config.TrustId = res.TrustId;
                        _config.Save();

                        _hubInfoTextView!.Text = $"Hub: {_config.HubAddress} (Paired)";
                        Toast.MakeText(this, "Paired successfully with Hub!", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        Toast.MakeText(this, $"Pairing failed: {res.Message}", ToastLength.Long)?.Show();
                    }
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Pairing error: {ex.Message}", ToastLength.Long)?.Show();
                }
            }
            else
            {
                Toast.MakeText(this, "Invalid madtom://pair URI string", ToastLength.Long)?.Show();
            }
        });
        builder.SetNegativeButton("Cancel", (s, e) => { });
        builder.Show();
    }

    private async void CheckAndStartService()
    {
        if (_cameraEngine == null || !_config.IsPaired)
        {
            _statusTextView!.Text = "Grant camera permission and pair with the hub first.";
            return;
        }
        try
        {
            if (_sessionClient != null) await _sessionClient.DisposeAsync();
            _sessionClient = new VideoSourceSessionClient(_cameraEngine, _config.HubAddress,
                _config.DeviceId, _config.DisplayName, "android",
                pinnedFingerprint: _config.HubFingerprintSha256, clientCert: _deviceCert);
            _sessionClient.StateChanged += (_, state) => RunOnUiThread(() => _statusTextView!.Text = string.IsNullOrEmpty(_sessionClient?.LastError) ? state.ToString() : _sessionClient.LastError);
            await _sessionClient.StartAsync();
        }
        catch (Exception ex) { _statusTextView!.Text = ex.Message; }
    }

    protected override async void OnDestroy()
    {
        base.OnDestroy();
        if (_sessionClient != null) await _sessionClient.DisposeAsync();
        if (_cameraEngine != null) await _cameraEngine.DisposeAsync();
        _deviceCert?.Dispose();
    }
}
#endif
