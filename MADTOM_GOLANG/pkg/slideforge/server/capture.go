package server

import (
	"context"
	"log"
	"slices"
	"sort"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/DarkDuck007/madtom/pkg/noxchat/buffer"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/proto"
)

const (
	telemetryHistorySize = 3600
	jobHistorySize       = 256
	logBatchSize         = 64
	apiBatchSize         = 16
	stateBatchSize       = 64
)

// liveChunks is a bounded backlog shared by all subscribers. Server.mu
// protects updates to the sequence counter and change notification.
type liveChunks struct {
	ring    *buffer.RingChunkBuffer
	lastID  uint64
	changed chan struct{}
}

func newLiveChunks(maxBytes int64) liveChunks {
	return liveChunks{ring: buffer.NewRingChunkBuffer(maxBytes), changed: make(chan struct{})}
}

func (s *Server) syncBridgeMode() {
	s.modeMu.Lock()
	defer s.modeMu.Unlock()
	s.mu.Lock()
	logs, store := s.logSubscribers != 0, s.storeRawLogs
	s.mu.Unlock()
	// The bot gates telemetry on state capture, including samples used by range queries.
	s.bridge.SetCaptureMode(true, logs, store)
}

func (s *Server) OnTelemetrySample(sample *madtomv1.NoxTelemetrySample) {
	converted := &madtomv1.SfTelemetrySample{TimestampUnixNano: sample.TimestampUnixNano, Metrics: sample.Metrics}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.telemetry = appendLatest(s.telemetry, converted, telemetryHistorySize)
	s.pendingState.TelemetrySamples = append(s.pendingState.TelemetrySamples, converted)
	s.flushFullStateLocked()
}

func (s *Server) OnJobEvent(event *madtomv1.NoxJobProgressEvent) {
	converted := &madtomv1.SfJobEvent{
		TimestampUnixNano: time.Now().UnixNano(), JobId: event.JobId, UserId: event.ChatId,
		Kind: event.JobType, Status: event.Status, Json: event.Details,
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.jobs = appendLatest(s.jobs, converted, jobHistorySize)
	s.pendingState.JobEvents = append(s.pendingState.JobEvents, converted)
	s.flushFullStateLocked()
}

func appendLatest[T any](values []T, value T, limit int) []T {
	if len(values) < limit {
		return append(values, value)
	}
	copy(values, values[1:])
	values[len(values)-1] = value
	return values
}

func (s *Server) snapshotState() ([]*madtomv1.SfTelemetrySample, []*madtomv1.SfJobEvent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.telemetry), slices.Clone(s.jobs)
}

func (s *Server) OnTurnEvent(*madtomv1.NoxTurnTagEvent)        {}
func (s *Server) OnChatTraceEvent(*madtomv1.NoxChatTraceEvent) {}

func (s *Server) OnLogRecord(rec *madtomv1.LogRecord) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.storeRawLogs {
		s.writeLogChunk(s.logChunker.Append(rec.Priority, rec.Unit, rec.Message, rec.Fields, rec.TimestampUnixNano))
	}
	if s.logSubscribers > 0 {
		s.pendingLogs = append(s.pendingLogs, rec)
		if len(s.pendingLogs) == logBatchSize {
			s.flushLogsLocked()
		}
	}
}

func (s *Server) OnApiCallRecord(rec *madtomv1.NoxRawApiCallRecord) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.storeRawLogs && s.apiSubscribers == 0 {
		return
	}
	converted := &madtomv1.SfApiCallRecord{
		TimestampUnixNano: rec.TimestampUnixNano, JobId: rec.GetTraceContext().GetRunId(),
		Kind: rec.Caller, Name: rec.Model, Seconds: float64(rec.DurationMs) / 1000,
		Ok: rec.ErrorMessage == "", Json: boundedUTF8(rec.RawRequestJson, 64<<10),
	}
	if s.storeRawLogs {
		s.writeAPIChunk(s.apiChunker.Append(&madtomv1.NoxRawApiCallRecord{
			CallId: rec.CallId, TimestampUnixNano: rec.TimestampUnixNano, TraceContext: rec.TraceContext,
			Caller: rec.Caller, Model: rec.Model, DurationMs: rec.DurationMs,
			RawRequestJson: converted.Json, ErrorMessage: rec.ErrorMessage,
		}))
	}
	if s.apiSubscribers > 0 {
		s.pendingAPI = append(s.pendingAPI, converted)
		if len(s.pendingAPI) == apiBatchSize {
			s.flushAPILocked()
		}
	}
}

