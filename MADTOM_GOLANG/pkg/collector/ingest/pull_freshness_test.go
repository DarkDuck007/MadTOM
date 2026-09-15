package ingest

import (
	"context"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
)

type freshnessServer struct {
	madtomv1.UnimplementedIngestServiceServer
	batches []*madtomv1.TelemetryBatch
	calls   atomic.Int32
}

func (s *freshnessServer) PollTelemetry(_ context.Context, req *madtomv1.PollRequest) (*madtomv1.TelemetryBatch, error) {
	i := int(s.calls.Add(1)) - 1
	if i >= len(s.batches) {
		return nil, status.Error(codes.Internal, "unexpected extra poll")
	}
	if i > 0 && (req.LastAckedSegmentId != s.batches[i-1].SegmentId || req.LastAckedOffset != s.batches[i-1].SegmentOffset) {
		return nil, status.Error(codes.Internal, "missing durable acknowledgement")
	}
	return s.batches[i], nil
}

func TestPullFreshnessUsesIngestedSamples(t *testing.T) {
	for _, name := range []string{"raw", "compressed", "unordered", "empty", "corrupt"} {
		t.Run(name, func(t *testing.T) {
			db, err := storage.OpenTSDB(t.TempDir())
			if err != nil {
				t.Fatal(err)
			}
			defer db.Close()
			pipeline := NewPipeline(db, registry.NewRegistry("test"), "test")
			defer pipeline.decoder.Close()
			now := time.Now().UnixNano()
			old := now - int64(time.Hour)
			metric := func(ts int64) *madtomv1.SystemMetrics {
				return &madtomv1.SystemMetrics{TimestampUnixNano: ts, Cpu: &madtomv1.CpuMetrics{TotalPct: 10}}
			}
			backlog := &madtomv1.TelemetryBatch{NodeId: "node", SegmentId: "old", SegmentOffset: 1, Samples: []*madtomv1.SystemMetrics{metric(old)}}
			latest := &madtomv1.TelemetryBatch{NodeId: "node", SegmentId: "new", SegmentOffset: 2, Samples: []*madtomv1.SystemMetrics{metric(now)}}
			wantPoints := 2
			if name == "unordered" {
				latest.Samples = append(latest.Samples, metric(old))
			}
			if name == "empty" {
				latest.Samples = nil
				wantPoints = 1
			}
			if name == "compressed" {
				encoder, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1))
				if err != nil {
					t.Fatal(err)
				}
				defer encoder.Close()
				for _, batch := range []*madtomv1.TelemetryBatch{backlog, latest} {
					raw, err := proto.Marshal(&madtomv1.TelemetryBatch{Samples: batch.Samples})
					if err != nil {
						t.Fatal(err)
					}
					batch.CompressedPayload = encoder.EncodeAll(raw, nil)
					batch.IsCompressed = true
					batch.Samples = nil
				}
			}
			if name == "corrupt" {
				latest.Samples = nil
				latest.IsCompressed = true
				latest.CompressedPayload = []byte("invalid zstd")
				wantPoints = 1
			}
			service := &freshnessServer{batches: []*madtomv1.TelemetryBatch{backlog, latest}}
			listener := bufconn.Listen(1024 * 1024)
			defer listener.Close()
			server := grpc.NewServer()
			madtomv1.RegisterIngestServiceServer(server, service)
			go server.Serve(listener)
			defer server.Stop()
			conn, err := grpc.NewClient("passthrough:///test", grpc.WithTransportCredentials(insecure.NewCredentials()), grpc.WithContextDialer(func(context.Context, string) (net.Conn, error) { return listener.Dial() }))
			if err != nil {
				t.Fatal(err)
			}
			defer conn.Close()
			scraper := NewPullScraper(pipeline)
			defer scraper.Stop()
			target := &ScrapeTarget{NodeID: "node", Interval: time.Minute, conn: conn}
			scraper.executeScrape(target)
			if got := service.calls.Load(); got != 2 {
				t.Fatalf("got %d polls, want 2", got)
			}
			wantAck := latest
			if name == "corrupt" {
				wantAck = backlog
			}
			if target.LastAckedSeg != wantAck.SegmentId || target.LastAckedOffset != wantAck.SegmentOffset {
				t.Fatalf("unexpected ACK: %s/%d", target.LastAckedSeg, target.LastAckedOffset)
			}
			points, err := db.QueryRange("node", "cpu.total", old, now)
			if err != nil || len(points) != wantPoints {
				t.Fatalf("stored points: %v, error: %v", points, err)
			}
			if name == "compressed" && len(latest.Samples) != 0 {
				t.Fatal("retained decoded payload on input batch")
			}
		})
	}
}
