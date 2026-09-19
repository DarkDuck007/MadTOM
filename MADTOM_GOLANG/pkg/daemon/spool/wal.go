package spool

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

const (
	DefaultMaxSegmentSize  = 10 * 1024 * 1024
	DefaultMaxTotalSpool   = 1024 * 1024 * 1024
	DefaultChunkMaxSamples = 500
	// Leave space below the default 4 MiB gRPC receive limit for envelope metadata.
	DefaultChunkMaxBytes = 3 * 1024 * 1024
	MaxRecordBytes       = 64 * 1024 * 1024
	WalFilePrefix        = "segment-"
	WalFileSuffix        = ".wal"
)

type segmentMeta struct {
	path, name string
	seq, size  int64
}

// WALManager appends durable records and tracks a durable acknowledged prefix per segment.
// Sampling retains its per-record fsync guarantee; rotation groups records, not commits.
type WALManager struct {
	mu                                      sync.Mutex
	dir, nodeID                             string
	maxSegmentSize, maxTotalSpool           int64
	enableZstd                              bool
	encoder                                 *zstd.Encoder
	decoder                                 *zstd.Decoder
	currentFile                             *os.File
	currentSeq, currentBytes, currentOffset int64
	segments                                []segmentMeta
	totalSpoolBytes                         int64
	state                                   walState
	closed                                  bool
	writeErr                                error
}

func NewWALManager(dir, nodeID string, maxTotalSpool int64, enableZstd bool) (*WALManager, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, err
	}
	if _, err := os.Stat(filepath.Join(dir, migrationDir)); err == nil {
		return nil, fmt.Errorf("unfinished WAL migration: restart with --migrate")
	} else if !os.IsNotExist(err) {
		return nil, err
	}
	if maxTotalSpool <= 0 {
		maxTotalSpool = DefaultMaxTotalSpool
	}
	w := &WALManager{dir: dir, nodeID: nodeID, maxSegmentSize: DefaultMaxSegmentSize, maxTotalSpool: maxTotalSpool, enableZstd: enableZstd}
	if err := w.recover(); err != nil {
		return nil, err
	}
	var err error
	w.decoder, err = zstd.NewReader(nil, zstd.WithDecoderConcurrency(1), zstd.WithDecoderMaxMemory(MaxRecordBytes))
	if err != nil {
		w.Close()
		return nil, err
	}
	if enableZstd {
		w.encoder, err = zstd.NewWriter(nil)
		if err != nil {
			w.Close()
			return nil, err
		}
	}
	return w, nil
}

