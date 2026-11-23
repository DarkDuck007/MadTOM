package spool

import (
	"crypto/rand"
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
	groupChatRe        = regexp.MustCompile(`(?i)Group\s+Chat:\s*['"]?([^'"\r\n]+)['"]?`)
	privateChatRe      = regexp.MustCompile(`(?i)Private\s+Chat:\s*['"]?([^'"\r\n]+)['"]?`)
	explicitChatRe     = regexp.MustCompile(`(?i)(?:^|[\r\n])Chat:\s*['"]?([^'"\r\n]+)['"]?`)
	contextGroupChatRe = regexp.MustCompile(`(?i)Type:\s*Group\s+Chat\s*\(\s*['"]?([^'")\r\n]+)['"]?\s*\)`)
	contextDmChatRe    = regexp.MustCompile(`(?i)Type:\s*1-on-1\s+Private\s+Chat\s*\(\s*Direct\s+Message\s+with\s+([^)\r\n]+)\s*\)`)
	unicodeEscapeRe    = regexp.MustCompile(`\\u([0-9a-fA-F]{4})`)
)

func unescapeRequestSnippet(raw string) string {
	if raw == "" {
		return ""
	}
	s := strings.ReplaceAll(raw, `\r\n`, "\n")
	s = strings.ReplaceAll(s, `\n`, "\n")
	s = strings.ReplaceAll(s, `\"`, `"`)
	s = strings.ReplaceAll(s, `\'`, `'`)
	if strings.Contains(s, `\u`) {
		s = unicodeEscapeRe.ReplaceAllStringFunc(s, func(match string) string {
			v, err := strconv.ParseUint(match[2:], 16, 16)
			if err != nil {
				return match
			}
			return string(rune(v))
		})
	}
	return s
}

func cleanExtractedChatTitle(val string) string {
	val = strings.TrimSpace(val)
	if idx := strings.Index(val, `\n`); idx >= 0 {
		val = val[:idx]
	}
	if idx := strings.Index(val, `\r`); idx >= 0 {
		val = val[:idx]
	}
	return strings.Trim(val, " \t'\"\\.")
}

func extractChatName(requestJson, caller string) string {
	if requestJson == "" {
		return ""
	}
	snippet := unescapeRequestSnippet(requestJson)
	if m := groupChatRe.FindStringSubmatch(snippet); len(m) > 1 {
		if title := cleanExtractedChatTitle(m[1]); title != "" {
			return title
		}
	}
	if m := privateChatRe.FindStringSubmatch(snippet); len(m) > 1 {
		if title := cleanExtractedChatTitle(m[1]); title != "" {
			return title
		}
	}
	if m := contextGroupChatRe.FindStringSubmatch(snippet); len(m) > 1 {
		if title := cleanExtractedChatTitle(m[1]); title != "" {
			return title
		}
	}
	if m := contextDmChatRe.FindStringSubmatch(snippet); len(m) > 1 {
		if title := cleanExtractedChatTitle(m[1]); title != "" {
			return title
		}
	}
	if m := explicitChatRe.FindStringSubmatch(snippet); len(m) > 1 {
		if title := cleanExtractedChatTitle(m[1]); title != "" {
			return title
		}
	}
	// No sender-name fallback: sender names are not chat names (agy context diffs carry no
	// chat header). NoxChat now fills chat_name from its chats table via the trace chat_id.
	_ = caller
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
	mu               sync.Mutex
	dir              string
	maxSegmentSize   int64
	maxTotalSpool    int64
	strictQuota      bool
	currentFile      *os.File
	currentSeq       int64
	currentBytes     int64
	segments         []segmentMeta
	totalSpoolBytes  int64
	totalLogRecords  uint64
	totalApiCalls    uint64
	maxLogSeq        uint64
	maxApiSeq        uint64
	logChunkCounts   map[uint64]uint64
	apiChunkCounts   map[uint64]uint64
	logChunks        map[uint64]*madtomv1.LogChunk
	apiChunks        map[uint64]*madtomv1.NoxApiCallChunk
	apiSummaryChunks map[uint64]*madtomv1.NoxApiCallChunk
	oldestTs         int64
	newestTs         int64
	closed           bool
	logOwners        map[uint64]frameLocation
	apiOwners        map[uint64]frameLocation
	generation       [32]byte
	recoveryErrors   uint64
	traceEntries     map[string]*chatTraceEntry
	traceByChat      map[int64]map[string]struct{}
	traceByName      map[string]map[string]struct{}
	apiTraceKeys     map[uint64][]string
	traceOrdinal     uint64
}

