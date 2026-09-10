package collector

import (
	"bufio"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

type MemoryCollector struct {
	mu            sync.Mutex
	lastPgFault   uint64
	lastPgMajFlt  uint64
	lastCheckTime time.Time
}

func NewMemoryCollector() *MemoryCollector {
	m := &MemoryCollector{}
	m.lastPgFault, m.lastPgMajFlt = m.readVmstatFaults()
	m.lastCheckTime = time.Now()
	return m
}

func (m *MemoryCollector) Collect(includeSwapZram bool) *madtomv1.MemoryMetrics {
	m.mu.Lock()
	defer m.mu.Unlock()

	now := time.Now()
	elapsedSec := now.Sub(m.lastCheckTime).Seconds()
	if elapsedSec <= 0 {
		elapsedSec = 1.0
	}

	metrics := &madtomv1.MemoryMetrics{}
	m.readMeminfo(metrics)

	// Page faults
	curPgFault, curPgMajFlt := m.readVmstatFaults()
	if curPgFault >= m.lastPgFault && elapsedSec > 0 {
		metrics.PgfaultSec = uint64(float64(curPgFault-m.lastPgFault) / elapsedSec)
	}
	if curPgMajFlt >= m.lastPgMajFlt && elapsedSec > 0 {
		metrics.PgmajfaultSec = uint64(float64(curPgMajFlt-m.lastPgMajFlt) / elapsedSec)
	}

	// ZRAM metrics
	if includeSwapZram {
		m.readZram(metrics)
	}

	m.lastPgFault = curPgFault
	m.lastPgMajFlt = curPgMajFlt
	m.lastCheckTime = now

	return metrics
}

func (m *MemoryCollector) readMeminfo(metrics *madtomv1.MemoryMetrics) {
	file, err := os.Open("/proc/meminfo")
	if err != nil {
		return
	}
	defer file.Close()

	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := scanner.Text()
		parts := strings.SplitN(line, ":", 2)
		if len(parts) != 2 {
			continue
		}
		key := strings.TrimSpace(parts[0])
		valFields := strings.Fields(parts[1])
		if len(valFields) == 0 {
			continue
		}
		valKb, err := strconv.ParseUint(valFields[0], 10, 64)
		if err != nil {
			continue
		}
		bytes := valKb * 1024

		switch key {
		case "MemTotal":
			metrics.MemTotalBytes = bytes
		case "MemFree":
			metrics.MemFreeBytes = bytes
		case "MemAvailable":
			metrics.MemAvailableBytes = bytes
		case "Buffers":
			metrics.BuffersBytes = bytes
		case "Cached":
			metrics.CachedBytes = bytes
		case "Dirty":
			metrics.DirtyBytes = bytes
		case "Writeback":
			metrics.WritebackBytes = bytes
		case "SwapTotal":
			metrics.SwapTotalBytes = bytes
		case "SwapFree":
			metrics.SwapFreeBytes = bytes
		}
	}
}

func (m *MemoryCollector) readVmstatFaults() (uint64, uint64) {
	file, err := os.Open("/proc/vmstat")
	if err != nil {
		return 0, 0
	}
	defer file.Close()

	var pgfault, pgmajfault uint64
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) < 2 {
			continue
		}
		if fields[0] == "pgfault" {
			pgfault, _ = strconv.ParseUint(fields[1], 10, 64)
		} else if fields[0] == "pgmajfault" {
			pgmajfault, _ = strconv.ParseUint(fields[1], 10, 64)
		}
	}
	return pgfault, pgmajfault
}

func (m *MemoryCollector) readZram(metrics *madtomv1.MemoryMetrics) {
	// Check /sys/block/zram0/mm_stat
	// Columns: orig_data_size compr_data_size mem_used_total mem_limit max_used_total same_pages pages_compacted ...
	data, err := os.ReadFile("/sys/block/zram0/mm_stat")
	if err == nil {
		fields := strings.Fields(string(data))
		if len(fields) >= 2 {
			metrics.ZramOrigBytes, _ = strconv.ParseUint(fields[0], 10, 64)
			metrics.ZramComprBytes, _ = strconv.ParseUint(fields[1], 10, 64)
			if metrics.ZramComprBytes > 0 {
				metrics.ZramRatio = float64(metrics.ZramOrigBytes) / float64(metrics.ZramComprBytes)
			}
		}
		return
	}

	// Fallback to separate files if mm_stat is not present
	origBytes, err1 := os.ReadFile("/sys/block/zram0/orig_data_size")
	comprBytes, err2 := os.ReadFile("/sys/block/zram0/compr_data_size")
	if err1 == nil && err2 == nil {
		metrics.ZramOrigBytes, _ = strconv.ParseUint(strings.TrimSpace(string(origBytes)), 10, 64)
		metrics.ZramComprBytes, _ = strconv.ParseUint(strings.TrimSpace(string(comprBytes)), 10, 64)
		if metrics.ZramComprBytes > 0 {
			metrics.ZramRatio = float64(metrics.ZramOrigBytes) / float64(metrics.ZramComprBytes)
		}
	}
}
