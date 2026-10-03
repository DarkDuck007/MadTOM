using System;
using System.IO;
using Avalonia;
using Avalonia.LinuxFramebuffer;

namespace MADTOM.Console;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var (options, error) = MADTOM.Console.Hosting.StudioCommandLineOptions.Parse(args);
        if (error != null)
        {
            global::System.Console.Error.WriteLine($"Error: {error}");
            PrintHelp();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.OutputMode == "drm")
        {
            if (!OperatingSystem.IsLinux())
            {
                global::System.Console.Error.WriteLine("Error: DRM direct-display mode (--output drm) is only supported on Linux with KMS/DRM graphics drivers.");
                return 1;
            }

            string drmCard = options.DrmCard ?? "/dev/dri/card0";

            if (!File.Exists(drmCard))
            {
                global::System.Console.Error.WriteLine($"Error: Specified DRM device '{drmCard}' was not found. Please verify device permissions and KMS/DRM drivers.");
                return 1;
            }

            global::System.Console.WriteLine($"[MADTOM Studio] Starting in direct-display mode (DRM/KMS) on {drmCard} (scaling={options.DrmScaling:0.##})...");

            try
            {
                BuildDrmApp()
                    .StartLinuxDrm(args, card: drmCard, scaling: options.DrmScaling);
                return 0;
            }
            catch (Exception ex)
            {
                global::System.Console.Error.WriteLine($"[MADTOM Studio] Fatal error initializing DRM direct-display backend: {ex.Message}");
                global::System.Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
        else
        {
            try
            {
                BuildDesktopApp()
                    .StartWithClassicDesktopLifetime(args);
                return 0;
            }
            catch (Exception ex)
            {
                global::System.Console.Error.WriteLine($"[MADTOM Studio] Fatal error during desktop execution: {ex.Message}");
                return 1;
            }
        }
    }

    private static void PrintHelp()
    {
        global::System.Console.WriteLine("MADTOM Studio - Unified Telemetry, Media & System Operations Host");
        global::System.Console.WriteLine("Usage: MADTOM.Studio [options]");
        global::System.Console.WriteLine();
        global::System.Console.WriteLine("Options:");
        global::System.Console.WriteLine("  -o, --output <mode>   Presentation output mode: 'desktop' (default) or 'drm' (direct display).");
        global::System.Console.WriteLine("  --card <path>         Linux DRM card device path (default: /dev/dri/card0). Only for DRM mode.");
        global::System.Console.WriteLine("  --scaling <factor>    Display scaling factor override (e.g. 1.0, 1.5, 2.0). Only for DRM mode.");
        global::System.Console.WriteLine("  -h, --help            Show this help information.");
    }

    public static AppBuilder BuildDesktopApp()
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

    public static AppBuilder BuildDrmApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}
