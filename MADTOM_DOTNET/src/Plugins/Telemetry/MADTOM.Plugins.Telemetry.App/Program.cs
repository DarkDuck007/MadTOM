using System;
using Avalonia;

namespace MADTOM.Plugins.Telemetry.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions
            {
                OverlayPopups = true
            })
            .With(new Win32PlatformOptions
            {
                OverlayPopups = true
            })
            .LogToTrace();
}

