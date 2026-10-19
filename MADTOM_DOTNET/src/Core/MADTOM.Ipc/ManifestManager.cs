using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MADTOM.Protocol.Core.V1;

namespace MADTOM.Ipc;

public sealed class ManifestManager : IAsyncDisposable
{
    private readonly string _manifestPath;
    private readonly ProcessManifest _manifest;
    private bool _isDisposed;

    public string ManifestPath => _manifestPath;
    public ProcessManifest Manifest => _manifest;

    public ManifestManager(ProcessManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        var dir = IpcPaths.GetManifestsDirectory();
        _manifestPath = Path.Combine(dir, $"{_manifest.Id}.json");
    }

    public async Task PublishAsync()
    {
        var json = Google.Protobuf.JsonFormatter.Default.Format(_manifest);

        var tempPath = _manifestPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _manifestPath, overwrite: true);
    }

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void CleanStaleManifests()
    {
        try
        {
            var dir = IpcPaths.GetManifestsDirectory();
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var manifest = Google.Protobuf.JsonParser.Default.Parse<ProcessManifest>(text);

                    if (manifest != null && manifest.Pid > 0 && !IsProcessAlive(manifest.Pid))
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        try
        {
            if (File.Exists(_manifestPath))
            {
                File.Delete(_manifestPath);
            }
        }
        catch { }

        return ValueTask.CompletedTask;
    }
}
