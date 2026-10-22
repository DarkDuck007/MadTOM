package spool

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

var (
	groupChatRe    = regexp.MustCompile(`(?i)Group\s+Chat:\s*['"]?([^'"\r\n\\]+)['"]?`)
	privateChatRe  = regexp.MustCompile(`(?i)Private\s+Chat:\s*['"]?([^'"\r\n\\]+)['"]?`)
	explicitChatRe = regexp.MustCompile(`(?i)(?:^|[\r\n])Chat:\s*['"]?([^'"\r\n\\]+)['"]?`)
	senderLineRe   = regexp.MustCompile(`\[#\d+\s+([^\]:\r\n]+?)(?:\s+->\s+#\d+)?\]:`)
)

func extractChatName(requestJson, caller string) string {
	if requestJson == "" {
		return ""
	}
	if m := groupChatRe.FindStringSubmatch(requestJson); len(m) > 1 && strings.TrimSpace(m[1]) != "" {
		return strings.TrimSpace(m[1])
	}
	if m := privateChatRe.FindStringSubmatch(requestJson); len(m) > 1 && strings.TrimSpace(m[1]) != "" {
		return strings.TrimSpace(m[1])
	}
	if m := explicitChatRe.FindStringSubmatch(requestJson); len(m) > 1 && strings.TrimSpace(m[1]) != "" {
		return strings.TrimSpace(m[1])
	}
	cLower := strings.ToLower(caller)
	if strings.Contains(cLower, "step") || strings.Contains(cLower, "chat") || strings.Contains(cLower, "agent") {
		matches := senderLineRe.FindAllStringSubmatch(requestJson, -1)
		var fallback string
		for _, match := range matches {
			if len(match) > 1 {
				raw := strings.TrimSpace(match[1])
				clean := raw
				if idx := strings.Index(clean, "("); idx > 0 {
					clean = strings.TrimSpace(clean[:idx])
				}
				if strings.EqualFold(clean, "Nox") || strings.EqualFold(clean, "Bot") {
					continue
				}
				if strings.Contains(strings.ToLower(raw), "(you)") {
					if fallback == "" {
						fallback = clean
					}
					continue
				}
				return clean
			}
		}
		if fallback != "" {
			return fallback
		}
	}
	return ""
}

const (
	DefaultMaxSegmentSize = 10 * 1024 * 1024
	DefaultMaxTotalSpool  = 1024 * 1024 * 1024 // 1 GB default max WAL spool
	WalFilePrefix         = "nox-spool-"
	WalFileSuffix         = ".wal"
)

type segmentMeta struct {
	path, name string
	seq, size  int64
}

// SpoolEnvelope wraps either a LogChunk or an ApiCallChunk for WAL storage.
type SpoolEnvelope struct {
	Kind     byte // 'L' for LogChunk, 'A' for ApiCallChunk
	LogChunk *madtomv1.LogChunk
	ApiChunk *madtomv1.NoxApiCallChunk
}

// WalSpool manages segment files on disk, automatically evicting oldest segments
// when total spool bytes exceed maxTotalSpool (default 1GB).
type WalSpool struct {
	mu              sync.Mutex
	dir             string
	maxSegmentSize  int64
	maxTotalSpool   int64
	currentFile     *os.File
	currentSeq      int64
	currentBytes    int64
	segments        []segmentMeta
	totalSpoolBytes int64
	totalLogRecords uint64
	totalApiCalls   uint64
	maxLogSeq       uint64
	maxApiSeq       uint64
	logChunkCounts  map[uint64]uint64
	apiChunkCounts  map[uint64]uint64
	logChunks       map[uint64]*madtomv1.LogChunk
	apiChunks       map[uint64]*madtomv1.NoxApiCallChunk
	oldestTs        int64
	newestTs        int64
	closed          bool
}

