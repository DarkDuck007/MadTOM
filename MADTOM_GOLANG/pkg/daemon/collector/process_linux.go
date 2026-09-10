package collector

import (
	"sort"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Read process snapshots from procfs. CPU share uses aggregate CPU tick deltas,
// avoiding assumptions about the kernel's CLK_TCK setting.
type ProcessCollector struct {
	previous map[int32]uint64
	total    uint64
}

func NewProcessCollector() *ProcessCollector { return &ProcessCollector{previous: map[int32]uint64{}} }
func (p *ProcessCollector) Collect() ([]*madtomv1.ProcessMetric, bool) {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil, false
	}
	stat, err := os.ReadFile("/proc/stat")
	if err != nil {
		return nil, false
	}
	var total uint64
	for _, v := range strings.Fields(strings.SplitN(string(stat), "\n", 2)[0])[1:] {
		n, _ := strconv.ParseUint(v, 10, 64)
		total += n
	}
	next := map[int32]uint64{}
	var result []*madtomv1.ProcessMetric
	for _, entry := range entries {
		pid, err := strconv.ParseInt(entry.Name(), 10, 32)
		if err != nil {
			continue
		}
		raw, err := os.ReadFile(filepath.Join("/proc", entry.Name(), "stat"))
		if err != nil {
			continue
		}
		line := string(raw)
		left, right := strings.Index(line, "("), strings.LastIndex(line, ")")
		if left < 0 || right < left {
			continue
		}
		fields := strings.Fields(line[right+1:])
		if len(fields) < 22 {
			continue
		}
		ut, _ := strconv.ParseUint(fields[11], 10, 64)
		st, _ := strconv.ParseUint(fields[12], 10, 64)
		ticks := ut + st
		next[int32(pid)] = ticks
		cpu := 0.0
		if prev, ok := p.previous[int32(pid)]; ok && total > p.total && ticks >= prev {
			cpu = 100 * float64(ticks-prev) / float64(total-p.total)
		}
		threads, _ := strconv.ParseUint(fields[17], 10, 32)
		rss, _ := strconv.ParseUint(fields[21], 10, 64)
		uid := "unknown"
		status, _ := os.ReadFile(filepath.Join("/proc", entry.Name(), "status"))
		for _, l := range strings.Split(string(status), "\n") {
			if strings.HasPrefix(l, "Uid:") {
				f := strings.Fields(l)
				if len(f) > 1 {
					uid = f[1]
				}
				break
			}
		}
		result = append(result, &madtomv1.ProcessMetric{Pid: int32(pid), Name: line[left+1 : right], User: uid, Threads: uint32(threads), CpuPct: cpu, RssBytes: rss * uint64(os.Getpagesize())})
	}
	// A process snapshot is telemetry, not a full process dump. Keep the most
	// useful bounded subset so a node with tens of thousands of short-lived
	// processes cannot freeze the collector UI or exhaust the WAL.
	sort.Slice(result, func(i, j int) bool {
		if result[i].CpuPct != result[j].CpuPct { return result[i].CpuPct > result[j].CpuPct }
		return result[i].RssBytes > result[j].RssBytes
	})
	if len(result) > 1000 { result = result[:1000] }
	p.previous = next
	p.total = total
	return result, true
}
