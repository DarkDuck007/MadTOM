package optin

import (
	"fmt"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// ResolveMode resolves the effective TelemetryOptInMode considering modern enum and legacy bool.
func ResolveMode(mode madtomv1.TelemetryOptInMode, legacyBool bool) madtomv1.TelemetryOptInMode {
	if mode != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		return mode
	}
	if legacyBool {
		return madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
	}
	return madtomv1.TelemetryOptInMode_OPT_IN_OFF
}

// GetMetricOptInMode determines the effective OptInMode for a given metric or device name.
func GetMetricOptInMode(cfg *madtomv1.NodeConfig, metric string) madtomv1.TelemetryOptInMode {
	if cfg == nil {
		return madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
	}

	// 1. Overall CPU
	if metric == "cpu.total" || metric == "cpu.user" || metric == "cpu.system" || metric == "cpu.iowait" || metric == "cpu.idle" {
		return ResolveMode(cfg.CpuOverallMode, cfg.CollectCpuOverall)
	}

	// 2. Per-Core CPU (cpu.core.<N>)
	if strings.HasPrefix(metric, "cpu.core.") {
		coreIdx := strings.TrimPrefix(metric, "cpu.core.")
		if cfg.CoreModes != nil {
			if mode, ok := cfg.CoreModes[coreIdx]; ok {
				return mode
			}
		}
		return ResolveMode(cfg.CpuPerCoreMode, cfg.CollectCpuPerCore)
	}

	// 3. Basic Memory
	if metric == "memory.total" || metric == "memory.available" || metric == "memory.used" ||
		metric == "memory.free" || metric == "memory.buffers" || metric == "memory.cached" {
		return ResolveMode(cfg.MemoryBasicMode, cfg.CollectMemoryBasic)
	}

	// 4. Overall Swap
	if metric == "memory.swap_total" || metric == "memory.swap_used" || metric == "memory.swap_free" {
		return ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram)
	}

	// 5. Partition Swap (swap.<dev>.*)
	if strings.HasPrefix(metric, "swap.") {
		parts := strings.Split(metric, ".")
		if len(parts) >= 2 {
			devName := parts[1]
			if cfg.SwapDeviceModes != nil {
				if mode, ok := cfg.SwapDeviceModes[devName]; ok {
					return mode
				}
			}
		}
		return ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram)
	}

	// 6. ZRAM (zram.<dev>.* or memory.zram_ratio)
	if strings.HasPrefix(metric, "zram.") {
		parts := strings.Split(metric, ".")
		if len(parts) >= 2 {
			devName := parts[1]
			if cfg.ZramDeviceModes != nil {
				if mode, ok := cfg.ZramDeviceModes[devName]; ok {
					return mode
				}
			}
		}
		return ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram)
	}
	if metric == "memory.zram_ratio" {
		return ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram)
	}

	// 7. Network NIC (nic.<iface>.*)
	if strings.HasPrefix(metric, "nic.") {
		parts := strings.Split(metric, ".")
		if len(parts) >= 2 {
			ifaceName := parts[1]
			if cfg.NicModes != nil {
				if mode, ok := cfg.NicModes[ifaceName]; ok {
					return mode
				}
			}
		}
		return ResolveMode(cfg.NetworkMode, cfg.CollectNetworkInterfaces)
	}

	// 8. Disk I/O (disk.io.<dev>.* or disk.io.*)
	if strings.HasPrefix(metric, "disk.io.") {
		parts := strings.Split(metric, ".")
		if len(parts) >= 4 { // e.g. disk.io.sda.read_bytes
			devName := parts[2]
			if cfg.DiskDeviceModes != nil {
				if mode, ok := cfg.DiskDeviceModes[devName]; ok {
					return mode
				}
			}
		}
		return ResolveMode(cfg.DiskIoMode, true)
	}

	// 9. Power & Battery (power.*)
	if strings.HasPrefix(metric, "power.") {
		if cfg.PowerMetricModes != nil {
			if mode, ok := cfg.PowerMetricModes[metric]; ok {
				return mode
			}
		}
		return ResolveMode(cfg.PowerMode, cfg.CollectPowerBattery)
	}

	// 10. TWAMP Latency (twamp.*)
	if strings.HasPrefix(metric, "twamp.") {
		return ResolveMode(cfg.TwampMode, cfg.TwampTarget != "")
	}

	// 11. Processes (proc.cpu.*)
	if strings.HasPrefix(metric, "proc.cpu.") {
		switch cfg.ProcessMode {
		case madtomv1.ProcessTelemetryMode_PROCESS_MODE_PROBED_AND_STORED:
			return madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
		case madtomv1.ProcessTelemetryMode_PROCESS_MODE_LIVE_ONLY:
			return madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY
		default:
			return madtomv1.TelemetryOptInMode_OPT_IN_OFF
		}
	}

	return madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
}

