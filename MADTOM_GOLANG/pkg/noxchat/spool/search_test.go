package spool

import (
	"context"
	"encoding/hex"
	"fmt"
	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func testSpool(t testing.TB) *WalSpool {
	t.Helper()
	w, err := NewWalSpool(t.TempDir(), 1<<30)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { w.Close() })
	return w
}
func testChunk(t testing.TB, id uint64, records ...*pb.LogRecord) *pb.LogChunk {
	t.Helper()
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1))
	if err != nil {
		t.Fatal(err)
	}
	defer enc.Close()
	raw, err := proto.Marshal(&pb.LogRecordList{Records: records})
	if err != nil {
		t.Fatal(err)
	}
	return &pb.LogChunk{ChunkId: id, FirstSeq: records[0].Seq, LastSeq: records[len(records)-1].Seq, FirstTsUnixNano: records[0].TimestampUnixNano, LastTsUnixNano: records[len(records)-1].TimestampUnixNano, RecordCount: uint32(len(records)), RawSize: uint32(len(raw)), ZstdRecords: enc.EncodeAll(raw, nil)}
}
func put(t testing.TB, w *WalSpool, c *pb.LogChunk) {
	t.Helper()
	if err := w.WriteLogChunk(c); err != nil {
		t.Fatal(err)
	}
}
func search(t testing.TB, w *WalSpool, r *pb.NoxSearchLogsRequest) *pb.NoxSearchLogsResponse {
	t.Helper()
	resp, err := w.SearchLogs(context.Background(), r, DefaultSearchLimits())
	if err != nil {
		t.Fatal(err)
	}
	return resp
}
func rec(seq uint64, text string) *pb.LogRecord {
	return &pb.LogRecord{Seq: seq, TimestampUnixNano: int64(seq) * 100, Message: text, Unit: "nox", Priority: 3}
}
func TestLiteralUnicodeAndPunctuation(t *testing.T) {
	for _, tc := range []struct {
		text, query   string
		sensitive     bool
		start, length int
	}{
		{"🙂 ERROR /tmp/a.b*", "error /TMP/a.b*", false, 2, 15},
		{"سلام دنیا", "دنیا", false, 5, 4},
		{"foo AND bar", "AND", false, 4, 3},
		{"abC", "bc", true, -1, 0},
		{"straße", "STRASSE", false, -1, 0},
		{"abc", "", false, 0, 0},
	} {
		a, b := LiteralIndex(tc.text, tc.query, tc.sensitive)
		if a != tc.start || b != tc.length {
			t.Errorf("%q %q: got %d,%d want %d,%d", tc.text, tc.query, a, b, tc.start, tc.length)
		}
	}
}
func TestSearchPagingAndTimeFiltering(t *testing.T) {
	w := testSpool(t)
	a, b, c := rec(1, "needle"), rec(2, "🙂 Needle"), rec(3, "needle")
	a.TimestampUnixNano = 300
	b.TimestampUnixNano = 100
	c.TimestampUnixNano = 200
	put(t, w, testChunk(t, 0, a, b, c))
	req := &pb.NoxSearchLogsRequest{Query: "needle", Limit: 1, StartTimeNano: 100, EndTimeNano: 300}
	first := search(t, w, req)
	if len(first.Hits) != 1 || first.Hits[0].Seq != 3 || first.NextCursor == "" {
		t.Fatalf("first: %v", first)
	}
	req.Cursor = first.NextCursor
	second := search(t, w, req)
	if len(second.Hits) != 1 || second.Hits[0].Seq != 2 || second.Hits[0].MatchStart != 3 {
		t.Fatalf("second: %v", second)
	}
	if second.NextCursor != "" {
		req.Cursor = second.NextCursor
		last := search(t, w, req)
		if !last.SnapshotComplete || len(last.Hits) != 0 {
			t.Fatal(last)
		}
	}
	req.Cursor = ""
	req.Query = "/missing"
	if r := search(t, w, req); !r.SnapshotComplete || len(r.Hits) != 0 {
		t.Fatal(r)
	}
}
func TestCursorRejectsChangedQueryChunkResetAndTampering(t *testing.T) {
	w := testSpool(t)
	put(t, w, testChunk(t, 0, rec(1, "x"), rec(2, "x")))
	r := &pb.NoxSearchLogsRequest{Query: "x", Limit: 1}
	first := search(t, w, r)
	r.Cursor = first.NextCursor
	r.Query = "y"
	if _, err := w.SearchLogs(context.Background(), r, DefaultSearchLimits()); status.Code(err) != codes.InvalidArgument {
		t.Fatal(err)
	}
	r.Query = "x"
	put(t, w, testChunk(t, 0, rec(1, "x"), rec(2, "x"), rec(3, "x")))
	if _, err := w.SearchLogs(context.Background(), r, DefaultSearchLimits()); status.Code(err) != codes.FailedPrecondition {
		t.Fatal(err)
	}
	r.Cursor = ""
	r.Cursor = search(t, w, r).NextCursor
	if err := w.Clear(); err != nil {
		t.Fatal(err)
	}
	if _, err := w.SearchLogs(context.Background(), r, DefaultSearchLimits()); status.Code(err) != codes.FailedPrecondition {
		t.Fatal(err)
	}
	r.Cursor = "garbage"
	if _, err := w.SearchLogs(context.Background(), r, DefaultSearchLimits()); err == nil {
		t.Fatal("accepted malformed cursor")
	}
}
func TestRotationKeepsNewRevisionAndEvictsExpiredMaps(t *testing.T) {
	w := testSpool(t)
	w.maxSegmentSize = 1
	put(t, w, testChunk(t, 0, rec(1, "old")))
	put(t, w, testChunk(t, 0, rec(1, "new"), rec(2, "new")))
	put(t, w, testChunk(t, 1, rec(3, "newest")))
	w.mu.Lock()
	w.maxTotalSpool = w.segments[1].size + w.segments[2].size
	err := w.enforceQuotaLocked()
	w.mu.Unlock()
	if err != nil {
		t.Fatal(err)
	}
	if got := search(t, w, &pb.NoxSearchLogsRequest{Query: "new"}); len(got.Hits) != 3 {
		t.Fatal(got)
	}
	w.mu.Lock()
	w.maxTotalSpool = w.segments[len(w.segments)-1].size
	err = w.enforceQuotaLocked()
	w.mu.Unlock()
	if err != nil {
		t.Fatal(err)
	}
	chunks, _, err := w.QueryLogChunks(0, 10)
	if err != nil || len(chunks) != 1 || chunks[0].ChunkId != 1 {
		t.Fatalf("%v %v", chunks, err)
	}
	_, _, logs, _, oldest, _ := w.Stats()
	if logs != 1 || oldest != 300 {
		t.Fatalf("stats %d %d", logs, oldest)
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	recovered, err := NewWalSpool(w.dir, 1<<30)
	if err != nil {
		t.Fatal(err)
	}
	defer recovered.Close()
	if got := search(t, recovered, &pb.NoxSearchLogsRequest{Query: "new"}); len(got.Hits) != 1 {
		t.Fatal(got)
	}
}
func TestFailedWriteNotPublishedAndRecoveryReportsTornFrame(t *testing.T) {
	w := testSpool(t)
	put(t, w, testChunk(t, 0, rec(1, "good")))
	if err := w.currentFile.Close(); err != nil {
		t.Fatal(err)
	}
	if err := w.WriteLogChunk(testChunk(t, 1, rec(2, "bad"))); err == nil {
		t.Fatal("write should fail")
	}
	chunks, _, _ := w.QueryLogChunks(0, 10)
	if len(chunks) != 1 {
		t.Fatal("failed chunk published")
	}
	f, err := os.OpenFile(w.segments[0].path, os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	f.Write([]byte{'L', 0})
	f.Close()
	recovered, err := NewWalSpool(w.dir, 1<<30)
	if err != nil {
		t.Fatal(err)
	}
	defer recovered.Close()
	r := search(t, recovered, &pb.NoxSearchLogsRequest{Query: "good"})
	if r.SnapshotComplete || r.CorruptChunks != 1 || len(r.Hits) != 1 {
		t.Fatal(r)
	}
	put(t, recovered, testChunk(t, 1, rec(2, "later")))
	if len(recovered.segments) != 2 {
		t.Fatal("must rotate after recovery")
	}
}
func TestCancellationBudgetsCorruptionAndExpiry(t *testing.T) {
	w := testSpool(t)
	put(t, w, testChunk(t, 0, rec(1, strings.Repeat("x", 1000))))
	put(t, w, testChunk(t, 1, rec(2, strings.Repeat("x", 1000))))
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := w.SearchLogs(ctx, &pb.NoxSearchLogsRequest{Query: "x"}, DefaultSearchLimits()); status.Code(err) != codes.Canceled {
		t.Fatal(err)
	}
	limits := DefaultSearchLimits()
	limits.PageBytes = limits.ChunkBytes
	limits.PageTime = time.Nanosecond
	resp, err := w.SearchLogs(context.Background(), &pb.NoxSearchLogsRequest{Query: "absent"}, limits)
	if err != nil || resp.NextCursor == "" || resp.SnapshotComplete {
		t.Fatalf("%v %v", resp, err)
	}
	cur, err := decodeCursor(resp.NextCursor, w.generation)
	if err != nil {
		t.Fatal(err)
	}
	cur.Expires = time.Now().Add(-time.Second).Unix()
	if _, err := w.SearchLogs(context.Background(), &pb.NoxSearchLogsRequest{Query: "absent", Cursor: encodeCursor(cur, w.generation)}, DefaultSearchLimits()); status.Code(err) != codes.FailedPrecondition {
		t.Fatal(err)
	}
	put(t, w, &pb.LogChunk{ChunkId: 2, FirstSeq: 3, LastSeq: 3, RecordCount: 1, ZstdRecords: []byte("corrupt")})
	resp = search(t, w, &pb.NoxSearchLogsRequest{Query: "x"})
	if resp.CorruptChunks != 1 || resp.SnapshotComplete {
		t.Fatal(resp)
	}
}
func TestDeleteFailureKeepsOwnership(t *testing.T) {
	w := testSpool(t)
	w.maxSegmentSize = 1
	put(t, w, testChunk(t, 0, rec(1, "x")))
	put(t, w, testChunk(t, 1, rec(2, "x")))
	path := w.segments[0].path
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	os.WriteFile(filepath.Join(path, "block"), []byte("x"), 0600)
	w.mu.Lock()
	w.maxTotalSpool = 1
	err := w.enforceQuotaLocked()
	w.mu.Unlock()
	if err == nil || len(w.logChunks) != 2 {
		t.Fatalf("deletion failure lost retained entries: %v", err)
	}
}
func BenchmarkSearchLogs(b *testing.B) {
	w := testSpool(b)
	for c := 0; c < 128; c++ {
		rs := make([]*pb.LogRecord, 256)
		for i := range rs {
			rs[i] = rec(uint64(c*256+i+1), strings.Repeat("normal log output سلام /var/log/nox ", 8))
		}
		put(b, w, testChunk(b, uint64(c), rs...))
	}
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		search(b, w, &pb.NoxSearchLogsRequest{Query: "no such identifier"})
	}
}

func TestCursorAllowsNewChunksButNotRetentionChanges(t *testing.T) {
	w := testSpool(t)
	w.maxSegmentSize = 1
	put(t, w, testChunk(t, 0, rec(1, "x"), rec(2, "x")))
	req := &pb.NoxSearchLogsRequest{Query: "x", Limit: 1}
	req.Cursor = search(t, w, req).NextCursor
	put(t, w, testChunk(t, 1, rec(3, "x")))
	page := search(t, w, req)
	if len(page.Hits) != 1 || page.Hits[0].Seq != 1 {
		t.Fatal(page)
	}
	w.mu.Lock()
	w.maxTotalSpool = w.segments[1].size
	err := w.enforceQuotaLocked()
	w.mu.Unlock()
	if err != nil {
		t.Fatal(err)
	}
	if _, err := w.SearchLogs(context.Background(), req, DefaultSearchLimits()); status.Code(err) != codes.FailedPrecondition {
		t.Fatal(err)
	}
}
func TestConcurrentAppendSearchAndReset(t *testing.T) {
	w := testSpool(t)
	done := make(chan error, 1)
	go func() {
		for i := uint64(1); i <= 40; i++ {
			raw, _ := proto.Marshal(&pb.LogRecordList{Records: []*pb.LogRecord{rec(i, "x")}})
			enc, _ := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1))
			z := enc.EncodeAll(raw, nil)
			enc.Close()
			if err := w.WriteLogChunk(&pb.LogChunk{ChunkId: i, FirstSeq: i, LastSeq: i, RecordCount: 1, RawSize: uint32(len(raw)), ZstdRecords: z}); err != nil {
				done <- err
				return
			}
		}
		done <- nil
	}()
	for i := 0; i < 20; i++ {
		_, err := w.SearchLogs(context.Background(), &pb.NoxSearchLogsRequest{Query: "x"}, DefaultSearchLimits())
		if err != nil && status.Code(err) != codes.FailedPrecondition {
			t.Fatal(err)
		}
		if i == 10 {
			if err := w.Clear(); err != nil {
				t.Fatal(err)
			}
		}
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}
func TestResponseGenerationDoesNotExposeCursorSigningKey(t *testing.T) {
	w := testSpool(t)
	put(t, w, testChunk(t, 0, rec(1, "x"), rec(2, "x")))
	resp := search(t, w, &pb.NoxSearchLogsRequest{Query: "x", Limit: 1})
	raw, err := hex.DecodeString(resp.Generation)
	if err != nil {
		t.Fatal(err)
	}
	var publicKey [32]byte
	copy(publicKey[:], raw)
	if _, err := decodeCursor(resp.NextCursor, publicKey); err == nil {
		t.Fatal("public generation must not authenticate cursor")
	}
}

