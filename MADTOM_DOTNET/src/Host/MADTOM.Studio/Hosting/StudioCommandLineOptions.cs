using System;

namespace MADTOM.Console.Hosting;

public sealed class StudioCommandLineOptions
{
    public string OutputMode { get; init; } = "desktop";
    public string? DrmCard { get; init; }
    public double DrmScaling { get; init; } = 1.0;
    public bool ShowHelp { get; init; }

    public static (StudioCommandLineOptions Options, string? Error) Parse(string[] args)
    {
        string outputMode = "desktop";
        string? drmCard = null;
        double drmScaling = 1.0;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) || arg.Equals("-o", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    outputMode = args[++i].ToLowerInvariant();
                }
            }
            else if (arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
            {
                outputMode = arg.Substring("--output=".Length).ToLowerInvariant();
            }
            else if (arg.Equals("--card", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    drmCard = args[++i];
                }
            }
            else if (arg.StartsWith("--card=", StringComparison.OrdinalIgnoreCase))
            {
                drmCard = arg.Substring("--card=".Length);
            }
            else if (arg.Equals("--scaling", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    drmScaling = s;
                    i++;
                }
            }
            else if (arg.StartsWith("--scaling=", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(arg.Substring("--scaling=".Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    drmScaling = s;
                }
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
            }
        }

        if (showHelp)
        {
            return (new StudioCommandLineOptions
            {
                OutputMode = outputMode,
                DrmCard = drmCard,
                DrmScaling = drmScaling,
                ShowHelp = true
            }, null);
        }

        if (outputMode != "desktop" && outputMode != "drm")
        {
            return (new StudioCommandLineOptions(), $"Unknown output mode '{outputMode}'. Supported modes: 'desktop', 'drm'.");
        }

        return (new StudioCommandLineOptions
        {
            OutputMode = outputMode,
            DrmCard = drmCard,
            DrmScaling = drmScaling,
            ShowHelp = false
        }, null);
    }
}
