using MadTOM.Services;

namespace MadTOM.Tests;

public class ClientCompressionDiagnosticsTests
{
    [Fact]
    public void ConcurrentPayloadAccountingDoesNotLoseUpdates()
    {
        var counter = new ClientPayloadCounter();
        Parallel.For(0, 10000, _ => counter.Record(127));
        Assert.Equal(10000, counter.Messages);
        Assert.Equal(1270000, counter.Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => counter.Record(-1));
        Assert.Equal(1270000, counter.Bytes);
    }

    [Fact]
    public void UncompressedCacheUsageIsNeverReportedAsZstdMemory()
    {
        var now = DateTime.UtcNow;
        var cache = new TelemetryHistoryCache(60, () => now);
        cache.Record("collector", "node", new DateTimeOffset(now).ToUnixTimeMilliseconds() * 1000000,
            new Dictionary<string, double> { ["cpu"] = 12 });
        var snapshot = ClientCompressionSnapshot.Capture(cache, Array.Empty<CollectorClientService>());
        Assert.Equal("0 B", snapshot.Summary);
        Assert.All(snapshot.Rows, row => Assert.Equal("0 B", row.Zstd));
        Assert.All(snapshot.Rows, row => Assert.Equal("—", row.Ratio));
        Assert.NotEqual("~0 B", snapshot.Rows[0].TotalPayload);
        cache.Clear();
        Assert.Equal("~0 B", ClientCompressionSnapshot.Capture(cache, Array.Empty<CollectorClientService>()).Rows[0].TotalPayload);
    }

    [Fact]
    public async Task PerCollectorCountersSurviveChannelResetAndRemoteDataStaysUnknown()
    {
        await using var first = new CollectorClientService("http://127.0.0.1:51001");
        await using var second = new CollectorClientService("http://127.0.0.1:51002");
        first.ReceivedPayloads.Single(p => p.Name == "live RX").Counter.Record(2048);
        second.ReceivedPayloads.Single(p => p.Name == "live RX").Counter.Record(1024);
        first.ResetChannel();
        var snapshot = ClientCompressionSnapshot.Capture(new TelemetryHistoryCache(), new[] { first, second });
        Assert.Equal("2 KiB", snapshot.Rows.Single(r => r.Scope == first.Endpoint + " · live RX").RawPassed);
        Assert.Equal("1 KiB", snapshot.Rows.Single(r => r.Scope == second.Endpoint + " · live RX").RawPassed);
        Assert.All(snapshot.Rows.Where(r => r.Scope.Contains("daemon → Collector")), row =>
        {
            Assert.Equal("Not reported", row.State);
            Assert.Equal("—", row.Zstd);
            Assert.Equal("—", row.Ratio);
        });
    }

    [Fact]
    public async Task RemovedCollectorsDisappearFromDiagnostics()
    {
        await using var manager = new MultiCollectorManager(":memory:");
        manager.AddCollector("second", "127.0.0.1:51003");
        var cache = new TelemetryHistoryCache();
        Assert.Contains(manager.GetCompressionDiagnostics(cache).Rows, row => row.Scope.Contains("51003"));
        manager.RemoveCollector("127.0.0.1:51003");
        Assert.DoesNotContain(manager.GetCompressionDiagnostics(cache).Rows, row => row.Scope.Contains("51003"));
        Assert.Equal("Unknown", ClientCompressionSnapshot.Unavailable.Summary);
    }

    [Fact]
    public void TransportRatioUsesOnlyCompressedPayloadAndOlderCollectorsStayUnknown()
    {
        var response = new MADTOM.Plugins.Telemetry.Proto.V1.ListNodesResponse();
        Assert.Equal("Not reported", ClientCompressionSnapshot.TransportRows("c", response)[0].State);
        response.TransportStatsSupported = true;
        Assert.Equal("Awaiting data", ClientCompressionSnapshot.TransportRows("c", response)[0].State);
        response.TransportStats.Add(new MADTOM.Plugins.Telemetry.Proto.V1.TransportCompressionStats
        {
            NodeId = "n", Mode = "PUSH", Batches = 10, ZstdBatches = 3,
            DecodedZstdBytes = 4096, ZstdBytes = 1024, RawBytes = 2048, RawBytesSupported = true
        });
        var row = ClientCompressionSnapshot.TransportRows("c", response)[0];
        Assert.Equal("Zstd observed", row.State);
        Assert.Equal("4.00×", row.Ratio);
        Assert.Equal("3/10", row.Count);
        Assert.Equal("1 KiB", row.Zstd);
        Assert.Equal("2 KiB", row.RawPassed);
        Assert.Equal("3 KiB", row.TotalPayload);
        response.TransportStats[0].ZstdBytes = 0;
        Assert.Equal("—", ClientCompressionSnapshot.TransportRows("c", response)[0].Ratio);
    }
}
