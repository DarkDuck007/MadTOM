using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MadTOM.Models;
using MadTOM.Services;
using Xunit;

namespace MadTOM.Tests;

public class TelemetryCollectorTests
{
    [Fact]
    public void LiveLODBucketAccumulator_AveragesPointsCorrectly()
    {
        // 5-minute scope (300s) / 60 points = 5 samples per bucket
        var accumulator = new LiveLODBucketAccumulator(TimeSpan.FromMinutes(5), 60);
        Assert.Equal(5, accumulator.BucketSize);

        var completedBuckets = new List<LODPoint>();
        accumulator.BucketCompleted += (_, point) => completedBuckets.Add(point);

        // Add 4 samples -> no completed bucket yet
        for (int i = 1; i <= 4; i++)
        {
            accumulator.AddSample(i * 10.0, i * 1_000_000_000L);
            Assert.Empty(completedBuckets);
        }

        // Add 5th sample -> completes bucket
        accumulator.AddSample(50.0, 5 * 1_000_000_000L);
        Assert.Single(completedBuckets);

        var bucket = completedBuckets[0];
        // Average of (10 + 20 + 30 + 40 + 50) / 5 = 30.0
        Assert.Equal(30.0, bucket.Value, 2);
        Assert.Equal(10.0, bucket.Min, 2);
        Assert.Equal(50.0, bucket.Max, 2);
    }

    [Fact]
    public void MultiCollectorManager_TracksConfiguredEndpoints()
    {
        var manager = new MultiCollectorManager();
        // Starts with default localhost
        Assert.Single(manager.ConfiguredEndpoints);
        Assert.Equal("127.0.0.1:50051", manager.ConfiguredEndpoints[0].Address);

        // Add second collector
        manager.AddCollector("Lab Fleet", "192.168.1.50:50051");
        Assert.Equal(2, manager.ConfiguredEndpoints.Count);

        // Remove collector
        manager.RemoveCollector("192.168.1.50:50051");
        Assert.Single(manager.ConfiguredEndpoints);
    }

    [Fact]
    public void FleetNodeModel_HasCollectorNameAndViewingOptIn()
    {
        var node = new FleetNodeModel
        {
            Id = "node-test-01",
            CollectorName = "Edge-Gateway",
            CollectorEndpoint = "10.0.0.1:50051",
            IsViewingOptedIn = true
        };

        Assert.Equal("Edge-Gateway", node.CollectorName);
        Assert.True(node.IsViewingOptedIn);
    }

    [Fact]
    public void CollectorSettingsViewModel_AddAndRemoveEndpoints()
    {
        var manager = new MultiCollectorManager();
        var provider = new CollectorTelemetryDataProvider(manager);
        var vm = new MadTOM.ViewModels.CollectorSettingsViewModel(manager, provider);

        Assert.Single(vm.Endpoints);

        vm.NewName = "Secondary";
        vm.NewAddress = "10.10.10.10:50051";
        vm.AddCollectorCommand.Execute(null);

        Assert.Equal(2, vm.Endpoints.Count);
        Assert.Contains(vm.Endpoints, e => e.Address == "10.10.10.10:50051");

        vm.RemoveCollectorCommand.Execute("10.10.10.10:50051");
        Assert.Single(vm.Endpoints);

        provider.Dispose();
    }

    [Fact]
    public void CollectorTelemetryDataProvider_GetClusterSummary_WorksWithEmptyAndDiscoveredNodes()
    {
        var manager = new MultiCollectorManager();
        var provider = new CollectorTelemetryDataProvider(manager);

        var summary = provider.GetClusterSummary();
        Assert.Equal(0, summary.HostCount);
        Assert.Equal(0, summary.GanderCount);
        Assert.Equal(0, summary.GoslingCount);

        provider.Dispose();
    }

    [Fact]
    public async Task EndToEnd_DaemonToCollectorToCSharpClient_Integration()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "MADTOM_GOLANG"))) root = root.Parent;
        Assert.NotNull(root);
        string collectorBin = System.IO.Path.Combine(root.FullName, "MADTOM_GOLANG", "bin", "madtom-collector");
        string daemonBin = System.IO.Path.Combine(root.FullName, "MADTOM_GOLANG", "bin", "madtom-daemon");
        Assert.True(System.IO.File.Exists(collectorBin) && System.IO.File.Exists(daemonBin), "Build both Go binaries before running the cross-language integration test (see MADTOM_GOLANG/README.md).");

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "madtom_e2e_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        string collectorDb = System.IO.Path.Combine(tempDir, "db");
        string daemonWal = System.IO.Path.Combine(tempDir, "wal");

        int testPort = 50065;

        var collectorProc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = collectorBin,
                Arguments = $"-name \"Lab Gateway\" -port {testPort} -data-dir \"{collectorDb}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        collectorProc.Start();

        var daemonProc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = daemonBin,
                Arguments = $"-node-id test-e2e-node -mode push -collector 127.0.0.1:{testPort} -spool-dir \"{daemonWal}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        daemonProc.Start();

        try
        {
            var manager = new MultiCollectorManager();
            manager.RemoveCollector("127.0.0.1:50051");
            manager.AddCollector("Lab Gateway", $"127.0.0.1:{testPort}");

            var provider = new CollectorTelemetryDataProvider(manager);

            FleetNodeModel? discovered = null;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(300);
                var nodes = provider.GetFleetNodes();
                if (nodes.Count > 0)
                {
                    discovered = nodes[0];
                    break;
                }
            }

            Assert.NotNull(discovered);
            Assert.Equal("test-e2e-node", discovered.Id);
            Assert.Equal("Lab Gateway", discovered.CollectorName);
            await using var client = new CollectorClientService($"127.0.0.1:{testPort}");
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var sample in client.SubscribeLiveAsync("test-e2e-node", timeout.Token))
            {
                Assert.NotNull(sample.Metrics.Cpu);
                Assert.NotEmpty(sample.Metrics.Cpu.PerCorePct);
                Assert.NotEmpty(sample.Metrics.CpuModel);
                Assert.True(sample.Metrics.ProcessesAvailable);
                Assert.NotEmpty(sample.Metrics.Processes);
                break;
            }
            var history = await client.QueryRangeAsync("test-e2e-node", "cpu.total", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, ct: timeout.Token);
            Assert.NotEmpty(history);

            provider.Dispose();
            await manager.DisposeAsync();
        }
        finally
        {
            try { daemonProc.Kill(); } catch { }
            try { collectorProc.Kill(); } catch { }
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }
}

