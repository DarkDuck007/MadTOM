package collector

import (
	"os"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/daemon/twamp"

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
	if cfg.CollectCpuOverall || cfg.CollectCpuPerCore {
		metrics.Cpu = e.cpuCollector.Collect(cfg.CollectCpuPerCore)
	}

	// 2. Memory
	if cfg.CollectMemoryBasic || cfg.CollectMemorySwapZram {
		metrics.Memory = e.memCollector.Collect(cfg.CollectMemorySwapZram)
	}

	// 3. Power & Battery
	if cfg.CollectPowerBattery {
		metrics.Power = e.pwrCollector.Collect()
	}

	// 4. Network Interfaces
	if cfg.CollectNetworkInterfaces {
		metrics.Network = e.netCollector.Collect()
	}

	// 5. Disk I/O
	metrics.DiskIo = e.diskCollector.Collect()

	metrics.Twamp = twamp.Probe(cfg.TwampTarget, cfg.TwampClocksSynchronized, uint32(time.Now().UnixNano()))
	metrics.Processes, metrics.ProcessesAvailable = e.processCollector.Collect()
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
