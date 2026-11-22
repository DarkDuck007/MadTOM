package spool

import (
	"context"
	"strings"
	"testing"

	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
)

func traceContext(chatID int64) *pb.NoxTraceContext {
	return &pb.NoxTraceContext{ChatId: proto.Int64(chatID), SessionId: "session", RunId: "run", TurnIndex: proto.Int64(2), StepIndex: proto.Int64(1)}
}

func traceCall(id string, chatID int64, ts int64) *pb.NoxRawApiCallRecord {
	return &pb.NoxRawApiCallRecord{CallId: id, ChatName: "Same title", TimestampUnixNano: ts, Caller: "stream_step", Model: "model", RawRequestJson: `[{"role":"system","content":"Nox"}]`, RawResponseJson: `{"content":"output"}`, TraceContext: traceContext(chatID)}
}

func apiChunk(t *testing.T, id uint64, records ...*pb.NoxRawApiCallRecord) *pb.NoxApiCallChunk {
	t.Helper()
	raw, err := proto.Marshal(&pb.NoxApiCallRecordList{Records: records})
	if err != nil {
		t.Fatal(err)
	}
	encoder, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1))
	if err != nil {
		t.Fatal(err)
	}
	defer encoder.Close()
	return &pb.NoxApiCallChunk{ChunkId: id, RecordCount: uint32(len(records)), FirstSeq: id * 16, LastSeq: id*16 + uint64(len(records)), FirstTsUnixNano: records[0].TimestampUnixNano, LastTsUnixNano: records[len(records)-1].TimestampUnixNano, RawSize: uint32(len(raw)), ZstdRecords: encoder.EncodeAll(raw, nil)}
}

func traceEvent(id string, chatID int64, ts int64) *pb.NoxChatTraceEvent {
	return &pb.NoxChatTraceEvent{EventId: id, TraceContext: traceContext(chatID), TimestampUnixNano: ts, Kind: "tool_result", Content: "complete result", Attributes: map[string]string{"tool_call_id": "tool-1"}}
}

func queryTrace(t *testing.T, w *WalSpool, req *pb.NoxChatTraceRequest) *pb.NoxChatTraceResponse {
	t.Helper()
	response, err := w.QueryChatTrace(context.Background(), req)
	if err != nil {
		t.Fatal(err)
	}
	return response
}

func TestChatTraceUsesStableChatZeroIdentityAndPreservesRawBodies(t *testing.T) {
	w, err := NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	zero := traceCall("zero", 0, 10)
	other := traceCall("other", 42, 10)
	unknown := traceCall("unknown", 0, 10)
	unknown.TraceContext = nil
	if err = w.WriteApiCallChunk(apiChunk(t, 1, zero, other, unknown)); err != nil {
		t.Fatal(err)
	}
	if err = w.WriteChatTraceEvent(traceEvent("event", 0, 20)); err != nil {
		t.Fatal(err)
	}
	first := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 1})
	if len(first.Calls) != 1 || first.Calls[0].CallId != "zero" || first.NextCursor == "" || first.SnapshotComplete {
		t.Fatal(first)
	}
	if first.Calls[0].RawRequestJson != "" || first.Calls[0].TraceContext.ChatId == nil || first.Calls[0].TraceContext.GetChatId() != 0 {
		t.Fatal(first)
	}
	full, err := w.GetApiCall("zero")
	if err != nil || full.RawRequestJson != zero.RawRequestJson || full.RawResponseJson != zero.RawResponseJson {
		t.Fatalf("%v %v", full, err)
	}
	second := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 1, Cursor: first.NextCursor})
	if len(second.Events) != 1 || second.Events[0].EventId != "event" || second.NextCursor != "" || !second.SnapshotComplete {
		t.Fatal(second)
	}
	// Returned messages cannot mutate the retained index.
	first.Calls[0].TraceContext.ChatId = proto.Int64(99)
	if got := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0)}); len(got.Calls) != 1 {
		t.Fatal(got)
	}
	// Summary-only raw API paging also carries the correlation context.
	chunks, _, err := w.QueryApiCallChunks(0, 1, true)
	if err != nil {
		t.Fatal(err)
	}
	decoder, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatal(err)
	}
	defer decoder.Close()
	raw, err := decoder.DecodeAll(chunks[0].ZstdRecords, nil)
	if err != nil {
		t.Fatal(err)
	}
	list := new(pb.NoxApiCallRecordList)
	if err = proto.Unmarshal(raw, list); err != nil {
		t.Fatal(err)
	}
	if list.Records[0].TraceContext.ChatId == nil || list.Records[0].TraceContext.GetRunId() != "run" || list.Records[0].RawRequestJson != "" {
		t.Fatal(list)
	}
}

