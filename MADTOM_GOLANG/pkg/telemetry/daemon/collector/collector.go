package collector

import (
	"os"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/twamp"
	"github.com/DarkDuck007/madtom/pkg/telemetry/optin"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// Engine orchestrates all subsystem collectors based on opt-in node configuration.
type Engine struct {
	mu               sync.RWMutex
	nodeID           string
	cpuCollector     *CPUCollector
	memCollector     *MemoryCollector
	pwrCollector     *PowerCollector
	netCollector     *NetworkCollector
	diskCollector    *DiskCollector
	processCollector *ProcessCollector
	cpuModel         string
	collectorAddr    string
	twampPort        int
}

// NewEngine creates and initializes all Linux collectors.
func NewEngine(nodeID string) *Engine {
	if nodeID == "" {
		nodeID = detectHostname()
	}
	return &Engine{
		nodeID:           nodeID,
		cpuCollector:     NewCPUCollector(),
		memCollector:     NewMemoryCollector(),
		pwrCollector:     NewPowerCollector(),
		netCollector:     NewNetworkCollector(),
		diskCollector:    NewDiskCollector(),
		processCollector: NewProcessCollector(),
		cpuModel:         readCPUModel(),
		twampPort:        twamp.DefaultPort,
	}
}

// SetCollectorAddr configures the known collector address used to resolve 'collector'/'auto' TWAMP targets.
func (e *Engine) SetCollectorAddr(addr string) {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.collectorAddr = addr
}

// SetTwampPort configures the default UDP port used for TWAMP probing.
func (e *Engine) SetTwampPort(port int) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if port > 0 {
		e.twampPort = port
	}
}

// Collect compiles a SystemMetrics snapshot evaluating dynamic opt-in flags.
func (e *Engine) Collect(cfg *madtomv1.NodeConfig) *madtomv1.SystemMetrics {
	e.mu.Lock()
	defer e.mu.Unlock()
	metrics := &madtomv1.SystemMetrics{
		CpuModel: e.cpuModel, Os: runtime.GOOS, Arch: runtime.GOARCH,
		TimestampUnixNano: time.Now().UnixNano(),
		NodeId:            e.nodeID,
	}

	if cfg == nil {
		cfg = DefaultConfig(e.nodeID)
	}

	// 1. CPU
	cpuOverall := optin.ResolveMode(cfg.CpuOverallMode, cfg.CollectCpuOverall)
	cpuPerCore := optin.ResolveMode(cfg.CpuPerCoreMode, cfg.CollectCpuPerCore)
	if cpuOverall != madtomv1.TelemetryOptInMode_OPT_IN_OFF || cpuPerCore != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		metrics.Cpu = e.cpuCollector.Collect(cpuPerCore != madtomv1.TelemetryOptInMode_OPT_IN_OFF)
	}

	// 2. Memory
	memBasic := optin.ResolveMode(cfg.MemoryBasicMode, cfg.CollectMemoryBasic)
	memSwap := optin.ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram)
	zram := optin.ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram)
	if memBasic != madtomv1.TelemetryOptInMode_OPT_IN_OFF || memSwap != madtomv1.TelemetryOptInMode_OPT_IN_OFF || zram != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		metrics.Memory = e.memCollector.CollectSelected(memSwap != madtomv1.TelemetryOptInMode_OPT_IN_OFF, zram != madtomv1.TelemetryOptInMode_OPT_IN_OFF)

		if metrics.Memory != nil {
			if memSwap == madtomv1.TelemetryOptInMode_OPT_IN_OFF {
				metrics.Memory.SwapDevices = nil
			} else if len(metrics.Memory.SwapDevices) > 0 && len(cfg.SwapDeviceModes) > 0 {
				var filtered []*madtomv1.SwapDevice
				for _, d := range metrics.Memory.SwapDevices {
					if optin.GetMetricOptInMode(cfg, "swap."+d.Name) != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
						filtered = append(filtered, d)
					}
				}
				metrics.Memory.SwapDevices = filtered
			}

			if zram == madtomv1.TelemetryOptInMode_OPT_IN_OFF {
				metrics.Memory.ZramDevices = nil
			} else if len(metrics.Memory.ZramDevices) > 0 && len(cfg.ZramDeviceModes) > 0 {
				var filtered []*madtomv1.ZramDevice
				for _, d := range metrics.Memory.ZramDevices {
					if optin.GetMetricOptInMode(cfg, "zram."+d.Name) != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
						filtered = append(filtered, d)
					}
				}
				metrics.Memory.ZramDevices = filtered
			}
		}
	}

	// 3. Power & Battery
	pwr := optin.ResolveMode(cfg.PowerMode, cfg.CollectPowerBattery)
	if pwr != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		metrics.Power = e.pwrCollector.Collect()
	}

	// 4. Network Interfaces
	netMode := optin.ResolveMode(cfg.NetworkMode, cfg.CollectNetworkInterfaces)
	if netMode != madtomv1.TelemetryOptInMode_OPT_IN_OFF || hasEnabledDevice(cfg.NicModes) {
		metrics.Network = e.netCollector.Collect()
		if metrics.Network != nil && len(metrics.Network.Interfaces) > 0 {
			var filtered []*madtomv1.NicMetric
			for _, nic := range metrics.Network.Interfaces {
				if optin.GetMetricOptInMode(cfg, "nic."+nic.Name+".rx_bytes") != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
					filtered = append(filtered, nic)
				}
			}
			metrics.Network.Interfaces = filtered
		}
	}

	// 5. Disk I/O
	diskMode := optin.ResolveMode(cfg.DiskIoMode, true)
	if diskMode != madtomv1.TelemetryOptInMode_OPT_IN_OFF || len(cfg.DiskDeviceModes) > 0 {
		metrics.DiskIo = e.diskCollector.Collect()
		if metrics.DiskIo != nil && len(metrics.DiskIo.Devices) > 0 {
			var filtered []*madtomv1.DiskIoDevice
			for _, dev := range metrics.DiskIo.Devices {
				if optin.GetMetricOptInMode(cfg, "disk.io."+dev.Name+".read_bytes") != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
					filtered = append(filtered, dev)
				}
			}
			metrics.DiskIo.Devices = filtered
		}
	}

	twampMode := optin.ResolveMode(cfg.TwampMode, cfg.TwampTarget != "")
	if twampMode != madtomv1.TelemetryOptInMode_OPT_IN_OFF && cfg.TwampTarget != "" {
		target := twamp.ResolveTarget(cfg.TwampTarget, e.twampPort, e.collectorAddr)
		if target != "" {
			metrics.Twamp = twamp.Probe(target, cfg.TwampClocksSynchronized, uint32(time.Now().UnixNano()))
		}
	}
	if cfg.ProcessMode != madtomv1.ProcessTelemetryMode_PROCESS_MODE_DISABLED {
		metrics.Processes, metrics.ProcessesAvailable = e.processCollector.CollectTop(cfg.ProcessSnapshotLimit)
	} else {
		metrics.Processes = nil
		metrics.ProcessesAvailable = false
	}
	return metrics
}

