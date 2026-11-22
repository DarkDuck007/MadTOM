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

	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"
)

const tracePageBytes = 4 << 20

// API bodies remain compressed in apiChunks. Only metadata is indexed by chat.
// Events are completed execution outcomes, never individual streamed tokens.
type chatTraceEntry struct {
	key       string
	chatID    int64
	hasChatID bool
	chatName  string
	ordinal   uint64
	timestamp int64
	location  frameLocation
	chunkID   uint64
	api       *pb.NoxRawApiCallRecord
	event     *pb.NoxChatTraceEvent
}

// ChatCallStat summarizes retained API calls and latest agent execution timestamp for a chat.
type ChatCallStat struct {
	ChatID           int64
	HasChatID        bool
	ChatName         string
	CallCount        uint32
	LastCallUnixNano int64
}

func normalizeChatTitle(title string) string {
	return strings.ToLower(strings.TrimSpace(title))
}

func cloneTraceContext(c *pb.NoxTraceContext) *pb.NoxTraceContext {
	if c == nil {
		return nil
	}
	return proto.Clone(c).(*pb.NoxTraceContext)
}

func apiCallSummary(r *pb.NoxRawApiCallRecord) *pb.NoxRawApiCallRecord {
	name := r.ChatName
	if name == "" {
		name = extractChatName(r.RawRequestJson, r.Caller)
	}
	result := &pb.NoxRawApiCallRecord{
		CallId: r.CallId, TimestampUnixNano: r.TimestampUnixNano, Caller: r.Caller,
		Model: r.Model, BaseUrl: r.BaseUrl, Temperature: r.Temperature, DurationMs: r.DurationMs,
		PromptTokens: r.PromptTokens, CompletionTokens: r.CompletionTokens, CachedTokens: r.CachedTokens,
		ChatName: name, ErrorMessage: r.ErrorMessage, TraceContext: cloneTraceContext(r.TraceContext),
	}
	for _, image := range r.ImageDataUrls {
		result.ImageDataUrls = append(result.ImageDataUrls, &pb.NoxImageData{Label: image.Label, MimeType: image.MimeType})
	}
	return result
}

func (w *WalSpool) removeTraceEntryLocked(key string) {
	entry := w.traceEntries[key]
	if entry == nil {
		return
	}
	delete(w.traceEntries, key)
	if entry.hasChatID {
		delete(w.traceByChat[entry.chatID], key)
		if len(w.traceByChat[entry.chatID]) == 0 {
			delete(w.traceByChat, entry.chatID)
		}
	}
	if normName := normalizeChatTitle(entry.chatName); normName != "" {
		delete(w.traceByName[normName], key)
		if len(w.traceByName[normName]) == 0 {
			delete(w.traceByName, normName)
		}
	}
}

func (w *WalSpool) putTraceEntryLocked(entry *chatTraceEntry) {
	if previous := w.traceEntries[entry.key]; previous != nil {
		entry.ordinal = previous.ordinal
		if previous.chatID != entry.chatID || previous.hasChatID != entry.hasChatID || previous.chatName != entry.chatName {
			w.removeTraceEntryLocked(entry.key)
		}
	} else {
		w.traceOrdinal++
		entry.ordinal = w.traceOrdinal
	}
	w.traceEntries[entry.key] = entry
	if entry.hasChatID {
		if w.traceByChat[entry.chatID] == nil {
			w.traceByChat[entry.chatID] = make(map[string]struct{})
		}
		w.traceByChat[entry.chatID][entry.key] = struct{}{}
	}
	if normName := normalizeChatTitle(entry.chatName); normName != "" {
		if w.traceByName[normName] == nil {
			w.traceByName[normName] = make(map[string]struct{})
		}
		w.traceByName[normName][entry.key] = struct{}{}
	}
}