func boundedUTF8(value string, limit int) string {
	if len(value) <= limit {
		return value
	}
	for !utf8.RuneStart(value[limit]) {
		limit--
	}
	return strings.Clone(value[:limit])
}

func (s *Server) flushLoop() {
	defer close(s.flushed)
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-s.done:
			return
		case <-ticker.C:
			s.mu.Lock()
			s.flushStateLocked()
			s.flushLogsLocked()
			s.flushAPILocked()
			if s.storeRawLogs {
				s.flushSpoolLocked()
			}
			s.mu.Unlock()
		}
	}
}

func (s *Server) flushFullStateLocked() {
	if len(s.pendingState.TelemetrySamples)+len(s.pendingState.JobEvents) >= stateBatchSize {
		s.flushStateLocked()
	}
}

func (s *Server) flushStateLocked() {
	if len(s.pendingState.TelemetrySamples)+len(s.pendingState.JobEvents) > 0 {
		s.publishLocked(&s.stateChunks, "", &s.pendingState)
		s.pendingState = madtomv1.SfStateChunkPayload{}
	}
}

func (s *Server) flushLogsLocked() {
	if len(s.pendingLogs) > 0 {
		s.publishLocked(&s.logChunks, "log", &madtomv1.SfLogApiChunkPayload{LogRecords: s.pendingLogs})
		s.pendingLogs = nil
	}
}

func (s *Server) flushAPILocked() {
	if len(s.pendingAPI) > 0 {
		s.publishLocked(&s.logChunks, "api", &madtomv1.SfLogApiChunkPayload{ApiCallRecords: s.pendingAPI})
		s.pendingAPI = nil
	}
}

func (s *Server) flushSpoolLocked() {
	s.writeLogChunk(s.logChunker.Flush())
	s.writeAPIChunk(s.apiChunker.Flush())
}

func (s *Server) writeLogChunk(chunk *madtomv1.LogChunk, err error) {
	if err == nil && chunk != nil {
		err = s.wal.WriteLogChunk(chunk)
	}
	if err != nil {
		log.Printf("[SlideForge_Probe] Retain log chunk: %v", err)
	}
}

func (s *Server) writeAPIChunk(chunk *madtomv1.NoxApiCallChunk, err error) {
	if err == nil && chunk != nil {
		err = s.wal.WriteApiCallChunk(chunk)
	}
	if err != nil {
		log.Printf("[SlideForge_Probe] Retain API chunk: %v", err)
	}
}

func (s *Server) publishLocked(live *liveChunks, kind string, payload proto.Message) {
	compressed, rawSize, err := s.compressor.CompressPayload(payload)
	if err != nil {
		log.Printf("[SlideForge_Probe] Encode %s stream chunk: %v", kind, err)
		return
	}
	live.lastID++
	now := time.Now().UnixNano()
	live.ring.Push(&buffer.Chunk{ID: live.lastID, Timestamp: now, Payload: compressed, RawSize: rawSize, Kind: kind})
	close(live.changed)
	live.changed = make(chan struct{})
	s.batches++
	s.zstdBytes += uint64(len(compressed))
	s.decodedBytes += uint64(rawSize)
	s.lastChunkAt = now
}

