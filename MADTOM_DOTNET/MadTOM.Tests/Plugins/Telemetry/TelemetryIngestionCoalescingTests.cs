using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MadTOM.Models;
using MadTOM.Services;
using MADTOM.Plugins.Telemetry.Proto.V1;
using Xunit;

namespace MadTOM.Tests;

public class TelemetryIngestionCoalescingTests
{
    private static long Nano(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;

    [Fact]
    public void PushSparkline_InitializesToFullCapacityWithLastElement()
    {
        double[] empty = Array.Empty<double>();
        var result = CollectorTelemetryDataProvider.PushSparkline(empty, 42.5, 60);

        Assert.Equal(60, result.Length);
        Assert.Equal(42.5, result[^1]);
        Assert.Equal(0.0, result[0]);
    }

    [Fact]
    public void PushSparkline_ShiftsLeftAndAppendsNewValue()
    {
        double[] current = Enumerable.Range(1, 60).Select(i => (double)i).ToArray();
        var next = CollectorTelemetryDataProvider.PushSparkline(current, 99.0, 60);

        Assert.Equal(60, next.Length);
        Assert.Equal(2.0, next[0]);
        Assert.Equal(60.0, next[58]);
        Assert.Equal(99.0, next[59]);
    }

    [Fact]
    public void PushSparkline_PartialArrayPadsAndShiftsCorrectly()
    {
        double[] partial = new double[] { 10.0, 20.0, 30.0 };
        var next = CollectorTelemetryDataProvider.PushSparkline(partial, 40.0, 60);

        Assert.Equal(60, next.Length);
        Assert.Equal(0.0, next[0]);
        Assert.Equal(10.0, next[56]);
        Assert.Equal(20.0, next[57]);
        Assert.Equal(30.0, next[58]);
        Assert.Equal(40.0, next[59]);
    }

    [Fact]
    public void IngestionRecordsIntoHistoryCacheWithoutUiDispatcherExecution()
    {
        var manager = new MultiCollectorManager(":memory:");
        var cache = new TelemetryHistoryCache(60);
        var provider = new CollectorTelemetryDataProvider(manager, cache);

        var node = new FleetNodeModel
        {
            Id = "node-alpha",
            CollectorEndpoint = "127.0.0.1:50051",
            GroupName = "Default"
        };

        var now = DateTime.UtcNow;
        var metrics = new SystemMetrics
        {
            TimestampUnixNano = Nano(now),
            Cpu = new CpuMetrics { TotalPct = 75.5, UserPct = 50.0, SystemPct = 25.5 },
            Memory = new MemoryMetrics { MemTotalBytes = 16L * 1024 * 1024 * 1024, MemAvailableBytes = 8L * 1024 * 1024 * 1024 }
        };

        // Ingest sample
        provider.IngestMetricsForTesting(node, metrics);

        // Verify that HistoryCache was immediately populated in background without needing UI drain
        var usage = cache.GetUsage(60);
        Assert.True(usage.LivePoints > 0, "HistoryCache should contain live points immediately upon ingestion.");
        Assert.True(node.LatestMetricValues.ContainsKey("cpu.total"));
        Assert.Equal(75.5, node.LatestMetricValues["cpu.total"]);

        // Now drain display updates and verify FleetNodeModel properties update
        provider.FlushDisplayUpdatesForTesting();
        Assert.Equal(75.5, node.CpuAvgPct);
        Assert.Equal(60, node.SparkCpu.Length);
        Assert.Equal(75.5, node.SparkCpu[^1]);
        Assert.Equal("online", node.Status);

        provider.Dispose();
    }

    [Fact]
    public void RapidMultiSampleBurstCoalescesDisplayWhileRecordingAllHistory()
    {
        var manager = new MultiCollectorManager(":memory:");
        var cache = new TelemetryHistoryCache(60);
        var provider = new CollectorTelemetryDataProvider(manager, cache);

        var node = new FleetNodeModel
        {
            Id = "node-beta",
            CollectorEndpoint = "127.0.0.1:50051",
            GroupName = "Default"
        };

        int updateNotificationCount = 0;
        provider.NodeTelemetryUpdated += (_, n) =>
        {
            if (n.Id == node.Id) updateNotificationCount++;
        };

        var baseTime = DateTime.UtcNow;
        // Send 5 rapid samples without pumping UI dispatcher
        for (int i = 1; i <= 5; i++)
        {
            var m = new SystemMetrics
            {
                TimestampUnixNano = Nano(baseTime.AddSeconds(i)),
                Cpu = new CpuMetrics { TotalPct = 10.0 * i }
            };
            provider.IngestMetricsForTesting(node, m);
        }

        // All 5 samples must be in HistoryCache
        var usage = cache.GetUsage(60);
        Assert.True(usage.LivePoints >= 5, "All burst samples must be recorded in history.");

        // Drain coalesced display update
        provider.FlushDisplayUpdatesForTesting();

        // Node state reflects the LAST sample (50.0%)
        Assert.Equal(50.0, node.CpuAvgPct);
        Assert.Equal(50.0, node.SparkCpu[^1]);
        Assert.Equal(1, updateNotificationCount);

        provider.Dispose();
    }

    [Fact]
    public void CacheRecordDoesNotPruneUnsealedBlocksOnEverySample()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var cache = new TelemetryHistoryCache(1, () => now); // 1 minute retention

        // Record 10 points within a single block (BlockSize is 256)
        for (int i = 0; i < 10; i++)
        {
            cache.Record("col", "node", Nano(now.AddSeconds(i)), new Dictionary<string, double> { ["m"] = i });
        }

        var usage = cache.GetUsage(1);
        Assert.Equal(10, usage.LivePoints);

        // Advance clock past 1 minute cutoff
        now = now.AddMinutes(2);

        // Record 1 more sample without sealing a block (Count is 11 < 256)
        cache.Record("col", "node", Nano(now), new Dictionary<string, double> { ["m"] = 100 });

        // Points in tail should not be eagerly stripped on this tick because BlocksCount == oldBlocks == 0
        var usageAfter = cache.RecalculateSlow();
        Assert.Equal(11, usageAfter.LivePoints);

        // But explicit Prune() does clean them up
        cache.Prune();
        var usagePruned = cache.RecalculateSlow();
        Assert.Equal(1, usagePruned.LivePoints);
    }
}
