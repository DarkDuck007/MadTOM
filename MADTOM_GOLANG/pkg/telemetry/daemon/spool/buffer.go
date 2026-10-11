package spool

import (
	"sync"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/proto"
)

const (
	DefaultMaxMemoryBufferBytes = 8 * 1024 * 1024 // 8 MB default in-memory buffer
)

type bufferedItem struct {
	seq       int64
	size      int64
	samples   []*madtomv1.SystemMetrics
	logChunks []*madtomv1.LogChunk
}

// MemoryBuffer buffers telemetry samples and log chunks in RAM while transport
// is active and healthy. When the buffer reaches capacity (e.g. during a collector outage),
// it flushes accumulated data to the underlying disk WAL.
type MemoryBuffer struct {
	mu           sync.Mutex
	maxBytes     int64
	currentBytes int64
	seqCounter   int64
	items        []*bufferedItem
	wal          *WALManager
}

// NewMemoryBuffer creates an in-memory buffer in front of the disk WAL.
// If maxBytes <= 0, DefaultMaxMemoryBufferBytes (8 MB) is used.
func NewMemoryBuffer(wal *WALManager, maxBytes int64) *MemoryBuffer {
	if maxBytes <= 0 {
		maxBytes = DefaultMaxMemoryBufferBytes
	}
	return &MemoryBuffer{
		maxBytes: maxBytes,
		wal:      wal,
	}
}

// SetMaxBytes updates the memory buffer capacity limit.
func (b *MemoryBuffer) SetMaxBytes(bytes int64) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if bytes > 0 {
		b.maxBytes = bytes
	}
}

// Enqueue adds samples or log chunks to the memory buffer. If the buffer is full
// or if the disk WAL has an unacknowledged backlog, data is flushed / written to disk WAL.
func (b *MemoryBuffer) Enqueue(samples []*madtomv1.SystemMetrics, logChunks []*madtomv1.LogChunk) error {
	b.mu.Lock()
	defer b.mu.Unlock()

	if len(samples) == 0 && len(logChunks) == 0 {
		return nil
	}

	size := int64(proto.Size(&madtomv1.TelemetryBatch{Samples: samples, LogChunks: logChunks}))

	// If disk WAL already has an unacknowledged backlog (from an earlier outage),
	// we must write to disk to preserve chronological replay order.
	if b.wal != nil && b.wal.HasPendingBacklog() {
		if err := b.flushLocked(); err != nil {
			return err
		}
		_, err := b.wal.WriteBatch(samples, logChunks)
		return err
	}

	// If memory buffer would exceed capacity, flush all buffered items to disk WAL!
	if b.currentBytes+size > b.maxBytes {
		if err := b.flushLocked(); err != nil {
			return err
		}
		if b.wal != nil {
			_, err := b.wal.WriteBatch(samples, logChunks)
			return err
		}
	}

	// Buffer in memory
	b.seqCounter++
	item := &bufferedItem{
		seq:       b.seqCounter,
		size:      size,
		samples:   samples,
		logChunks: logChunks,
	}
	b.items = append(b.items, item)
	b.currentBytes += size
	return nil
}

// PopBatch retrieves the next batch to send.
// If disk WAL has a backlog, it reads from disk first.
// Otherwise, it reads from the in-memory buffer.
func (b *MemoryBuffer) PopBatch(maxSamples int) (*madtomv1.TelemetryBatch, bool, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	if b.wal != nil && b.wal.HasPendingBacklog() {
		batch, err := b.wal.ReadBatchChunk(maxSamples)
		return batch, true, err
	}

	if len(b.items) == 0 {
		return nil, false, nil
	}

	batch := &madtomv1.TelemetryBatch{
		SegmentId: "mem",
	}

	var lastSeq int64
	var usedCost int
	for _, item := range b.items {
		cost := int(item.size)
		if usedCost+cost > DefaultChunkMaxBytes || (len(batch.Samples) > 0 && len(batch.Samples)+len(item.samples) > maxSamples) {
			break
		}
		batch.Samples = append(batch.Samples, item.samples...)
		batch.LogChunks = append(batch.LogChunks, item.logChunks...)
		lastSeq = item.seq
		usedCost += cost
		if len(batch.Samples) >= maxSamples || len(batch.LogChunks) >= 16 {
			break
		}
	}

	if len(batch.Samples) == 0 && len(batch.LogChunks) == 0 {
		return nil, false, nil
	}

	batch.SegmentOffset = lastSeq
	batch.IsBacklog = len(b.items) > 1 || len(batch.Samples) > 1 || len(batch.LogChunks) > 1
	return batch, false, nil
}

// Acknowledge commits a successfully received batch.
// If fromDisk is true, it commits to the disk WAL.
// Otherwise, it removes the acknowledged records from the in-memory buffer.
func (b *MemoryBuffer) Acknowledge(fromDisk bool, segmentID string, offset int64) error {
	b.mu.Lock()
	defer b.mu.Unlock()

	if fromDisk {
		if b.wal != nil {
			return b.wal.AcknowledgeSegment(segmentID, offset)
		}
		return nil
	}

	// In-memory acknowledgement
	var remaining []*bufferedItem
	for _, item := range b.items {
		if item.seq <= offset {
			b.currentBytes -= item.size
			if b.currentBytes < 0 {
				b.currentBytes = 0
			}
		} else {
			remaining = append(remaining, item)
		}
	}
	b.items = remaining
	return nil
}

func (b *MemoryBuffer) flushLocked() error {
	if b.wal == nil || len(b.items) == 0 {
		b.items = nil
		b.currentBytes = 0
		return nil
	}
	for _, item := range b.items {
		if _, err := b.wal.WriteBatch(item.samples, item.logChunks); err != nil {
			return err
		}
	}
	b.items = nil
	b.currentBytes = 0
	return nil
}

// FlushToDisk flushes all in-memory items to the disk WAL.
func (b *MemoryBuffer) FlushToDisk() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.flushLocked()
}

// CurrentMemoryBytes returns the current memory buffer utilization in bytes.
func (b *MemoryBuffer) CurrentMemoryBytes() int64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.currentBytes
}

// MaxMemoryBytes returns the maximum memory buffer limit in bytes.
func (b *MemoryBuffer) MaxMemoryBytes() int64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.maxBytes
}

// ItemCount returns the number of buffered in-memory items.
func (b *MemoryBuffer) ItemCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.items)
}
