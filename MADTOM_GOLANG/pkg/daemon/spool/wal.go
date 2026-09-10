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
	DefaultMaxSegmentSize = 10 * 1024 * 1024   // 10 MB per segment file
	DefaultMaxTotalSpool  = 1024 * 1024 * 1024 // 1 GB bounded hard limit
	WalFilePrefix         = "segment-"
	WalFileSuffix         = ".wal"
)

// WALManager manages bounded on-disk write-ahead telemetry log segments.
type WALManager struct {
	mu             sync.Mutex
	dir            string
	nodeID         string
	maxSegmentSize int64
	maxTotalSpool  int64
	enableZstd     bool

	encoder *zstd.Encoder
	decoder *zstd.Decoder

	currentFile   *os.File
	currentSeq    int64
	currentBytes  int64
	currentOffset int64
}

// NewWALManager initializes or recovers the WAL spool directory.
func NewWALManager(dir string, nodeID string, maxTotalSpool int64, enableZstd bool) (*WALManager, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, fmt.Errorf("failed to create spool directory: %w", err)
	}

	if maxTotalSpool <= 0 {
		maxTotalSpool = DefaultMaxTotalSpool
	}

	w := &WALManager{
		dir:            dir,
		nodeID:         nodeID,
		maxSegmentSize: DefaultMaxSegmentSize,
		maxTotalSpool:  maxTotalSpool,
		enableZstd:     enableZstd,
	}

	if enableZstd {
		enc, err := zstd.NewWriter(nil)
		if err != nil {
			return nil, err
		}
		dec, err := zstd.NewReader(nil)
		if err != nil {
			return nil, err
		}
		w.encoder = enc
		w.decoder = dec
	}

	// Recover existing highest segment sequence
	segments, err := w.listSegments()
	if err != nil {
		return nil, err
	}

	if len(segments) > 0 {
		w.currentSeq = segments[len(segments)-1].seq
	}

	return w, nil
}

type segmentMeta struct {
	path string
	name string
	seq  int64
	size int64
}

// WriteMetrics appends a slice of SystemMetrics into the active WAL segment.
func (w *WALManager) WriteMetrics(samples []*madtomv1.SystemMetrics) (*madtomv1.TelemetryBatch, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	if len(samples) == 0 {
		return nil, nil
	}

	// Each batch is sealed independently because acknowledgements delete a segment.
	{
		if err := w.rotateLocked(); err != nil {
			return nil, err
		}
	}

	batch := &madtomv1.TelemetryBatch{
		NodeId:    w.nodeID,
		SegmentId: filepath.Base(w.currentFile.Name()),
		IsBacklog: false,
		Samples:   samples,
	}

	if w.enableZstd && w.encoder != nil {
		rawBytes, err := proto.Marshal(&madtomv1.TelemetryBatch{Samples: samples})
		if err == nil {
			batch.IsCompressed = true
			batch.CompressedPayload = w.encoder.EncodeAll(rawBytes, make([]byte, 0, len(rawBytes)))
			batch.Samples = nil // Omit uncompressed samples
		}
	}

	payload, err := proto.Marshal(batch)
	if err != nil {
		return nil, fmt.Errorf("failed to marshal batch: %w", err)
	}

	// Length-prefixed write: [4 bytes length uint32 BE][payload]
	lenBuf := make([]byte, 4)
	binary.BigEndian.PutUint32(lenBuf, uint32(len(payload)))

	if _, err := w.currentFile.Write(lenBuf); err != nil {
		return nil, err
	}
	if _, err := w.currentFile.Write(payload); err != nil {
		return nil, err
	}
	if err := w.currentFile.Sync(); err != nil {
		return nil, err
	}

	totalWritten := int64(4 + len(payload))
	w.currentBytes += totalWritten
	w.currentOffset += totalWritten
	batch.SegmentOffset = w.currentOffset

	// Check total spool quota and purge oldest if exceeded
	w.enforceQuotaLocked()

	return batch, nil
}

