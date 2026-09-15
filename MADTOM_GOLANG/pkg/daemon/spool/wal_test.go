package spool

import (
	"os"
	"testing"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestWALManagerCycle(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-wal-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	// Set small quota to test rotation and FIFO drop
	wal, err := NewWALManager(tempDir, "node-test", 1024*1024, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	samples := []*madtomv1.SystemMetrics{
		{
			TimestampUnixNano: time.Now().UnixNano(),
			NodeId:            "node-test",
			Cpu:               &madtomv1.CpuMetrics{TotalPct: 42.5},
		},
	}

	// Write batch
	batch, err := wal.WriteMetrics(samples)
	if err != nil {
		t.Fatalf("failed to write metrics: %v", err)
	}
	if batch == nil || batch.SegmentId == "" {
		t.Fatalf("expected non-empty batch segment ID")
	}

	// Read oldest batch
	oldest, err := wal.ReadOldestBatch()
	if err != nil {
		t.Fatalf("failed to read oldest batch: %v", err)
	}
	if oldest == nil {
		t.Fatal("expected pending batch")
	}
	if len(oldest.Samples) != 1 || oldest.Samples[0].Cpu.TotalPct != 42.5 {
		t.Fatalf("unexpected read samples: %+v", oldest.Samples)
	}

	// Acknowledge segment
	if err := wal.AcknowledgeSegment(oldest.SegmentId); err != nil {
		t.Fatalf("failed to ack segment: %v", err)
	}

	// Verify file was removed
	if wal.HasPendingBacklog() {
		t.Fatal("expected no pending backlog after ACK")
	}
}

func TestWALCompression(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-wal-comp-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	wal, err := NewWALManager(tempDir, "node-comp", 1024*1024, true)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	samples := []*madtomv1.SystemMetrics{
		{
			TimestampUnixNano: time.Now().UnixNano(),
			NodeId:            "node-comp",
			Cpu:               &madtomv1.CpuMetrics{TotalPct: 99.9, UserPct: 50.0},
		},
	}

	batch, err := wal.WriteMetrics(samples)
	if err != nil {
		t.Fatal(err)
	}
	if !batch.IsCompressed || len(batch.CompressedPayload) == 0 {
		t.Fatal("expected compressed batch")
	}

	readBatch, err := wal.ReadOldestBatch()
	if err != nil {
		t.Fatal(err)
	}
	if len(readBatch.Samples) != 1 || readBatch.Samples[0].Cpu.TotalPct != 99.9 {
		t.Fatalf("unexpected decompressed samples: %+v", readBatch.Samples)
	}
}

func TestAcknowledgementPreservesLaterSamplesAcrossRestart(t *testing.T) {
	dir := t.TempDir()
	wal, err := NewWALManager(dir, "node", 1<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	first, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "node", TimestampUnixNano: 1}})
	if err != nil {
		t.Fatal(err)
	}
	second, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "node", TimestampUnixNano: 2}})
	if err != nil {
		t.Fatal(err)
	}
	if first.SegmentId == second.SegmentId {
		t.Fatal("independently acknowledged batches share a segment")
	}
	wal.Close()
	wal, err = NewWALManager(dir, "node", 1<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()
	if err := wal.AcknowledgeSegment(first.SegmentId); err != nil {
		t.Fatal(err)
	}
	batch, err := wal.ReadOldestBatch()
	if err != nil {
		t.Fatal(err)
	}
	if batch == nil || batch.Samples[0].TimestampUnixNano != 2 {
		t.Fatal("unacknowledged sample lost")
	}
	if err := wal.AcknowledgeSegment("../outside.wal"); err == nil {
		t.Fatal("path traversal accepted")
	}
}

func TestLegacyMultiRecordSegmentIsFullyReplayed(t *testing.T) {
	dir := t.TempDir()
	wal, err := NewWALManager(dir, "node", 1<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()
	first, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "node", TimestampUnixNano: 1}})
	if err != nil {
		t.Fatal(err)
	}
	second, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{{NodeId: "node", TimestampUnixNano: 2}})
	if err != nil {
		t.Fatal(err)
	}
	wal.Close()
	payload, err := os.ReadFile(dir + "/" + second.SegmentId)
	if err != nil {
		t.Fatal(err)
	}
	file, err := os.OpenFile(dir+"/"+first.SegmentId, os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := file.Write(payload); err != nil {
		t.Fatal(err)
	}
	file.Close()
	batch, err := wal.ReadOldestBatch()
	if err != nil {
		t.Fatal(err)
	}
	if len(batch.Samples) != 2 || batch.Samples[1].TimestampUnixNano != 2 {
		t.Fatal("legacy segment records lost")
	}
}

