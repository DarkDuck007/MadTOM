package collector

import (
	"container/heap"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// Read process snapshots from procfs. CPU share uses aggregate CPU tick deltas,
// avoiding assumptions about the kernel's CLK_TCK setting.
const processSnapshotLimit = 1000

type processCandidate struct {
	pid     int32
	name    string
	threads uint32
	cpu     float64
	rss     uint64
}

func betterProcess(a, b processCandidate) bool {
	if a.cpu != b.cpu {
		return a.cpu > b.cpu
	}
	if a.rss != b.rss {
		return a.rss > b.rss
	}
	return a.pid < b.pid
}

type processHeap []processCandidate

func (h processHeap) Len() int           { return len(h) }
func (h processHeap) Less(i, j int) bool { return betterProcess(h[j], h[i]) }
func (h processHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }
func (h *processHeap) Push(x any)        { *h = append(*h, x.(processCandidate)) }
func (h *processHeap) Pop() any          { old := *h; x := old[len(old)-1]; *h = old[:len(old)-1]; return x }

type ProcessCollector struct {
	procRoot string
	readFile func(string) ([]byte, error)
	previous map[int32]uint64
	total    uint64
}

func NewProcessCollector() *ProcessCollector {
	return &ProcessCollector{previous: map[int32]uint64{}, procRoot: "/proc", readFile: os.ReadFile}
}
func (p *ProcessCollector) Collect() ([]*madtomv1.ProcessMetric, bool) {
	return p.CollectTop(0)
}

func (p *ProcessCollector) CollectTop(configuredLimit uint32) ([]*madtomv1.ProcessMetric, bool) {
	limit := processSnapshotLimit
	if configuredLimit > 0 && configuredLimit < uint32(limit) {
		limit = int(configuredLimit)
	}
	entries, err := os.ReadDir(p.procRoot)
	if err != nil {
		return nil, false
	}
	stat, err := p.readFile(filepath.Join(p.procRoot, "stat"))
	if err != nil {
		return nil, false
	}
	var total uint64
	for _, v := range strings.Fields(strings.SplitN(string(stat), "\n", 2)[0])[1:] {
		n, _ := strconv.ParseUint(v, 10, 64)
		total += n
	}
	next := map[int32]uint64{}
	candidates := make(processHeap, 0, limit)
	for _, entry := range entries {
		pid, err := strconv.ParseInt(entry.Name(), 10, 32)
		if err != nil {
			continue
		}
		raw, err := p.readFile(filepath.Join(p.procRoot, entry.Name(), "stat"))
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
		candidate := processCandidate{int32(pid), strings.Clone(line[left+1 : right]), uint32(threads), cpu, rss * uint64(os.Getpagesize())}
		if len(candidates) < limit {
			heap.Push(&candidates, candidate)
		} else if betterProcess(candidate, candidates[0]) {
			candidates[0] = candidate
			heap.Fix(&candidates, 0)
		}
	}
	// Only materialize protobufs and read UID/status details for retained leaders.
	sort.Slice(candidates, func(i, j int) bool { return betterProcess(candidates[i], candidates[j]) })
	result := make([]*madtomv1.ProcessMetric, 0, len(candidates))
	for _, c := range candidates {
		uid := "unknown"
		status, _ := p.readFile(filepath.Join(p.procRoot, strconv.Itoa(int(c.pid)), "status"))
		for _, line := range strings.Split(string(status), "\n") {
			if strings.HasPrefix(line, "Uid:") {
				fields := strings.Fields(line)
				if len(fields) > 1 {
					uid = fields[1]
				}
				break
			}
		}
		result = append(result, &madtomv1.ProcessMetric{Pid: c.pid, Name: c.name, User: uid, Threads: c.threads, CpuPct: c.cpu, RssBytes: c.rss})
	}

	p.previous = next
	p.total = total
	return result, true
}
