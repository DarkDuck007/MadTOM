package collector

import (
	"bufio"
	"os"
	"path/filepath"
	"sort"
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

	// ZRAM & Swap partition metrics
	if includeSwapZram {
		m.readSwaps(metrics)
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

	if metrics.SwapTotalBytes >= metrics.SwapFreeBytes {
		metrics.SwapUsedBytes = metrics.SwapTotalBytes - metrics.SwapFreeBytes
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

func (m *MemoryCollector) readSwaps(metrics *madtomv1.MemoryMetrics) {
	file, err := os.Open("/proc/swaps")
	if err != nil {
		return
	}
	defer file.Close()

	scanner := bufio.NewScanner(file)
	// Skip header line
	if scanner.Scan() {
		_ = scanner.Text()
	}

	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) < 5 {
			continue
		}
		filePath := fields[0]
		devName := strings.TrimPrefix(filePath, "/dev/")
		if strings.HasPrefix(devName, "/") {
			devName = strings.TrimPrefix(devName, "/")
		}
		if devName == "" {
			devName = filePath
		}

		sizeKb, err1 := strconv.ParseUint(fields[2], 10, 64)
		usedKb, err2 := strconv.ParseUint(fields[3], 10, 64)
		prio, _ := strconv.ParseInt(fields[4], 10, 32)

		if err1 == nil && err2 == nil {
			metrics.SwapDevices = append(metrics.SwapDevices, &madtomv1.SwapDevice{
				Name:       devName,
				TotalBytes: sizeKb * 1024,
				UsedBytes:  usedKb * 1024,
				Priority:   uint32(prio),
			})
		}
	}
}

func (m *MemoryCollector) readZram(metrics *madtomv1.MemoryMetrics) {
	matches, err := filepath.Glob("/sys/block/zram*")
	if err != nil || len(matches) == 0 {
		return
	}
	sort.Strings(matches)

	var totalOrig, totalCompr uint64

	for _, dir := range matches {
		devName := filepath.Base(dir)
		var disksize, origBytes, comprBytes, memUsed uint64

		if data, err := os.ReadFile(filepath.Join(dir, "disksize")); err == nil {
			disksize, _ = strconv.ParseUint(strings.TrimSpace(string(data)), 10, 64)
		}

		if data, err := os.ReadFile(filepath.Join(dir, "mm_stat")); err == nil {
			fields := strings.Fields(string(data))
			if len(fields) >= 3 {
				origBytes, _ = strconv.ParseUint(fields[0], 10, 64)
				comprBytes, _ = strconv.ParseUint(fields[1], 10, 64)
				memUsed, _ = strconv.ParseUint(fields[2], 10, 64)
			}
		} else {
			if b, err := os.ReadFile(filepath.Join(dir, "orig_data_size")); err == nil {
				origBytes, _ = strconv.ParseUint(strings.TrimSpace(string(b)), 10, 64)
			}
			if b, err := os.ReadFile(filepath.Join(dir, "compr_data_size")); err == nil {
				comprBytes, _ = strconv.ParseUint(strings.TrimSpace(string(b)), 10, 64)
			}
			if b, err := os.ReadFile(filepath.Join(dir, "mem_used_total")); err == nil {
				memUsed, _ = strconv.ParseUint(strings.TrimSpace(string(b)), 10, 64)
			}
		}

		metrics.ZramDevices = append(metrics.ZramDevices, &madtomv1.ZramDevice{
			Name:           devName,
			DisksizeBytes:  disksize,
			OrigDataBytes:  origBytes,
			ComprDataBytes: comprBytes,
			MemUsedBytes:   memUsed,
		})

		totalOrig += origBytes
		totalCompr += comprBytes
	}

	metrics.ZramOrigBytes = totalOrig
	metrics.ZramComprBytes = totalCompr
	if totalCompr > 0 {
		metrics.ZramRatio = float64(totalOrig) / float64(totalCompr)
	}
}
