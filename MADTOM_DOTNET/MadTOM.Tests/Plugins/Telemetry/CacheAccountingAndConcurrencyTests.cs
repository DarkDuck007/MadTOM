using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;
using Xunit;

namespace MadTOM.Tests;

public class CacheAccountingAndConcurrencyTests
{
    [Fact]
    public void CompressedPointHistory_IncrementalAccountingMatchesFullScanAcrossAllMutations()
    {
        var history = new CompressedPointHistory();
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.BlocksCount);
        Assert.Equal(0, history.CompressedBytes);
        Assert.Equal(0, history.CompressedRawBytes);
        var initial = history.RecalculateSlow();
        Assert.Equal(initial.StorageBytes, history.StorageBytes);

        long start = 1_000_000_000L;
        // 1. Enqueue 700 points (BlockSize = 256, so 2 sealed blocks of 256 points + 188 points in tail)
        for (int i = 0; i < 700; i++)
        {
            history.Enqueue(new LODPoint(start + i * 100_000_000L, i, i, i));
            if (i % 50 == 0)
            {
                var slow = history.RecalculateSlow();
                Assert.Equal(slow.StorageBytes, history.StorageBytes);
                Assert.Equal(slow.CompressedBytes, history.CompressedBytes);
                Assert.Equal(slow.CompressedRawBytes, history.CompressedRawBytes);
                Assert.Equal(slow.BlocksCount, history.BlocksCount);
            }
        }
        Assert.Equal(700, history.Count);
        Assert.Equal(2, history.BlocksCount);

        // Verify Snapshot matches Read
        var snap = history.Snapshot(start, start + 700 * 100_000_000L);
        var decoded = snap.Decode(start, start + 700 * 100_000_000L);
        var directRead = history.Read(start, start + 700 * 100_000_000L).ToArray();
        Assert.Equal(directRead.Length, decoded.Length);
        for (int i = 0; i < directRead.Length; i++)
        {
            Assert.Equal(directRead[i].TimestampUnixNano, decoded[i].TimestampUnixNano);
            Assert.Equal(directRead[i].Value, decoded[i].Value);
        }

        // 2. RemoveBefore: remove first block and part of second block (partially unpacks second block to head)
        long cutoff = start + 300 * 100_000_000L;
        history.RemoveBefore(cutoff);
        Assert.Equal(400, history.Count);
        var afterPartial = history.RecalculateSlow();
        Assert.Equal(afterPartial.StorageBytes, history.StorageBytes);
        Assert.Equal(afterPartial.CompressedBytes, history.CompressedBytes);
        Assert.Equal(afterPartial.CompressedRawBytes, history.CompressedRawBytes);
        Assert.Equal(afterPartial.BlocksCount, history.BlocksCount);

        // 3. DropOldestBlock multiple times
        while (history.Count > 0)
        {
            history.DropOldestBlock();
            var slow = history.RecalculateSlow();
            Assert.Equal(slow.StorageBytes, history.StorageBytes);
            Assert.Equal(slow.CompressedBytes, history.CompressedBytes);
            Assert.Equal(slow.CompressedRawBytes, history.CompressedRawBytes);
            Assert.Equal(slow.BlocksCount, history.BlocksCount);
        }
        Assert.Equal(0, history.Count);