// ReadOldestBatch reads the next pending un-acknowledged batch from the oldest segment.
func (w *WALManager) ReadOldestBatch() (*madtomv1.TelemetryBatch, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	segments, err := w.listSegments()
	if err != nil || len(segments) == 0 {
		return nil, nil
	}

	oldest := segments[0]
	f, err := os.Open(oldest.path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	// Recover older spool files which may contain multiple batches. A segment
	// acknowledgement covers all records, so return every record before deletion.
	batch := &madtomv1.TelemetryBatch{NodeId: w.nodeID, SegmentId: oldest.name, IsBacklog: true}
	var offset int64
	for {
		var lenBuf [4]byte
		_, err := io.ReadFull(f, lenBuf[:])
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("incomplete WAL header: %w", err)
		}
		length := int64(binary.BigEndian.Uint32(lenBuf[:]))
		if length <= 0 || length > oldest.size-offset-4 {
			return nil, fmt.Errorf("invalid WAL record length")
		}
		payload := make([]byte, length)
		if _, err := io.ReadFull(f, payload); err != nil {
			return nil, err
		}
		record := &madtomv1.TelemetryBatch{}
		if err := proto.Unmarshal(payload, record); err != nil {
			return nil, err
		}
		if record.IsCompressed {
			decoder := w.decoder
			if decoder == nil {
				var err error
				decoder, err = zstd.NewReader(nil)
				if err != nil {
					return nil, err
				}
				defer decoder.Close()
			}
			raw, err := decoder.DecodeAll(record.CompressedPayload, nil)
			if err != nil {
				return nil, err
			}
			inner := &madtomv1.TelemetryBatch{}
			if err := proto.Unmarshal(raw, inner); err != nil {
				return nil, err
			}
			record.Samples = inner.Samples
		}
		batch.Samples = append(batch.Samples, record.Samples...)
		offset += 4 + length
	}
	if offset == 0 {
		return nil, nil
	}
	batch.SegmentOffset = offset

	return batch, nil
}

// AcknowledgeSegment removes or marks a segment file as acknowledged after collector confirmation.
func (w *WALManager) AcknowledgeSegment(segmentID string) error {
	w.mu.Lock()
	defer w.mu.Unlock()

	if filepath.Base(segmentID) != segmentID || !strings.HasPrefix(segmentID, WalFilePrefix) || !strings.HasSuffix(segmentID, WalFileSuffix) {
		return fmt.Errorf("invalid segment ID")
	}
	targetPath := filepath.Join(w.dir, segmentID)
	// If the acknowledged file is currently open, close it first
	if w.currentFile != nil && filepath.Base(w.currentFile.Name()) == segmentID {
		_ = w.currentFile.Sync()
		_ = w.currentFile.Close()
		w.currentFile = nil
		w.currentBytes = 0
		w.currentOffset = 0
	}

	if err := os.Remove(targetPath); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("failed to remove acknowledged segment %s: %w", segmentID, err)
	}

	return nil
}

// HasPendingBacklog returns true if there are offline segment files waiting to be flushed.
func (w *WALManager) HasPendingBacklog() bool {
	w.mu.Lock()
	defer w.mu.Unlock()

	segments, err := w.listSegments()
	if err != nil {
		return false
	}
	for _, s := range segments {
		if s.size > 0 {
			return true
		}
	}
	return false
}

func (w *WALManager) rotateLocked() error {
	if w.currentFile != nil {
		_ = w.currentFile.Sync()
		_ = w.currentFile.Close()
		w.currentFile = nil
	}

	w.currentSeq++
	name := fmt.Sprintf("%s%08d%s", WalFilePrefix, w.currentSeq, WalFileSuffix)
	filePath := filepath.Join(w.dir, name)

	f, err := os.OpenFile(filePath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return fmt.Errorf("failed to create new WAL segment %s: %w", name, err)
	}

	w.currentFile = f
	w.currentBytes = 0
	w.currentOffset = 0
	return nil
}

func (w *WALManager) listSegments() ([]segmentMeta, error) {
	entries, err := os.ReadDir(w.dir)
	if err != nil {
		return nil, err
	}

	var segments []segmentMeta
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		name := entry.Name()
		if strings.HasPrefix(name, WalFilePrefix) && strings.HasSuffix(name, WalFileSuffix) {
			seqStr := strings.TrimSuffix(strings.TrimPrefix(name, WalFilePrefix), WalFileSuffix)
			seq, err := strconv.ParseInt(seqStr, 10, 64)
			if err != nil {
				continue
			}
			info, err := entry.Info()
			if err != nil {
				continue
			}
			segments = append(segments, segmentMeta{
				path: filepath.Join(w.dir, name),
				name: name,
				seq:  seq,
				size: info.Size(),
			})
		}
	}

	sort.Slice(segments, func(i, j int) bool {
		return segments[i].seq < segments[j].seq
	})

	return segments, nil
}

func (w *WALManager) enforceQuotaLocked() {
	segments, err := w.listSegments()
	if err != nil || len(segments) <= 1 {
		return
	}

	var totalSize int64
	for _, s := range segments {
		totalSize += s.size
	}

	// Purge oldest segments until totalSize is within maxTotalSpool
	for totalSize > w.maxTotalSpool && len(segments) > 1 {
		oldest := segments[0]
		// Don't remove the currently active write file
		if w.currentFile != nil && filepath.Base(w.currentFile.Name()) == oldest.name {
			break
		}

		_ = os.Remove(oldest.path)
		totalSize -= oldest.size
		segments = segments[1:]
	}
}

// Close gracefully syncs and closes the active WAL file.
func (w *WALManager) Close() error {
	w.mu.Lock()
	defer w.mu.Unlock()

	if w.currentFile != nil {
		_ = w.currentFile.Sync()
		err := w.currentFile.Close()
		w.currentFile = nil
		return err
	}
	return nil
}
