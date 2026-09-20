package ingest

import (
	"context"
	"net"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
)

type slowBacklogServer struct {
	madtomv1.UnimplementedIngestServiceServer
}

func (*slowBacklogServer) PollTelemetry(ctx context.Context, req *madtomv1.PollRequest) (*madtomv1.TelemetryBatch, error) {
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-time.After(3 * time.Second):
	}
	timestamp := time.Now()
	segment := "latest"
	if req.LastAckedSegmentId == "" {
		timestamp = timestamp.Add(-time.Hour)
		segment = "backlog"
	}
	return &madtomv1.TelemetryBatch{NodeId: "node", SegmentId: segment, SegmentOffset: 1, Samples: []*madtomv1.SystemMetrics{{NodeId: "node", TimestampUnixNano: timestamp.UnixNano(), Cpu: &madtomv1.CpuMetrics{TotalPct: 10}}}}, nil
}

func TestPullBacklogUsesIndependentRPCDeadlines(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	server := grpc.NewServer()
	madtomv1.RegisterIngestServiceServer(server, &slowBacklogServer{})
	go server.Serve(listener)
	defer server.Stop()
	db, err := storage.OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	scraper := NewPullScraper(NewPipeline(db, registry.NewRegistry("test"), "test"))
	defer scraper.Stop()
	target := &ScrapeTarget{NodeID: "node", Address: listener.Addr().String(), Interval: 5 * time.Second}
	scraper.executeScrape(target)
	if target.LastAckedSeg != "latest" {
		t.Fatalf("backlog drain stopped at %q", target.LastAckedSeg)
	}
}
