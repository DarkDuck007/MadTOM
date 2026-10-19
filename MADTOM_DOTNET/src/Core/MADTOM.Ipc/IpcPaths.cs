using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MADTOM.Ipc;

public static class IpcPaths
{
    public static string GetManifestsDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "MADTOM", "run");
            Directory.CreateDirectory(dir);
            return dir;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            var dir = Path.Combine(runtimeDir, "madtom", "manifests");
            Directory.CreateDirectory(dir);
            return dir;
        }

        var uid = GetUnixUid();
        var fallbackDir = Path.Combine(Path.GetTempPath(), $"madtom-{uid}", "manifests");
        Directory.CreateDirectory(fallbackDir);
        return fallbackDir;
    }

    public static string GetSocketPath(string processId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $@"\\.\pipe\madtom-{processId}";
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            var socketDir = Path.Combine(runtimeDir, "madtom");
            Directory.CreateDirectory(socketDir);
            return Path.Combine(socketDir, $"{processId}.sock");
        }

        var uid = GetUnixUid();
        var fallbackDir = Path.Combine(Path.GetTempPath(), $"madtom-{uid}");
        Directory.CreateDirectory(fallbackDir);
        return Path.Combine(fallbackDir, $"{processId}.sock");
    }

    private static string GetUnixUid()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var uid = geteuid();
                return uid.ToString();
            }
        }
        catch { }
        return Environment.UserName;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