// NewWalSpool initializes or recovers a WAL spool directory.
func NewWalSpool(dir string, maxTotalSpool int64) (*WalSpool, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, err
	}
	if maxTotalSpool <= 0 {
		maxTotalSpool = DefaultMaxTotalSpool
	}
	w := &WalSpool{
		dir:            dir,
		maxSegmentSize: DefaultMaxSegmentSize,
		maxTotalSpool:  maxTotalSpool,
		logChunkCounts: make(map[uint64]uint64),
		apiChunkCounts: make(map[uint64]uint64),
		logChunks:      make(map[uint64]*madtomv1.LogChunk),
		apiChunks:      make(map[uint64]*madtomv1.NoxApiCallChunk),
	}
	if err := w.scanAndRecover(); err != nil {
		return nil, err
	}
	return w, nil
}

// MaxSeqs returns the highest recovered log and API call sequence numbers.
func (w *WalSpool) MaxSeqs() (uint64, uint64) {
	w.mu.Lock()
	defer w.mu.Unlock()
	return w.maxLogSeq, w.maxApiSeq
}

func (w *WalSpool) scanAndRecover() error {
	entries, err := os.ReadDir(w.dir)
	if err != nil {
		return err
	}

	var total int64
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasPrefix(entry.Name(), WalFilePrefix) || !strings.HasSuffix(entry.Name(), WalFileSuffix) {
			continue
		}
		numStr := strings.TrimSuffix(strings.TrimPrefix(entry.Name(), WalFilePrefix), WalFileSuffix)
		seq, err := strconv.ParseInt(numStr, 10, 64)
		if err != nil {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			continue
		}
		w.segments = append(w.segments, segmentMeta{
			path: filepath.Join(w.dir, entry.Name()),
			name: entry.Name(),
			seq:  seq,
			size: info.Size(),
		})
		total += info.Size()
		if seq > w.currentSeq {
			w.currentSeq = seq
		}
	}

	sort.Slice(w.segments, func(i, j int) bool { return w.segments[i].seq < w.segments[j].seq })
	w.totalSpoolBytes = total

	// Scan existing segments to recover record counts, timestamps, and max sequence numbers
	for _, seg := range w.segments {
		f, err := os.Open(seg.path)
		if err != nil {
			continue
		}
		for {
			var hdr [5]byte
			if _, err := io.ReadFull(f, hdr[:]); err != nil {
				break
			}
			kind := hdr[0]
			payloadLen := binary.BigEndian.Uint32(hdr[1:5])
			payload := make([]byte, payloadLen)
			if _, err := io.ReadFull(f, payload); err != nil {
				break
			}
			if kind == 'L' {
				chunk := &madtomv1.LogChunk{}
				if err := proto.Unmarshal(payload, chunk); err == nil {
					w.logChunks[chunk.ChunkId] = chunk
					prev := w.logChunkCounts[chunk.ChunkId]
					cur := uint64(chunk.RecordCount)
					if cur > prev {
						w.totalLogRecords += cur - prev
						w.logChunkCounts[chunk.ChunkId] = cur
					}
					if chunk.LastSeq > w.maxLogSeq {
						w.maxLogSeq = chunk.LastSeq
					}
					if chunk.FirstTsUnixNano > 0 && (w.oldestTs == 0 || chunk.FirstTsUnixNano < w.oldestTs) {
						w.oldestTs = chunk.FirstTsUnixNano
					}
					if chunk.LastTsUnixNano > w.newestTs {
						w.newestTs = chunk.LastTsUnixNano
					}
				}
			} else if kind == 'A' {
				chunk := &madtomv1.NoxApiCallChunk{}
				if err := proto.Unmarshal(payload, chunk); err == nil {
					w.apiChunks[chunk.ChunkId] = chunk
					prev := w.apiChunkCounts[chunk.ChunkId]
					cur := uint64(chunk.RecordCount)
					if cur > prev {
						w.totalApiCalls += cur - prev
						w.apiChunkCounts[chunk.ChunkId] = cur
					}
					if chunk.LastSeq > w.maxApiSeq {
						w.maxApiSeq = chunk.LastSeq
					}
					if chunk.FirstTsUnixNano > 0 && (w.oldestTs == 0 || chunk.FirstTsUnixNano < w.oldestTs) {
						w.oldestTs = chunk.FirstTsUnixNano
					}
					if chunk.LastTsUnixNano > w.newestTs {
						w.newestTs = chunk.LastTsUnixNano
					}
				}
			}
		}
		_ = f.Close()
	}

	// Open the latest segment or rotate
	if len(w.segments) > 0 {
		latest := w.segments[len(w.segments)-1]
		if latest.size < w.maxSegmentSize {
			f, err := os.OpenFile(latest.path, os.O_WRONLY|os.O_APPEND, 0644)
			if err == nil {
				w.currentFile = f
				w.currentBytes = latest.size
			}
		}
	}

	return w.enforceQuotaLocked()
}