// DefaultConfig provides initial opt-in settings enabling standard metrics.
func DefaultConfig(nodeID string) *madtomv1.NodeConfig {
	return &madtomv1.NodeConfig{
		NodeId:                    nodeID,
		CollectCpuOverall:         true,
		CollectCpuPerCore:         true,
		CollectMemoryBasic:        true,
		CollectMemorySwapZram:     true,
		CollectPowerBattery:       true,
		CollectNetworkInterfaces:  true,
		CollectNetworkConnections: false,
		FastPollIntervalMs:        1000,
		NormalPollIntervalMs:      10000,
		SlowPollIntervalMs:        30000,
		EnableZstdCompression:     false,
		MaxSpoolBytes:             1024 * 1024 * 1024, // 1 GB
		ProcessMode:               madtomv1.ProcessTelemetryMode_PROCESS_MODE_LIVE_ONLY,
		TopNProcesses:             5,
	}
}

func detectHostname() string {
	name, err := os.Hostname()
	if err != nil || strings.TrimSpace(name) == "" {
		return "unknown-node"
	}
	return strings.TrimSpace(name)
}

func readCPUModel() string {
	raw, _ := os.ReadFile("/proc/cpuinfo")
	for _, line := range strings.Split(string(raw), "\n") {
		key, value, ok := strings.Cut(line, ":")
		if ok && (strings.TrimSpace(key) == "model name" || strings.TrimSpace(key) == "Hardware") {
			return strings.TrimSpace(value)
		}
	}
	return runtime.GOARCH
}

// An override map containing only OFF entries does not require a network probe.
func hasEnabledDevice(modes map[string]madtomv1.TelemetryOptInMode) bool {
	for _, mode := range modes {
		if mode != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
			return true
		}
	}
	return false
}