func TestChatTraceSnapshotExcludesLiveAppendsAndSurvivesUnsealedSnapshots(t *testing.T) {
	w, err := NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	a, b := traceCall("a", 7, 10), traceCall("b", 7, 20)
	if err = w.WriteApiCallChunk(apiChunk(t, 1, a, b)); err != nil {
		t.Fatal(err)
	}
	first := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7), Limit: 1})
	if err = w.WriteApiCallChunk(apiChunk(t, 1, a, b, traceCall("new", 7, 5))); err != nil {
		t.Fatal(err)
	}
	if err = w.WriteChatTraceEvent(traceEvent("later", 7, 30)); err != nil {
		t.Fatal(err)
	}
	second := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7), Limit: 1, Cursor: first.NextCursor})
	if len(second.Calls) != 1 || second.Calls[0].CallId != "b" || !second.SnapshotComplete {
		t.Fatal(second)
	}
	if fresh := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7)}); len(fresh.Calls) != 3 || len(fresh.Events) != 1 {
		t.Fatal(fresh)
	}
	// Querying summaries must never decode all retained API payloads again.
	w.apiChunks[1].ZstdRecords = []byte("changed only for this test")
	if got := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7)}); len(got.Calls) != 3 {
		t.Fatal(got)
	}
}

func TestChatTraceWALRecoveryAndSessionFilters(t *testing.T) {
	dir := t.TempDir()
	w, err := NewWalSpool(dir, 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	a := traceCall("a", 7, 10)
	b := traceCall("b", 7, 20)
	b.TraceContext.RunId = "other-run"
	if err = w.WriteApiCallChunk(apiChunk(t, 1, a, b)); err != nil {
		t.Fatal(err)
	}
	if err = w.WriteChatTraceEvent(traceEvent("event", 7, 30)); err != nil {
		t.Fatal(err)
	}
	old := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7), Limit: 1})
	if err = w.Close(); err != nil {
		t.Fatal(err)
	}
	w, err = NewWalSpool(dir, 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	got := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(7), SessionId: "session", RunId: "run"})
	if len(got.Calls) != 1 || got.Calls[0].CallId != "a" || len(got.Events) != 1 {
		t.Fatal(got)
	}
	if _, err = w.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{ChatId: proto.Int64(7), Cursor: old.NextCursor}); status.Code(err) != codes.InvalidArgument {
		t.Fatal(err)
	}
}

func TestChatTraceQuotaEvictionAndClearInvalidateCursors(t *testing.T) {
	event := traceEvent("a", 0, 1)
	event.Content = strings.Repeat("x", 600)
	w, err := NewWalSpool(t.TempDir(), int64((proto.Size(event)+5)*2+10))
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	w.maxSegmentSize = 1
	if err = w.WriteChatTraceEvent(event); err != nil {
		t.Fatal(err)
	}
	event = proto.Clone(event).(*pb.NoxChatTraceEvent)
	event.EventId = "b"
	event.TimestampUnixNano = 2
	if err = w.WriteChatTraceEvent(event); err != nil {
		t.Fatal(err)
	}
	page := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 1})
	event = proto.Clone(event).(*pb.NoxChatTraceEvent)
	event.EventId = "c"
	event.TimestampUnixNano = 3
	if err = w.WriteChatTraceEvent(event); err != nil {
		t.Fatal(err)
	}
	if _, err = w.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Cursor: page.NextCursor}); status.Code(err) != codes.FailedPrecondition {
		t.Fatal(err)
	}
	got := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 1})
	if len(got.Events) != 1 || got.Events[0].EventId != "b" {
		t.Fatal(got)
	}
	_, _, _, apiCount, _, _ := w.Stats()
	if apiCount != 0 {
		t.Fatal("trace events incorrectly counted as API calls", apiCount)
	}
	if err = w.Clear(); err != nil {
		t.Fatal(err)
	}
	if _, err = w.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Cursor: got.NextCursor}); status.Code(err) != codes.InvalidArgument {
		t.Fatal(err)
	}
	if got = queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0)}); len(got.Events) != 0 {
		t.Fatal(got)
	}
}