// WriteLogChunk appends a LogChunk to the WAL.
func (w *WalSpool) WriteLogChunk(chunk *madtomv1.LogChunk) error {
	raw, err := proto.Marshal(chunk)
	if err != nil {
		return err
	}
	w.mu.Lock()
	w.logChunks[chunk.ChunkId] = chunk
	w.mu.Unlock()
	return w.writeFrame('L', chunk.ChunkId, raw, uint64(chunk.RecordCount), chunk.LastSeq, chunk.FirstTsUnixNano, chunk.LastTsUnixNano)
}

// WriteApiCallChunk appends a NoxApiCallChunk to the WAL.
func (w *WalSpool) WriteApiCallChunk(chunk *madtomv1.NoxApiCallChunk) error {
	raw, err := proto.Marshal(chunk)
	if err != nil {
		return err
	}
	w.mu.Lock()
	w.apiChunks[chunk.ChunkId] = chunk
	w.mu.Unlock()
	return w.writeFrame('A', chunk.ChunkId, raw, uint64(chunk.RecordCount), chunk.LastSeq, chunk.FirstTsUnixNano, chunk.LastTsUnixNano)
}

func (w *WalSpool) writeFrame(kind byte, chunkID uint64, payload []byte, count uint64, lastSeq uint64, firstTs, lastTs int64) error {
	w.mu.Lock()
	defer w.mu.Unlock()

	if w.closed {
		return fmt.Errorf("WAL spool is closed")
	}

	frameLen := 1 + 4 + len(payload)
	if w.currentFile == nil || (w.currentBytes+int64(frameLen) > w.maxSegmentSize) {
		if err := w.rotateLocked(); err != nil {
			return err
		}
	}

	header := make([]byte, 5)
	header[0] = kind
	binary.BigEndian.PutUint32(header[1:5], uint32(len(payload)))

	if _, err := w.currentFile.Write(header); err != nil {
		return err
	}
	if _, err := w.currentFile.Write(payload); err != nil {
		return err
	}
	_ = w.currentFile.Sync()

	w.currentBytes += int64(frameLen)
	w.segments[len(w.segments)-1].size = w.currentBytes
	w.totalSpoolBytes += int64(frameLen)

	if kind == 'L' {
		prev := w.logChunkCounts[chunkID]
		if count > prev {
			w.totalLogRecords += count - prev
			w.logChunkCounts[chunkID] = count
		}
		if lastSeq > w.maxLogSeq {
			w.maxLogSeq = lastSeq
		}
	} else if kind == 'A' {
		prev := w.apiChunkCounts[chunkID]
		if count > prev {
			w.totalApiCalls += count - prev
			w.apiChunkCounts[chunkID] = count
		}
		if lastSeq > w.maxApiSeq {
			w.maxApiSeq = lastSeq
		}
	}

	if firstTs > 0 && (w.oldestTs == 0 || firstTs < w.oldestTs) {
		w.oldestTs = firstTs
	}
	if lastTs > w.newestTs {
		w.newestTs = lastTs
	}

	return w.enforceQuotaLocked()
}

