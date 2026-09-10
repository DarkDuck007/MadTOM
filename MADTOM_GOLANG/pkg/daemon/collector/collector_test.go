package collector

import (
	"testing"
	"time"
)

func TestCollectorEngineLinux(t *testing.T) {
	engine := NewEngine("test-node-01")
	cfg := DefaultConfig("test-node-01")

	// Allow 100ms between ticks for delta calculations
	time.Sleep(100 * time.Millisecond)

	metrics := engine.Collect(cfg)
	if metrics == nil {
		t.Fatal("expected non-nil SystemMetrics")
	}

	if metrics.NodeId != "test-node-01" {
		t.Fatalf("expected node_id 'test-node-01', got '%s'", metrics.NodeId)
	}

	// Verify CPU
	if metrics.Cpu == nil {
		t.Fatal("expected non-nil CpuMetrics")
	}
	t.Logf("CPU Total: %.2f%%, User: %.2f%%, System: %.2f%%, Idle: %.2f%%, Cores: %d",
		metrics.Cpu.TotalPct, metrics.Cpu.UserPct, metrics.Cpu.SystemPct, metrics.Cpu.IdlePct, len(metrics.Cpu.PerCorePct))

	// Verify Memory
	if metrics.Memory == nil {
		t.Fatal("expected non-nil MemoryMetrics")
	}
	if metrics.Memory.MemTotalBytes == 0 {
		t.Fatal("expected MemTotalBytes > 0")
	}
	t.Logf("Mem Total: %d MB, Avail: %d MB, Free: %d MB, Swap Total: %d MB",
		metrics.Memory.MemTotalBytes/(1024*1024),
		metrics.Memory.MemAvailableBytes/(1024*1024),
		metrics.Memory.MemFreeBytes/(1024*1024),
		metrics.Memory.SwapTotalBytes/(1024*1024))

	// Verify Network
	if metrics.Network != nil {
		t.Logf("Found %d network interfaces", len(metrics.Network.Interfaces))
		for _, iface := range metrics.Network.Interfaces {
			t.Logf("  NIC %s: rx_bytes=%d, tx_bytes=%d, speed=%d Mbps, up=%v",
				iface.Name, iface.RxBytes, iface.TxBytes, iface.LinkSpeedMbps, iface.CarrierUp)
		}
	}

	// Verify Opt-In disable
	cfg.CollectCpuOverall = false
	cfg.CollectCpuPerCore = false
	disabledMetrics := engine.Collect(cfg)
	if disabledMetrics.Cpu != nil {
		t.Fatal("expected nil CpuMetrics when disabled in config")
	}
}