type frameLocation struct {
	Segment int64
	Offset  int64
	Length  int64
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
		dir:              dir,
		maxSegmentSize:   DefaultMaxSegmentSize,
		maxTotalSpool:    maxTotalSpool,
		logChunkCounts:   make(map[uint64]uint64),
		apiChunkCounts:   make(map[uint64]uint64),
		logChunks:        make(map[uint64]*madtomv1.LogChunk),
		apiChunks:        make(map[uint64]*madtomv1.NoxApiCallChunk),
		apiSummaryChunks: make(map[uint64]*madtomv1.NoxApiCallChunk),
	}
	w.logOwners = make(map[uint64]frameLocation)
	w.apiOwners = make(map[uint64]frameLocation)
	w.traceEntries = make(map[string]*chatTraceEntry)
	w.traceByChat = make(map[int64]map[string]struct{})
	w.traceByName = make(map[string]map[string]struct{})
	w.apiTraceKeys = make(map[uint64][]string)
	if _, err := rand.Read(w.generation[:]); err != nil {
		return nil, err
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

// NextChunkIDs returns the next unused log and API chunk IDs based on recovered WAL chunks.
func (w *WalSpool) NextChunkIDs() (uint64, uint64) {
	w.mu.Lock()
	defer w.mu.Unlock()
	var nextLog, nextApi uint64
	for id := range w.logChunks {
		if id+1 > nextLog {
			nextLog = id + 1
		}
	}
	for id := range w.apiChunks {
		if id+1 > nextApi {
			nextApi = id + 1
		}
	}
	return nextLog, nextApi
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

	// Recover complete frames; never append behind a torn tail. Rotate on the next write.
	for _, seg := range w.segments {
		f, err := os.Open(seg.path)
		if err != nil {
			return err
		}
		var offset int64
		for {
			var hdr [5]byte
			n, err := io.ReadFull(f, hdr[:])
			if err != nil {
				if n != 0 {
					w.recoveryErrors++
				}
				break
			}
			size := int64(binary.BigEndian.Uint32(hdr[1:]))
			if size > 64<<20 || size > seg.size-offset-5 {
				w.recoveryErrors++
				break
			}
			payload := make([]byte, int(size))
			if _, err := io.ReadFull(f, payload); err != nil {
				w.recoveryErrors++
				break
			}
			loc := frameLocation{seg.seq, offset, size + 5}
			if err := w.publishFrame(hdr[0], payload, loc); err != nil {
				w.recoveryErrors++
			}
			offset += size + 5
		}
		if err := f.Close(); err != nil {
			return err
		}
	}
	w.recountLocked()

	return w.enforceQuotaLocked()
}

// WriteLogChunk appends a LogChunk to the WAL.
func (w *WalSpool) WriteLogChunk(chunk *madtomv1.LogChunk) error {
	raw, err := proto.Marshal(chunk)
	if err != nil {
		return err
	}
	return w.writeFrame('L', chunk.ChunkId, raw, uint64(chunk.RecordCount), chunk.LastSeq, chunk.FirstTsUnixNano, chunk.LastTsUnixNano)
}

// WriteApiCallChunk appends a NoxApiCallChunk to the WAL.
func (w *WalSpool) WriteApiCallChunk(chunk *madtomv1.NoxApiCallChunk) error {
	raw, err := proto.Marshal(chunk)
	if err != nil {
		return err
	}
	return w.writeFrame('A', chunk.ChunkId, raw, uint64(chunk.RecordCount), chunk.LastSeq, chunk.FirstTsUnixNano, chunk.LastTsUnixNano)
}

func (w *WalSpool) writeFrame(kind byte, chunkID uint64, payload []byte, count uint64, lastSeq uint64, firstTs, lastTs int64) error {
	w.mu.Lock()
	defer w.mu.Unlock()

	if w.closed {
		return fmt.Errorf("WAL spool is closed")
	}

	frameLen := 1 + 4 + len(payload)
	if w.strictQuota && int64(frameLen) > w.maxTotalSpool {
		return fmt.Errorf("frame exceeds WAL spool limit")
	}
	if w.currentFile == nil || (w.currentBytes+int64(frameLen) > w.maxSegmentSize) {
		if err := w.rotateLocked(); err != nil {
			return err
		}
	}

	if len(payload) > 64<<20 {
		return fmt.Errorf("frame exceeds 64 MiB")
	}
	frame := make([]byte, frameLen)
	frame[0] = kind
	binary.BigEndian.PutUint32(frame[1:5], uint32(len(payload)))
	copy(frame[5:], payload)
	offset := w.currentBytes
	n, err := w.currentFile.Write(frame)
	if err == nil && n != len(frame) {
		err = io.ErrShortWrite
	}
	if err == nil {
		err = w.currentFile.Sync()
	}
	if err != nil {
		// Do not publish failed writes. Roll back the incomplete frame before accepting another append.
		if rollback := w.currentFile.Truncate(offset); rollback != nil {
			w.closed = true
			return fmt.Errorf("write: %v; rollback: %w", err, rollback)
		}
		return err
	}
	w.currentBytes += int64(frameLen)
	w.segments[len(w.segments)-1].size = w.currentBytes
	w.totalSpoolBytes += int64(frameLen)
	if err := w.publishFrame(kind, payload, frameLocation{w.currentSeq, offset, int64(frameLen)}); err != nil {
		return err
	}
	// Incremental totals; retention recomputes bounds after eviction.
	if kind == 'L' {
		w.totalLogRecords = w.totalLogRecords - w.logChunkCounts[chunkID] + count
		w.logChunkCounts[chunkID] = count
		if lastSeq > w.maxLogSeq {
			w.maxLogSeq = lastSeq
		}
	} else if kind == 'A' {
		w.totalApiCalls = w.totalApiCalls - w.apiChunkCounts[chunkID] + count
		w.apiChunkCounts[chunkID] = count
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
	for w.totalSpoolBytes > w.maxTotalSpool && (len(w.segments) > 1 || w.strictQuota && len(w.segments) > 0) {
		oldest := w.segments[0]
		if err := os.Remove(oldest.path); err != nil && !os.IsNotExist(err) {
			return err
		}
		if w.currentFile != nil && oldest.seq == w.currentSeq {
			_ = w.currentFile.Close()
			w.currentFile = nil
			w.currentBytes = 0
		}
		w.evictSegmentLocked(oldest.seq)
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

// QueryLogChunkPage supports timestamp seek and directional traversal. A zero
// starting ID selects the first page when forward or the latest page otherwise.
func (w *WalSpool) QueryLogChunkPage(req *madtomv1.LogChunkQuery) ([]*madtomv1.LogChunk, bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	order := make([]uint64, 0, len(w.logChunks))
	for id := range w.logChunks {
		order = append(order, id)
	}
	if len(order) == 0 {
		return nil, false
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })

	startID := req.StartChunkId
	if req.SeekTimestampUnixNano > 0 {
		startID = w.closestLogChunk(order, req.SeekTimestampUnixNano)
	}
	limit := int(min(req.MaxChunks, 32))
	if limit == 0 {
		limit = 8
	}
	var first, last int
	if req.Forward {
		first = sort.Search(len(order), func(i int) bool { return order[i] >= startID })
		last = min(first+limit, len(order))
	} else {
		last = len(order)
		if req.SeekTimestampUnixNano > 0 {
			last = sort.Search(len(order), func(i int) bool { return order[i] > startID })
		} else if req.StartChunkId != 0 {
			last = sort.Search(len(order), func(i int) bool { return order[i] >= startID })
		}
		first = max(0, last-limit)
	}
	chunks := make([]*madtomv1.LogChunk, 0, last-first)
	for _, id := range order[first:last] {
		c := w.logChunks[id]
		if req.SummaryOnly {
			c = &madtomv1.LogChunk{
				NodeId: c.NodeId, ChunkId: c.ChunkId, RecordCount: c.RecordCount, Sealed: c.Sealed,
				FirstSeq: c.FirstSeq, LastSeq: c.LastSeq, FirstTsUnixNano: c.FirstTsUnixNano,
				LastTsUnixNano: c.LastTsUnixNano, RawSize: c.RawSize,
			}
		}
		chunks = append(chunks, c)
	}
	return chunks, (req.Forward && last < len(order)) || (!req.Forward && first > 0)
}

func (w *WalSpool) closestLogChunk(order []uint64, ts int64) uint64 {
	var closest uint64
	bestDistance := ^uint64(0)
	for _, id := range order {
		chunk := w.logChunks[id]
		var distance uint64
		switch {
		case ts < chunk.FirstTsUnixNano:
			distance = uint64(chunk.FirstTsUnixNano) - uint64(ts)
		case ts > chunk.LastTsUnixNano:
			distance = uint64(ts) - uint64(chunk.LastTsUnixNano)
		default:
			return id
		}
		if distance < bestDistance {
			closest, bestDistance = id, distance
		}
	}
	return closest
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
		if summaryOnly {
			if sum, ok := w.apiSummaryChunks[id]; ok && sum != nil {
				chunks = append(chunks, proto.Clone(sum).(*madtomv1.NoxApiCallChunk))
			} else {
				chunks = append(chunks, w.apiChunks[id])
			}
		} else {
			chunks = append(chunks, w.apiChunks[id])
		}
		if uint32(len(chunks)) >= maxChunks {
			break
		}
	}
	hasMore := len(order) > len(chunks)

	return chunks, hasMore, nil
}

// GetApiCall searches in-memory index and segments (newest to oldest) for a specific API call record by CallId.
func (w *WalSpool) GetApiCall(callID string) (*madtomv1.NoxRawApiCallRecord, error) {
	if callID == "" {
		return nil, fmt.Errorf("empty call id")
	}

	w.mu.Lock()
	var chunkIds []uint64
	if entry := w.traceEntries["a:"+callID]; entry != nil && entry.api != nil {
		chunkIds = append(chunkIds, entry.chunkID)
	} else {
		for id := range w.apiChunks {
			chunkIds = append(chunkIds, id)
		}
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

// ChunkStats reports retention in chunks rather than individual records.
func (w *WalSpool) ChunkStats() (current, limit, logs, api uint64, oldest int64) {
	w.mu.Lock()
	defer w.mu.Unlock()
	return uint64(w.totalSpoolBytes), uint64(w.maxTotalSpool), uint64(len(w.logChunks)), uint64(len(w.apiChunks)), w.oldestTs
}

// SetMaxBytes applies a hard retention limit, including to the active segment.
func (w *WalSpool) SetMaxBytes(limit int64) error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if limit <= 0 {
		return fmt.Errorf("WAL spool limit must be positive")
	}
	previous, previousSegment, previousStrict := w.maxTotalSpool, w.maxSegmentSize, w.strictQuota
	w.maxTotalSpool = limit
	w.maxSegmentSize = min(DefaultMaxSegmentSize, limit)
	w.strictQuota = true
	if err := w.enforceQuotaLocked(); err != nil {
		w.maxTotalSpool, w.maxSegmentSize, w.strictQuota = previous, previousSegment, previousStrict
		return err
	}
	return nil
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

	// Invalidate searches even if a later deletion fails.
	if _, err := rand.Read(w.generation[:]); err != nil {
		return err
	}
	for len(w.segments) > 0 {
		seg := w.segments[0]
		if err := os.Remove(seg.path); err != nil && !os.IsNotExist(err) {
			return err
		}
		w.evictSegmentLocked(seg.seq)
		w.totalSpoolBytes -= seg.size
		w.segments = w.segments[1:]
	}
	w.currentBytes = 0
	w.recoveryErrors = 0
	return nil
}

// publishFrame takes ownership of freshly unmarshaled, immutable chunk data.
func (w *WalSpool) publishFrame(kind byte, payload []byte, loc frameLocation) error {
	switch kind {
	case 'L':
		c := new(madtomv1.LogChunk)
		if err := proto.Unmarshal(payload, c); err != nil {
			return err
		}
		w.logChunks[c.ChunkId] = c
		w.logOwners[c.ChunkId] = loc
	case 'A':
		c := new(madtomv1.NoxApiCallChunk)
		if err := proto.Unmarshal(payload, c); err != nil {
			return err
		}
		w.apiChunks[c.ChunkId] = c
		w.apiOwners[c.ChunkId] = loc
		if err := w.indexApiTraceLocked(c, loc); err != nil {
			// Keep raw inspection available even if a corrupt inner payload cannot be indexed.
			w.recoveryErrors++
		}
	case 'T':
		event := new(madtomv1.NoxChatTraceEvent)
		if err := proto.Unmarshal(payload, event); err != nil {
			return err
		}
		w.indexTraceEventLocked(event, loc)
	default:
		return fmt.Errorf("unknown frame kind %d", kind)
	}
	return nil
}

func (w *WalSpool) evictSegmentLocked(segment int64) {
	for key, entry := range w.traceEntries {
		if entry.location.Segment == segment {
			w.removeTraceEntryLocked(key)
		}
	}
	for id, loc := range w.logOwners {
		if loc.Segment == segment {
			delete(w.logOwners, id)
			delete(w.logChunks, id)
		}
	}
	for id, loc := range w.apiOwners {
		if loc.Segment == segment {
			delete(w.apiOwners, id)
			delete(w.apiChunks, id)
			delete(w.apiSummaryChunks, id)
			delete(w.apiTraceKeys, id)
		}
	}
	w.recountLocked()
}

func (w *WalSpool) recountLocked() {
	w.totalLogRecords = 0
	w.totalApiCalls = 0
	w.oldestTs = 0
	w.newestTs = 0
	clear(w.logChunkCounts)
	clear(w.apiChunkCounts)
	bounds := func(first, last int64) {
		if first > 0 && (w.oldestTs == 0 || first < w.oldestTs) {
			w.oldestTs = first
		}
		if last > w.newestTs {
			w.newestTs = last
		}
	}
	for id, c := range w.logChunks {
		n := uint64(c.RecordCount)
		w.logChunkCounts[id] = n
		w.totalLogRecords += n
		if c.LastSeq > w.maxLogSeq {
			w.maxLogSeq = c.LastSeq
		}
		bounds(c.FirstTsUnixNano, c.LastTsUnixNano)
	}
	for id, c := range w.apiChunks {
		n := uint64(c.RecordCount)
		w.apiChunkCounts[id] = n
		w.totalApiCalls += n
		if c.LastSeq > w.maxApiSeq {
			w.maxApiSeq = c.LastSeq
		}
		bounds(c.FirstTsUnixNano, c.LastTsUnixNano)
	}
	for _, entry := range w.traceEntries {
		if entry.event != nil {
			bounds(entry.timestamp, entry.timestamp)
		}
	}
}