func TestChatTraceRejectsUnscopedInvalidAndCanceledQueries(t *testing.T) {
	w, err := NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	for _, req := range []*pb.NoxChatTraceRequest{{}, {ChatId: proto.Int64(0), Limit: 201}, {ChatId: proto.Int64(0), Cursor: "not-a-cursor"}} {
		if _, err = w.QueryChatTrace(context.Background(), req); status.Code(err) != codes.InvalidArgument {
			t.Fatal(err)
		}
	}
	if err = w.WriteChatTraceEvent(traceEvent("a", 0, 1)); err != nil {
		t.Fatal(err)
	}
	if err = w.WriteChatTraceEvent(traceEvent("b", 0, 2)); err != nil {
		t.Fatal(err)
	}
	page := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 1})
	if _, err = w.QueryChatTrace(context.Background(), &pb.NoxChatTraceRequest{ChatId: proto.Int64(1), Cursor: page.NextCursor}); status.Code(err) != codes.InvalidArgument {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err = w.QueryChatTrace(ctx, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0)}); status.Code(err) != codes.Canceled {
		t.Fatal(err)
	}
}

func TestChatTracePageByteBudgetPreservesLargeCompletedEvents(t *testing.T) {
	w, err := NewWalSpool(t.TempDir(), 16<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	for _, id := range []string{"a", "b"} {
		event := traceEvent(id, 0, 1)
		event.Content = strings.Repeat(id, 3<<20)
		if err = w.WriteChatTraceEvent(event); err != nil {
			t.Fatal(err)
		}
	}
	first := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Limit: 200})
	if len(first.Events) != 1 || len(first.Events[0].Content) != 3<<20 || first.NextCursor == "" {
		t.Fatal("byte budget did not page full events")
	}
	second := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatId: proto.Int64(0), Cursor: first.NextCursor})
	if len(second.Events) != 1 || second.Events[0].EventId != "b" || !second.SnapshotComplete {
		t.Fatal(second)
	}
}

func TestExtractChatNameUnescapesJsonAndIndexesByTitle(t *testing.T) {
	w, err := NewWalSpool(t.TempDir(), 1<<20)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()

	// JSON-escaped unicode and newline in raw_request_json without explicit TraceContext
	rec := &pb.NoxRawApiCallRecord{
		CallId:            "legacy-1",
		TimestampUnixNano: 555,
		Caller:            "stream_step",
		Model:             "model",
		RawRequestJson:    `{"messages":[{"role":"user","content":"Group Chat: '\u06af\u0631\u0648\u0647 \u062a\u0633\u062a'\n=== RECENT MESSAGES ==="}]}`,
	}
	if err := w.WriteApiCallChunk(apiChunk(t, 1, rec)); err != nil {
		t.Fatal(err)
	}

	got := queryTrace(t, w, &pb.NoxChatTraceRequest{ChatName: "گروه تست"})
	if len(got.Calls) != 1 || got.Calls[0].CallId != "legacy-1" || got.Calls[0].ChatName != "گروه تست" {
		t.Fatalf("expected legacy call indexed by unescaped title, got %+v", got.Calls)
	}

	_, byName := w.ChatCallStats()
	stat, ok := byName["گروه تست"]
	if !ok || stat.CallCount != 1 || stat.LastCallUnixNano != 555 {
		t.Fatalf("unexpected ChatCallStats byName: %+v", stat)
	}
}
