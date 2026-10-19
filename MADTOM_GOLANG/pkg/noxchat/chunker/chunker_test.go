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
