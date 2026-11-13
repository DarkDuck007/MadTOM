package server

import (
	"context"
	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	"github.com/DarkDuck007/madtom/pkg/noxchat/spool"
	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"testing"
)

func TestSearchLogsNodeAvailabilityAndConcurrency(t *testing.T) {
	cfg := config.DefaultConfig()
	w, err := spool.NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	s := &Server{cfg: cfg, walSpool: w}
	req := &pb.NoxSearchLogsRequest{NodeId: "wrong", Query: "needle"}
	if _, err := s.SearchLogs(context.Background(), req); status.Code(err) != codes.InvalidArgument {
		t.Fatal(err)
	}
	req.NodeId = cfg.NodeID
	resp, err := s.SearchLogs(context.Background(), req)
	if err != nil || !resp.SnapshotComplete {
		t.Fatalf("%v %v", resp, err)
	}
	s.searchSlots <- struct{}{}
	if _, err := s.SearchLogs(context.Background(), req); status.Code(err) != codes.ResourceExhausted {
		t.Fatal(err)
	}
	<-s.searchSlots
	cfg.LogSearchEnabled = false
	if _, err := s.SearchLogs(context.Background(), req); status.Code(err) != codes.Unimplemented {
		t.Fatal(err)
	}
}

func TestLogHeadersPreserveRangesWithoutMutatingStoredPayload(t *testing.T) {
	w, err := spool.NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	c := &pb.LogChunk{ChunkId: 7, FirstSeq: 2000, LastSeq: 2255, RecordCount: 256, ZstdRecords: []byte("payload")}
	if err := w.WriteLogChunk(c); err != nil {
		t.Fatal(err)
	}
	s := &Server{cfg: config.DefaultConfig(), walSpool: w}
	headers, err := s.QueryLogChunks(context.Background(), &pb.LogChunkQuery{SummaryOnly: true})
	if err != nil || len(headers.Chunks) != 1 {
		t.Fatalf("%v %v", headers, err)
	}
	h := headers.Chunks[0]
	if h.FirstSeq != 2000 || h.LastSeq != 2255 || len(h.ZstdRecords) != 0 {
		t.Fatal(h)
	}
	full, err := s.QueryLogChunks(context.Background(), &pb.LogChunkQuery{StartChunkId: 7, MaxChunks: 1})
	if err != nil || string(full.Chunks[0].ZstdRecords) != "payload" {
		t.Fatalf("%v %v", full, err)
	}
}