func BenchmarkSearchScenarios(b *testing.B) {
	for _, chunks := range []int{8, 128, 1024} {
		b.Run(fmt.Sprintf("chunks_%d", chunks), func(b *testing.B) {
			w := testSpool(b)
			var decoded uint64
			for c := 0; c < chunks; c++ {
				rs := make([]*pb.LogRecord, 256)
				for i := range rs {
					rs[i] = rec(uint64(c*256+i+1), strings.Repeat("normal log output سلام /var/log/nox ", 8))
				}
				ch := testChunk(b, uint64(c), rs...)
				decoded += uint64(ch.RawSize)
				put(b, w, ch)
			}
			compressed, _, _, _, _, _ := w.Stats()
			for _, q := range []string{"normal", "/var/log/nox", "سلام", "no such identifier"} {
				b.Run(q, func(b *testing.B) {
					b.ReportAllocs()
					b.ResetTimer()
					for i := 0; i < b.N; i++ {
						req := &pb.NoxSearchLogsRequest{Query: q}
						for {
							resp := search(b, w, req)
							if q != "no such identifier" || resp.NextCursor == "" {
								break
							}
							req.Cursor = resp.NextCursor
						}
					}
					b.ReportMetric(float64(decoded), "corpus_decoded_bytes")
					b.ReportMetric(float64(compressed), "corpus_WAL_bytes")
				})
			}
		})
	}
}
