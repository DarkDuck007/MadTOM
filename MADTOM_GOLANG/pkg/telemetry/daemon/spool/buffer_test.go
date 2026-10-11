package spool

import (
	"os"
	"testing"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestMemoryBuffer_NormalOperation_NoDiskIO(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-mem-buffer-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	wal, err := NewWALManager(tempDir, "node-mem", 1024*1024, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	buf := NewMemoryBuffer(wal, 1024*1024)

	sample := &madtomv1.SystemMetrics{
		TimestampUnixNano: time.Now().UnixNano(),
		NodeId:            "node-mem",
		Cpu:               &madtomv1.CpuMetrics{TotalPct: 55.0},
	}

	// 1. Enqueue in memory
	if err := buf.Enqueue([]*madtomv1.SystemMetrics{sample}, nil); err != nil {
		t.Fatalf("failed to enqueue: %v", err)
	}

	if buf.ItemCount() != 1 {
		t.Fatalf("expected 1 in-memory item, got %d", buf.ItemCount())
	}
	if buf.CurrentMemoryBytes() <= 0 {
		t.Fatalf("expected positive memory bytes, got %d", buf.CurrentMemoryBytes())
	}

	// Disk WAL must have ZERO segments
	if wal.HasPendingBacklog() {
		t.Fatal("expected no disk backlog while memory buffer has room")
	}

	// 2. PopBatch from memory
	batch, fromDisk, err := buf.PopBatch(10)
	if err != nil {
		t.Fatalf("pop failed: %v", err)
	}
	if fromDisk {
		t.Fatal("expected fromDisk=false for in-memory buffer")
	}
	if batch == nil || len(batch.Samples) != 1 || batch.Samples[0].Cpu.TotalPct != 55.0 {
		t.Fatalf("unexpected popped batch: %+v", batch)
	}
	if batch.SegmentId != "mem" {
		t.Fatalf("expected SegmentId 'mem', got '%s'", batch.SegmentId)
	}

	// 3. Acknowledge batch
	if err := buf.Acknowledge(fromDisk, batch.SegmentId, batch.SegmentOffset); err != nil {
		t.Fatalf("ack failed: %v", err)
	}

	if buf.ItemCount() != 0 {
		t.Fatalf("expected 0 in-memory items after ack, got %d", buf.ItemCount())
	}
	if buf.CurrentMemoryBytes() != 0 {
		t.Fatalf("expected 0 memory bytes after ack, got %d", buf.CurrentMemoryBytes())
	}
}

func TestMemoryBuffer_SpillsToDiskWhenBufferFull(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-mem-spill-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	wal, err := NewWALManager(tempDir, "node-spill", 1024*1024, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	// Small 50-byte memory limit to trigger overflow quickly
	buf := NewMemoryBuffer(wal, 50)

	for i := 0; i < 10; i++ {
		sample := &madtomv1.SystemMetrics{
			TimestampUnixNano: int64(i + 1),
			NodeId:            "node-spill",
			Cpu:               &madtomv1.CpuMetrics{TotalPct: float64(i * 10)},
		}
		_ = buf.Enqueue([]*madtomv1.SystemMetrics{sample}, nil)
	}

	// Since limit was 50 bytes, overflow must have flushed to disk WAL!
	if !wal.HasPendingBacklog() {
		t.Fatal("expected disk backlog after buffer overflowed")
	}

	// PopBatch should now read from disk first
	batch, fromDisk, err := buf.PopBatch(10)
	if err != nil {
		t.Fatalf("pop failed: %v", err)
	}
	if !fromDisk {
		t.Fatal("expected fromDisk=true after spill to disk WAL")
	}
	if batch == nil || len(batch.Samples) == 0 {
		t.Fatal("expected batches read from disk")
	}

	// Acknowledge disk batch
	if err := buf.Acknowledge(fromDisk, batch.SegmentId, batch.SegmentOffset); err != nil {
		t.Fatalf("disk ack failed: %v", err)
	}
}

func TestMemoryBuffer_FlushToDiskOnShutdown(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-mem-shutdown-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	wal, err := NewWALManager(tempDir, "node-shut", 1024*1024, false)
	if err != nil {
		t.Fatal(err)
	}

	buf := NewMemoryBuffer(wal, 1024*1024)
	sample := &madtomv1.SystemMetrics{
		TimestampUnixNano: 100,
		NodeId:            "node-shut",
		Cpu:               &madtomv1.CpuMetrics{TotalPct: 44.0},
	}
	_ = buf.Enqueue([]*madtomv1.SystemMetrics{sample}, nil)

	// Flush to disk before close
	if err := buf.FlushToDisk(); err != nil {
		t.Fatalf("flush failed: %v", err)
	}
	wal.Close()

	// Reopen WAL and verify sample was persisted to disk
	wal2, err := NewWALManager(tempDir, "node-shut", 1024*1024, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal2.Close()

	if !wal2.HasPendingBacklog() {
		t.Fatal("expected pending backlog in reopened WAL after flush")
	}
	batch, err := wal2.ReadOldestBatch()
	if err != nil || batch == nil || len(batch.Samples) != 1 || batch.Samples[0].Cpu.TotalPct != 44.0 {
		t.Fatalf("unexpected recovered batch: %+v, err: %v", batch, err)
	}
}
