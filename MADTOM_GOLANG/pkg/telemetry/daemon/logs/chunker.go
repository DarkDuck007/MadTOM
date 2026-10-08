package logs

import (
	"fmt"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

const (
	// DefaultRecordsPerChunk is the target record count per sealed chunk.
	DefaultRecordsPerChunk = 256
)

// Chunker aggregates LogRecord entries into zstd-compressed LogChunk messages.
// It assigns monotonic sequence numbers and compresses records in blocks of 256.
type Chunker struct {
	mu             sync.Mutex
	nodeID         string
	currentSeq     uint64
	records        []*madtomv1.LogRecord
	encoder        *zstd.Encoder
	recordsPerSize int
}

// NewChunker creates a new Chunker starting from initialSeq.
func NewChunker(nodeID string, initialSeq uint64) (*Chunker, error) {
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return nil, fmt.Errorf("init zstd encoder: %w", err)
	}
	return &Chunker{
		nodeID:         nodeID,
		currentSeq:     initialSeq,
		records:        make([]*madtomv1.LogRecord, 0, DefaultRecordsPerChunk),
		encoder:        enc,
		recordsPerSize: DefaultRecordsPerChunk,
	}, nil
}

// CurrentSeq returns the next sequence number to be assigned.
func (c *Chunker) CurrentSeq() uint64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.currentSeq
}

// AddRecord adds a log record, assigns its monotonic sequence number,
// and returns a sealed LogChunk if the chunk boundary (256 records) is reached.
func (c *Chunker) AddRecord(rec *madtomv1.LogRecord) (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	rec.Seq = c.currentSeq
	c.currentSeq++
	if rec.TimestampUnixNano == 0 {
		rec.TimestampUnixNano = time.Now().UnixNano()
	}

	c.records = append(c.records, rec)
	if len(c.records) >= c.recordsPerSize || (rec.Seq+1)%uint64(c.recordsPerSize) == 0 {
		return c.sealLocked()
	}
	return nil, nil
}

// SealCurrent seals any currently buffered records into a sealed LogChunk.
func (c *Chunker) SealCurrent() (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.records) == 0 {
		return nil, nil
	}
	return c.sealLocked()
}

// BuildUnsealedChunk creates a snapshot LogChunk of the current records
// with sealed = false, for live streaming without advancing or closing the active buffer.
func (c *Chunker) BuildUnsealedChunk() (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.records) == 0 {
		return nil, nil
	}
	return c.encodeChunkLocked(c.records, false)
}

func (c *Chunker) sealLocked() (*madtomv1.LogChunk, error) {
	if len(c.records) == 0 {
		return nil, nil
	}
	chunk, err := c.encodeChunkLocked(c.records, true)
	if err != nil {
		return nil, err
	}
	c.records = make([]*madtomv1.LogRecord, 0, c.recordsPerSize)
	return chunk, nil
}

func (c *Chunker) encodeChunkLocked(recs []*madtomv1.LogRecord, sealed bool) (*madtomv1.LogChunk, error) {
	first := recs[0]
	last := recs[len(recs)-1]

	list := &madtomv1.LogRecordList{Records: recs}
	raw, err := proto.Marshal(list)
	if err != nil {
		return nil, fmt.Errorf("marshal log records: %w", err)
	}

	compressed := c.encoder.EncodeAll(raw, nil)

	return &madtomv1.LogChunk{
		NodeId:           c.nodeID,
		ChunkId:          first.Seq / uint64(c.recordsPerSize),
		FirstSeq:         first.Seq,
		LastSeq:          last.Seq,
		FirstTsUnixNano:  first.TimestampUnixNano,
		LastTsUnixNano:   last.TimestampUnixNano,
		RecordCount:      uint32(len(recs)),
		Sealed:           sealed,
		ZstdRecords:      compressed,
		RawSize:          uint32(len(raw)),
	}, nil
}

// Close releases resources held by the chunker.
func (c *Chunker) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.encoder != nil {
		_ = c.encoder.Close()
	}
}