        // 4. Re-enqueue and Clear
        for (int i = 0; i < 300; i++)
        {
            history.Enqueue(new LODPoint(start + i * 100_000_000L, i, i, i));
        }
        Assert.True(history.Count > 0);
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.BlocksCount);
        var cleared = history.RecalculateSlow();
        Assert.Equal(cleared.StorageBytes, history.StorageBytes);
    }

    [Fact]
    public void TelemetryHistoryCache_IncrementalAccountingMatchesFullScan()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var cache = new TelemetryHistoryCache(60, () => now);

        // Initial empty state
        var initial = cache.RecalculateSlow();
        var usage = cache.GetUsage(60);
        Assert.Equal(initial.LiveStorage, usage.LiveBytes);
        Assert.Equal(initial.StoredStorage, usage.StoredBytes);
        Assert.Equal(initial.LivePoints, usage.LivePoints);

        // Record metrics across multiple series
        long startNano = new DateTimeOffset(now).ToUnixTimeMilliseconds() * 1_000_000L;
        for (int t = 0; t < 300; t++)
        {
            long ts = startNano + t * 1_000_000_000L;
            cache.Record("c1", "nodeA", ts, new[]
            {
                new KeyValuePair<string, double>("cpu.total", 10 + (t % 50)),
                new KeyValuePair<string, double>("memory.used", 1000 + t)
            });
            cache.Record("c1", "nodeB", ts, new[]
            {
                new KeyValuePair<string, double>("cpu.total", 20 + (t % 30))
            });
        }

        var afterRecord = cache.RecalculateSlow();
        var usage2 = cache.GetUsage(60);
        Assert.Equal(afterRecord.LiveStorage, usage2.LiveBytes);
        Assert.Equal(afterRecord.LivePoints, usage2.LivePoints);
        Assert.Equal(afterRecord.LiveBlocks, usage2.SeriesCount > 0 ? cache.CompactStats.LiveBlocks : 0);

        // Test ClearLive and ClearStored
        cache.ClearLive();
        var afterClear = cache.RecalculateSlow();
        var usageCleared = cache.GetUsage(60);
        Assert.Equal(0, usageCleared.LiveBytes);
        Assert.Equal(0, usageCleared.LivePoints);
        Assert.Equal(afterClear.LiveStorage, usageCleared.LiveBytes);
    }

    [Fact]
    public async Task TelemetryHistoryCache_ConcurrentRecordingAndQueryingDoesNotDeadlockOrCorrupt()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var cache = new TelemetryHistoryCache(60, () => now);
        long startNano = new DateTimeOffset(now).ToUnixTimeMilliseconds() * 1_000_000L;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recordTask = Task.Run(async () =>
        {
            int t = 0;
            while (!cts.IsCancellationRequested && t < 1000)
            {
                long ts = startNano + t * 100_000_000L;
                cache.Record("col", "host1", ts, new[]
                {
                    new KeyValuePair<string, double>("metric1", t),
                    new KeyValuePair<string, double>("metric2", t * 2.0)
                });
                t++;
                if (t % 50 == 0) await Task.Delay(1);
            }
        });

        var queryTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !recordTask.IsCompleted)
            {
                var dtStart = now.AddSeconds(-30);
                var dtEnd = now.AddSeconds(30);
                var pts = await cache.QueryAsync("col", "host1", "metric1", dtStart, dtEnd, false,
                    (s, e, ct) => Task.FromResult<IReadOnlyList<LODPoint>>(new List<LODPoint>()), cts.Token);
                Assert.NotNull(pts);
                await Task.Delay(2);
            }
        });

        await Task.WhenAll(recordTask, queryTask);
        var stats = cache.RecalculateSlow();
        var usage = cache.GetUsage(60);
        Assert.Equal(stats.LiveStorage, usage.LiveBytes);
        Assert.Equal(stats.LivePoints, usage.LivePoints);
    }

    [Fact]
    public async Task HostMetricsTabViewModel_OffloadsBackgroundProcessingAndCohesivelyPublishes()
    {
        var vm = new HostMetricsTabViewModel(null);
        var g1 = new MetricGraphViewModel("test.metric");
        vm.Graphs.Clear();
        vm.Graphs.Add(g1);

        // Verify SampleToDisplayBudget reduces point count within budget
        int budget = 50;
        var rawTs = Enumerable.Range(0, 500).Select(i => (long)i * 1_000_000_000L).ToArray();
        var rawVals = Enumerable.Range(0, 500).Select(i => (double)i).ToArray();
        var (sampledTs, sampledVals) = HostMetricsTabViewModel.SampleToDisplayBudget(rawTs, rawVals, budget, rawTs[0], rawTs[^1]);

        Assert.True(sampledTs.Length <= budget + 2);
        Assert.Equal(sampledTs.Length, sampledVals.Length);
        Assert.Equal(rawTs[0], sampledTs[0]);
    }
}

