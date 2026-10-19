using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MADTOM.Ipc;
using MADTOM.Protocol.Core.V1;
using Xunit;

namespace MadTOM.Tests.Core;

public class IpcTests
{
    [Fact]
    public async Task ManifestManager_WritesAndDeletesManifestOnDispose()
    {
        var manifest = new ProcessManifest
        {
            Id = "test-process-" + Guid.NewGuid().ToString("N")[..8],
            Version = "0.1.0",
            Pid = Process.GetCurrentProcess().Id
        };
        manifest.Endpoints.Add(new EndpointDescriptor { Transport = "uds", Address = "/tmp/test.sock", Enabled = true });
        manifest.Provides.Add(new CapabilityDescriptor { Capability = "madtom.test.v1", Version = "1.0" });

        var manager = new ManifestManager(manifest);
        await manager.PublishAsync();

        Assert.True(File.Exists(manager.ManifestPath));

        var registry = new ProcessRegistry();
        var provider = registry.FindProvider("madtom.test.v1");
        Assert.NotNull(provider);
        Assert.Equal("/tmp/test.sock", provider.Address);

        await manager.DisposeAsync();
        Assert.False(File.Exists(manager.ManifestPath));
    }

    [Fact]
    public async Task ProcessRegistry_IgnoresStalePidManifests()
    {
        var deadPid = 9999999;
        while (ManifestManager.IsProcessAlive(deadPid))
        {
            deadPid--;
        }

        var staleManifest = new ProcessManifest
        {
            Id = "stale-dead-proc-" + Guid.NewGuid().ToString("N")[..8],
            Pid = deadPid
        };
        staleManifest.Endpoints.Add(new EndpointDescriptor { Transport = "uds", Address = "/tmp/stale.sock", Enabled = true });
        staleManifest.Provides.Add(new CapabilityDescriptor { Capability = "madtom.stale.v1", Version = "1.0" });

        var manager = new ManifestManager(staleManifest);
        await manager.PublishAsync();

        var registry = new ProcessRegistry();
        var found = registry.FindProvider("madtom.stale.v1");
        Assert.Null(found);

        // ManifestManager cleanup should have removed the file
        Assert.False(File.Exists(manager.ManifestPath));
    }
}
