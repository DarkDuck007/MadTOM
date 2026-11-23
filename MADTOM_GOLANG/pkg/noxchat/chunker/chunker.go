package chunker

import (
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

const (
	DefaultRecordsPerLogChunk    = 256
	DefaultMaxLogChunkAgeSeconds = 2
)

// LogChunker batches LogRecord messages into sealed, zstd-compressed LogChunk messages.
type LogChunker struct {
	mu           sync.Mutex
	nodeID       string
	encoder      *zstd.Encoder
	currentChunk *madtomv1.LogChunk
	records      []*madtomv1.LogRecord
	currentSeq   uint64
	chunkCounter uint64
	lastFlush    time.Time
	dirty        bool
}

func NewLogChunker(nodeID string, startSeq uint64) (*LogChunker, error) {
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return nil, err
	}
	var startChunk uint64
	if startSeq > 0 {
		startChunk = (startSeq / DefaultRecordsPerLogChunk) + 1
	}
	return &LogChunker{
		nodeID:       nodeID,
		encoder:      enc,
		currentSeq:   startSeq,
		records:      make([]*madtomv1.LogRecord, 0, DefaultRecordsPerLogChunk),
		lastFlush:    time.Now(),
		chunkCounter: startChunk,
	}, nil
}

func (c *LogChunker) SetNextChunkID(nextChunkID uint64) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if nextChunkID > c.chunkCounter {
		c.chunkCounter = nextChunkID
	}
}

// Append adds a record and returns a sealed LogChunk if 256 records were reached.
func (c *LogChunker) Append(priority uint32, unit, message string, fields map[string]string, tsNano int64) (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.currentSeq++
	rec := &madtomv1.LogRecord{
		Seq:               c.currentSeq,
		TimestampUnixNano: tsNano,
		Priority:          priority,
		Unit:              unit,
		Message:           message,
		Fields:            fields,
	}
	c.records = append(c.records, rec)
	c.dirty = true

	if len(c.records) >= DefaultRecordsPerLogChunk {
		return c.sealLocked(true)
	}
	return nil, nil
}

// FlushUnsealed creates an unsealed snapshot if new records arrived and age exceeds threshold.
func (c *LogChunker) FlushUnsealed(maxAge time.Duration) (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if len(c.records) == 0 || !c.dirty {
		return nil, nil
	}
	if time.Since(c.lastFlush) >= maxAge {
		return c.sealLocked(false)
	}
	return nil, nil
}

// Flush seals any pending log records.
func (c *LogChunker) Flush() (*madtomv1.LogChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.sealLocked(true)
}

func (c *LogChunker) sealLocked(isSealed bool) (*madtomv1.LogChunk, error) {
	if len(c.records) == 0 {
		return nil, nil
	}

	first := c.records[0]
	last := c.records[len(c.records)-1]

	list := &madtomv1.LogRecordList{Records: c.records}
	raw, err := proto.Marshal(list)
	if err != nil {
		return nil, err
	}

	compressed := c.encoder.EncodeAll(raw, nil)
	chunkID := c.chunkCounter

	chunk := &madtomv1.LogChunk{
		NodeId:          c.nodeID,
		ChunkId:         chunkID,
		FirstSeq:        first.Seq,
		LastSeq:         last.Seq,
		FirstTsUnixNano: first.TimestampUnixNano,
		LastTsUnixNano:  last.TimestampUnixNano,
		RecordCount:     uint32(len(c.records)),
		Sealed:          isSealed,
		ZstdRecords:     compressed,
		RawSize:         uint32(len(raw)),
	}

	c.lastFlush = time.Now()
	c.dirty = false
	if isSealed {
		c.records = make([]*madtomv1.LogRecord, 0, DefaultRecordsPerLogChunk)
		c.chunkCounter++
	}
	return chunk, nil
}

func (c *LogChunker) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.encoder != nil {
		c.encoder.Close()
	}
}

// ApiCallChunker batches raw LLM API calls into sealed, zstd-compressed NoxApiCallChunk blocks.
type ApiCallChunker struct {
	mu           sync.Mutex
	nodeID       string
	encoder      *zstd.Encoder
	records      []*madtomv1.NoxRawApiCallRecord
	currentSeq   uint64
	chunkCounter uint64
	lastFlush    time.Time
	dirty        bool
}

