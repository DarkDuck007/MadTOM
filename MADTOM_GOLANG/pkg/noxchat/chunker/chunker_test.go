package chunker_test

import (
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxchat/chunker"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

func TestLogChunkerZstdCompression(t *testing.T) {
	c, err := chunker.NewLogChunker("test-node-1", 0)
	if err != nil {
		t.Fatalf("failed to create log chunker: %v", err)
	}

	now := time.Now().UnixNano()
	var emittedChunk *madtomv1.LogChunk

	for i := 0; i < chunker.DefaultRecordsPerLogChunk; i++ {
		ch, err := c.Append(6, "noxchat.main", "Testing high-throughput log line with zstd compression", nil, now+int64(i)*1000)
		if err != nil {
			t.Fatalf("append failed: %v", err)
		}
		if ch != nil {
			emittedChunk = ch
		}
	}

	if emittedChunk == nil {
		t.Fatalf("expected chunk to be sealed after %d records", chunker.DefaultRecordsPerLogChunk)
	}

	if emittedChunk.RawSize <= uint32(len(emittedChunk.ZstdRecords)) {
		t.Logf("Raw: %d bytes, Compressed: %d bytes", emittedChunk.RawSize, len(emittedChunk.ZstdRecords))
	}

	dec, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatalf("failed to create zstd reader: %v", err)
	}
	defer dec.Close()

	decompressed, err := dec.DecodeAll(emittedChunk.ZstdRecords, nil)
	if err != nil {
		t.Fatalf("failed to decompress chunk: %v", err)
	}

	var list madtomv1.LogRecordList
	if err := proto.Unmarshal(decompressed, &list); err != nil {
		t.Fatalf("failed to unmarshal LogRecordList: %v", err)
	}

	if len(list.Records) != chunker.DefaultRecordsPerLogChunk {
		t.Errorf("expected %d records in list, got %d", chunker.DefaultRecordsPerLogChunk, len(list.Records))
	}
	if list.Records[0].Unit != "noxchat.main" {
		t.Errorf("expected unit 'noxchat.main', got '%s'", list.Records[0].Unit)
	}
}

func TestChunkersMonotonicChunkIDsAcrossRestarts(t *testing.T) {
	now := time.Now().UnixNano()

	// Session 1: write 3 API call records and flush unsealed -> ChunkId 0 (seqs 1..3)
	api1, err := chunker.NewApiCallChunker("test-node-1", 0)
	if err != nil {
		t.Fatalf("NewApiCallChunker failed: %v", err)
	}
	defer api1.Close()

	for i := 0; i < 3; i++ {
		if _, err := api1.Append(&madtomv1.NoxRawApiCallRecord{CallId: "c1", TimestampUnixNano: now}); err != nil {
			t.Fatalf("append failed: %v", err)
		}
	}
	ch0, err := api1.FlushUnsealed(0)
	if err != nil || ch0 == nil {
		t.Fatalf("expected unsealed chunk 0, err=%v", err)
	}
	if ch0.ChunkId != 0 || ch0.FirstSeq != 1 || ch0.LastSeq != 3 {
		t.Fatalf("unexpected chunk 0: id=%d seq=%d..%d", ch0.ChunkId, ch0.FirstSeq, ch0.LastSeq)
	}

	// Simulate probe restart at startSeq=3, nextChunkID=1
	api2, err := chunker.NewApiCallChunker("test-node-1", 3)
	if err != nil {
		t.Fatalf("NewApiCallChunker restart failed: %v", err)
	}
	defer api2.Close()
	api2.SetNextChunkID(1)

	if _, err := api2.Append(&madtomv1.NoxRawApiCallRecord{CallId: "c2", TimestampUnixNano: now + 1}); err != nil {
		t.Fatalf("append after restart failed: %v", err)
	}
	ch1, err := api2.FlushUnsealed(0)
	if err != nil || ch1 == nil {
		t.Fatalf("expected unsealed chunk 1 after restart, err=%v", err)
	}
	if ch1.ChunkId == ch0.ChunkId {
		t.Fatalf("restart chunk overwrote previous chunk ID %d!", ch1.ChunkId)
	}
	if ch1.ChunkId != 1 || ch1.FirstSeq != 4 || ch1.LastSeq != 4 {
		t.Fatalf("unexpected chunk 1: id=%d seq=%d..%d", ch1.ChunkId, ch1.FirstSeq, ch1.LastSeq)
	}
}
