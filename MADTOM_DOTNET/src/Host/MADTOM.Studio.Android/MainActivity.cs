#nullable enable
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace MADTOM.Studio.Android;

[Activity(
    Label = "MADTOM Studio",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize,
    LaunchMode = LaunchMode.SingleTop)]
public class MainActivity : AvaloniaMainActivity
{
    private WifiManager.MulticastLock? _multicastLock;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            var wifiManager = (WifiManager?)GetSystemService(Context.WifiService);
            if (wifiManager != null)
            {
                _multicastLock = wifiManager.CreateMulticastLock("madtom_multicast");
                _multicastLock?.SetReferenceCounted(true);
                _multicastLock?.Acquire();
            }
        }
        catch
        {
            // Ignore if wifi service unavailable
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        try
        {
            if (_multicastLock != null && _multicastLock.IsHeld)
            {
                _multicastLock.Release();
            }
        }
        catch
        {
            // Ignore
        }
    }
}
