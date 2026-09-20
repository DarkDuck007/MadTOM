package spool

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"strings"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/proto"
)

func writeSample(t *testing.T, w *WALManager, n int64) *madtomv1.TelemetryBatch {
	t.Helper()
	batch, err := w.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "n", TimestampUnixNano: n}})
	if err != nil {
		t.Fatal(err)
	}
	return batch
}
func openWAL(t *testing.T, dir string, compressed bool) *WALManager {
	t.Helper()
	w, err := NewWALManager(dir, "n", 1<<20, compressed)
	if err != nil {
		t.Fatal(err)
	}
	return w
}
func pending(t *testing.T, w *WALManager, n int) *madtomv1.TelemetryBatch {
	t.Helper()
	batch, err := w.ReadBatchChunk(n)
	if err != nil {
		t.Fatal(err)
	}
	if batch == nil {
		t.Fatal("expected pending records")
	}
	return batch
}
func ack(t *testing.T, w *WALManager, b *madtomv1.TelemetryBatch) {
	t.Helper()
	if err := w.AcknowledgeSegment(b.SegmentId, b.SegmentOffset); err != nil {
		t.Fatal(err)
	}
}

func TestGroupedWALLostAckAppendDuringFlightAndRecovery(t *testing.T) {
	for _, compressed := range []bool{false, true} {
		t.Run(map[bool]string{false: "raw", true: "zstd"}[compressed], func(t *testing.T) {
			dir := t.TempDir()
			w := openWAL(t, dir, compressed)
			for i := int64(1); i <= 30; i++ {
				writeSample(t, w, i)
			}
			if len(w.segments) != 1 {
				t.Fatalf("expected grouped segment, got %d", len(w.segments))
			}
			first := pending(t, w, 10)
			// Lost acknowledgement replays the same prefix without consuming it.
			retry := pending(t, w, 10)
			if !proto.Equal(first, retry) {
				t.Fatal("unacknowledged prefix changed")
			}
			writeSample(t, w, 31)
			ack(t, w, first)
			ack(t, w, first) // Duplicate must not advance beyond its offset.
			w.Close()
			w = openWAL(t, dir, false)
			defer w.Close() // Compressed records remain readable with zstd disabled.
			rest := pending(t, w, 100)
			if rest.IsCompressed || len(rest.Samples) != 21 || rest.Samples[0].TimestampUnixNano != 11 || rest.Samples[20].TimestampUnixNano != 31 {
				t.Fatal("wrong recovered prefix")
			}
			ack(t, w, rest)
			if w.HasPendingBacklog() {
				t.Fatal("fully acknowledged prefix remains pending")
			}
			next := writeSample(t, w, 32)
			if next.SegmentId != rest.SegmentId || next.SegmentOffset <= rest.SegmentOffset {
				t.Fatal("active segment was not reused safely")
			}
			ack(t, w, rest)
			if got := pending(t, w, 10); len(got.Samples) != 1 || got.Samples[0].TimestampUnixNano != 32 {
				t.Fatal("late ACK deleted appended record")
			}
		})
	}
}

func TestWALRangeAckOnlyConsumesFinalPrefix(t *testing.T) {
	w := openWAL(t, t.TempDir(), false)
	defer w.Close()
	w.maxSegmentSize = 100
	for i := int64(1); i <= 20; i++ {
		writeSample(t, w, i)
	}
	batch := pending(t, w, 5)
	if !strings.Contains(batch.SegmentId, ":") {
		t.Fatal("expected a range")
	}
	ack(t, w, batch)
	remaining := pending(t, w, 100)
	if len(remaining.Samples) != 15 || remaining.Samples[0].TimestampUnixNano != 6 {
		t.Fatal("range ACK skipped later records")
	}
}

func TestInvalidOffsetsNeverAdvanceWAL(t *testing.T) {
	w := openWAL(t, t.TempDir(), false)
	defer w.Close()
	first := writeSample(t, w, 1)
	writeSample(t, w, 2)
	for _, offset := range []int64{0, -1, first.SegmentOffset - 1, 1 << 40} {
		if err := w.AcknowledgeSegment(first.SegmentId, offset); err == nil {
			t.Fatalf("accepted invalid offset %d", offset)
		}
	}
	if got := pending(t, w, 100); len(got.Samples) != 2 {
		t.Fatal("invalid ACK consumed data")
	}
}

func TestRecoveryTruncatesOnlyTornNewestFrame(t *testing.T) {
	for _, suffix := range [][]byte{{0, 0}, {0, 0, 0, 20, 1, 2}} {
		t.Run(string(rune(len(suffix)+'0')), func(t *testing.T) {
			dir := t.TempDir()
			w := openWAL(t, dir, false)
			first := writeSample(t, w, 1)
			w.Close()
			f, err := os.OpenFile(filepath.Join(dir, first.SegmentId), os.O_APPEND|os.O_WRONLY, 0600)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = f.Write(suffix); err != nil {
				t.Fatal(err)
			}
			f.Close()
			w = openWAL(t, dir, false)
			defer w.Close()
			writeSample(t, w, 2)
			batch := pending(t, w, 100)
			if len(batch.Samples) != 2 || batch.Samples[1].TimestampUnixNano != 2 {
				t.Fatal("torn tail hid later append")
			}
		})
	}
}

