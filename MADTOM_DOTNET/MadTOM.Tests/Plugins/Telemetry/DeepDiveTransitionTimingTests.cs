using System;
using System.Collections.Generic;
using System.Threading;
using MadTOM.Services;
using Xunit;

namespace MadTOM.Tests;

public class DeepDiveTransitionTimingTests : IDisposable
{
    private readonly List<DeepDiveTransitionTracker.TransitionRecord> _records = new();

    public DeepDiveTransitionTimingTests()
    {
        DeepDiveTransitionTracker.SetEnabledForTesting(true);
        DeepDiveTransitionTracker.OutputSink = obj =>
        {
            if (obj is DeepDiveTransitionTracker.TransitionRecord rec)
            {
                _records.Add(rec);
            }
        };
    }

    public void Dispose()
    {
        DeepDiveTransitionTracker.OutputSink = null;
        DeepDiveTransitionTracker.SetEnabledForTesting(false);
    }

    [Fact]
    public void FullLifecycle_EmitsCompletedRecordWithAllMetrics()
    {
        DeepDiveTransitionTracker.Begin("test-node-1", fromView: "fleet");
        Assert.True(DeepDiveTransitionTracker.IsActive);

        using (DeepDiveTransitionTracker.MeasureStep("navigation.view-switch"))
        {
            Thread.Sleep(5);
        }

        using (DeepDiveTransitionTracker.MeasureStep("host-detail.specs"))
        {
            Thread.Sleep(2);
        }

        using (DeepDiveTransitionTracker.MeasureStep("metrics.load-layout"))
        {
            Thread.Sleep(3);
        }

        DeepDiveTransitionTracker.MarkQueryStart();
        Thread.Sleep(5);
        DeepDiveTransitionTracker.RecordQueryStats(rpcCount: 3, cacheHits: 2, transformMs: 4.5);
        DeepDiveTransitionTracker.MarkDataReady();

        DeepDiveTransitionTracker.RecordBatchPublish(1.5);
        DeepDiveTransitionTracker.ExpectCharts(new[] { "CPU Breakdown", "Memory Breakdown" });

        // First chart rendered - should remain active
        DeepDiveTransitionTracker.RecordChartRendered("CPU Breakdown");
        Assert.True(DeepDiveTransitionTracker.IsActive);
        Assert.Empty(_records);

        // Second chart rendered - should trigger completion
        DeepDiveTransitionTracker.RecordChartRendered("Memory Breakdown");
        Assert.False(DeepDiveTransitionTracker.IsActive);
        Assert.Single(_records);

        var record = _records[0];
        Assert.Equal("deepdive-transition", record.Kind);
        Assert.Equal("test-node-1", record.TargetHost);
        Assert.Equal("fleet", record.FromView);
        Assert.Equal("completed", record.Outcome);
        Assert.True(record.TotalWallMs >= 10);
        Assert.True(record.ViewSwitchMs >= 3);
        Assert.True(record.HostSpecsMs >= 1);
        Assert.True(record.LayoutLoadMs >= 2);
        Assert.True(record.DataQueryWallMs >= 4);
        Assert.Equal(3, record.RpcCount);
        Assert.Equal(2, record.CacheHits);
        Assert.Equal(4.5, record.TransformMs, precision: 1);
        Assert.Equal(1.5, record.BatchPublishMs, precision: 1);
        Assert.Equal(2, record.ExpectedChartsCount);
        Assert.Equal(2, record.RenderedChartsCount);
    }

    [Fact]
    public void NewTransition_SupersedesPendingTransition()
    {
        DeepDiveTransitionTracker.Begin("node-alpha", fromView: "fleet");
        Assert.True(DeepDiveTransitionTracker.IsActive);

        // User rapidly clicks another node before charts finish
        DeepDiveTransitionTracker.Begin("node-beta", fromView: "sidebar");
        Assert.True(DeepDiveTransitionTracker.IsActive);

        Assert.Single(_records);
        Assert.Equal("node-alpha", _records[0].TargetHost);
        Assert.Equal("superseded", _records[0].Outcome);

        DeepDiveTransitionTracker.Cancel("navigated-away");
        Assert.Equal(2, _records.Count);
        Assert.Equal("node-beta", _records[1].TargetHost);
        Assert.Equal("navigated-away", _records[1].Outcome);
    }

    [Fact]
    public void Cancel_EmitsCancelledRecord()
    {
        DeepDiveTransitionTracker.Begin("node-gamma", fromView: "dropdown");
        DeepDiveTransitionTracker.Cancel("operation-canceled");

        Assert.False(DeepDiveTransitionTracker.IsActive);
        Assert.Single(_records);
        Assert.Equal("node-gamma", _records[0].TargetHost);
        Assert.Equal("dropdown", _records[0].FromView);
        Assert.Equal("operation-canceled", _records[0].Outcome);
    }

    [Fact]
    public void Disabled_DoesNotTrack()
    {
        DeepDiveTransitionTracker.SetEnabledForTesting(false);
        DeepDiveTransitionTracker.Begin("node-delta", fromView: "fleet");

        Assert.False(DeepDiveTransitionTracker.IsActive);
        Assert.Null(DeepDiveTransitionTracker.MeasureStep("test-step"));
        Assert.Empty(_records);
    }
}

