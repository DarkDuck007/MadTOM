package optin

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestResolveMode(t *testing.T) {
	// Mode explicitly set to Monitor Only
	if m := ResolveMode(madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY, true); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY {
		t.Fatalf("expected MONITOR_ONLY, got %v", m)
	}
	// Mode explicitly set to Store
	if m := ResolveMode(madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE, true); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Fatalf("expected MONITOR_AND_STORE, got %v", m)
	}
	// Mode is 0 (unset/off), legacy bool is true -> fallback to MONITOR_AND_STORE
	if m := ResolveMode(madtomv1.TelemetryOptInMode_OPT_IN_OFF, true); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Fatalf("expected legacy fallback to MONITOR_AND_STORE, got %v", m)
	}
	// Mode is 0, legacy bool is false -> OPT_IN_OFF
	if m := ResolveMode(madtomv1.TelemetryOptInMode_OPT_IN_OFF, false); m != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		t.Fatalf("expected OPT_IN_OFF, got %v", m)
	}
}

func TestGetMetricOptInMode(t *testing.T) {
	cfg := &madtomv1.NodeConfig{
		CollectCpuOverall: true,
		CpuOverallMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
		CollectCpuPerCore: true,
		CpuPerCoreMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		CoreModes: map[string]madtomv1.TelemetryOptInMode{
			"3": madtomv1.TelemetryOptInMode_OPT_IN_OFF,
		},
		NetworkMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
		NicModes: map[string]madtomv1.TelemetryOptInMode{
			"eth0": madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		},
		MemorySwapMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		SwapDeviceModes: map[string]madtomv1.TelemetryOptInMode{
			"swapfile": madtomv1.TelemetryOptInMode_OPT_IN_OFF,
		},
	}

	if m := GetMetricOptInMode(cfg, "cpu.total"); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY {
		t.Fatalf("expected cpu.total to be MONITOR_ONLY, got %v", m)
	}
	if m := GetMetricOptInMode(cfg, "cpu.core.0"); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Fatalf("expected cpu.core.0 to be MONITOR_AND_STORE, got %v", m)
	}
	if m := GetMetricOptInMode(cfg, "cpu.core.3"); m != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		t.Fatalf("expected cpu.core.3 to be OPT_IN_OFF, got %v", m)
	}
	if m := GetMetricOptInMode(cfg, "nic.eth0.rx_bytes"); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Fatalf("expected eth0 to be MONITOR_AND_STORE, got %v", m)
	}
	if m := GetMetricOptInMode(cfg, "nic.docker0.rx_bytes"); m != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY {
		t.Fatalf("expected docker0 to inherit parent MONITOR_ONLY, got %v", m)
	}
	if m := GetMetricOptInMode(cfg, "swap.swapfile.used_bytes"); m != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		t.Fatalf("expected swap.swapfile to be OPT_IN_OFF, got %v", m)
	}
}

func TestStripMonitorOnlyMetrics(t *testing.T) {
	cfg := &madtomv1.NodeConfig{
		CpuOverallMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
		CpuPerCoreMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		MemorySwapMode: madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
		NetworkMode:    madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		NicModes: map[string]madtomv1.TelemetryOptInMode{
			"docker0": madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
		},
	}

	sample := &madtomv1.SystemMetrics{
		Cpu: &madtomv1.CpuMetrics{
			TotalPct:   45.5,
			PerCorePct: []float64{10.0, 20.0},
		},
		Memory: &madtomv1.MemoryMetrics{
			MemTotalBytes: 1000,
			SwapUsedBytes: 500,
			SwapDevices: []*madtomv1.SwapDevice{
				{Name: "zram0", UsedBytes: 500},
			},
		},
		Network: &madtomv1.NetworkMetrics{
			Interfaces: []*madtomv1.NicMetric{
				{Name: "eth0", RxBytes: 100},
				{Name: "docker0", RxBytes: 50},
			},
		},
	}

	StripMonitorOnlyMetrics(sample, cfg)

	// CPU overall was MONITOR_ONLY -> TotalPct should be 0
	if sample.Cpu.TotalPct != 0 {
		t.Fatalf("expected Cpu.TotalPct to be stripped/zeroed, got %v", sample.Cpu.TotalPct)
	}
	// CPU per core was MONITOR_AND_STORE -> preserved
	if len(sample.Cpu.PerCorePct) != 2 || sample.Cpu.PerCorePct[0] != 10.0 {
		t.Fatalf("expected PerCorePct preserved, got %v", sample.Cpu.PerCorePct)
	}
	// Swap was MONITOR_ONLY -> SwapUsedBytes zeroed, SwapDevices cleared
	if sample.Memory.SwapUsedBytes != 0 || len(sample.Memory.SwapDevices) != 0 {
		t.Fatalf("expected Swap to be stripped, got %v", sample.Memory)
	}
	// Network: eth0 preserved, docker0 stripped
	if len(sample.Network.Interfaces) != 1 || sample.Network.Interfaces[0].Name != "eth0" {
		t.Fatalf("expected only eth0 preserved, got %v", sample.Network.Interfaces)
	}
}
