import json
import unittest
from summarize_ui_performance import summarize


def line(**data):
    return '[ui-performance] ' + json.dumps(data)


class SummaryTests(unittest.TestCase):
    def test_scope_grouping_and_cancelled_runs(self):
        rows = []
        for second, outcome, ms in [(0, 'complete', 10), (1, 'complete', 20), (2, 'cancelled', 500)]:
            rows.append(line(Kind='refresh', Node='node', Range=f'start=2026-09-20T00:00:0{second}Z; end=2026-09-20T00:01:0{second}Z; graphs=6',
                             Outcome=outcome, WallMs=ms, RpcCount=0, Cache={'stored-hit-decoded': 14}))
        result = summarize(rows)
        self.assertIn('complete | n=2 median=15.0ms p95=20.0ms', result)
        self.assertIn('cancelled | n=1', result)
        self.assertIn('storedHits=28', result)

    def test_malformed_lines_and_weighted_interval_mean(self):
        stats = lambda n, mean: dict(Count=n, MeanMs=mean, P95Ms=mean, MaxMs=mean, Over50Ms=0)
        rows = ['unrelated', '[ui-performance] broken']
        for n, ms in [(1, 10), (3, 2)]:
            rows.append(line(Kind='interval', AllocatedBytes=1048576, Seconds=5, DroppedRecords=2, Metrics={'render': stats(n, ms)}))
        result = summarize(rows)
        self.assertIn('n=4 mean=4.00ms', result)
        self.assertIn('0.20 MiB/s', result)
        self.assertIn('malformed lines=1', result)
        self.assertIn('dropped output records=2', result)

    def test_zero_rpc_classification_and_lock_reporting(self):
        rows = [
            line(Kind='refresh', Node='node1', Range='start=2026-09-20T00:00:00Z; end=2026-09-20T00:05:00Z; graphs=6',
                 Outcome='complete', WallMs=8, RpcCount=0, IsZeroRpc=True, LockWaitMs=0.5, LockHoldMs=0.2, DecodedPoints=500,
                 Cache={'live-hit': 6}),
            line(Kind='refresh', Node='node1', Range='start=2026-09-20T00:00:00Z; end=2026-09-20T00:05:00Z; graphs=6',
                 Outcome='complete', WallMs=50, RpcCount=2, IsZeroRpc=False, LockWaitMs=1.0, LockHoldMs=0.4, DecodedPoints=1000,
                 Cache={'miss': 2, 'stored-hit-decoded': 4}),
            line(Kind='interval', AllocatedBytes=2097152, Seconds=5, DroppedRecords=0, Metrics={}, LiveBytes=10485760, StoredBytes=5242880),
        ]
        result = summarize(rows)
        self.assertIn('node1 | 5 min / 6 graphs | zero-rpc | complete | n=1 median=8.0ms', result)
        self.assertIn('node1 | 5 min / 6 graphs | network | complete | n=1 median=50.0ms', result)
        self.assertIn('lockWaitAvg=', result)
        self.assertIn('Peak cache occupancy: live=10.00 MiB, stored=5.00 MiB', result)

    def test_deepdive_transition_summary(self):
        rows = [
            line(Kind='deepdive-transition', TargetHost='la1.node', FromView='fleet', Outcome='completed',
                 TotalWallMs=120.5, ViewSwitchMs=1.5, HostSpecsMs=2.0, LayoutSaveMs=0.5, LayoutLoadMs=3.0,
                 PopulateMetricsMs=1.2, DataQueryWallMs=95.0, RpcCount=4, CacheHits=2, TransformMs=10.0,
                 BatchPublishMs=2.0, RenderAllChartsMs=5.3, ExpectedChartsCount=4, RenderedChartsCount=4,
                 Steps={'navigation.view-switch': 1.5, 'metrics.load-layout': 3.0}),
            line(Kind='deepdive-transition', TargetHost='la1.node', FromView='fleet', Outcome='completed',
                 TotalWallMs=150.0, ViewSwitchMs=2.0, HostSpecsMs=2.5, LayoutSaveMs=0.5, LayoutLoadMs=4.0,
                 PopulateMetricsMs=1.5, DataQueryWallMs=120.0, RpcCount=4, CacheHits=2, TransformMs=12.0,
                 BatchPublishMs=2.5, RenderAllChartsMs=5.0, ExpectedChartsCount=4, RenderedChartsCount=4,
                 Steps={'navigation.view-switch': 2.0, 'metrics.load-layout': 4.0}),
            line(Kind='deepdive-transition', TargetHost='oc1.node', FromView='sidebar', Outcome='cancelled',
                 TotalWallMs=30.0, ViewSwitchMs=1.0, HostSpecsMs=1.0, LayoutSaveMs=0.0, LayoutLoadMs=2.0,
                 PopulateMetricsMs=0.5, DataQueryWallMs=20.0, RpcCount=0, CacheHits=0, TransformMs=0.0,
                 BatchPublishMs=0.0, RenderAllChartsMs=0.0, ExpectedChartsCount=4, RenderedChartsCount=0,
                 Steps={})
        ]
        result = summarize(rows)
        self.assertIn('Deep-dive transition summaries (moment node clicked -> all charts rendered):', result)
        self.assertIn('la1.node (from fleet) | completed | n=2 median=135.2ms', result)
        self.assertIn('charts=8/8', result)
        self.assertIn('viewSwitchAvg=1.75ms', result)
        self.assertIn('saveLayoutAvg=0.50ms', result)
        self.assertIn('loadLayoutAvg=3.50ms', result)
        self.assertIn('oc1.node (from sidebar) | cancelled | n=1', result)


if __name__ == '__main__':
    unittest.main()
