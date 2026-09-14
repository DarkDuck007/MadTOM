package ingest

import (
	"fmt"
	"sort"
	"strings"
	"sync"

	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

// Pipeline processes incoming batches, writes them to Pebble TSDB, and fans out to live subscribers.
type Pipeline struct {
	mu            sync.RWMutex
	tsdb          *storage.TSDB
	reg           *registry.Registry
	decoder       *zstd.Decoder
	subscribers   map[string][]chan *madtomv1.LiveTelemetryEvent // key: nodeID
	latest        map[string]*madtomv1.SystemMetrics
	collectorName string
}

// NewPipeline creates a new ingestion processing pipeline.
func NewPipeline(tsdb *storage.TSDB, reg *registry.Registry, collectorName string) *Pipeline {
	dec, _ := zstd.NewReader(nil)
	return &Pipeline{
		tsdb:          tsdb,
		reg:           reg,
		decoder:       dec,
		subscribers:   make(map[string][]chan *madtomv1.LiveTelemetryEvent),
		collectorName: collectorName,
		latest:        make(map[string]*madtomv1.SystemMetrics),
	}
}

// ProcessBatch ingests a TelemetryBatch, decompressing if needed, and writes points to TSDB.
func (p *Pipeline) ProcessBatch(batch *madtomv1.TelemetryBatch, mode string) error {
	if batch == nil {
		return nil
	}

	samples := batch.Samples

	// Decompress if payload was compressed
	if batch.IsCompressed && len(batch.CompressedPayload) > 0 && p.decoder != nil {
		decompressed, err := p.decoder.DecodeAll(batch.CompressedPayload, nil)
		if err != nil {
			return fmt.Errorf("decompress batch: %w", err)
		}
		inner := &madtomv1.TelemetryBatch{}
		if err := proto.Unmarshal(decompressed, inner); err != nil {
			return err
		}
		samples = inner.Samples
	}

	p.reg.RegisterOrTouch(batch.NodeId, mode, nil)

	var records []storage.MetricRecord
	var latestSample *madtomv1.SystemMetrics

	cfg := p.reg.GetConfig(batch.NodeId)

	for _, sample := range samples {
		if sample == nil {
			continue
		}
		sample.NodeId = batch.NodeId
		records = appendSampleRecords(records, batch.NodeId, sample, cfg)
		if latestSample == nil || sample.TimestampUnixNano > latestSample.TimestampUnixNano {
			latestSample = sample
		}
	}

	if len(records) > 0 {
		if err := p.tsdb.PutRecordsBatch(records); err != nil {
			return fmt.Errorf("tsdb put batch: %w", err)
		}
	}

	if latestSample != nil {
		p.fanOutLive(batch.NodeId, latestSample)
	}

	return nil
}

func sanitizeMetricName(name string) string {
	var sb strings.Builder
	for _, r := range strings.ToLower(name) {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '_' || r == '-' {
			sb.WriteRune(r)
		} else {
			sb.WriteRune('_')
		}
	}
	s := strings.Trim(sb.String(), "_")
	if s == "" {
		return "unknown"
	}
	return s
}

func appendSampleRecords(records []storage.MetricRecord, nodeID string, s *madtomv1.SystemMetrics, cfg *madtomv1.NodeConfig) []storage.MetricRecord {
	ts := s.TimestampUnixNano
	add := func(metric string, val float64) {
		records = append(records, storage.MetricRecord{
			NodeID:        nodeID,
			MetricName:    metric,
			TimestampNano: ts,
			Value:         val,
		})
	}

	if s.Twamp != nil && s.Twamp.Available {
		add("twamp.rtt", s.Twamp.RttMs)
		if s.Twamp.OneWayAvailable {
			add("twamp.forward", s.Twamp.ForwardMs)
			add("twamp.reverse", s.Twamp.ReverseMs)
		}
	}

	if s.Cpu != nil {
		add("cpu.total", s.Cpu.TotalPct)
		add("cpu.user", s.Cpu.UserPct)
		add("cpu.system", s.Cpu.SystemPct)
		add("cpu.iowait", s.Cpu.IowaitPct)
	}

	if s.Memory != nil {
		add("memory.total", float64(s.Memory.MemTotalBytes))
		add("memory.available", float64(s.Memory.MemAvailableBytes))
		add("memory.used", float64(s.Memory.MemTotalBytes-s.Memory.MemAvailableBytes))
		add("memory.swap_total", float64(s.Memory.SwapTotalBytes))
		add("memory.swap_free", float64(s.Memory.SwapFreeBytes))
		if s.Memory.ZramRatio > 0 {
			add("memory.zram_ratio", s.Memory.ZramRatio)
		}
	}

	if s.Power != nil {
		if s.Power.BatteryPresent {
			add("power.battery_pct", s.Power.BatteryPct)
			add("power.rate_watts", s.Power.RateWatts)
		}
	}

	if s.Network != nil {
		for _, nic := range s.Network.Interfaces {
			add(fmt.Sprintf("nic.%s.rx_bytes", nic.Name), float64(nic.RxBytes))
			add(fmt.Sprintf("nic.%s.tx_bytes", nic.Name), float64(nic.TxBytes))
		}
	}

	if s.DiskIo != nil {
		add("disk.io.read_bytes", float64(s.DiskIo.ReadBytes))
		add("disk.io.write_bytes", float64(s.DiskIo.WriteBytes))
		add("disk.io.read_ops", float64(s.DiskIo.ReadOps))
		add("disk.io.write_ops", float64(s.DiskIo.WriteOps))
		for _, dev := range s.DiskIo.Devices {
			add(fmt.Sprintf("disk.io.%s.read_bytes", dev.Name), float64(dev.ReadBytes))
			add(fmt.Sprintf("disk.io.%s.write_bytes", dev.Name), float64(dev.WriteBytes))
			add(fmt.Sprintf("disk.io.%s.read_ops", dev.Name), float64(dev.ReadOps))
			add(fmt.Sprintf("disk.io.%s.write_ops", dev.Name), float64(dev.WriteOps))
		}
	}

	if cfg != nil && cfg.ProcessMode == madtomv1.ProcessTelemetryMode_PROCESS_MODE_PROBED_AND_STORED && len(s.Processes) > 0 {
		topN := int(cfg.TopNProcesses)
		if topN <= 0 {
			topN = 5
		} else if topN > 10 {
			topN = 10
		}

		nameCpu := make(map[string]float64)
		for _, proc := range s.Processes {
			if proc == nil || proc.Name == "" {
				continue
			}
			nameCpu[proc.Name] += proc.CpuPct
		}

		type procItem struct {
			name string
			cpu  float64
		}
		var sortedProcs []procItem
		for name, cpu := range nameCpu {
			sortedProcs = append(sortedProcs, procItem{name: name, cpu: cpu})
		}
		sort.Slice(sortedProcs, func(i, j int) bool {
			return sortedProcs[i].cpu > sortedProcs[j].cpu
		})

		var topSum float64
		limit := topN
		if len(sortedProcs) < limit {
			limit = len(sortedProcs)
		}
		for i := 0; i < limit; i++ {
			item := sortedProcs[i]
			cleanName := sanitizeMetricName(item.name)
			add(fmt.Sprintf("proc.cpu.%s", cleanName), item.cpu)
			topSum += item.cpu
		}

		totalCpu := 0.0
		if s.Cpu != nil {
			totalCpu = s.Cpu.TotalPct
		}
		otherCpu := totalCpu - topSum
		if otherCpu < 0 {
			otherCpu = 0
		}
		add("proc.cpu.other", otherCpu)
	}

	return records
}

func (p *Pipeline) fanOutLive(nodeID string, sample *madtomv1.SystemMetrics) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if previous := p.latest[nodeID]; previous != nil && previous.TimestampUnixNano >= sample.TimestampUnixNano {
		return
	}
	p.latest[nodeID] = sample

	chans, exists := p.subscribers[nodeID]
	if !exists || len(chans) == 0 {
		return
	}

	event := &madtomv1.LiveTelemetryEvent{
		NodeId:        nodeID,
		CollectorName: p.collectorName,
		Metrics:       sample,
	}

	for _, ch := range chans {
		select {
		case ch <- event:
		default: // Non-blocking drop if client is slow
		}
	}
}

// Subscribe opens a channel receiving live 1Hz telemetry for nodeID.
func (p *Pipeline) Subscribe(nodeID string) (chan *madtomv1.LiveTelemetryEvent, func()) {
	p.mu.Lock()
	defer p.mu.Unlock()

	ch := make(chan *madtomv1.LiveTelemetryEvent, 100)
	p.subscribers[nodeID] = append(p.subscribers[nodeID], ch)
	if sample := p.latest[nodeID]; sample != nil {
		ch <- &madtomv1.LiveTelemetryEvent{NodeId: nodeID, CollectorName: p.collectorName, Metrics: sample}
	}

	unsubscribe := func() {
		p.mu.Lock()
		defer p.mu.Unlock()

		list := p.subscribers[nodeID]
		for i, sub := range list {
			if sub == ch {
				p.subscribers[nodeID] = append(list[:i], list[i+1:]...)
				close(ch)
				break
			}
		}
	}

	return ch, unsubscribe
}
