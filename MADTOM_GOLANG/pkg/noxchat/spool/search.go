package spool

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"
	"unicode/utf8"

	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
)

// SearchLimits bounds work per page. Oversized/corrupt chunks are reported, never truncated for matching.
type SearchLimits struct {
	MaxResults    int
	MaxQueryBytes int
	PageBytes     uint64
	ChunkBytes    uint64
	PageTime      time.Duration
	CursorTTL     time.Duration
}

func DefaultSearchLimits() SearchLimits {
	return SearchLimits{200, 4096, 64 << 20, 16 << 20, 2 * time.Second, 5 * time.Minute}
}

type searchCursor struct {
	Query   string
	Digest  string
	High    uint64
	Chunk   int
	Record  int
	Expires int64
	Corrupt uint64
}
type searchChunk struct {
	chunk *pb.LogChunk
	loc   frameLocation
}

func (w *WalSpool) searchSnapshot(high uint64) ([]searchChunk, string, [32]byte, uint64) {
	w.mu.Lock()
	defer w.mu.Unlock()
	chunks := make([]searchChunk, 0, len(w.logChunks))
	for id, c := range w.logChunks {
		if c.FirstSeq <= high {
			chunks = append(chunks, searchChunk{c, w.logOwners[id]})
		}
	}
	sort.Slice(chunks, func(i, j int) bool { return chunks[i].chunk.FirstSeq > chunks[j].chunk.FirstSeq })
	h := sha256.New()
	h.Write(w.generation[:])
	for _, c := range chunks {
		fmt.Fprintf(h, "%d:%d:%d:%d;", c.chunk.ChunkId, c.loc.Segment, c.loc.Offset, c.loc.Length)
	}
	return chunks, hex.EncodeToString(h.Sum(nil)), w.generation, w.recoveryErrors
}

func encodeCursor(c searchCursor, key [32]byte) string {
	raw, _ := json.Marshal(c)
	mac := hmac.New(sha256.New, key[:])
	mac.Write(raw)
	return base64.RawURLEncoding.EncodeToString(append(raw, mac.Sum(nil)...))
}
func decodeCursor(token string, key [32]byte) (searchCursor, error) {
	var c searchCursor
	if len(token) > 8192 {
		return c, fmt.Errorf("invalid cursor")
	}
	raw, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil || len(raw) < 32 {
		return c, fmt.Errorf("invalid cursor")
	}
	payload, signature := raw[:len(raw)-32], raw[len(raw)-32:]
	mac := hmac.New(sha256.New, key[:])
	mac.Write(payload)
	if !hmac.Equal(signature, mac.Sum(nil)) {
		return c, fmt.Errorf("snapshot expired; run a new search")
	}
	err = json.Unmarshal(payload, &c)
	return c, err
}

// LiteralIndex uses invariant one-rune uppercase mapping, shared with the Studio Rune matcher.
func LiteralIndex(message, query string, sensitive bool) (int, int) {
	return literalMatcher(query, sensitive)(message)
}
func literalMatcher(query string, sensitive bool) func(string) (int, int) {
	if query == "" {
		return func(string) (int, int) { return 0, 0 }
	}
	length := utf8.RuneCountInString(query)
	if !sensitive {
		query = strings.Map(unicode.ToUpper, query)
	}
	return func(message string) (int, int) {
		if !sensitive {
			message = strings.Map(unicode.ToUpper, message)
		}
		at := strings.Index(message, query)
		if at < 0 {
			return -1, 0
		}
		return utf8.RuneCountInString(message[:at]), length
	}
}

