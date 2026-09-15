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

func TestPipeline_3TierOptInAndNewMetrics(t *testing.T) {
	db, err := storage.OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	reg := registry.NewRegistry("test-collector")
	pipeline := NewPipeline(db, reg, "test-collector")

	now := time.Now().UnixNano()

	cfg := &madtomv1.NodeConfig{
		NodeId:            "node-2",
		CollectCpuOverall: true,
		CpuOverallMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		CollectCpuPerCore: true,
		CpuPerCoreMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		CoreModes: map[string]madtomv1.TelemetryOptInMode{
			"1": madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY, // Core 1 is MONITOR_ONLY -> TSDB should NOT store it
		},
		MemorySwapMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		ZramMode:       madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		NetworkMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY, // Network default is MONITOR_ONLY
		NicModes: map[string]madtomv1.TelemetryOptInMode{
			"eth0": madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE, // eth0 overridden to STORE
		},
	}
	reg.SetConfig("node-2", cfg)

	sample := &madtomv1.SystemMetrics{
		NodeId:            "node-2",
		TimestampUnixNano: now,
		Cpu: &madtomv1.CpuMetrics{
			TotalPct:   33.3,
			PerCorePct: []float64{20.0, 40.0},
		},
		Memory: &madtomv1.MemoryMetrics{
			MemTotalBytes:  16000000000,
			SwapTotalBytes: 8000000000,
			SwapFreeBytes:  6000000000,
			SwapUsedBytes:  2000000000,
			ZramOrigBytes:  500000000,
			ZramComprBytes: 250000000,
			ZramRatio:      2.0,
			SwapDevices: []*madtomv1.SwapDevice{
				{Name: "zram0", TotalBytes: 8000000000, UsedBytes: 2000000000},
			},
			ZramDevices: []*madtomv1.ZramDevice{
				{Name: "zram0", DisksizeBytes: 8000000000, MemUsedBytes: 260000000, OrigDataBytes: 500000000, ComprDataBytes: 250000000},
			},
		},
		Network: &madtomv1.NetworkMetrics{
			Interfaces: []*madtomv1.NicMetric{
				{Name: "eth0", RxBytes: 12345},
				{Name: "docker0", RxBytes: 67890},
			},
		},
	}

	batch := &madtomv1.TelemetryBatch{
		NodeId:  "node-2",
		Samples: []*madtomv1.SystemMetrics{sample},
	}

	if err := pipeline.ProcessBatch(batch, "PUSH"); err != nil {
		t.Fatalf("process batch failed: %v", err)
	}

	// 1. cpu.core.0 should be in TSDB (20.0)
	ptsC0, err := db.QueryRange("node-2", "cpu.core.0", now-1000, now+1000)
	if err != nil || len(ptsC0) != 1 || ptsC0[0].Value != 20.0 {
		t.Fatalf("expected cpu.core.0 = 20.0, got %+v (err: %v)", ptsC0, err)
	}

	// 2. cpu.core.1 was MONITOR_ONLY -> should NOT be in TSDB
	ptsC1, err := db.QueryRange("node-2", "cpu.core.1", now-1000, now+1000)
	if err != nil || len(ptsC1) != 0 {
		t.Fatalf("expected cpu.core.1 to be 0 points, got %d", len(ptsC1))
	}

	// 3. memory.swap_used should be stored (2000000000)
	ptsSwapUsed, err := db.QueryRange("node-2", "memory.swap_used", now-1000, now+1000)
	if err != nil || len(ptsSwapUsed) != 1 || ptsSwapUsed[0].Value != 2000000000 {
		t.Fatalf("expected swap_used 2000000000, got %+v", ptsSwapUsed)
	}

	// 4. memory.swap_free must NOT be stored
	ptsSwapFree, _ := db.QueryRange("node-2", "memory.swap_free", now-1000, now+1000)
	if len(ptsSwapFree) != 0 {
		t.Fatalf("expected memory.swap_free NOT stored, got %d points", len(ptsSwapFree))
	}

	// 5. memory.zram_ratio must NOT be stored
	ptsZramRatio, _ := db.QueryRange("node-2", "memory.zram_ratio", now-1000, now+1000)
	if len(ptsZramRatio) != 0 {
		t.Fatalf("expected memory.zram_ratio NOT stored, got %d points", len(ptsZramRatio))
	}

	// 6. swap.zram0.used_bytes and zram.zram0.mem_used_bytes stored
	ptsSwapDev, _ := db.QueryRange("node-2", "swap.zram0.used_bytes", now-1000, now+1000)
	if len(ptsSwapDev) != 1 || ptsSwapDev[0].Value != 2000000000 {
		t.Fatalf("expected swap.zram0.used_bytes 2000000000, got %+v", ptsSwapDev)
	}

	ptsZramDev, _ := db.QueryRange("node-2", "zram.zram0.mem_used_bytes", now-1000, now+1000)
	if len(ptsZramDev) != 1 || ptsZramDev[0].Value != 260000000 {
		t.Fatalf("expected zram.zram0.mem_used_bytes 260000000, got %+v", ptsZramDev)
	}

	// 7. eth0 was overridden to STORE -> stored; docker0 was default MONITOR_ONLY -> NOT stored
	ptsEth0, _ := db.QueryRange("node-2", "nic.eth0.rx_bytes", now-1000, now+1000)
	if len(ptsEth0) != 1 || ptsEth0[0].Value != 12345 {
		t.Fatalf("expected eth0 rx_bytes stored, got %+v", ptsEth0)
	}

	ptsDocker0, _ := db.QueryRange("node-2", "nic.docker0.rx_bytes", now-1000, now+1000)
	if len(ptsDocker0) != 0 {
		t.Fatalf("expected docker0 rx_bytes NOT stored, got %d points", len(ptsDocker0))
	}
}
