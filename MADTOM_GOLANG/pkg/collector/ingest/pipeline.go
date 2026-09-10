package ingest

import (
	"fmt"
	"sync"
	"time"

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

	for _, sample := range samples {
		if sample == nil || sample.NodeId != batch.NodeId {
			return fmt.Errorf("sample node ID mismatch")
		}
		if err := p.ingestSample(batch.NodeId, sample); err != nil {
			return err
		}
		if time.Since(time.Unix(0, sample.TimestampUnixNano)) < 30*time.Second {
			p.fanOutLive(batch.NodeId, sample)
		}
	}

	return nil
}

func (p *Pipeline) ingestSample(nodeID string, s *madtomv1.SystemMetrics) error {
	var writeErr error
	put := func(node, metric string, ts int64, value float64) {
		if writeErr == nil {
			writeErr = p.tsdb.PutMetric(node, metric, ts, value)
		}
	}
	ts := s.TimestampUnixNano
	if s.Twamp != nil && s.Twamp.Available {
		put(nodeID, "twamp.rtt", ts, s.Twamp.RttMs)
		if s.Twamp.OneWayAvailable {
			put(nodeID, "twamp.forward", ts, s.Twamp.ForwardMs)
			put(nodeID, "twamp.reverse", ts, s.Twamp.ReverseMs)
		}
	}

	if s.Cpu != nil {
		put(nodeID, "cpu.total", ts, s.Cpu.TotalPct)
		put(nodeID, "cpu.user", ts, s.Cpu.UserPct)
		put(nodeID, "cpu.system", ts, s.Cpu.SystemPct)
		put(nodeID, "cpu.iowait", ts, s.Cpu.IowaitPct)
	}

	if s.Memory != nil {
		put(nodeID, "memory.total", ts, float64(s.Memory.MemTotalBytes))
		put(nodeID, "memory.available", ts, float64(s.Memory.MemAvailableBytes))
		put(nodeID, "memory.used", ts, float64(s.Memory.MemTotalBytes-s.Memory.MemAvailableBytes))
		put(nodeID, "memory.swap_total", ts, float64(s.Memory.SwapTotalBytes))
		put(nodeID, "memory.swap_free", ts, float64(s.Memory.SwapFreeBytes))
		if s.Memory.ZramRatio > 0 {
			put(nodeID, "memory.zram_ratio", ts, s.Memory.ZramRatio)
		}
	}

	if s.Power != nil {
		if s.Power.BatteryPresent {
			put(nodeID, "power.battery_pct", ts, s.Power.BatteryPct)
			put(nodeID, "power.rate_watts", ts, s.Power.RateWatts)
		}
	}

	if s.Network != nil {
		for _, nic := range s.Network.Interfaces {
			put(nodeID, fmt.Sprintf("nic.%s.rx_bytes", nic.Name), ts, float64(nic.RxBytes))
			put(nodeID, fmt.Sprintf("nic.%s.tx_bytes", nic.Name), ts, float64(nic.TxBytes))
		}
	}
	return writeErr
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