func (s *Server) SubscribeState(_ *madtomv1.SfStateSubscriptionRequest, stream madtomv1.SlideForgeControlService_SubscribeStateServer) error {
	s.mu.Lock()
	chunks, _ := s.stateChunks.ring.Snapshot()
	after := s.stateChunks.lastID
	if len(chunks) > 0 {
		after = chunks[0].ID - 1
	}
	s.mu.Unlock()
	return s.streamChunks(stream.Context(), &s.stateChunks, after, func(c *buffer.Chunk, dropped uint64) error {
		return stream.Send(&madtomv1.SfStateChunk{
			NodeId: s.cfg.NodeID, ChunkId: c.ID, TimestampUnixNano: c.Timestamp,
			ZstdPayload: c.Payload, DecodedSize: c.RawSize, DroppedChunks: dropped,
		})
	})
}

func (s *Server) SubscribeLogsAndApi(req *madtomv1.SfLogApiSubscriptionRequest, stream madtomv1.SlideForgeControlService_SubscribeLogsAndApiServer) error {
	s.mu.Lock()
	s.logSubscribers++
	if req.IncludeRawApiCalls {
		s.apiSubscribers++
	}
	after := s.logChunks.lastID
	s.mu.Unlock()
	s.syncBridgeMode()
	defer func() {
		s.mu.Lock()
		s.logSubscribers--
		if req.IncludeRawApiCalls {
			s.apiSubscribers--
			if s.apiSubscribers == 0 {
				s.pendingAPI = nil
			}
		}
		if s.logSubscribers == 0 {
			s.pendingLogs = nil
			s.logChunks.ring.ClearAndReset()
		}
		s.mu.Unlock()
		s.syncBridgeMode()
	}()
	return s.streamChunks(stream.Context(), &s.logChunks, after, func(c *buffer.Chunk, dropped uint64) error {
		if c.Kind == "api" && !req.IncludeRawApiCalls {
			return nil
		}
		return stream.Send(&madtomv1.SfLogApiChunk{
			NodeId: s.cfg.NodeID, ChunkId: c.ID, TimestampUnixNano: c.Timestamp,
			ZstdPayload: c.Payload, DecodedSize: c.RawSize, DroppedChunks: dropped, ChunkKind: c.Kind,
		})
	})
}

func (s *Server) streamChunks(ctx context.Context, live *liveChunks, after uint64, send func(*buffer.Chunk, uint64) error) error {
	var dropped uint64
	for {
		s.mu.Lock()
		chunks, _ := live.ring.Snapshot()
		changed := live.changed
		s.mu.Unlock()
		for _, c := range chunks {
			if c.ID <= after {
				continue
			}
			dropped += c.ID - after - 1
			if err := send(c, dropped); err != nil {
				return err
			}
			after = c.ID
		}
		select {
		case <-s.done:
			return nil
		case <-ctx.Done():
			return ctx.Err()
		case <-changed:
		}
	}
}

func (s *Server) QueryTelemetryRange(_ context.Context, req *madtomv1.RangeQueryRequest) (*madtomv1.RangeQueryResponse, error) {
	samples, _ := s.snapshotState()
	var points []*madtomv1.TimeSeriesPoint
	for _, sample := range samples {
		ts := sample.TimestampUnixNano
		if ts < req.StartTimeUnixNano || (req.EndTimeUnixNano != 0 && ts > req.EndTimeUnixNano) {
			continue
		}
		if value, ok := sample.Metrics[req.MetricName]; ok {
			points = append(points, &madtomv1.TimeSeriesPoint{TimestampUnixNano: ts, Value: value, MinValue: value, MaxValue: value})
		}
	}
	sort.SliceStable(points, func(i, j int) bool { return points[i].TimestampUnixNano < points[j].TimestampUnixNano })
	if req.TargetPoints == 1 && len(points) > 1 {
		points = points[len(points)-1:]
	} else if req.TargetPoints > 1 && uint64(req.TargetPoints) < uint64(len(points)) {
		sampled := make([]*madtomv1.TimeSeriesPoint, int(req.TargetPoints))
		for i := range sampled {
			sampled[i] = points[i*(len(points)-1)/(len(sampled)-1)]
		}
		points = sampled
	}
	return &madtomv1.RangeQueryResponse{NodeId: s.cfg.NodeID, MetricName: req.MetricName, Points: points}, nil
}