func makeHit(c *pb.LogChunk, r *pb.LogRecord, start, length int) *pb.NoxLogSearchHit {
	text := []rune(r.Message)
	lo := max(0, start-100)
	hi := min(len(text), max(lo+600, start+min(length, 500)))
	if hi-lo > 4096 {
		hi = lo + 4096
	}
	matchLen := min(length, max(0, hi-start))
	return &pb.NoxLogSearchHit{ChunkId: c.ChunkId, Seq: r.Seq, TimestampUnixNano: r.TimestampUnixNano, Priority: r.Priority, Unit: r.Unit,
		Snippet: string(text[lo:hi]), MatchStart: uint32(len(utf16.Encode(text[lo:start]))), MatchLength: uint32(len(utf16.Encode(text[start : start+matchLen]))), MessageTruncated: lo > 0 || hi < len(text)}
}

func (w *WalSpool) SearchLogs(ctx context.Context, req *pb.NoxSearchLogsRequest, limits SearchLimits) (*pb.NoxSearchLogsResponse, error) {
	begun := time.Now()
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}
	if limits.MaxResults <= 0 || limits.ChunkBytes == 0 || limits.PageBytes < limits.ChunkBytes || limits.PageTime <= 0 || limits.CursorTTL <= 0 {
		return nil, status.Error(codes.Internal, "invalid search limits")
	}
	if len(req.Query) > limits.MaxQueryBytes || len(req.Unit) > limits.MaxQueryBytes || req.StartTimeNano < 0 || req.EndTimeNano < 0 || (req.EndTimeNano != 0 && req.StartTimeNano >= req.EndTimeNano) {
		return nil, status.Error(codes.InvalidArgument, "invalid query or time interval")
	}
	for _, p := range req.Priorities {
		if p > 7 {
			return nil, status.Error(codes.InvalidArgument, "priority must be 0 through 7")
		}
	}
	if req.Query == "" && req.StartTimeNano == 0 && req.EndTimeNano == 0 {
		return nil, status.Error(codes.InvalidArgument, "empty query requires a time bound")
	}
	limit := int(req.Limit)
	if limit == 0 {
		limit = 100
	}
	limit = min(limit, limits.MaxResults)
	query := proto.Clone(req).(*pb.NoxSearchLogsRequest)
	query.Cursor = ""
	query.Limit = 0
	raw, _ := proto.MarshalOptions{Deterministic: true}.Marshal(query)
	hash := sha256.Sum256(raw)
	cur := searchCursor{Query: hex.EncodeToString(hash[:]), Expires: time.Now().Add(limits.CursorTTL).Unix()}
	w.mu.Lock()
	cur.High = w.maxLogSeq
	key := w.generation
	w.mu.Unlock()
	if req.Cursor != "" {
		decoded, err := decodeCursor(req.Cursor, key)
		if err != nil {
			return nil, status.Error(codes.FailedPrecondition, err.Error())
		}
		if decoded.Expires < time.Now().Unix() {
			return nil, status.Error(codes.FailedPrecondition, "snapshot expired; run a new search")
		}
		if decoded.Query != cur.Query {
			return nil, status.Error(codes.InvalidArgument, "cursor belongs to different filters")
		}
		cur = decoded
	}
	chunks, digest, key, recoveryErrors := w.searchSnapshot(cur.High)
	if cur.Digest != "" && cur.Digest != digest {
		return nil, status.Error(codes.FailedPrecondition, "retained chunks changed; run a new search")
	}
	cur.Digest = digest
	if cur.Chunk < 0 || cur.Chunk > len(chunks) || cur.Record < 0 {
		return nil, status.Error(codes.InvalidArgument, "invalid cursor position")
	}
	publicGeneration := sha256.Sum256(key[:])
	resp := &pb.NoxSearchLogsResponse{HighWaterSeq: cur.High, Generation: hex.EncodeToString(publicGeneration[:]), StopReason: "exhausted", CorruptChunks: cur.Corrupt + recoveryErrors}
	dec, err := zstd.NewReader(nil, zstd.WithDecoderConcurrency(1), zstd.WithDecoderMaxMemory(limits.ChunkBytes), zstd.WithDecodeAllCapLimit(true))
	if err != nil {
		return nil, err
	}
	defer dec.Close()
	var buffer []byte
	matchLiteral := literalMatcher(req.Query, req.CaseSensitive)
	for cur.Chunk < len(chunks) {
		if err := ctx.Err(); err != nil {
			return nil, status.FromContextError(err).Err()
		}
		if time.Since(begun) >= limits.PageTime || (resp.ScannedBytes >= limits.PageBytes) {
			resp.StopReason = "scan_budget"
			break
		}
		ch := chunks[cur.Chunk].chunk
		if uint64(ch.RawSize) > limits.ChunkBytes || uint64(len(ch.ZstdRecords)) > limits.ChunkBytes {
			resp.CorruptChunks++
			cur.Chunk++
			cur.Record = 0
			continue
		}
		// A bounded output buffer also handles frames without a declared decompressed size.
		capacity := int(ch.RawSize)
		if capacity == 0 {
			capacity = int(limits.ChunkBytes)
		}
		if cap(buffer) < capacity {
			buffer = make([]byte, 0, capacity)
		}
		decoded, err := dec.DecodeAll(ch.ZstdRecords, buffer[:0])
		buffer = decoded
		resp.ScannedChunks++
		if err != nil {
			resp.CorruptChunks++
			cur.Chunk++
			cur.Record = 0
			continue
		}
		resp.ScannedBytes += uint64(len(decoded))
		list := new(pb.LogRecordList)
		if err := proto.Unmarshal(decoded, list); err != nil {
			resp.CorruptChunks++
			cur.Chunk++
			cur.Record = 0
			continue
		}
		sort.Slice(list.Records, func(i, j int) bool { return list.Records[i].Seq > list.Records[j].Seq })
		valid := len(list.Records) > 0 && uint32(len(list.Records)) == ch.RecordCount
		if valid {
			valid = list.Records[0].Seq == ch.LastSeq && list.Records[len(list.Records)-1].Seq == ch.FirstSeq
		}
		for i := 1; valid && i < len(list.Records); i++ {
			valid = list.Records[i-1].Seq > list.Records[i].Seq
		}
		if !valid {
			resp.CorruptChunks++
			cur.Chunk++
			cur.Record = 0
			continue
		}
		for cur.Record < len(list.Records) {
			if err := ctx.Err(); err != nil {
				return nil, status.FromContextError(err).Err()
			}
			if time.Since(begun) >= limits.PageTime {
				resp.StopReason = "scan_budget"
				break
			}
			r := list.Records[cur.Record]
			cur.Record++
			resp.ScannedRecords++
			if r.Seq > cur.High || (req.StartTimeNano != 0 && r.TimestampUnixNano < req.StartTimeNano) || (req.EndTimeNano != 0 && r.TimestampUnixNano >= req.EndTimeNano) || (req.Unit != "" && req.Unit != r.Unit) {
				continue
			}
			if len(req.Priorities) > 0 {
				found := false
				for _, p := range req.Priorities {
					if r.Priority == p {
						found = true
					}
				}
				if !found {
					continue
				}
			}
			start, length := matchLiteral(r.Message)
			if start < 0 {
				continue
			}
			resp.Hits = append(resp.Hits, makeHit(ch, r, start, length))
			if len(resp.Hits) >= limit {
				resp.StopReason = "page_limit"
				break
			}
		}
		if cur.Record >= len(list.Records) {
			cur.Chunk++
			cur.Record = 0
		}
		if resp.StopReason != "exhausted" {
			break
		}
	}
	_, after, _, _ := w.searchSnapshot(cur.High)
	if after != digest {
		return nil, status.Error(codes.FailedPrecondition, "retained chunks changed; run a new search")
	}
	if cur.Chunk < len(chunks) {
		cur.Corrupt = resp.CorruptChunks - recoveryErrors
		resp.NextCursor = encodeCursor(cur, key)
	} else {
		resp.StopReason = "exhausted"
		resp.SnapshotComplete = resp.CorruptChunks == 0
		if !resp.SnapshotComplete {
			resp.StopReason = "corruption"
		}
	}
	resp.TookMs = time.Since(begun).Milliseconds()
	return resp, nil
}
