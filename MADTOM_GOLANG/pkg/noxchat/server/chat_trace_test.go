package server

import (
	"context"
	"testing"

	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
)

func newTraceServer(t *testing.T) *Server {
	t.Helper()
	cfg := config.DefaultConfig()
	cfg.SpoolDir = t.TempDir()
	cfg.StoreRawLogs = true
	s, err := NewServer(cfg, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(s.Stop)
	return s
}

func TestChatTraceQueryIncludesStagedCallsAndKeepsSnapshotAcrossLiveAppend(t *testing.T) {
	s := newTraceServer(t)
	for i, id := range []string{"a", "b"} {
		s.OnApiCallRecord(&pb.NoxRawApiCallRecord{CallId: id, TimestampUnixNano: int64(i + 1), TraceContext: &pb.NoxTraceContext{ChatId: proto.Int64(0)}})
	}
	first, err := s.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{NodeId: s.cfg.NodeID, ChatId: proto.Int64(0), Limit: 1})
	if err != nil || len(first.Calls) != 1 || first.Calls[0].CallId != "a" || first.NextCursor == "" {
		t.Fatalf("%v %v", first, err)
	}
	s.OnApiCallRecord(&pb.NoxRawApiCallRecord{CallId: "new", TimestampUnixNano: 3, TraceContext: &pb.NoxTraceContext{ChatId: proto.Int64(0)}})
	second, err := s.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{NodeId: s.cfg.NodeID, ChatId: proto.Int64(0), Limit: 1, Cursor: first.NextCursor})
	if err != nil || len(second.Calls) != 1 || second.Calls[0].CallId != "b" || !second.SnapshotComplete {
		t.Fatalf("%v %v", second, err)
	}
	current, err := s.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{NodeId: s.cfg.NodeID, ChatId: proto.Int64(0)})
	if err != nil || len(current.Calls) != 3 {
		t.Fatalf("%v %v", current, err)
	}
}

func TestChatTraceEventsStreamAndRespectStorePolicy(t *testing.T) {
	s := newTraceServer(t)
	ch := make(chan *pb.NoxLogApiChunk, 4)
	s.logSubs[1] = ch
	event := &pb.NoxChatTraceEvent{EventId: "stored", TraceContext: &pb.NoxTraceContext{ChatId: proto.Int64(0)}, TimestampUnixNano: 1, Kind: "rag_result", Content: "memory"}
	s.OnChatTraceEvent(event)
	select {
	case msg := <-ch:
		var actual pb.NoxChatTraceEvent
		if msg.ChunkKind != "chat_trace" || proto.Unmarshal(msg.ZstdPayload, &actual) != nil || actual.EventId != "stored" {
			t.Fatal(msg)
		}
	default:
		t.Fatal("trace event was not streamed")
	}
	s.cfg.StoreRawLogs = false
	event = proto.Clone(event).(*pb.NoxChatTraceEvent)
	event.EventId = "live-only"
	s.OnChatTraceEvent(event)
	got, err := s.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{NodeId: s.cfg.NodeID, ChatId: proto.Int64(0)})
	if err != nil || len(got.Events) != 1 || got.Events[0].EventId != "stored" {
		t.Fatalf("%v %v", got, err)
	}
	select {
	case msg := <-ch:
		if msg.ChunkKind != "chat_trace" {
			t.Fatal(msg)
		}
	default:
		t.Fatal("monitor-only event was not streamed")
	}
}

func TestChatTraceRejectsWrongNodeMissingIdentityAndBusyQueries(t *testing.T) {
	s := newTraceServer(t)
	for _, req := range []*pb.NoxChatTraceRequest{{NodeId: "wrong", ChatId: proto.Int64(0)}, {NodeId: s.cfg.NodeID}, {NodeId: s.cfg.NodeID, ChatId: proto.Int64(0), Limit: 201}} {
		if _, err := s.QueryChatTrace(context.Background(), req); status.Code(err) != codes.InvalidArgument {
			t.Fatal(err)
		}
	}
	s.chatTraceSlots = make(chan struct{}, 2)
	s.chatTraceSlots <- struct{}{}
	s.chatTraceSlots <- struct{}{}
	if _, err := s.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{NodeId: s.cfg.NodeID, ChatId: proto.Int64(0)}); status.Code(err) != codes.ResourceExhausted {
		t.Fatal(err)
	}
	<-s.chatTraceSlots
	<-s.chatTraceSlots
}
