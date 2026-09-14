package ingest

import (
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestPipeline_ProcessTelemetryStorageModes(t *testing.T) {
	db, err := storage.OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	reg := registry.NewRegistry("test-collector")
	pipeline := NewPipeline(db, reg, "test-collector")

	now := time.Now().UnixNano()

	// Create sample with 5 processes
	sample := &madtomv1.SystemMetrics{
		NodeId:            "node-1",
		TimestampUnixNano: now,
		Cpu: &madtomv1.CpuMetrics{
			TotalPct: 50.0,
		},
		Processes: []*madtomv1.ProcessMetric{
			{Pid: 101, Name: "postgres", CpuPct: 20.0},
			{Pid: 102, Name: "postgres", CpuPct: 5.0}, // same name -> should aggregate to 25.0%
			{Pid: 201, Name: "nginx", CpuPct: 10.0},
			{Pid: 301, Name: "redis-server", CpuPct: 5.0},
			{Pid: 401, Name: "madtom-daemon", CpuPct: 2.0},
			{Pid: 501, Name: "systemd", CpuPct: 1.0},
		},
	}

	batch := &madtomv1.TelemetryBatch{
		NodeId:  "node-1",
		Samples: []*madtomv1.SystemMetrics{sample},
	}

	// 1. Case: Default LIVE_ONLY mode -> zero proc.cpu.* records in TSDB
	cfg := reg.GetConfig("node-1")
	cfg.ProcessMode = madtomv1.ProcessTelemetryMode_PROCESS_MODE_LIVE_ONLY
	reg.SetConfig("node-1", cfg)

	if err := pipeline.ProcessBatch(batch, "PUSH"); err != nil {
		t.Fatalf("process batch failed: %v", err)
	}

	pts, err := db.QueryRange("node-1", "proc.cpu.postgres", now-1000, now+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(pts) != 0 {
		t.Fatalf("expected 0 proc.cpu.postgres points in LIVE_ONLY mode, got %d", len(pts))
	}

	// 2. Case: PROBED_AND_STORED with Top-3
	cfg.ProcessMode = madtomv1.ProcessTelemetryMode_PROCESS_MODE_PROBED_AND_STORED
	cfg.TopNProcesses = 3
	reg.SetConfig("node-1", cfg)

	sample2 := &madtomv1.SystemMetrics{
		NodeId:            "node-1",
		TimestampUnixNano: now + int64(time.Second),
		Cpu: &madtomv1.CpuMetrics{
			TotalPct: 50.0,
		},
		Processes: sample.Processes,
	}
	batch2 := &madtomv1.TelemetryBatch{
		NodeId:  "node-1",
		Samples: []*madtomv1.SystemMetrics{sample2},
	}

	if err := pipeline.ProcessBatch(batch2, "PUSH"); err != nil {
		t.Fatalf("process batch 2 failed: %v", err)
	}

	ts2 := sample2.TimestampUnixNano

	// postgres should have 25.0% (20 + 5)
	ptsPg, err := db.QueryRange("node-1", "proc.cpu.postgres", ts2-1000, ts2+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(ptsPg) != 1 || ptsPg[0].Value != 25.0 {
		t.Fatalf("expected postgres value 25.0, got %+v", ptsPg)
	}

	// nginx should have 10.0%
	ptsNginx, err := db.QueryRange("node-1", "proc.cpu.nginx", ts2-1000, ts2+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(ptsNginx) != 1 || ptsNginx[0].Value != 10.0 {
		t.Fatalf("expected nginx value 10.0, got %+v", ptsNginx)
	}

	// redis_server should have 5.0%
	ptsRedis, err := db.QueryRange("node-1", "proc.cpu.redis-server", ts2-1000, ts2+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(ptsRedis) != 1 || ptsRedis[0].Value != 5.0 {
		t.Fatalf("expected redis-server value 5.0, got %+v", ptsRedis)
	}

	// madtom-daemon was rank 4 -> should NOT be recorded individually because top_n is 3
	ptsMadtom, err := db.QueryRange("node-1", "proc.cpu.madtom-daemon", ts2-1000, ts2+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(ptsMadtom) != 0 {
		t.Fatalf("expected madtom-daemon to be excluded when top_n=3, got %+v", ptsMadtom)
	}

	// proc.cpu.other = 50.0 (total) - (25 + 10 + 5) = 10.0
	ptsOther, err := db.QueryRange("node-1", "proc.cpu.other", ts2-1000, ts2+1000)
	if err != nil {
		t.Fatal(err)
	}
	if len(ptsOther) != 1 || ptsOther[0].Value != 10.0 {
		t.Fatalf("expected proc.cpu.other value 10.0, got %+v", ptsOther)
	}
}
