package buffer_test

import (
	"testing"

	"github.com/DarkDuck007/madtom/pkg/noxchat/buffer"
)

func TestRingChunkBufferDropOldestEviction(t *testing.T) {
	// 100 byte buffer
	buf := buffer.NewRingChunkBuffer(100)

	// Add 3 chunks of 10 bytes payload + 32 overhead = 42 bytes each.
	// 42 * 3 = 126 > 100. Pushing 3rd chunk must evict 1st chunk.
	chunk1 := &buffer.Chunk{ID: 1, Payload: make([]byte, 10)}
	chunk2 := &buffer.Chunk{ID: 2, Payload: make([]byte, 10)}
	chunk3 := &buffer.Chunk{ID: 3, Payload: make([]byte, 10)}

	buf.Push(chunk1)
	buf.Push(chunk2)
	buf.Push(chunk3)

	chunks, dropped := buf.Snapshot()
	if len(chunks) != 2 {
		t.Fatalf("expected 2 chunks after eviction, got %d", len(chunks))
	}
	if dropped != 1 {
		t.Errorf("expected droppedChunks == 1, got %d", dropped)
	}

	if chunks[0].ID != 2 || chunks[1].ID != 3 {
		t.Errorf("expected chunk IDs [2, 3], got [%d, %d]", chunks[0].ID, chunks[1].ID)
	}

	curBytes, maxBytes, count, _ := buf.Stats()
	if curBytes > maxBytes {
		t.Errorf("buffer exceeded max bytes: %d > %d", curBytes, maxBytes)
	}
	if count != 2 {
		t.Errorf("expected 2 chunks, got %d", count)
	}
}

func TestRingChunkBufferClearAndResetOnDisconnect(t *testing.T) {
	buf := buffer.NewRingChunkBuffer(1024)
	buf.Push(&buffer.Chunk{ID: 1, Payload: []byte("test-data-1")})
	buf.Push(&buffer.Chunk{ID: 2, Payload: []byte("test-data-2")})

	_, _, count, _ := buf.Stats()
	if count != 2 {
		t.Fatalf("expected 2 items, got %d", count)
	}

	buf.ClearAndReset()

	curBytes, _, countAfter, dropped := buf.Stats()
	if countAfter != 0 {
		t.Errorf("expected 0 items after ClearAndReset, got %d", countAfter)
	}
	if curBytes != 0 {
		t.Errorf("expected 0 bytes after ClearAndReset, got %d", curBytes)
	}
	if dropped != 0 {
		t.Errorf("expected dropped to be reset to 0, got %d", dropped)
	}
}