func NewApiCallChunker(nodeID string, startSeq uint64) (*ApiCallChunker, error) {
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return nil, err
	}
	var startChunk uint64
	if startSeq > 0 {
		startChunk = (startSeq / 16) + 1
	}
	return &ApiCallChunker{
		nodeID:       nodeID,
		encoder:      enc,
		currentSeq:   startSeq,
		records:      make([]*madtomv1.NoxRawApiCallRecord, 0, 16),
		chunkCounter: startChunk,
		lastFlush:    time.Now(),
	}, nil
}

func (c *ApiCallChunker) SetNextChunkID(nextChunkID uint64) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if nextChunkID > c.chunkCounter {
		c.chunkCounter = nextChunkID
	}
}

// Append adds a raw API call and returns a sealed chunk every 16 calls.
func (c *ApiCallChunker) Append(rec *madtomv1.NoxRawApiCallRecord) (*madtomv1.NoxApiCallChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.currentSeq++
	c.records = append(c.records, rec)
	c.dirty = true

	// Seal if reached 16 calls
	if len(c.records) >= 16 {
		return c.sealLocked(true)
	}
	return nil, nil
}

// FlushUnsealed flushes pending API call records as an unsealed chunk if dirty and age >= maxAge.
func (c *ApiCallChunker) FlushUnsealed(maxAge time.Duration) (*madtomv1.NoxApiCallChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if len(c.records) == 0 || !c.dirty {
		return nil, nil
	}
	if time.Since(c.lastFlush) >= maxAge {
		return c.sealLocked(false)
	}
	return nil, nil
}

// Flush forces sealing of any pending API call records.
func (c *ApiCallChunker) Flush() (*madtomv1.NoxApiCallChunk, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.sealLocked(true)
}

func (c *ApiCallChunker) sealLocked(isSealed bool) (*madtomv1.NoxApiCallChunk, error) {
	if len(c.records) == 0 {
		return nil, nil
	}

	first := c.records[0]
	last := c.records[len(c.records)-1]

	list := &madtomv1.NoxApiCallRecordList{Records: c.records}
	raw, err := proto.Marshal(list)
	if err != nil {
		return nil, err
	}

	compressed := c.encoder.EncodeAll(raw, nil)
	firstSeq := c.currentSeq - uint64(len(c.records)) + 1
	chunkID := c.chunkCounter

	chunk := &madtomv1.NoxApiCallChunk{
		NodeId:          c.nodeID,
		ChunkId:         chunkID,
		FirstSeq:        firstSeq,
		LastSeq:         c.currentSeq,
		FirstTsUnixNano: first.TimestampUnixNano,
		LastTsUnixNano:  last.TimestampUnixNano,
		RecordCount:     uint32(len(c.records)),
		Sealed:          isSealed,
		ZstdRecords:     compressed,
		RawSize:         uint32(len(raw)),
	}

	c.lastFlush = time.Now()
	c.dirty = false
	if isSealed {
		c.records = make([]*madtomv1.NoxRawApiCallRecord, 0, 16)
		c.chunkCounter++
	}
	return chunk, nil
}

func (c *ApiCallChunker) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.encoder != nil {
		c.encoder.Close()
	}
}

// StateChunker serializes live state/telemetry/jobs batches into zstd-compressed NoxStateChunk payloads.
type StateChunker struct {
	nodeID  string
	encoder *zstd.Encoder
}

func NewStateChunker(nodeID string) (*StateChunker, error) {
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedFastest))
	if err != nil {
		return nil, err
	}
	return &StateChunker{nodeID: nodeID, encoder: enc}, nil
}

func (s *StateChunker) CompressStatePayload(payload *madtomv1.NoxStateChunkPayload) ([]byte, uint32, error) {
	return s.CompressPayload(payload)
}

// CompressPayload encodes a protobuf batch as a zstd frame.
func (s *StateChunker) CompressPayload(payload proto.Message) ([]byte, uint32, error) {
	raw, err := proto.Marshal(payload)
	if err != nil {
		return nil, 0, err
	}
	compressed := s.encoder.EncodeAll(raw, nil)
	return compressed, uint32(len(raw)), nil
}

func (s *StateChunker) Close() {
	if s.encoder != nil {
		s.encoder.Close()
	}
}
