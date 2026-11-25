using System;
using System.Linq;
using System.Threading.Tasks;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;
using MADTOM.Plugins.NoxAI.ViewModels;
using MadTOM.Services;
using Xunit;

namespace MADTOM.Tests.Plugins.NoxAI;

public class TokenTelemetryTests
{
    private const long Sec = 1_000_000_000L;

    [Fact]
    public void FromCumulative_sums_increases_per_bucket_and_handles_restarts()
    {
        long t0 = 1_764_000_000L * Sec; // multiple of 60 s
        long[] ts = { t0, t0 + 5 * Sec, t0 + 12 * Sec, t0 + 15 * Sec, t0 + 25 * Sec, t0 + 41 * Sec };
        // +100 at 5 s, +50 at 12 s, restart at 15 s (new value 30 = increase), +0 at 25 s, +70 at 41 s
        double[] cum = { 1000, 1100, 1150, 30, 30, 100 };
        var (bts, vals) = TokenBuckets.FromCumulative(ts, cum, t0, t0 + 59 * Sec, 10 * Sec);
        Assert.Equal(6, bts.Length);
        Assert.Equal(t0, bts[0]);
        Assert.Equal(new double[] { 100, 80, 0, 0, 70, 0 }, vals);
        Assert.Equal(250, TokenBuckets.Increase(ts, cum, t0, t0 + 59 * Sec));
        // only points inside the window count
        Assert.Equal(70, TokenBuckets.Increase(ts, cum, t0 + 30 * Sec, t0 + 59 * Sec));
    }

    [Fact]
    public void Bucket_size_follows_the_window()
    {
        Assert.Equal(10, TokenBuckets.BucketSecondsFor(300));
        Assert.Equal(30, TokenBuckets.BucketSecondsFor(900));
        Assert.Equal(60, TokenBuckets.BucketSecondsFor(3600));
        Assert.Equal(900, TokenBuckets.BucketSecondsFor(86400));
        Assert.Equal(3600, TokenBuckets.BucketSecondsFor(604800));
        Assert.Equal("30 s", TokenBuckets.BucketLabel(30));
        Assert.Equal("15 min", TokenBuckets.BucketLabel(900));
        Assert.Equal("18.0k", TokenBuckets.Format(18000));
        Assert.Equal("1.25M", TokenBuckets.Format(1_250_000));
    }

    [Fact]
    public async Task Telemetry_tab_charts_uncached_and_cached_tokens_per_bucket()
    {
        await using var manager = new NoxNodeManager();
        var vm = new NoxTelemetryTabViewModel(manager, new TelemetryHistoryCache());
        vm.SelectedTimeRange = "5M";
        Assert.Equal(new[] { "llm.tokens.uncached", "llm.tokens.cached", "llm.tokens.out" },
            vm.TokenSeries.Select(s => s.Metric).ToArray());

        long t0 = 1_764_000_000L * Sec;
        void Feed(long t, double uncached, double cached, double output)
        {
            var s = new NoxTelemetrySample { TimestampUnixNano = t };
            s.Metrics["llm.tokens.in"] = uncached + cached;
            s.Metrics["llm.tokens.uncached"] = uncached;
            s.Metrics["llm.tokens.cached"] = cached;
            s.Metrics["llm.tokens.out"] = output;
            vm.OnTelemetrySampleReceived(s);
        }
        Feed(t0, 18000, 0, 300);
        Feed(t0 + 1 * Sec, 20700, 16000, 500);   // one warm call: 2.7k uncached, 16k cached, 200 out
        Feed(t0 + 2 * Sec, 20700, 16000, 500);

        var uncached = vm.TokenSeries.Single(s => s.Metric == "llm.tokens.uncached");
        var cached = vm.TokenSeries.Single(s => s.Metric == "llm.tokens.cached");
        Assert.Equal(2700, uncached.Values.Sum());
        Assert.Equal(16000, cached.Values.Sum());
        Assert.Contains("per 10 s", vm.TokenChartTitle);
        Assert.Contains("2.7k uncached", vm.TokenWindowSummary);
        Assert.Contains("16.0k cached", vm.TokenWindowSummary);
        Assert.Contains("20.7k uncached", vm.TokenThroughputSummary);
        Assert.Contains("44% cached", vm.TokenThroughputSummary);
    }
}
