using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace MADTOM.Console.Hosting;

public sealed class DisplayBacklightDevice
{
    public string Id { get; }
    public string DisplayName { get; }
    public string SysfsPath { get; }
    public int MaxBrightness { get; }
    public int CurrentBrightness { get; set; }

    public double BrightnessPercent
    {
        get => MaxBrightness > 0 ? (CurrentBrightness * 100.0 / MaxBrightness) : 0;
        set
        {
            int newVal = (int)Math.Round((value / 100.0) * MaxBrightness);
            CurrentBrightness = Math.Clamp(newVal, 0, MaxBrightness);
        }
    }

    /// <summary>
    /// 5% of maximum brightness. Setting brightness below this requires confirmation.
    /// </summary>
    public int MinSafeBrightness => Math.Max(1, (int)Math.Ceiling(MaxBrightness * 0.05));

    public DisplayBacklightDevice(string id, string displayName, string sysfsPath, int maxBrightness, int currentBrightness)
    {
        Id = id;
        DisplayName = displayName;
        SysfsPath = sysfsPath;
        MaxBrightness = Math.Max(1, maxBrightness);
        CurrentBrightness = Math.Clamp(currentBrightness, 0, MaxBrightness);
    }
}

public interface IBrightnessService
{
    bool IsSupported { get; }
    IReadOnlyList<DisplayBacklightDevice> GetDevices();
    bool SetBrightness(DisplayBacklightDevice device, int value);
}

public sealed class LinuxBrightnessService : IBrightnessService
{
    private readonly string _backlightBasePath;

    public LinuxBrightnessService(string? backlightBasePath = null)
    {
        _backlightBasePath = backlightBasePath ?? "/sys/class/backlight";
    }

    public static bool IsRunningWithoutDesktopEnvironment()
    {
        // Explicit force override for testing or embedded environments
        string? force = Environment.GetEnvironmentVariable("MADTOM_FORCE_BRIGHTNESS");
        if (force == "1" || string.Equals(force, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || OperatingSystem.IsAndroid())
            return false;

        // Running in direct KMS/DRM single-view mode
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
            return true;

        // In Linux desktop environments, session variables are populated.
        // If all of these are unset/empty, the app is running bare-metal on framebuffer/TTY/kiosk.
        string? xdgDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        string? desktopSession = Environment.GetEnvironmentVariable("DESKTOP_SESSION");
        string? gdmSession = Environment.GetEnvironmentVariable("GDMSESSION");

        return string.IsNullOrWhiteSpace(xdgDesktop)
            && string.IsNullOrWhiteSpace(desktopSession)
            && string.IsNullOrWhiteSpace(gdmSession);
    }

    public bool IsSupported
    {
        get
        {
            if (!IsRunningWithoutDesktopEnvironment())
                return false;

            if (!Directory.Exists(_backlightBasePath))
                return false;

            try
            {
                var dirs = Directory.GetDirectories(_backlightBasePath);
                foreach (var dir in dirs)
                {
                    string maxFile = Path.Combine(dir, "max_brightness");
                    string brightFile = Path.Combine(dir, "brightness");
                    if (File.Exists(maxFile) && File.Exists(brightFile))
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }

    public IReadOnlyList<DisplayBacklightDevice> GetDevices()
    {
        var devices = new List<DisplayBacklightDevice>();

        if (!Directory.Exists(_backlightBasePath))
            return devices;

        try
        {
            var dirs = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderBy(Directory.GetDirectories(_backlightBasePath), d => d));
            int index = 1;
            foreach (var dir in dirs)
            {
                string id = Path.GetFileName(dir);
                string maxFile = Path.Combine(dir, "max_brightness");
                string brightFile = Path.Combine(dir, "brightness");

                if (!File.Exists(maxFile) || !File.Exists(brightFile))
                    continue;

                if (!int.TryParse(File.ReadAllText(maxFile).Trim(), out int maxVal) || maxVal <= 0)
                    continue;

                if (!int.TryParse(File.ReadAllText(brightFile).Trim(), out int curVal))
                    curVal = maxVal;

                string displayName = dirs.Length == 1 
                    ? $"Primary Display ({id})"
                    : $"Display {index++} ({id})";

                devices.Add(new DisplayBacklightDevice(id, displayName, dir, maxVal, curVal));
            }
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"[LinuxBrightnessService] Error querying backlight devices: {ex.Message}");
        }

        return devices;
    }

    public bool SetBrightness(DisplayBacklightDevice device, int value)
    {
        int clamped = Math.Clamp(value, 0, device.MaxBrightness);
        string brightFile = Path.Combine(device.SysfsPath, "brightness");

        try
        {
            File.WriteAllText(brightFile, clamped.ToString());
            device.CurrentBrightness = clamped;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Try fallback via tee or pkexec if running as unprivileged user
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"echo {clamped} | sudo -n tee {brightFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(1000);
                if (proc?.ExitCode == 0)
                {
                    device.CurrentBrightness = clamped;
                    return true;
                }
            }
            catch
            {
                // Fallback failed
            }

            global::System.Console.Error.WriteLine($"[LinuxBrightnessService] Permission denied writing to {brightFile}");
            return false;
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"[LinuxBrightnessService] Failed setting brightness on {brightFile}: {ex.Message}");
            return false;
        }
    }
}
