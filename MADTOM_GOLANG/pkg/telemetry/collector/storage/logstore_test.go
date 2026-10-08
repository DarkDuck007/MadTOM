package storage

import (
	"os"
	"path/filepath"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/cockroachdb/pebble"
)

func TestLogStore_PutAndQuery(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "test_pebble")
	db, err := pebble.Open(dir, &pebble.Options{})
	if err != nil {
		t.Fatalf("pebble.Open: %v", err)
	}
	defer db.Close()
	defer os.RemoveAll(dir)

	store := NewLogStore(db)

	chunk0 := &madtomv1.LogChunk{
		NodeId:          "node-1",
		ChunkId:         0,
		FirstSeq:        0,
		LastSeq:         255,
		FirstTsUnixNano: 1000,
		LastTsUnixNano:  2000,
		RecordCount:     256,
		Sealed:          true,
		ZstdRecords:     []byte("test_compressed_data_0"),
		RawSize:         1024,
	}

	chunk1 := &madtomv1.LogChunk{
		NodeId:          "node-1",
		ChunkId:         1,
		FirstSeq:        256,
		LastSeq:         511,
		FirstTsUnixNano: 2001,
		LastTsUnixNano:  3000,
		RecordCount:     256,
		Sealed:          true,
		ZstdRecords:     []byte("test_compressed_data_1"),
		RawSize:         1024,
	}

	if err := store.PutChunk(chunk0); err != nil {
		t.Fatalf("PutChunk 0: %v", err)
	}
	if err := store.PutChunk(chunk1); err != nil {
		t.Fatalf("PutChunk 1: %v", err)
	}

	// Test GetChunk
	loaded, err := store.GetChunk("node-1", 0)
	if err != nil {
		t.Fatalf("GetChunk: %v", err)
	}
	if loaded.ChunkId != 0 || loaded.RecordCount != 256 {
		t.Errorf("Unexpected loaded chunk: %+v", loaded)
	}

	// Test GetStats
	stats, err := store.GetStats("node-1")
	if err != nil {
		t.Fatalf("GetStats: %v", err)
	}
	if stats.TotalRecords != 512 || stats.FirstSeq != 0 || stats.LastSeq != 511 {
		t.Errorf("Unexpected stats: %+v", stats)
	}

	// Test QueryChunks
	chunks, hasMore, err := store.QueryChunks("node-1", 0, 10, 0, true)
	if err != nil {
		t.Fatalf("QueryChunks: %v", err)
	}
	if len(chunks) != 2 {
		t.Errorf("Expected 2 chunks, got %d", len(chunks))
	}
	if hasMore {
		t.Errorf("Expected hasMore false")
	}

	// Test Retention
	// Set maxBytes so only chunk0 is deleted
	if err := store.EnforceRetention("node-1", stats.TotalBytes-10, 0); err != nil {
		t.Fatalf("EnforceRetention: %v", err)
	}

	statsAfter, _ := store.GetStats("node-1")
	if statsAfter.TotalRecords != 256 {
		t.Errorf("Expected 256 records after retention, got %d", statsAfter.TotalRecords)
	}
}
