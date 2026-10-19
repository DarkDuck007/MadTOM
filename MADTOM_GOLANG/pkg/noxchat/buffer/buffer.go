package buffer

import (
	"sync"
)

// Chunk represents any chunk item stored in the bounded ring buffer.
type Chunk struct {
	ID        uint64
	Timestamp int64
	Payload   []byte
	RawSize   uint32
}

// RingChunkBuffer is a thread-safe, byte-bounded FIFO chunk buffer.
// When capacity is exceeded while subscribers are connected, it evicts the oldest
// chunk (FIFO) and increments DroppedChunks counter.
// When subscribers disconnect, ClearAndReset immediately frees memory.
type RingChunkBuffer struct {
	mu            sync.Mutex
	maxBytes      int64
	currentBytes  int64
	chunks        []*Chunk
	droppedChunks uint64
}

// NewRingChunkBuffer creates a new ring buffer with the given byte ceiling.
func NewRingChunkBuffer(maxBytes int64) *RingChunkBuffer {
	if maxBytes <= 0 {
		maxBytes = 1024 * 1024 // 1 MB default
	}
	return &RingChunkBuffer{
		maxBytes: maxBytes,
		chunks:   make([]*Chunk, 0, 32),
	}
}

// SetMaxBytes updates the capacity limit.
func (b *RingChunkBuffer) SetMaxBytes(bytes int64) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if bytes > 0 {
		b.maxBytes = bytes
		b.evictIfNeededLocked(0)
	}
}

// Push appends a new chunk to the buffer. If adding it exceeds maxBytes,
// oldest chunks are evicted and droppedChunks is incremented.
func (b *RingChunkBuffer) Push(chunk *Chunk) uint64 {
	b.mu.Lock()
	defer b.mu.Unlock()

	size := int64(len(chunk.Payload)) + 32
	b.evictIfNeededLocked(size)

	b.chunks = append(b.chunks, chunk)
	b.currentBytes += size
	return b.droppedChunks
}

// PopOldest retrieves and removes the oldest chunk in the buffer.
func (b *RingChunkBuffer) PopOldest() (*Chunk, uint64, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()

	if len(b.chunks) == 0 {
		return nil, b.droppedChunks, false
	}

	first := b.chunks[0]
	b.chunks = b.chunks[1:]
	size := int64(len(first.Payload)) + 32
	b.currentBytes -= size
	if b.currentBytes < 0 {
		b.currentBytes = 0
	}

	return first, b.droppedChunks, true
}

// Snapshot returns a copy of all current chunks without removing them.
func (b *RingChunkBuffer) Snapshot() ([]*Chunk, uint64) {
	b.mu.Lock()
	defer b.mu.Unlock()

	res := make([]*Chunk, len(b.chunks))
	copy(res, b.chunks)
	return res, b.droppedChunks
}

// ClearAndReset frees all memory and resets dropped chunks counter upon disconnect.
func (b *RingChunkBuffer) ClearAndReset() {
	b.mu.Lock()
	defer b.mu.Unlock()

	b.chunks = make([]*Chunk, 0, 32)
	b.currentBytes = 0
	b.droppedChunks = 0
}

// Stats returns (currentBytes, maxBytes, chunkCount, droppedChunks).
func (b *RingChunkBuffer) Stats() (int64, int64, int, uint64) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.currentBytes, b.maxBytes, len(b.chunks), b.droppedChunks
}

func (b *RingChunkBuffer) evictIfNeededLocked(incomingSize int64) {
	for (b.currentBytes+incomingSize > b.maxBytes) && len(b.chunks) > 0 {
		oldest := b.chunks[0]
		b.chunks = b.chunks[1:]
		b.currentBytes -= int64(len(oldest.Payload)) + 32
		if b.currentBytes < 0 {
			b.currentBytes = 0
		}
		b.droppedChunks++
	}
}