func (w *WalSpool) indexApiTraceLocked(chunk *pb.NoxApiCallChunk, loc frameLocation) error {
	if len(chunk.ZstdRecords) == 0 {
		return nil
	}
	decoder, err := zstd.NewReader(nil, zstd.WithDecoderMaxMemory(64<<20), zstd.WithDecoderConcurrency(1))
	if err != nil {
		return err
	}
	defer decoder.Close()
	raw, err := decoder.DecodeAll(chunk.ZstdRecords, nil)
	if err != nil {
		return err
	}
	list := new(pb.NoxApiCallRecordList)
	if err = proto.Unmarshal(raw, list); err != nil {
		return err
	}
	keys := make([]string, 0, len(list.Records))
	present := make(map[string]struct{})
	summaryRecords := make([]*pb.NoxRawApiCallRecord, len(list.Records))
	for i, record := range list.Records {
		summary := apiCallSummary(record)
		summaryRecords[i] = summary
		if record.CallId == "" {
			continue
		}
		hasChatID := record.TraceContext != nil && record.TraceContext.ChatId != nil
		var chatID int64
		if hasChatID {
			chatID = record.TraceContext.GetChatId()
		}
		if !hasChatID && summary.ChatName == "" {
			continue
		}
		key := "a:" + record.CallId
		keys = append(keys, key)
		present[key] = struct{}{}
		w.putTraceEntryLocked(&chatTraceEntry{
			key:       key,
			chatID:    chatID,
			hasChatID: hasChatID,
			chatName:  summary.ChatName,
			timestamp: record.TimestampUnixNano,
			location:  loc,
			chunkID:   chunk.ChunkId,
			api:       summary,
		})
	}
	// Cache pre-compressed summary chunk so QueryApiCallChunks(summaryOnly: true) is O(1).
	if lightRaw, err := proto.Marshal(&pb.NoxApiCallRecordList{Records: summaryRecords}); err == nil {
		if enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault)); err == nil {
			sumChunk := proto.Clone(chunk).(*pb.NoxApiCallChunk)
			sumChunk.ZstdRecords = enc.EncodeAll(lightRaw, nil)
			sumChunk.RawSize = uint32(len(lightRaw))
			_ = enc.Close()
			w.apiSummaryChunks[chunk.ChunkId] = sumChunk
		}
	}
	for _, key := range w.apiTraceKeys[chunk.ChunkId] {
		if _, ok := present[key]; !ok {
			if entry := w.traceEntries[key]; entry != nil && entry.api != nil && entry.chunkID == chunk.ChunkId {
				w.removeTraceEntryLocked(key)
			}
		}
	}
	w.apiTraceKeys[chunk.ChunkId] = keys
	return nil
}

func (w *WalSpool) indexTraceEventLocked(event *pb.NoxChatTraceEvent, loc frameLocation) {
	if event.EventId == "" || event.TraceContext == nil || event.TraceContext.ChatId == nil {
		return
	}
	w.putTraceEntryLocked(&chatTraceEntry{
		key:       "e:" + event.EventId,
		chatID:    event.TraceContext.GetChatId(),
		hasChatID: true,
		timestamp: event.TimestampUnixNano,
		location:  loc,
		event:     event,
	})
}

// ChatCallStats returns per-chat API call counts and latest agent timestamps indexed in the WAL spool.
func (w *WalSpool) ChatCallStats() (map[int64]ChatCallStat, map[string]ChatCallStat) {
	w.mu.Lock()
	defer w.mu.Unlock()

	byID := make(map[int64]ChatCallStat, len(w.traceByChat))
	byName := make(map[string]ChatCallStat, len(w.traceByName))

	for chatID, keys := range w.traceByChat {
		var stat ChatCallStat
		stat.ChatID = chatID
		stat.HasChatID = true
		for k := range keys {
			entry := w.traceEntries[k]
			if entry == nil {
				continue
			}
			if entry.api != nil {
				stat.CallCount++
				if stat.ChatName == "" && entry.chatName != "" {
					stat.ChatName = entry.chatName
				}
			}
			if entry.timestamp > stat.LastCallUnixNano {
				stat.LastCallUnixNano = entry.timestamp
			}
		}
		if stat.CallCount > 0 || stat.LastCallUnixNano > 0 {
			byID[chatID] = stat
		}
	}

	for normName, keys := range w.traceByName {
		var stat ChatCallStat
		stat.ChatName = normName
		for k := range keys {
			entry := w.traceEntries[k]
			if entry == nil || entry.api == nil {
				continue
			}
			stat.CallCount++
			if entry.timestamp > stat.LastCallUnixNano {
				stat.LastCallUnixNano = entry.timestamp
			}
		}
		if stat.CallCount > 0 {
			byName[normName] = stat
		}
	}

	return byID, byName
}

