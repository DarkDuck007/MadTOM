package ingest

import (
	"fmt"
	"sort"
	"strings"
	"sync"

	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/storage"
	"github.com/DarkDuck007/madtom/pkg/telemetry/optin"
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
	transport     map[string]*madtomv1.TransportCompressionStats
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
		transport:     make(map[string]*madtomv1.TransportCompressionStats),
	}
}

// ProcessBatch ingests a TelemetryBatch, decompressing if needed, and writes points to TSDB.
func (p *Pipeline) ProcessBatch(batch *madtomv1.TelemetryBatch, mode string) error {
	_, err := p.processBatch(batch, mode)
	return err
}

// batchSummary describes successfully ingested samples without retaining decoded payloads.
type batchSummary struct {
	sampleCount     int
	latestTimestamp int64
}

func (p *Pipeline) processBatch(batch *madtomv1.TelemetryBatch, mode string) (batchSummary, error) {
	var summary batchSummary
	if batch == nil {
		return summary, nil
	}

	samples := batch.Samples
	decodedBytes := 0

	// Decompress if payload was compressed
	if batch.IsCompressed && len(batch.CompressedPayload) > 0 && p.decoder != nil {
		decompressed, err := p.decoder.DecodeAll(batch.CompressedPayload, nil)
		if err != nil {
			return batchSummary{}, fmt.Errorf("decompress batch: %w", err)
		}
		inner := &madtomv1.TelemetryBatch{}
		if err := proto.Unmarshal(decompressed, inner); err != nil {
			return batchSummary{}, err
		}
		samples = inner.Samples
		decodedBytes = len(decompressed)
	}

	p.recordTransport(batch, mode, decodedBytes)
	p.reg.RegisterOrTouch(batch.NodeId, mode, nil)

	var records []storage.MetricRecord
	var latestSample *madtomv1.SystemMetrics

	cfg := p.reg.GetConfig(batch.NodeId)

	for _, sample := range samples {
		if sample == nil {
			continue
		}
		summary.sampleCount++
		sample.NodeId = batch.NodeId
		records = appendSampleRecords(records, batch.NodeId, sample, cfg)
		if latestSample == nil || sample.TimestampUnixNano > latestSample.TimestampUnixNano {
			latestSample = sample
		}
	}

	if len(records) > 0 {
		if err := p.tsdb.PutRecordsBatch(records); err != nil {
			return batchSummary{}, fmt.Errorf("tsdb put batch: %w", err)
		}
	}

	if latestSample != nil {
		summary.latestTimestamp = latestSample.TimestampUnixNano
		p.fanOutLive(batch.NodeId, latestSample)
	}

	return summary, nil
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
		if optin.GetMetricOptInMode(cfg, metric) != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
			return
		}
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
		for i, corePct := range s.Cpu.PerCorePct {
			add(fmt.Sprintf("cpu.core.%d", i), corePct)
		}
	}

	if s.Memory != nil {
		add("memory.total", float64(s.Memory.MemTotalBytes))
		add("memory.available", float64(s.Memory.MemAvailableBytes))
		add("memory.used", float64(s.Memory.MemTotalBytes-s.Memory.MemAvailableBytes))
		add("memory.swap_total", float64(s.Memory.SwapTotalBytes))
		add("memory.swap_used", float64(s.Memory.SwapUsedBytes))

		for _, dev := range s.Memory.SwapDevices {
			if dev != nil && dev.Name != "" {
				add(fmt.Sprintf("swap.%s.total_bytes", dev.Name), float64(dev.TotalBytes))
				add(fmt.Sprintf("swap.%s.used_bytes", dev.Name), float64(dev.UsedBytes))
			}
		}

		for _, dev := range s.Memory.ZramDevices {
			if dev != nil && dev.Name != "" {
				add(fmt.Sprintf("zram.%s.disksize_bytes", dev.Name), float64(dev.DisksizeBytes))
				add(fmt.Sprintf("zram.%s.mem_used_bytes", dev.Name), float64(dev.MemUsedBytes))
				add(fmt.Sprintf("zram.%s.orig_data_bytes", dev.Name), float64(dev.OrigDataBytes))
				add(fmt.Sprintf("zram.%s.compr_data_bytes", dev.Name), float64(dev.ComprDataBytes))
			}
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
		default:
			// Replace the pending snapshot. The consumer may have drained it
			// since the first select, so receiving must also be non-blocking.
			select {
			case <-ch:
			default:
			}
			// Publishers and unsubscribe hold p.mu; only the consumer can
			// touch this channel concurrently, and it can only free space.
			ch <- event
		}
	}
}

// Subscribe retains at most one pending snapshot, replacing it with newer telemetry.
func (p *Pipeline) Subscribe(nodeID string) (<-chan *madtomv1.LiveTelemetryEvent, func()) {
	p.mu.Lock()
	defer p.mu.Unlock()

	ch := make(chan *madtomv1.LiveTelemetryEvent, 1)
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
				copy(list[i:], list[i+1:])
				list[len(list)-1] = nil // Release the channel from the backing array.
				list = list[:len(list)-1]
				if len(list) == 0 {
					delete(p.subscribers, nodeID)
				} else {
					p.subscribers[nodeID] = list
				}
				close(ch)
				break
			}
		}
	}

	return ch, unsubscribe
}
