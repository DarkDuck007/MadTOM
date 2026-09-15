package ingest

import (
	"sync"
	"testing"

	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestLiveCoalescingPreservesHistory(t *testing.T) {
	db, err := storage.OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	p := NewPipeline(db, registry.NewRegistry("test"), "test")
	defer p.decoder.Close()
	slow, stopSlow := p.Subscribe("node")
	defer stopSlow()
	fast, stopFast := p.Subscribe("node")
	defer stopFast()
	for i := int64(1); i <= 150; i++ {
		if err := p.ProcessBatch(&madtomv1.TelemetryBatch{NodeId: "node", Samples: []*madtomv1.SystemMetrics{{TimestampUnixNano: i, Cpu: &madtomv1.CpuMetrics{TotalPct: float64(i)}}}}, "PUSH"); err != nil {
			t.Fatal(err)
		}
		select {
		case event := <-fast:
			if event.Metrics.TimestampUnixNano != i {
				t.Fatalf("fast subscriber got %d, want %d", event.Metrics.TimestampUnixNano, i)
			}
		default:
			t.Fatal("missing fast subscriber update")
		}
	}
	if len(slow) != 1 {
		t.Fatalf("slow subscriber retains %d snapshots", len(slow))
	}
	if event := <-slow; event.Metrics.TimestampUnixNano != 150 {
		t.Fatalf("stale snapshot: %v", event)
	}
	points, err := db.QueryRange("node", "cpu.total", 1, 150)
	if err != nil || len(points) != 150 {
		t.Fatalf("history lost: %d points, %v", len(points), err)
	}
	for i, point := range points {
		if point.TimestampUnixNano != int64(i+1) || point.Value != float64(i+1) {
			t.Fatalf("changed stored point: %+v", point)
		}
	}

	// Cached state and monotonicity apply to new subscribers as well.
	cached, stopCached := p.Subscribe("node")
	p.fanOutLive("node", &madtomv1.SystemMetrics{TimestampUnixNano: 149})
	p.fanOutLive("node", &madtomv1.SystemMetrics{TimestampUnixNano: 150})
	if len(cached) != 1 {
		t.Fatalf("unexpected cached queue length: %d", len(cached))
	}
	if event := <-cached; event.Metrics.TimestampUnixNano != 150 {
		t.Fatal("cached state regressed")
	}
	stopCached()
	stopCached() // Cleanup is idempotent.
	if _, ok := <-cached; ok {
		t.Fatal("unsubscribed channel still open")
	}
	stopSlow()
	stopFast()
	if len(p.subscribers) != 0 {
		t.Fatal("empty subscriber entry retained")
	}
}

func TestLivePublishConcurrentWithSubscriptionChurn(t *testing.T) {
	p := NewPipeline(nil, nil, "test")
	defer p.decoder.Close()
	var workers sync.WaitGroup
	workers.Add(2)
	go func() {
		defer workers.Done()
		for i := int64(1); i <= 1000; i++ {
			p.fanOutLive("node", &madtomv1.SystemMetrics{TimestampUnixNano: i})
		}
	}()
	go func() {
		defer workers.Done()
		for i := 0; i < 1000; i++ {
			ch, unsubscribe := p.Subscribe("node")
			select {
			case <-ch:
			default:
			}
			unsubscribe()
			unsubscribe()
			for range ch {
			} // May contain the last pending snapshot before close.
		}
	}()
	workers.Wait()
	ch, unsubscribe := p.Subscribe("node")
	defer unsubscribe()
	if event := <-ch; event.Metrics.TimestampUnixNano != 1000 {
		t.Fatal("lost latest cached sample")
	}
}
