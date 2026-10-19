using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MADTOM.Protocol.Core.V1;

namespace MADTOM.Ipc;

public interface IProcessRegistry
{
    EndpointDescriptor? FindProvider(string capabilityId);
    IReadOnlyList<EndpointDescriptor> FindAllProviders(string capabilityId);
    IReadOnlyList<ProcessManifest> GetAllActiveManifests();
    IDisposable WatchProviders(string capabilityId, Action<IReadOnlyList<EndpointDescriptor>> callback);
}

public sealed class ProcessRegistry : IProcessRegistry
{
    private static readonly Lazy<ProcessRegistry> _instance = new(() => new ProcessRegistry());
    public static ProcessRegistry Instance => _instance.Value;

    public ProcessRegistry()
    {
    }

    public EndpointDescriptor? FindProvider(string capabilityId)
    {
        return FindAllProviders(capabilityId).FirstOrDefault();
    }

    public IReadOnlyList<EndpointDescriptor> FindAllProviders(string capabilityId)
    {
        var manifests = GetAllActiveManifests();
        var endpoints = new List<EndpointDescriptor>();

        foreach (var m in manifests)
        {
            var match = m.Provides.Any(p => string.Equals(p.Capability, capabilityId, StringComparison.OrdinalIgnoreCase));
            if (match)
            {
                foreach (var ep in m.Endpoints)
                {
                    if (ep.Enabled)
                    {
                        endpoints.Add(ep);
                    }
                }
            }
        }

        return endpoints;
    }

    public IReadOnlyList<ProcessManifest> GetAllActiveManifests()
    {
        ManifestManager.CleanStaleManifests();

        var dir = IpcPaths.GetManifestsDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<ProcessManifest>();

        var list = new List<ProcessManifest>();
        var files = Directory.GetFiles(dir, "*.json");

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var manifest = Google.Protobuf.JsonParser.Default.Parse<ProcessManifest>(text);

                if (manifest != null && manifest.Pid > 0 && ManifestManager.IsProcessAlive(manifest.Pid))
                {
                    list.Add(manifest);
                }
            }
            catch { }
        }

        return list;
    }

    public IDisposable WatchProviders(string capabilityId, Action<IReadOnlyList<EndpointDescriptor>> callback)
    {
        var dir = IpcPaths.GetManifestsDirectory();
        var fsw = new FileSystemWatcher(dir, "*.json")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        void Refresh()
        {
            try
            {
                var endpoints = FindAllProviders(capabilityId);
                callback(endpoints);
            }
            catch { }
        }

        FileSystemEventHandler onEvent = (_, _) =>
        {
            // Debounce slightly to allow file writes to settle
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(50);
                Refresh();
            });
        };

        fsw.Created += onEvent;
        fsw.Changed += onEvent;
        fsw.Deleted += onEvent;
        fsw.Renamed += (s, e) => onEvent(s, null!);

        // Initial trigger
        Refresh();

        return fsw;
    }
}
