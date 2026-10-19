using System;
using MADTOM.Media.Contracts;
using Xunit;

namespace MadTOM.Tests.Core;

public class MediaContractsTests
{
    [Fact]
    public void AssetManifest_RenditionOverlap_PassesWithin3Seconds()
    {
        var now = DateTime.UtcNow;
        var manifest = new AssetManifest
        {
            FeedId = "test-feed",
            StartUtc = now,
            EndUtc = now.AddMinutes(5),
            Renditions = new()
            {
                new RenditionInfo
                {
                    Role = "original",
                    StartUtc = now,
                    EndUtc = now.AddMinutes(5).AddSeconds(1),
                    DurationMs = 301000
                },
                new RenditionInfo
                {
                    Role = "proxy",
                    StartUtc = now.AddSeconds(1),
                    EndUtc = now.AddMinutes(5),
                    DurationMs = 299000
                }
            }
        };

        // Within 3 seconds tolerance (AT6)
        Assert.True(manifest.VerifyRenditionOverlap(TimeSpan.FromSeconds(3)));

        // Mismatched by > 5 seconds should fail
        manifest.Renditions[1].EndUtc = now.AddMinutes(5).AddSeconds(10);
        Assert.False(manifest.VerifyRenditionOverlap(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ClockSyncCalculator_CalculatesOffsetAndRttAccurately()
    {
        // Suppose client sends at 1000ms.
        // Server receives at 1050ms (network delay 40ms, server clock is +10ms relative to client).
        // Server replies at 1060ms (10ms server processing).
        // Client receives at 1100ms (network delay 40ms).
        long t1 = 1000;
        long t2 = 1050;
        long t3 = 1060;
        long t4 = 1100;

        var (offset, rtt) = ClockSyncCalculator.Calculate(t1, t2, t3, t4);

        // Expected: RTT = (1100 - 1000) - (1060 - 1050) = 100 - 10 = 90ms.
        // Expected: Offset = ((1050 - 1000) + (1060 - 1100)) / 2 = (50 - 40) / 2 = +5ms.
        Assert.Equal(90, rtt);
        Assert.Equal(5.0, offset);
    }
}
