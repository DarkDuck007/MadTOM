using MADTOM.Plugins.SlideForge.Proto.V1;
using MADTOM.Plugins.SlideForge.Services;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Tests.Plugins.SlideForge;

public class SfOverviewViewModelTestsTimeframe
{
    private const long BaseTs = 1_700_000_000_000_000_000L;
    private const long Sec = 1_000_000_000L;

    private static SfOverviewViewModel MakeVm() =>
        new(new SfRpc(new FakeSlideForgeClient(), "node-a"));

    private static SfTelemetrySample MakeSample(long tsNano, double active, double queued)
    {
        var s = new SfTelemetrySample { TimestampUnixNano = tsNano };
        s.Metrics["jobs_active"] = active;
        s.Metrics["jobs_queued"] = queued;
        return s;
    }

    [Fact]
    public void Default_timeframe_is_5m_and_all_options_present()
    {
        var vm = MakeVm();
        Assert.Equal("5m", vm.SelectedTimeframe.Label);
        Assert.Equal(300, vm.SelectedTimeframe.Seconds);
        Assert.Equal(
            new[] { ("1m", 60), ("5m", 300), ("15m", 900), ("1h", 3600), ("6h", 21600), ("24h", 86400) },
            vm.Timeframes.Select(t => (t.Label, t.Seconds)).ToArray());
    }

    [Fact]
    public void Window_start_and_end_follow_selected_timeframe()
    {
        var vm = MakeVm();
        long ts = BaseTs + 100 * Sec;
        vm.ApplyTelemetry(MakeSample(ts, 2, 1));

        Assert.Equal(ts, vm.WindowEnd);
        Assert.Equal(ts - 300 * Sec, vm.WindowStart);

        foreach (var tf in vm.Timeframes)
        {
            vm.SelectedTimeframe = tf;
            Assert.Equal(ts, vm.WindowEnd);
            Assert.Equal(ts - tf.Seconds * Sec, vm.WindowStart);
        }
    }

    [Fact]
    public void One_minute_timeframe_only_shows_points_in_last_60_seconds()
    {
        var vm = MakeVm();
        for (int i = 0; i < 200; i++)
        {
            vm.ApplyTelemetry(MakeSample(BaseTs + i * Sec, i % 5, i % 3));
        }

        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "1m");

        var activeSeries = vm.JobsSeries[0];
        var queuedSeries = vm.JobsSeries[1];

        Assert.Equal(BaseTs + 199 * Sec, vm.WindowEnd);
        Assert.Equal(BaseTs + 139 * Sec, vm.WindowStart);
        Assert.Equal(61, activeSeries.Timestamps.Length);
        Assert.Equal(61, queuedSeries.Timestamps.Length);
        Assert.All(activeSeries.Timestamps, t => Assert.InRange(t, vm.WindowStart, vm.WindowEnd));
        Assert.All(queuedSeries.Timestamps, t => Assert.InRange(t, vm.WindowStart, vm.WindowEnd));
    }

    [Fact]
    public void One_hour_and_longer_use_coarse_ring_and_bound_series_length()
    {
        var vm = MakeVm();
        // Feed 7200 samples (2 hours at 1s), with values alternating 2 and 4 -> 10s bucket average = 3.0
        for (int i = 0; i < 7200; i++)
        {
            double val = (i % 2 == 0) ? 2.0 : 4.0;
            vm.ApplyTelemetry(MakeSample(BaseTs + i * Sec, val, 1.0));
        }

        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "1h");
        var active1h = vm.JobsSeries[0];
        Assert.InRange(active1h.Timestamps.Length, 1, SfOverviewViewModel.MaxChartPoints);
        Assert.Equal(active1h.Timestamps.Length, active1h.Values.Length);
        // Coarse buckets for 1h = ~360 buckets; full buckets average to 3.0
        Assert.Equal(3.0, active1h.Values[10], 3);
        Assert.Equal(0, active1h.Timestamps[0] % SfOverviewViewModel.CoarseBucketNano);
    }

    [Fact]
    public void Memory_bound_under_100k_samples()
    {
        var vm = MakeVm();
        for (int i = 0; i < 100_000; i++)
        {
            vm.ApplyTelemetry(MakeSample(BaseTs + i * Sec, i % 10, i % 4));
        }

        Assert.True(vm.ActiveCoarseBucketCount <= SfOverviewViewModel.MaxCoarseBuckets,
            $"Active coarse ring exceeded bound: {vm.ActiveCoarseBucketCount}");
        Assert.True(vm.QueuedCoarseBucketCount <= SfOverviewViewModel.MaxCoarseBuckets,
            $"Queued coarse ring exceeded bound: {vm.QueuedCoarseBucketCount}");
        Assert.True(vm.ActiveJobsHistory.Count <= SfOverviewViewModel.MaxRawPoints,
            $"Active raw ring exceeded bound: {vm.ActiveJobsHistory.Count}");
        Assert.True(vm.QueuedJobsHistory.Count <= SfOverviewViewModel.MaxRawPoints,
            $"Queued raw ring exceeded bound: {vm.QueuedJobsHistory.Count}");

        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "24h");
        Assert.True(vm.JobsSeries[0].Timestamps.Length <= SfOverviewViewModel.MaxChartPoints);
        Assert.True(vm.JobsSeries[1].Timestamps.Length <= SfOverviewViewModel.MaxChartPoints);
        Assert.True(vm.JobsSeries[0].Timestamps.Length > 0);
    }

    [Fact]
    public void Switching_timeframe_rebuilds_series_immediately()
    {
        var vm = MakeVm();
        for (int i = 0; i < 4000; i++)
        {
            vm.ApplyTelemetry(MakeSample(BaseTs + i * Sec, 1.0, 2.0));
        }

        // Default 5m -> raw points in last 300s (301 points)
        Assert.Equal(301, vm.JobsSeries[0].Timestamps.Length);

        // Switch to 1m -> 61 raw points
        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "1m");
        Assert.Equal(61, vm.JobsSeries[0].Timestamps.Length);

        // Switch to 15m -> 900 raw points
        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "15m");
        Assert.Equal(900, vm.JobsSeries[0].Timestamps.Length);

        // Switch to 1h -> coarse buckets (~360 points <= 600)
        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "1h");
        Assert.InRange(vm.JobsSeries[0].Timestamps.Length, 350, 365);

        // Switch back to 1m -> 61 raw points again
        vm.SelectedTimeframe = vm.Timeframes.Single(t => t.Label == "1m");
        Assert.Equal(61, vm.JobsSeries[0].Timestamps.Length);
    }
}
