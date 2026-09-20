package collector

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

type cpuTicks struct {
	user    uint64
	nice    uint64
	system  uint64
	idle    uint64
	iowait  uint64
	irq     uint64
	softirq uint64
	steal   uint64
}

func (t cpuTicks) total() uint64 {
	return t.user + t.nice + t.system + t.idle + t.iowait + t.irq + t.softirq + t.steal
}

type CPUCollector struct {
	mu            sync.Mutex
	lastOverall   cpuTicks
	lastCores     []cpuTicks
	lastCtxt      uint64
	lastCheckTime time.Time
}

func NewCPUCollector() *CPUCollector {
	c := &CPUCollector{}
	c.lastOverall, c.lastCores, c.lastCtxt = c.readProcStat()
	c.lastCheckTime = time.Now()
	return c
}

func (c *CPUCollector) Collect(includePerCore bool) *madtomv1.CpuMetrics {
	c.mu.Lock()
	defer c.mu.Unlock()

	now := time.Now()
	curOverall, curCores, curCtxt := c.readProcStat()
	elapsedSec := now.Sub(c.lastCheckTime).Seconds()
	if elapsedSec <= 0 {
		elapsedSec = 1.0
	}

	metrics := &madtomv1.CpuMetrics{}

	// Overall delta
	deltaTotal := float64(curOverall.total() - c.lastOverall.total())
	if deltaTotal > 0 {
		deltaIdle := float64(curOverall.idle - c.lastOverall.idle)
		deltaUser := float64(curOverall.user - c.lastOverall.user)
		deltaSystem := float64(curOverall.system - c.lastOverall.system)
		deltaIowait := float64(curOverall.iowait - c.lastOverall.iowait)

		metrics.IdlePct = clampPct((deltaIdle / deltaTotal) * 100.0)
		metrics.UserPct = clampPct((deltaUser / deltaTotal) * 100.0)
		metrics.SystemPct = clampPct((deltaSystem / deltaTotal) * 100.0)
		metrics.IowaitPct = clampPct((deltaIowait / deltaTotal) * 100.0)
		metrics.TotalPct = clampPct(100.0 - metrics.IdlePct)
	}

	// Per-core matrix
	if includePerCore && len(curCores) > 0 {
		metrics.PerCorePct = make([]float64, len(curCores))
		for i, core := range curCores {
			if i < len(c.lastCores) {
				cDeltaTotal := float64(core.total() - c.lastCores[i].total())
				if cDeltaTotal > 0 {
					cDeltaIdle := float64(core.idle - c.lastCores[i].idle)
					metrics.PerCorePct[i] = clampPct(100.0 - (cDeltaIdle/cDeltaTotal)*100.0)
				}
			}
		}
	}

	// Context switches per second
	if curCtxt >= c.lastCtxt && elapsedSec > 0 {
		metrics.CtxtSwitchesSec = uint64(float64(curCtxt-c.lastCtxt) / elapsedSec)
	}

	// Load averages
	c.readLoadAvg(metrics)

	// Frequencies (MHz)
	metrics.CoreFreqMhz = c.readFrequencies(len(curCores))

	c.lastOverall = curOverall
	c.lastCores = curCores
	c.lastCtxt = curCtxt
	c.lastCheckTime = now

	return metrics
}

func (c *CPUCollector) readProcStat() (cpuTicks, []cpuTicks, uint64) {
	file, err := os.Open("/proc/stat")
	if err != nil {
		return cpuTicks{}, nil, 0
	}
	defer file.Close()

	var overall cpuTicks
	var cores []cpuTicks
	var ctxt uint64

	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := scanner.Text()
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}

		if fields[0] == "cpu" {
			overall = parseTicks(fields[1:])
		} else if strings.HasPrefix(fields[0], "cpu") && len(fields[0]) > 3 {
			coreIdx, err := strconv.Atoi(fields[0][3:])
			if err == nil {
				for len(cores) <= coreIdx {
					cores = append(cores, cpuTicks{})
				}
				cores[coreIdx] = parseTicks(fields[1:])
			}
		} else if fields[0] == "ctxt" {
			ctxt, _ = strconv.ParseUint(fields[1], 10, 64)
		}
	}

	return overall, cores, ctxt
}

func parseTicks(fields []string) cpuTicks {
	var t cpuTicks
	if len(fields) > 0 {
		t.user, _ = strconv.ParseUint(fields[0], 10, 64)
	}
	if len(fields) > 1 {
		t.nice, _ = strconv.ParseUint(fields[1], 10, 64)
	}
	if len(fields) > 2 {
		t.system, _ = strconv.ParseUint(fields[2], 10, 64)
	}
	if len(fields) > 3 {
		t.idle, _ = strconv.ParseUint(fields[3], 10, 64)
	}
	if len(fields) > 4 {
		t.iowait, _ = strconv.ParseUint(fields[4], 10, 64)
	}
	if len(fields) > 5 {
		t.irq, _ = strconv.ParseUint(fields[5], 10, 64)
	}
	if len(fields) > 6 {
		t.softirq, _ = strconv.ParseUint(fields[6], 10, 64)
	}
	if len(fields) > 7 {
		t.steal, _ = strconv.ParseUint(fields[7], 10, 64)
	}
	return t
}

func (c *CPUCollector) readLoadAvg(metrics *madtomv1.CpuMetrics) {
	data, err := os.ReadFile("/proc/loadavg")
	if err != nil {
		return
	}
	fields := strings.Fields(string(data))
	if len(fields) >= 3 {
		metrics.Load_1M, _ = strconv.ParseFloat(fields[0], 64)
		metrics.Load_5M, _ = strconv.ParseFloat(fields[1], 64)
		metrics.Load_15M, _ = strconv.ParseFloat(fields[2], 64)
	}
}

func (c *CPUCollector) readFrequencies(numCores int) []uint32 {
	if numCores == 0 {
		return nil
	}
	freqs := make([]uint32, numCores)
	for i := 0; i < numCores; i++ {
		path := fmt.Sprintf("/sys/devices/system/cpu/cpu%d/cpufreq/scaling_cur_freq", i)
		data, err := os.ReadFile(path)
		if err == nil {
			val, err := strconv.ParseUint(strings.TrimSpace(string(data)), 10, 32)
			if err == nil {
				freqs[i] = uint32(val / 1000) // convert kHz to MHz
			}
		}
	}
	return freqs
}

func clampPct(v float64) float64 {
	if v < 0.0 {
		return 0.0
	}
	if v > 100.0 {
		return 100.0
	}
	return v
}
