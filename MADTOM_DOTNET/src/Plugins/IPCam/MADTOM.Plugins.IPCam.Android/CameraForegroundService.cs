#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MADTOM.Identity;
using MADTOM.Plugins.IPCam;

namespace MADTOM.Plugins.IPCam.Android;

[Service(ForegroundServiceType = ForegroundService.TypeCamera, Exported = false)]
public class CameraForegroundService : Service
{
    public const string ChannelId = "madtom_camera_stream";
    public const int NotificationId = 1001;

    private VideoSourceSessionClient? _sessionClient;
    private AndroidCameraCaptureEngine? _cameraEngine;
    private DeviceCertificate? _deviceCert;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification("MADTOM Camera Streaming Active");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeCamera);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        Task.Run(async () =>
        {
            try
            {
                var config = IPCamConfig.Load();
                var certDir = Path.Combine(FilesDir?.AbsolutePath ?? "/data/data/com.madtom.ipcam/files", "ipcam");
                Directory.CreateDirectory(certDir);
                _deviceCert = DeviceCertificate.LoadOrCreate(Path.Combine(certDir, "device.pfx"), config.DisplayName);

                _cameraEngine = new AndroidCameraCaptureEngine(this);
                await _cameraEngine.InitializeAsync();

                _sessionClient = new VideoSourceSessionClient(
                    _cameraEngine,
                    config.HubAddress,
                    config.DeviceId,
                    config.DisplayName,
                    platform: "android",
                    pinnedFingerprint: config.IsPaired ? config.HubFingerprintSha256 : null,
                    clientCert: _deviceCert);

                await _sessionClient.StartAsync();
            }
            catch (Exception ex)
            {
                UpdateNotification($"Streaming Error: {ex.Message}");
            }
        });

        return StartCommandResult.Sticky;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "MADTOM Video Stream",
                NotificationImportance.Low)
            {
                Description = "Ongoing camera streaming to MADTOM Hub"
            };

            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(channel);
        }
    }

    private Notification BuildNotification(string text)
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder.SetContentTitle("MADTOM IPCam")
               .SetContentText(text)
               .SetSmallIcon(global::Android.Resource.Drawable.IcMenuCamera)
               .SetOngoing(true);

        return builder.Build();
    }

    private void UpdateNotification(string text)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, BuildNotification(text));
    }

    public override async void OnDestroy()
    {
        base.OnDestroy();

        if (_sessionClient != null)
        {
            await _sessionClient.StopAsync();
            await _sessionClient.DisposeAsync();
        }

        if (_cameraEngine != null)
        {
            await _cameraEngine.DisposeAsync();
        }

        _deviceCert?.Dispose();
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
#endif