func (w *WALManager) WriteMetrics(samples []*madtomv1.SystemMetrics) (*madtomv1.TelemetryBatch, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return nil, fmt.Errorf("WAL is closed")
	}
	if w.writeErr != nil {
		return nil, w.writeErr
	}
	if len(samples) == 0 {
		return nil, nil
	}
	batch := &madtomv1.TelemetryBatch{NodeId: w.nodeID, Samples: samples}
	// New records must fit an uncompressed replay response. Legacy oversized records
	// are reported explicitly during replay, never silently skipped or acknowledged.
	if proto.Size(batch) > DefaultChunkMaxBytes-1024 {
		return nil, fmt.Errorf("WAL batch exceeds replay byte limit")
	}
	if w.enableZstd {
		raw, err := proto.Marshal(&madtomv1.TelemetryBatch{Samples: samples})
		if err != nil {
			return nil, err
		}
		batch.CompressedPayload = w.encoder.EncodeAll(raw, nil)
		batch.IsCompressed = true
		batch.Samples = nil
	}
	limit := w.maxSegmentSize
	if w.maxTotalSpool < limit {
		limit = w.maxTotalSpool
	}
	// Segment IDs have fixed overhead in the normal sequence range; marshal again after rotation.
	batch.SegmentId = fmt.Sprintf("%s%08d%s", WalFilePrefix, w.currentSeq, WalFileSuffix)
	if w.currentFile != nil {
		batch.SegmentId = filepath.Base(w.currentFile.Name())
	}
	payload, err := proto.Marshal(batch)
	if err != nil {
		return nil, err
	}
	if int64(len(payload)+4) > w.maxTotalSpool {
		return nil, fmt.Errorf("WAL record exceeds spool quota")
	}
	if w.currentFile == nil || (w.currentBytes > 0 && w.currentBytes+int64(len(payload)+4) > limit) {
		if err := w.rotateLocked(); err != nil {
			return nil, err
		}
		batch.SegmentId = filepath.Base(w.currentFile.Name())
		payload, err = proto.Marshal(batch)
		if err != nil {
			return nil, err
		}
	}
	if int64(len(payload)+4) > w.maxTotalSpool {
		return nil, fmt.Errorf("WAL record exceeds spool quota")
	}
	frame := make([]byte, 4+len(payload))
	binary.BigEndian.PutUint32(frame, uint32(len(payload)))
	copy(frame[4:], payload)
	before := w.currentOffset
	n, err := w.currentFile.Write(frame)
	if err == nil && n != len(frame) {
		err = io.ErrShortWrite
	}
	if err == nil {
		err = w.currentFile.Sync()
	}
	if err != nil {
		// A failed append must not become a hole in front of subsequent valid records.
		if rollback := w.currentFile.Truncate(before); rollback != nil {
			w.writeErr = fmt.Errorf("WAL rollback failed: %w", rollback)
		} else if syncErr := w.currentFile.Sync(); syncErr != nil {
			w.writeErr = fmt.Errorf("WAL rollback sync failed: %w", syncErr)
		}
		return nil, err
	}
	w.currentBytes += int64(len(frame))
	w.currentOffset = w.currentBytes
	w.segments[len(w.segments)-1].size = w.currentBytes
	w.totalSpoolBytes += int64(len(frame))
	batch.SegmentOffset = w.currentOffset
	if err := w.enforceQuotaLocked(); err != nil {
		return nil, err
	}
	return batch, nil
}

// ReadBatchChunk advances only at record boundaries, and never mutates the replay cursor.
// maxSamples is a target: a single atomic record may contain more samples.
func (w *WALManager) ReadBatchChunk(maxSamples int) (*madtomv1.TelemetryBatch, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return nil, fmt.Errorf("WAL is closed")
	}
	if w.writeErr != nil {
		return nil, w.writeErr
	}
	if maxSamples <= 0 {
		maxSamples = DefaultChunkMaxSamples
	}
	batch := &madtomv1.TelemetryBatch{NodeId: w.nodeID}
	first, last := "", ""
	used := proto.Size(batch) + 256
	stop := false
	for _, seg := range w.segments {
		offset := w.state.Acked[seg.name]
		if offset >= seg.size {
			continue
		}
		f, err := os.Open(seg.path)
		if err != nil {
			return nil, err
		}
		if _, err = f.Seek(offset, io.SeekStart); err != nil {
			f.Close()
			return nil, err
		}
		for offset < seg.size {
			record, next, err := w.readRecord(f, offset, seg.size)
			if err != nil {
				f.Close()
				return nil, fmt.Errorf("read %s at %d: %w", seg.name, offset, err)
			}
			cost := proto.Size(&madtomv1.TelemetryBatch{Samples: record.Samples})
			if used+cost > DefaultChunkMaxBytes || (len(batch.Samples) > 0 && len(batch.Samples)+len(record.Samples) > maxSamples) {
				if first == "" {
					f.Close()
					return nil, fmt.Errorf("WAL record exceeds replay byte limit")
				}
				stop = true
				break
			}
			if first == "" {
				first = seg.name
			}
			last = seg.name
			batch.SegmentOffset = next
			used += cost
			batch.Samples = append(batch.Samples, record.Samples...)
			offset = next
			if len(batch.Samples) >= maxSamples {
				stop = true
				break
			}
		}
		if err := f.Close(); err != nil {
			return nil, err
		}
		if stop {
			break
		}
	}
	if first == "" {
		return nil, nil
	}
	batch.SegmentId = first
	if last != first {
		batch.SegmentId = first + ":" + last
	}
	batch.IsBacklog = stop || len(w.segments) > 1 || len(batch.Samples) > 1
	if w.enableZstd && len(batch.Samples) > 5 {
		raw, err := proto.Marshal(&madtomv1.TelemetryBatch{Samples: batch.Samples})
		if err != nil {
			return nil, err
		}
		batch.CompressedPayload = w.encoder.EncodeAll(raw, nil)
		batch.IsCompressed = true
		batch.Samples = nil
	}
	return batch, nil
}