func (w *WalSpool) rotateLocked() error {
	if w.currentFile != nil {
		_ = w.currentFile.Close()
		w.currentFile = nil
	}

	w.currentSeq++
	name := fmt.Sprintf("%s%08d%s", WalFilePrefix, w.currentSeq, WalFileSuffix)
	path := filepath.Join(w.dir, name)

	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		return err
	}

	w.currentFile = f
	w.currentBytes = 0
	w.segments = append(w.segments, segmentMeta{path: path, name: name, seq: w.currentSeq, size: 0})
	return nil
}

func (w *WalSpool) enforceQuotaLocked() error {
	for w.totalSpoolBytes > w.maxTotalSpool && len(w.segments) > 1 {
		oldest := w.segments[0]
		_ = os.Remove(oldest.path)
		w.totalSpoolBytes -= oldest.size
		w.segments = w.segments[1:]
	}
	return nil
}

// QueryLogChunks reads up to maxChunks matching LogChunks from in-memory index / spool.
func (w *WalSpool) QueryLogChunks(startChunkID uint64, maxChunks uint32) ([]*madtomv1.LogChunk, bool, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	if maxChunks == 0 {
		maxChunks = 32
	}

	var order []uint64
	for id := range w.logChunks {
		if id >= startChunkID {
			order = append(order, id)
		}
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })

	var chunks []*madtomv1.LogChunk
	for _, id := range order {
		chunks = append(chunks, w.logChunks[id])
		if uint32(len(chunks)) >= maxChunks {
			break
		}
	}
	hasMore := len(order) > len(chunks)
	return chunks, hasMore, nil
}

// QueryApiCallChunks reads up to maxChunks matching NoxApiCallChunks from in-memory index / spool.
// If summaryOnly is true, large payloads (raw JSON strings and full image byte arrays) are excluded to ensure lightweight list retrieval.
func (w *WalSpool) QueryApiCallChunks(startChunkID uint64, maxChunks uint32, summaryOnly bool) ([]*madtomv1.NoxApiCallChunk, bool, error) {
	w.mu.Lock()
	defer w.mu.Unlock()

	if maxChunks == 0 {
		maxChunks = 32
	}

	var order []uint64
	for id := range w.apiChunks {
		if id >= startChunkID {
			order = append(order, id)
		}
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })

	var chunks []*madtomv1.NoxApiCallChunk
	for _, id := range order {
		chunks = append(chunks, w.apiChunks[id])
		if uint32(len(chunks)) >= maxChunks {
			break
		}
	}
	hasMore := len(order) > len(chunks)

	if summaryOnly && len(chunks) > 0 {
		dec, err := zstd.NewReader(nil)
		if err == nil {
			defer dec.Close()
			enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
			if err == nil {
				defer enc.Close()
				summaryChunks := make([]*madtomv1.NoxApiCallChunk, len(chunks))
				for chIdx, orig := range chunks {
					ch := proto.Clone(orig).(*madtomv1.NoxApiCallChunk)
					summaryChunks[chIdx] = ch
					if len(ch.ZstdRecords) == 0 {
						continue
					}
					raw, err := dec.DecodeAll(ch.ZstdRecords, nil)
					if err != nil {
						continue
					}
					list := &madtomv1.NoxApiCallRecordList{}
					if err := proto.Unmarshal(raw, list); err != nil {
						continue
					}

					lightList := &madtomv1.NoxApiCallRecordList{
						Records: make([]*madtomv1.NoxRawApiCallRecord, len(list.Records)),
					}
					for idx, r := range list.Records {
						var lightImgs []*madtomv1.NoxImageData
						for _, img := range r.ImageDataUrls {
							lightImgs = append(lightImgs, &madtomv1.NoxImageData{
								Label:    img.Label,
								MimeType: img.MimeType,
							})
						}
						chatName := r.ChatName
						if chatName == "" && r.RawRequestJson != "" {
							chatName = extractChatName(r.RawRequestJson, r.Caller)
						}
						lightList.Records[idx] = &madtomv1.NoxRawApiCallRecord{
							CallId:            r.CallId,
							TimestampUnixNano: r.TimestampUnixNano,
							Caller:            r.Caller,
							Model:             r.Model,
							BaseUrl:           r.BaseUrl,
							Temperature:       r.Temperature,
							DurationMs:        r.DurationMs,
							PromptTokens:      r.PromptTokens,
							CompletionTokens:  r.CompletionTokens,
							CachedTokens:      r.CachedTokens,
							ImageDataUrls:     lightImgs,
							ErrorMessage:      r.ErrorMessage,
							ChatName:          chatName,
							// RawRequestJson and RawResponseJson omitted for summary
						}
					}

					lightRaw, err := proto.Marshal(lightList)
					if err == nil {
						ch.ZstdRecords = enc.EncodeAll(lightRaw, nil)
						ch.RawSize = uint32(len(lightRaw))
					}
				}
				chunks = summaryChunks
			}
		}
	}

	return chunks, hasMore, nil
}

