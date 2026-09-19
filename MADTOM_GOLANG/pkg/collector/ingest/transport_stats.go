package ingest

import (
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/proto"
	"sort"
	"time"
)

func (p *Pipeline) recordTransport(batch *madtomv1.TelemetryBatch, mode string, decodedBytes int) {
	if batch.NodeId == "" {
		return
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	key := batch.NodeId + "\x00" + mode
	stats := p.transport[key]
	if stats == nil {
		stats = &madtomv1.TransportCompressionStats{NodeId: batch.NodeId, Mode: mode, RawBytesSupported: true}
		p.transport[key] = stats
	}
	stats.Batches++
	stats.LastSeenUnixNano = time.Now().UnixNano()
	if batch.IsCompressed && decodedBytes > 0 {
		stats.ZstdBatches++
		stats.ZstdBytes += uint64(len(batch.CompressedPayload))
		stats.DecodedZstdBytes += uint64(decodedBytes)
	} else {
		stats.RawBytes += uint64(proto.Size(&madtomv1.TelemetryBatch{Samples: batch.Samples}))
	}
}

// TransportStats returns independent snapshots; callers cannot mutate live counters.
func (p *Pipeline) TransportStats() []*madtomv1.TransportCompressionStats {
	if p == nil {
		return nil
	}
	p.mu.RLock()
	defer p.mu.RUnlock()
	result := make([]*madtomv1.TransportCompressionStats, 0, len(p.transport))
	for _, stats := range p.transport {
		result = append(result, proto.Clone(stats).(*madtomv1.TransportCompressionStats))
	}
	sort.Slice(result, func(i, j int) bool {
		if result[i].NodeId == result[j].NodeId {
			return result[i].Mode < result[j].Mode
		}
		return result[i].NodeId < result[j].NodeId
	})
	return result
}