func (w *WALManager) ReadOldestBatch() (*madtomv1.TelemetryBatch, error) {
	return w.ReadBatchChunk(DefaultChunkMaxSamples)
}

func (w *WALManager) readRecord(f *os.File, offset, size int64) (*madtomv1.TelemetryBatch, int64, error) {
	var header [4]byte
	if _, err := io.ReadFull(f, header[:]); err != nil {
		return nil, offset, err
	}
	length := int64(binary.BigEndian.Uint32(header[:]))
	if length <= 0 || length > MaxRecordBytes || length > size-offset-4 {
		return nil, offset, fmt.Errorf("invalid WAL record length")
	}
	payload := make([]byte, int(length))
	if _, err := io.ReadFull(f, payload); err != nil {
		return nil, offset, err
	}
	record := &madtomv1.TelemetryBatch{}
	if err := proto.Unmarshal(payload, record); err != nil {
		return nil, offset, err
	}
	if record.IsCompressed {
		if len(record.CompressedPayload) == 0 || len(record.Samples) != 0 {
			return nil, offset, fmt.Errorf("invalid compressed WAL envelope")
		}
		raw, err := w.decoder.DecodeAll(record.CompressedPayload, nil)
		if err != nil {
			return nil, offset, err
		}
		inner := &madtomv1.TelemetryBatch{}
		if err := proto.Unmarshal(raw, inner); err != nil {
			return nil, offset, err
		}
		record.Samples = inner.Samples
	}
	return record, offset + 4 + length, nil
}

// AcknowledgeSegment commits only through the supplied end offset. Offsets are mandatory.
// Existing collectors already echo this field for push, pull and reverse-push.
func (w *WALManager) AcknowledgeSegment(segmentID string, offset int64) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return fmt.Errorf("WAL is closed")
	}
	if offset <= 0 {
		return fmt.Errorf("acknowledgement requires a positive record end offset")
	}
	parts := strings.Split(segmentID, ":")
	if len(parts) > 2 {
		return fmt.Errorf("invalid segment range")
	}
	start, err := parseSegment(parts[0])
	if err != nil {
		return err
	}
	end := start
	if len(parts) == 2 {
		end, err = parseSegment(parts[1])
		if err != nil {
			return err
		}
	}
	if start > end || end > w.state.LastSequence {
		return fmt.Errorf("invalid acknowledgement range")
	}
	// Validate the final boundary before acknowledging any earlier segment.
	found := false
	for _, seg := range w.segments {
		if seg.seq == end {
			found = true
			if offset > w.state.Acked[seg.name] {
				if offset > seg.size {
					return fmt.Errorf("acknowledgement exceeds segment size")
				}
				boundary, err := recordBoundary(seg.path, w.state.Acked[seg.name], offset)
				if err != nil {
					return err
				}
				if !boundary {
					return fmt.Errorf("acknowledgement is not a record boundary")
				}
			}
		}
	}
	// Already deleted (acknowledged or quota-evicted); sequence IDs are never reused.
	if !found {
		return nil
	}
	next := w.state.clone()
	for _, seg := range w.segments {
		if seg.seq >= start && seg.seq <= end {
			limit := seg.size
			if seg.seq == end {
				limit = offset
			}
			if limit > next.Acked[seg.name] {
				next.Acked[seg.name] = limit
			}
		}
	}
	if err := w.persistState(next); err != nil {
		return err
	}
	w.state = next
	return w.removeAcknowledgedLocked()
}

