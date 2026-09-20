using MadTOM.Models;
using MadTOM.Services;
using MadTOM.ViewModels;

namespace MadTOM.Tests;

public class TelemetryCacheUiTests
{
    private sealed class CachedProvider : ITelemetryDataProvider
    {
        public TelemetryHistoryCache HistoryCache { get; } = new();
        public FleetNodeModel Node { get; } = new() { Id = "node", CollectorEndpoint = "test" };
        public event EventHandler<FleetNodeModel>? NodeTelemetryUpdated { add { } remove { } }
        public event EventHandler<LogEntryModel>? LogReceived { add { } remove { } }
        public IReadOnlyList<FleetNodeModel> GetFleetNodes() => new[] { Node };
        public FleetNodeModel? GetNode(string id) => Node;
        public ClusterTelemetrySummary GetClusterSummary() => new();
        public IReadOnlyList<ProcessInfoModel> GetProcesses(string id) => Array.Empty<ProcessInfoModel>();
        public IReadOnlyList<DropRuleModel> GetDropRules(string id) => Array.Empty<DropRuleModel>();
        public IReadOnlyList<RegionTrafficModel> GetRegions(string id) => Array.Empty<RegionTrafficModel>();
        public Task<IReadOnlyList<LODPoint>> QueryHistoryAsync(string id, string metric, DateTime start, DateTime end, CancellationToken ct = default) =>
            HistoryCache.QueryAsync("test", id, metric, start, end, true, (_, _, _) => throw new Exception("Monitor-only history should be local"), ct);
        public void SendSignal(string id, int pid, int signal) { }
        public void PauseLogs(bool paused) { }
        public void ClearLogs() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task ReopeningDetailGraphsRestoresMonitorOnlyHistory()
    {
        using var provider = new CachedProvider();
        long timestamp = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds() * 1_000_000;
        provider.HistoryCache.Record("test", "node", timestamp, new Dictionary<string, double> { ["cpu.total"] = 12 });
        provider.HistoryCache.Record("test", "node", timestamp + 1_000_000_000, new Dictionary<string, double> { ["cpu.total"] = 34 });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        for (int visit = 0; visit < 2; visit++)
        {
            var view = new HostMetricsTabViewModel(provider, presetStore: new GraphPresetStore(path));
            view.UpdateForNode("node", provider.Node, provider.GetFleetNodes());
            view.SetScope("30m");
            view.Graphs.Clear();
            var graph = new MetricGraphViewModel("cpu.total");
            view.Graphs.Add(graph);
            await view.RefreshHistoryAsync();
            Assert.Equal(new[] { 12d, 34d }, graph.Series[0].Values);
        }
    }