// WriteChatTraceEvent participates in the same quota, recovery and clear rules as raw API calls.
func (w *WalSpool) WriteChatTraceEvent(event *pb.NoxChatTraceEvent) error {
	if event == nil || event.EventId == "" || event.TraceContext == nil || event.TraceContext.ChatId == nil {
		return status.Error(codes.InvalidArgument, "trace event requires event_id and chat_id")
	}
	raw, err := proto.Marshal(event)
	if err != nil {
		return err
	}
	return w.writeFrame('T', 0, raw, 0, 0, event.TimestampUnixNano, event.TimestampUnixNano)
}

type chatTraceCursor struct {
	Query     string `json:"q"`
	Digest    string `json:"d"`
	High      uint64 `json:"h"`
	Timestamp int64  `json:"t"`
	Key       string `json:"k"`
	Expires   int64  `json:"e"`
}

func encodeChatTraceCursor(cursor chatTraceCursor, key [32]byte) string {
	raw, _ := json.Marshal(cursor)
	mac := hmac.New(sha256.New, key[:])
	mac.Write(raw)
	return base64.RawURLEncoding.EncodeToString(append(raw, mac.Sum(nil)...))
}

func decodeChatTraceCursor(token string, key [32]byte) (chatTraceCursor, error) {
	var cursor chatTraceCursor
	if len(token) > 8192 {
		return cursor, fmt.Errorf("cursor too large")
	}
	raw, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil || len(raw) < sha256.Size {
		return cursor, fmt.Errorf("invalid cursor")
	}
	payload, signature := raw[:len(raw)-sha256.Size], raw[len(raw)-sha256.Size:]
	mac := hmac.New(sha256.New, key[:])
	mac.Write(payload)
	if !hmac.Equal(signature, mac.Sum(nil)) {
		return cursor, fmt.Errorf("cursor signature mismatch or snapshot expired")
	}
	err = json.Unmarshal(payload, &cursor)
	return cursor, err
}