func (w *WALManager) HasPendingBacklog() bool {
	w.mu.Lock()
	defer w.mu.Unlock()
	for _, seg := range w.segments {
		if seg.size > w.state.Acked[seg.name] {
			return true
		}
	}
	return false
}

func (w *WALManager) rotateLocked() error {
	if w.currentFile != nil {
		if err := w.currentFile.Close(); err != nil {
			return err
		}
		w.currentFile = nil
	}
	if err := w.removeAcknowledgedLocked(); err != nil {
		return err
	}
	next := w.state.clone()
	if next.LastSequence == 1<<63-1 {
		return fmt.Errorf("WAL sequence exhausted")
	}
	next.LastSequence++
	if err := w.persistState(next); err != nil {
		return err
	}
	w.state = next
	w.currentSeq = next.LastSequence
	name := fmt.Sprintf("%s%08d%s", WalFilePrefix, w.currentSeq, WalFileSuffix)
	path := filepath.Join(w.dir, name)
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return err
	}
	if err := syncDirectory(w.dir); err != nil {
		f.Close()
		return err
	}
	w.currentFile = f
	w.currentBytes = 0
	w.currentOffset = 0
	w.segments = append(w.segments, segmentMeta{path: path, name: name, seq: w.currentSeq})
	return nil
}

func (w *WALManager) removeAcknowledgedLocked() error {
	for i := 0; i < len(w.segments); {
		seg := w.segments[i]
		if w.currentFile != nil && filepath.Base(w.currentFile.Name()) == seg.name {
			i++
			continue
		}
		if w.state.Acked[seg.name] < seg.size {
			i++
			continue
		}
		if err := w.removeSegmentLocked(i); err != nil {
			return err
		}
	}
	return nil
}

func (w *WALManager) removeSegmentLocked(i int) error {
	seg := w.segments[i]
	if err := os.Remove(seg.path); err != nil && !os.IsNotExist(err) {
		return err
	}
	if err := syncDirectory(w.dir); err != nil {
		return err
	}
	w.totalSpoolBytes -= seg.size
	w.segments = append(w.segments[:i], w.segments[i+1:]...)
	delete(w.state.Acked, seg.name)
	return nil
}

func (w *WALManager) enforceQuotaLocked() error {
	for w.totalSpoolBytes > w.maxTotalSpool && len(w.segments) > 1 {
		if err := w.removeSegmentLocked(0); err != nil {
			return err
		}
	}
	return nil
}

func parseSegment(name string) (int64, error) {
	if filepath.Base(name) != name || !strings.HasPrefix(name, WalFilePrefix) || !strings.HasSuffix(name, WalFileSuffix) {
		return 0, fmt.Errorf("invalid segment ID")
	}
	seq, err := strconv.ParseInt(strings.TrimSuffix(strings.TrimPrefix(name, WalFilePrefix), WalFileSuffix), 10, 64)
	if err != nil || seq <= 0 {
		return 0, fmt.Errorf("invalid segment sequence")
	}
	return seq, nil
}

func (w *WALManager) scanDirectory() ([]segmentMeta, int64, error) {
	entries, err := os.ReadDir(w.dir)
	if err != nil {
		return nil, 0, err
	}
	var segments []segmentMeta
	var total int64
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		seq, err := parseSegment(entry.Name())
		if err != nil {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			return nil, 0, err
		}
		segments = append(segments, segmentMeta{filepath.Join(w.dir, entry.Name()), entry.Name(), seq, info.Size()})
		total += info.Size()
	}
	sort.Slice(segments, func(i, j int) bool { return segments[i].seq < segments[j].seq })
	return segments, total, nil
}

func (w *WALManager) Close() error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return nil
	}
	w.closed = true
	if w.encoder != nil {
		w.encoder.Close()
	}
	if w.decoder != nil {
		w.decoder.Close()
	}
	if w.currentFile != nil {
		err := w.currentFile.Close()
		w.currentFile = nil
		return err
	}
	return nil
}