func TestRecoveryRejectsCorruptCompleteRecords(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	first := writeSample(t, w, 1)
	w.Close()
	f, err := os.OpenFile(filepath.Join(dir, first.SegmentId), os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	frame := []byte{0, 0, 0, 1, 0xff}
	if _, err = f.Write(frame); err != nil {
		t.Fatal(err)
	}
	f.Close()
	w = openWAL(t, dir, false)
	defer w.Close()
	if _, err := w.ReadBatchChunk(100); err == nil {
		t.Fatal("complete corrupt record silently skipped")
	}
}

func TestAckCheckpointFailurePreservesPendingData(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	defer w.Close()
	first := writeSample(t, w, 1)
	if err := os.Remove(filepath.Join(dir, stateFile)); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(dir, stateFile), 0700); err != nil {
		t.Fatal(err)
	}
	if err := w.AcknowledgeSegment(first.SegmentId, first.SegmentOffset); err == nil {
		t.Fatal("checkpoint failure ignored")
	}
	if !w.HasPendingBacklog() {
		t.Fatal("failed checkpoint consumed data")
	}
}

func TestSequenceHighWaterSurvivesRemovalOfAllSegments(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	first := writeSample(t, w, 1)
	ack(t, w, first)
	w.Close()
	if err := os.Remove(filepath.Join(dir, first.SegmentId)); err != nil {
		t.Fatal(err)
	}
	w = openWAL(t, dir, false)
	defer w.Close()
	second := writeSample(t, w, 2)
	if first.SegmentId == second.SegmentId {
		t.Fatal("sequence identity reused")
	}
	ack(t, w, first)
	if !w.HasPendingBacklog() {
		t.Fatal("stale ACK consumed new segment")
	}
}

func TestReplayByteBudgetAndAtomicSampleRecord(t *testing.T) {
	w, err := NewWALManager(t.TempDir(), "n", 20<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	for i := int64(1); i <= 4; i++ {
		_, err := w.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "n", TimestampUnixNano: i, CpuModel: strings.Repeat("x", 1<<20)}})
		if err != nil {
			t.Fatal(err)
		}
	}
	batch := pending(t, w, 500)
	if proto.Size(batch) > DefaultChunkMaxBytes || len(batch.Samples) != 2 {
		t.Fatalf("bad byte-bounded chunk: %d bytes, %d samples", proto.Size(batch), len(batch.Samples))
	}
	ack(t, w, batch)
	if len(pending(t, w, 500).Samples) != 2 {
		t.Fatal("byte limit skipped records")
	}
	if _, err := w.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "n", CpuModel: strings.Repeat("x", DefaultChunkMaxBytes)}}); err == nil {
		t.Fatal("oversized record accepted")
	}
}

func TestRecoveryRejectsTornSealedSegment(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	w.maxSegmentSize = 1
	first := writeSample(t, w, 1)
	writeSample(t, w, 2)
	w.Close()
	header := make([]byte, 4)
	binary.BigEndian.PutUint32(header, 100)
	f, err := os.OpenFile(filepath.Join(dir, first.SegmentId), os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	f.Write(header)
	f.Close()
	if reopened, err := NewWALManager(dir, "n", 1<<20, false); err == nil {
		reopened.Close()
		t.Fatal("torn sealed segment silently repaired")
	}
}

func TestLostAcknowledgementReplaysAfterRestart(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	for i := int64(1); i <= 4; i++ {
		writeSample(t, w, i)
	}
	sent := pending(t, w, 2)
	w.Close()
	w = openWAL(t, dir, false)
	defer w.Close()
	if replay := pending(t, w, 2); !proto.Equal(sent, replay) {
		t.Fatal("lost ACK did not replay after restart")
	}
	ack(t, w, sent)
	if tail := pending(t, w, 2); tail.Samples[0].TimestampUnixNano != 3 {
		t.Fatal("ACK skipped tail")
	}
}

func TestRecordSampleTargetIsAtomic(t *testing.T) {
	w := openWAL(t, t.TempDir(), false)
	defer w.Close()
	_, err := w.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "n", TimestampUnixNano: 1}, {NodeId: "n", TimestampUnixNano: 2}, {NodeId: "n", TimestampUnixNano: 3}})
	if err != nil {
		t.Fatal(err)
	}
	writeSample(t, w, 4)
	first := pending(t, w, 1)
	if len(first.Samples) != 3 {
		t.Fatal("atomic record split")
	}
	ack(t, w, first)
	if got := pending(t, w, 1); len(got.Samples) != 1 || got.Samples[0].TimestampUnixNano != 4 {
		t.Fatal("tail skipped")
	}
}

func BenchmarkWALSegmentGrouping(b *testing.B) {
	for _, grouped := range []bool{false, true} {
		b.Run(map[bool]string{false: "one_record_segments", true: "grouped_segments"}[grouped], func(b *testing.B) {
			w, err := NewWALManager(b.TempDir(), "n", 1<<30, false)
			if err != nil {
				b.Fatal(err)
			}
			defer w.Close()
			if !grouped {
				w.maxSegmentSize = 1
			}
			b.ResetTimer()
			for i := 0; i < b.N; i++ {
				if _, err := w.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "n", TimestampUnixNano: int64(i + 1)}}); err != nil {
					b.Fatal(err)
				}
			}
			b.StopTimer()
			b.ReportMetric(float64(len(w.segments))/float64(b.N), "segments/op")
		})
	}
}
