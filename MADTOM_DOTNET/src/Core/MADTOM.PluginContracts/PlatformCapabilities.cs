using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace MADTOM.PluginContracts;

public static class PlatformCapabilities
{
    public static bool SupportsFloatingWindows
    {
        get
        {
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                return false;

            return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;
        }
    }
}