    [Fact]
    public async Task DenseCachedHistoryUsesGraphBudgetWithoutLosingSubsecondDetail()
    {
        using var provider = new CachedProvider();
        long timestamp = DateTimeOffset.UtcNow.AddMinutes(-29).ToUnixTimeMilliseconds() * 1_000_000;
        for (int i = 0; i < 17000; i++)
            provider.HistoryCache.Record("test", "node", timestamp + i * 100_000_000L,
                new Dictionary<string, double> { ["cpu.total"] = i == 1001 ? 999 : i % 10 });
        var view = new HostMetricsTabViewModel(provider, presetStore: new GraphPresetStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        view.UpdateForNode("node", provider.Node, provider.GetFleetNodes());
        view.SetScope("30m");
        view.Graphs.Clear();
        var graph = new MetricGraphViewModel("cpu.total") { HistoryPointBudget = 2250 };
        view.Graphs.Add(graph);
        await view.RefreshHistoryAsync();
        Assert.InRange(graph.Values.Length, 1800, 2250);
        Assert.Contains(999d, graph.Values);
        Assert.Contains(graph.Timestamps, t => t % 1_000_000_000L != 0);
        Assert.Equal(17000, (await provider.QueryHistoryAsync("node", "cpu.total", DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow)).Count);
    }

    [Fact]
    public void PerformanceControlsSaveBothNumericValuesAndRejectInvalidHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "madtom-performance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var provider = new CachedProvider();
            string path = Path.Combine(directory, "performance.json");
            var settings = new GraphPerformanceSettings(path);
            var vm = new CollectorSettingsViewModel(new MultiCollectorManager(":memory:"), provider,
                new NodeGroupStore(Path.Combine(directory, "groups.json")),
                new GlobalMetricsStore(Path.Combine(directory, "metrics.json")),
                new TelemetryCacheSettingsStore(Path.Combine(directory, "cache.json")), settings);
            Assert.Equal(1, vm.GraphPointsPerPixel);
            Assert.Equal(3, vm.HistoryPointsPerPixel);
            vm.GraphPointsPerPixel = 0.5;
            vm.HistoryPointsPerPixel = 5.5;
            vm.ApplyGraphPerformanceCommand.Execute(null);
            var saved = new GraphPerformanceSettings(path);
            Assert.Equal(0.5, saved.PointsPerPixel);
            Assert.Equal(5.5, saved.HistoryPointsPerPixel);
            vm.HistoryPointsPerPixel = 11;
            vm.ApplyGraphPerformanceCommand.Execute(null);
            Assert.Equal(5.5, new GraphPerformanceSettings(path).HistoryPointsPerPixel);
            Assert.Contains("1–10", vm.StatusMessage);
            Assert.Equal(60, provider.HistoryCache.RetentionMinutes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SettingsValidatePersistEstimateAndClearCache()
    {
        string directory = Path.Combine(Path.GetTempPath(), "madtom-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new TelemetryCacheSettingsStore(Path.Combine(directory, "cache.json"));
            Assert.Equal(60, store.LoadMinutes());
            using var provider = new CachedProvider();
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            provider.HistoryCache.Record("test", "node", timestamp, new Dictionary<string, double> { ["cpu.total"] = 1 });
            var vm = new CollectorSettingsViewModel(new MultiCollectorManager(":memory:"), provider,
                new NodeGroupStore(Path.Combine(directory, "groups.json")), new GlobalMetricsStore(Path.Combine(directory, "metrics.json")), store);
            Assert.Equal("60", vm.CacheRetentionMinutes);
            Assert.Contains("MiB", vm.CacheMemoryEstimate);
            vm.LiveCacheLimitMiB = 8;
            vm.StoredCacheLimitMiB = 4;
            vm.StoredCacheRetentionSeconds = 90;
            vm.CacheRetentionMinutes = "120";
            vm.ApplyCacheRetention();
            Assert.Equal(120, provider.HistoryCache.RetentionMinutes);
            Assert.Equal(8 * 1048576L, provider.HistoryCache.LiveLimitBytes);
            Assert.Equal(4, store.Load().StoredLimitMiB);
            Assert.Equal(90, provider.HistoryCache.StoredRetentionSeconds);
            Assert.Contains("MiB", vm.TotalCacheUsage);
            Assert.Equal(120, new TelemetryCacheSettingsStore(Path.Combine(directory, "cache.json")).LoadMinutes());
            vm.CacheRetentionMinutes = "invalid";
            vm.ApplyCacheRetention();
            Assert.Equal(120, provider.HistoryCache.RetentionMinutes);
            Assert.Contains("1–1440", vm.StatusMessage);
            vm.ClearTelemetryCache();
            Assert.Empty(await provider.QueryHistoryAsync("node", "cpu.total", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
            Assert.Equal(120, store.LoadMinutes());
            File.WriteAllText(Path.Combine(directory, "cache.json"), "{\"RetentionMinutes\":-1}");
            Assert.Equal(60, store.LoadMinutes());
            Assert.Equal(64, store.Load().LiveLimitMiB);
            Assert.Equal(32, store.Load().StoredLimitMiB);
            Assert.Equal(30, store.Load().StoredRetentionSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }
}
