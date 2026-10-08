package logs

import (
	"fmt"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

func TestChunker_AddAndSeal(t *testing.T) {
	nodeID := "test-node"
	chunker, err := NewChunker(nodeID, 0)
	if err != nil {
		t.Fatalf("NewChunker: %v", err)
	}
	defer chunker.Close()

	decoder, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatalf("zstd.NewReader: %v", err)
	}
	defer decoder.Close()

	// Add 255 records -> no sealed chunk yet
	for i := 0; i < 255; i++ {
		chunk, err := chunker.AddRecord(&madtomv1.LogRecord{
			Message:  fmt.Sprintf("log record %d", i),
			Unit:     "systemd",
			Priority: 6,
		})
		if err != nil {
			t.Fatalf("AddRecord failed at %d: %v", i, err)
		}
		if chunk != nil {
			t.Fatalf("Expected nil chunk before 256 records, got chunk at %d", i)
		}
	}

	// 256th record -> returns sealed chunk 0
	chunk, err := chunker.AddRecord(&madtomv1.LogRecord{
		Message:  "log record 255",
		Unit:     "systemd",
		Priority: 6,
	})
	if err != nil {
		t.Fatalf("AddRecord 255 failed: %v", err)
	}
	if chunk == nil {
		t.Fatalf("Expected sealed chunk at 256 records")
	}

	if chunk.ChunkId != 0 {
		t.Errorf("Expected ChunkId 0, got %d", chunk.ChunkId)
	}
	if chunk.FirstSeq != 0 || chunk.LastSeq != 255 {
		t.Errorf("Expected seq 0..255, got %d..%d", chunk.FirstSeq, chunk.LastSeq)
	}
	if chunk.RecordCount != 256 {
		t.Errorf("Expected RecordCount 256, got %d", chunk.RecordCount)
	}
	if !chunk.Sealed {
		t.Errorf("Expected Sealed true")
	}
	if len(chunk.ZstdRecords) == 0 {
		t.Fatalf("ZstdRecords is empty")
	}

	// Verify decompression
	raw, err := decoder.DecodeAll(chunk.ZstdRecords, nil)
	if err != nil {
		t.Fatalf("DecodeAll: %v", err)
	}
	var list madtomv1.LogRecordList
	if err := proto.Unmarshal(raw, &list); err != nil {
		t.Fatalf("proto.Unmarshal: %v", err)
	}
	if len(list.Records) != 256 {
		t.Fatalf("Expected 256 records in list, got %d", len(list.Records))
	}
	if list.Records[0].Seq != 0 || list.Records[255].Seq != 255 {
		t.Errorf("Record seq mismatch: first=%d, last=%d", list.Records[0].Seq, list.Records[255].Seq)
	}

	// Next record -> sequence continues at 256
	if chunker.CurrentSeq() != 256 {
		t.Errorf("Expected CurrentSeq 256, got %d", chunker.CurrentSeq())
	}
}

func TestChunker_BuildUnsealedAndSealCurrent(t *testing.T) {
	nodeID := "test-node"
	chunker, err := NewChunker(nodeID, 1000)
	if err != nil {
		t.Fatalf("NewChunker: %v", err)
	}
	defer chunker.Close()

	for i := 0; i < 10; i++ {
		_, err := chunker.AddRecord(&madtomv1.LogRecord{
			Message: fmt.Sprintf("unsealed log %d", i),
		})
		if err != nil {
			t.Fatalf("AddRecord %d: %v", i, err)
		}
	}

	// Snapshot unsealed chunk
	unsealed, err := chunker.BuildUnsealedChunk()
	if err != nil {
		t.Fatalf("BuildUnsealedChunk: %v", err)
	}
	if unsealed == nil {
		t.Fatalf("Expected unsealed chunk, got nil")
	}
	if unsealed.Sealed {
		t.Errorf("Expected Sealed false")
	}
	if unsealed.RecordCount != 10 {
		t.Errorf("Expected RecordCount 10, got %d", unsealed.RecordCount)
	}
	if unsealed.FirstSeq != 1000 || unsealed.LastSeq != 1009 {
		t.Errorf("Expected seq 1000..1009, got %d..%d", unsealed.FirstSeq, unsealed.LastSeq)
	}

	// SealCurrent
	sealed, err := chunker.SealCurrent()
	if err != nil {
		t.Fatalf("SealCurrent: %v", err)
	}
	if sealed == nil {
		t.Fatalf("Expected sealed chunk, got nil")
	}
	if !sealed.Sealed {
		t.Errorf("Expected Sealed true")
	}
	if sealed.RecordCount != 10 {
		t.Errorf("Expected RecordCount 10, got %d", sealed.RecordCount)
	}

	// Buffer should now be empty
	empty, err := chunker.BuildUnsealedChunk()
	if err != nil {
		t.Fatalf("BuildUnsealedChunk after seal: %v", err)
	}
	if empty != nil {
		t.Errorf("Expected nil empty chunk after seal, got %+v", empty)
	}
}
