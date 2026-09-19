package ingest

import (
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestTransportStatsDecodedPayloadsAndRetries(t *testing.T) {
	p := NewPipeline(nil, registry.NewRegistry("test"), "test")
	defer p.decoder.Close()
	enc, err := zstd.NewWriter(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer enc.Close()
	raw, _ := proto.Marshal(&madtomv1.TelemetryBatch{Samples: []*madtomv1.SystemMetrics{{TimestampUnixNano: 1}}})
	payload := enc.EncodeAll(raw, nil)
	b := &madtomv1.TelemetryBatch{NodeId: "n", IsCompressed: true, CompressedPayload: payload}
	for i := 0; i < 2; i++ {
		if err := p.ProcessBatch(b, "PUSH"); err != nil {
			t.Fatal(err)
		}
	}
	if err := p.ProcessBatch(&madtomv1.TelemetryBatch{NodeId: "n", Samples: []*madtomv1.SystemMetrics{{TimestampUnixNano: 1}}}, "PUSH"); err != nil {
		t.Fatal(err)
	}
	if err := p.ProcessBatch(&madtomv1.TelemetryBatch{NodeId: "n", IsCompressed: true, CompressedPayload: []byte("bad")}, "PUSH"); err == nil {
		t.Fatal("expected decode error")
	}
	stats := p.TransportStats()
	if len(stats) != 1 || stats[0].Batches != 3 || stats[0].ZstdBatches != 2 || stats[0].ZstdBytes != uint64(2*len(payload)) || stats[0].DecodedZstdBytes != uint64(2*len(raw)) || stats[0].RawBytes != uint64(len(raw)) || !stats[0].RawBytesSupported {
		t.Fatalf("wrong stats: %v", stats)
	}
	stats[0].Batches = 0
	if p.TransportStats()[0].Batches != 3 {
		t.Fatal("snapshot mutated counters")
	}
	if err := p.ProcessBatch(&madtomv1.TelemetryBatch{NodeId: "n"}, "PULL"); err != nil {
		t.Fatal(err)
	}
	if len(p.TransportStats()) != 2 {
		t.Fatal("modes merged")
	}
}
