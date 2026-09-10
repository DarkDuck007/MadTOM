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