// QueryChatTrace reads only a chat's metadata index. Each page is bounded to 200
// outcomes and 4 MiB; full API payloads continue to use GetApiCallDetail.
func (w *WalSpool) QueryChatTrace(ctx context.Context, req *pb.NoxChatTraceRequest) (*pb.NoxChatTraceResponse, error) {
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}
	if (req.ChatId == nil && strings.TrimSpace(req.ChatName) == "") || len(req.SessionId) > 512 || len(req.RunId) > 512 || len(req.ChatName) > 512 || req.Limit > 200 {
		return nil, status.Error(codes.InvalidArgument, "chat_id or chat_name is required and limit must not exceed 200")
	}
	limit := int(req.Limit)
	if limit == 0 {
		limit = 100
	}
	normChatName := normalizeChatTitle(req.ChatName)
	queryBytes, _ := json.Marshal(struct {
		HasChat           bool
		Chat              int64
		Name, Session, Run string
	}{req.ChatId != nil, req.GetChatId(), normChatName, req.SessionId, req.RunId})
	queryHash := sha256.Sum256(queryBytes)
	query := hex.EncodeToString(queryHash[:])
	w.mu.Lock()
	key := w.generation
	cursor := chatTraceCursor{Query: query, High: w.traceOrdinal, Expires: time.Now().Add(10 * time.Minute).Unix()}
	if req.Cursor != "" {
		var err error
		cursor, err = decodeChatTraceCursor(req.Cursor, key)
		if err != nil || cursor.Query != query || cursor.Expires < time.Now().Unix() {
			w.mu.Unlock()
			return nil, status.Error(codes.InvalidArgument, "invalid or expired chat trace cursor")
		}
	}
	candidateKeys := make(map[string]struct{})
	if req.ChatId != nil {
		for k := range w.traceByChat[req.GetChatId()] {
			candidateKeys[k] = struct{}{}
		}
	}
	if normChatName != "" {
		for k := range w.traceByName[normChatName] {
			entry := w.traceEntries[k]
			if entry != nil && (!entry.hasChatID || req.ChatId == nil || entry.chatID == req.GetChatId()) {
				candidateKeys[k] = struct{}{}
			}
		}
	}
	entries := make([]*chatTraceEntry, 0, len(candidateKeys))
	for entryKey := range candidateKeys {
		if len(entries)%128 == 0 {
			if err := ctx.Err(); err != nil {
				w.mu.Unlock()
				return nil, status.FromContextError(err).Err()
			}
		}
		entry := w.traceEntries[entryKey]
		if entry == nil || entry.ordinal > cursor.High {
			continue
		}
		trace := entry.api.GetTraceContext()
		if entry.event != nil {
			trace = entry.event.TraceContext
		}
		if req.SessionId != "" && trace.GetSessionId() != req.SessionId || req.RunId != "" && trace.GetRunId() != req.RunId {
			continue
		}
		entries = append(entries, entry)
	}
	recoveryErrors := w.recoveryErrors
	w.mu.Unlock()
	sort.Slice(entries, func(i, j int) bool {
		if entries[i].timestamp != entries[j].timestamp {
			return entries[i].timestamp < entries[j].timestamp
		}
		return entries[i].key < entries[j].key
	})
	digest := sha256.New()
	for _, entry := range entries {
		fmt.Fprintf(digest, "%s:%d:%d;", entry.key, entry.ordinal, entry.timestamp)
	}
	snapshotDigest := hex.EncodeToString(digest.Sum(nil))
	if req.Cursor != "" && cursor.Digest != snapshotDigest {
		return nil, status.Error(codes.FailedPrecondition, "retained chat trace changed or expired; refresh history")
	}
	cursor.Digest = snapshotDigest
	response := &pb.NoxChatTraceResponse{SnapshotComplete: true, Notice: "Only captured, retained execution records are available; older history may have expired."}
	if recoveryErrors > 0 {
		response.Notice = "Some WAL frames could not be recovered; retained execution history is incomplete."
	}
	if len(entries) > 0 {
		response.OldestTimestampUnixNano = entries[0].timestamp
		response.NewestTimestampUnixNano = entries[len(entries)-1].timestamp
	}
	count, bytes := 0, 0
	for _, entry := range entries {
		if err := ctx.Err(); err != nil {
			return nil, status.FromContextError(err).Err()
		}
		if req.Cursor != "" && (entry.timestamp < cursor.Timestamp || entry.timestamp == cursor.Timestamp && entry.key <= cursor.Key) {
			continue
		}
		size := 0
		if entry.api != nil {
			size = proto.Size(entry.api)
		} else {
			size = proto.Size(entry.event)
		}
		if size > tracePageBytes {
			return nil, status.Error(codes.ResourceExhausted, "one execution event exceeds the 4 MiB page limit")
		}
		if count >= limit || bytes+size > tracePageBytes {
			response.NextCursor = encodeChatTraceCursor(cursor, key)
			response.SnapshotComplete = false
			break
		}
		if entry.api != nil {
			response.Calls = append(response.Calls, proto.Clone(entry.api).(*pb.NoxRawApiCallRecord))
		} else {
			response.Events = append(response.Events, proto.Clone(entry.event).(*pb.NoxChatTraceEvent))
		}
		count++
		bytes += size
		cursor.Timestamp = entry.timestamp
		cursor.Key = entry.key
	}
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}
	return response, nil
}