func TestReadBatchChunkAndRangeAck(t *testing.T) {
	dir := t.TempDir()
	wal, err := NewWALManager(dir, "node-chunk", 10<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	// Write 50 separate segments
	for i := 1; i <= 50; i++ {
		_, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{
			{NodeId: "node-chunk", TimestampUnixNano: int64(i)},
		})
		if err != nil {
			t.Fatalf("write failed on %d: %v", i, err)
		}
	}

	// Read chunk of up to 20 samples
	chunk1, err := wal.ReadBatchChunk(20)
	if err != nil {
		t.Fatalf("ReadBatchChunk(20) failed: %v", err)
	}
	if len(chunk1.Samples) != 20 {
		t.Fatalf("expected 20 samples, got %d", len(chunk1.Samples))
	}
	if chunk1.SegmentId != "segment-00000001.wal:segment-00000020.wal" {
		t.Fatalf("unexpected segment ID range: %q", chunk1.SegmentId)
	}

	// Acknowledge range 1..20
	if err := wal.AcknowledgeSegment(chunk1.SegmentId); err != nil {
		t.Fatalf("AcknowledgeSegment range failed: %v", err)
	}

	// Read remaining chunk of 50 samples (should have 30 remaining)
	chunk2, err := wal.ReadBatchChunk(50)
	if err != nil {
		t.Fatalf("ReadBatchChunk(50) failed: %v", err)
	}
	if len(chunk2.Samples) != 30 {
		t.Fatalf("expected 30 samples remaining, got %d", len(chunk2.Samples))
	}
	if chunk2.SegmentId != "segment-00000021.wal:segment-00000050.wal" {
		t.Fatalf("unexpected second segment ID range: %q", chunk2.SegmentId)
	}

	// Acknowledge range 21..50
	if err := wal.AcknowledgeSegment(chunk2.SegmentId); err != nil {
		t.Fatalf("AcknowledgeSegment range failed: %v", err)
	}

	// Verify all backlog is cleared
	if wal.HasPendingBacklog() {
		t.Fatal("expected no pending backlog after acknowledging all chunks")
	}
}

func TestWALManagerOfflineAccumulationAndQuotaEnforcement(t *testing.T) {
	dir := t.TempDir()
	// Set 200 KB quota to exercise bounded FIFO drop under high volume
	wal, err := NewWALManager(dir, "node-perf", 200*1024, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	start := time.Now()
	numBatches := 2000
	for i := 1; i <= numBatches; i++ {
		_, err := wal.WriteMetrics([]*madtomv1.SystemMetrics{
			{
				NodeId:            "node-perf",
				TimestampUnixNano: int64(i),
				Cpu:               &madtomv1.CpuMetrics{TotalPct: float64(i % 100)},
			},
		})
		if err != nil {
			t.Fatalf("failed write on batch %d: %v", i, err)
		}
	}
	elapsed := time.Since(start)
	t.Logf("Wrote %d batches in %s (%.2f µs/batch)", numBatches, elapsed, float64(elapsed.Microseconds())/float64(numBatches))

	// Under O(1) in-memory tracking, 2,000 file writes must complete quickly without stalling on stat() syscalls
	if elapsed > 10*time.Second {
		t.Fatalf("offline batch writes too slow, possible O(N) disk scan leak: took %s", elapsed)
	}

	// Verify bounded quota was respected
	if wal.totalSpoolBytes > wal.maxTotalSpool {
		t.Fatalf("totalSpoolBytes %d exceeded maxTotalSpool %d", wal.totalSpoolBytes, wal.maxTotalSpool)
	}

	if !wal.HasPendingBacklog() {
		t.Fatal("expected pending backlog")
	}

	// Read chunk from surviving backlog
	chunk, err := wal.ReadBatchChunk(50)
	if err != nil {
		t.Fatalf("failed to read backlog chunk: %v", err)
	}
	if chunk == nil || len(chunk.Samples) == 0 {
		t.Fatal("expected non-empty chunk")
	}

	// Verify restarting WAL recovers the segments and total size cleanly
	wal.Close()
	recoveredWal, err := NewWALManager(dir, "node-perf", 200*1024, false)
	if err != nil {
		t.Fatalf("failed to recover WAL: %v", err)
	}
	defer recoveredWal.Close()

	if recoveredWal.totalSpoolBytes > recoveredWal.maxTotalSpool {
		t.Fatalf("recovered totalSpoolBytes %d exceeded max", recoveredWal.totalSpoolBytes)
	}
	if len(recoveredWal.segments) != len(wal.segments) {
		t.Fatalf("recovered segment count mismatch: got %d, want %d", len(recoveredWal.segments), len(wal.segments))
	}
}