// GetApiCall searches in-memory index and segments (newest to oldest) for a specific API call record by CallId.
func (w *WalSpool) GetApiCall(callID string) (*madtomv1.NoxRawApiCallRecord, error) {
	if callID == "" {
		return nil, fmt.Errorf("empty call id")
	}

	w.mu.Lock()
	var chunkIds []uint64
	for id := range w.apiChunks {
		chunkIds = append(chunkIds, id)
	}
	sort.Slice(chunkIds, func(i, j int) bool { return chunkIds[i] > chunkIds[j] }) // newest first
	chunks := make([]*madtomv1.NoxApiCallChunk, len(chunkIds))
	for i, id := range chunkIds {
		chunks[i] = w.apiChunks[id]
	}
	w.mu.Unlock()

	dec, err := zstd.NewReader(nil)
	if err != nil {
		return nil, err
	}
	defer dec.Close()

	for _, ch := range chunks {
		if len(ch.ZstdRecords) == 0 {
			continue
		}
		raw, err := dec.DecodeAll(ch.ZstdRecords, nil)
		if err != nil {
			continue
		}
		list := &madtomv1.NoxApiCallRecordList{}
		if err := proto.Unmarshal(raw, list); err == nil {
			for _, r := range list.Records {
				if r.CallId == callID {
					if r.ChatName == "" && r.RawRequestJson != "" {
						r.ChatName = extractChatName(r.RawRequestJson, r.Caller)
					}
					return r, nil
				}
			}
		}
	}

	return nil, fmt.Errorf("call id %s not found in spool", callID)
}

// Stats returns the current WAL spool metrics.
func (w *WalSpool) Stats() (uint64, uint64, uint64, uint64, int64, int64) {
	w.mu.Lock()
	defer w.mu.Unlock()
	return uint64(w.totalSpoolBytes), uint64(w.maxTotalSpool), w.totalLogRecords, w.totalApiCalls, w.oldestTs, w.newestTs
}

// Close flushes and closes active WAL file handles.
func (w *WalSpool) Close() error {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.closed = true
	if w.currentFile != nil {
		return w.currentFile.Close()
	}
	return nil
}

// Clear removes all WAL segment files from disk and resets all counters and chunk maps.
func (w *WalSpool) Clear() error {
	w.mu.Lock()
	defer w.mu.Unlock()

	if w.currentFile != nil {
		_ = w.currentFile.Close()
		w.currentFile = nil
	}

	for _, seg := range w.segments {
		_ = os.Remove(seg.path)
	}

	w.segments = nil
	w.currentSeq = 0
	w.currentBytes = 0
	w.totalSpoolBytes = 0
	w.totalLogRecords = 0
	w.totalApiCalls = 0
	w.maxLogSeq = 0
	w.maxApiSeq = 0
	w.oldestTs = 0
	w.newestTs = 0
	w.logChunkCounts = make(map[uint64]uint64)
	w.apiChunkCounts = make(map[uint64]uint64)
	w.logChunks = make(map[uint64]*madtomv1.LogChunk)
	w.apiChunks = make(map[uint64]*madtomv1.NoxApiCallChunk)

	return nil
}