// StripMonitorOnlyMetrics removes any metrics configured as OPT_IN_MONITOR_ONLY or OPT_IN_OFF from
// the system metrics sample, ensuring that offline WAL spools only contain OPT_IN_MONITOR_AND_STORE metrics.
func StripMonitorOnlyMetrics(s *madtomv1.SystemMetrics, cfg *madtomv1.NodeConfig) {
	if s == nil || cfg == nil {
		return
	}

	// CPU Overall
	if ResolveMode(cfg.CpuOverallMode, cfg.CollectCpuOverall) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		if s.Cpu != nil {
			s.Cpu.TotalPct = 0
			s.Cpu.UserPct = 0
			s.Cpu.SystemPct = 0
			s.Cpu.IowaitPct = 0
			s.Cpu.IdlePct = 0
		}
	}

	// CPU Per-Core
	if s.Cpu != nil && len(s.Cpu.PerCorePct) > 0 {
		basePerCore := ResolveMode(cfg.CpuPerCoreMode, cfg.CollectCpuPerCore)
		if basePerCore != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE && len(cfg.CoreModes) == 0 {
			s.Cpu.PerCorePct = nil
			s.Cpu.CoreFreqMhz = nil
		} else {
			for i := range s.Cpu.PerCorePct {
				mode := GetMetricOptInMode(cfg, fmt.Sprintf("cpu.core.%d", i))
				if mode != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
					s.Cpu.PerCorePct[i] = 0
				}
			}
		}
	}

	// Memory Basic
	if ResolveMode(cfg.MemoryBasicMode, cfg.CollectMemoryBasic) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		if s.Memory != nil {
			s.Memory.MemTotalBytes = 0
			s.Memory.MemFreeBytes = 0
			s.Memory.MemAvailableBytes = 0
			s.Memory.BuffersBytes = 0
			s.Memory.CachedBytes = 0
			s.Memory.DirtyBytes = 0
			s.Memory.WritebackBytes = 0
		}
	}

	// Swap
	if ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		if s.Memory != nil {
			s.Memory.SwapTotalBytes = 0
			s.Memory.SwapFreeBytes = 0
			s.Memory.SwapUsedBytes = 0
		}
	}
	if s.Memory != nil && len(s.Memory.SwapDevices) > 0 {
		var filteredSwaps []*madtomv1.SwapDevice
		for _, dev := range s.Memory.SwapDevices {
			if GetMetricOptInMode(cfg, "swap."+dev.Name) == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
				filteredSwaps = append(filteredSwaps, dev)
			}
		}
		s.Memory.SwapDevices = filteredSwaps
	}

	// ZRAM
	if ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		if s.Memory != nil {
			s.Memory.ZramOrigBytes = 0
			s.Memory.ZramComprBytes = 0
			s.Memory.ZramRatio = 0
		}
	}
	if s.Memory != nil && len(s.Memory.ZramDevices) > 0 {
		var filteredZrams []*madtomv1.ZramDevice
		for _, dev := range s.Memory.ZramDevices {
			if GetMetricOptInMode(cfg, "zram."+dev.Name) == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
				filteredZrams = append(filteredZrams, dev)
			}
		}
		s.Memory.ZramDevices = filteredZrams
	}

	// Network
	if s.Network != nil {
		var filteredNics []*madtomv1.NicMetric
		for _, nic := range s.Network.Interfaces {
			if GetMetricOptInMode(cfg, "nic."+nic.Name+".rx_bytes") == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
				filteredNics = append(filteredNics, nic)
			}
		}
		s.Network.Interfaces = filteredNics
		if len(filteredNics) == 0 && ResolveMode(cfg.NetworkMode, cfg.CollectNetworkInterfaces) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			s.Network = nil
		}
	}

	// Disk I/O
	if s.DiskIo != nil {
		if ResolveMode(cfg.DiskIoMode, true) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			s.DiskIo.ReadBytes = 0
			s.DiskIo.WriteBytes = 0
			s.DiskIo.ReadOps = 0
			s.DiskIo.WriteOps = 0
		}
		var filteredDisks []*madtomv1.DiskIoDevice
		for _, dev := range s.DiskIo.Devices {
			if GetMetricOptInMode(cfg, "disk.io."+dev.Name+".read_bytes") == madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
				filteredDisks = append(filteredDisks, dev)
			}
		}
		s.DiskIo.Devices = filteredDisks
	}

	// Power
	if ResolveMode(cfg.PowerMode, cfg.CollectPowerBattery) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		s.Power = nil
	}

	// TWAMP
	if ResolveMode(cfg.TwampMode, cfg.TwampTarget != "") != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		s.Twamp = nil
	}

	// Processes
	if cfg.ProcessMode != madtomv1.ProcessTelemetryMode_PROCESS_MODE_PROBED_AND_STORED {
		s.Processes = nil
		s.ProcessesAvailable = false
	}
}
