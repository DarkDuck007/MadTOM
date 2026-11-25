package server

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	"github.com/DarkDuck007/madtom/pkg/noxchat/bridge"
	"github.com/DarkDuck007/madtom/pkg/noxchat/buffer"
	"github.com/DarkDuck007/madtom/pkg/noxchat/chunker"
	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	"github.com/DarkDuck007/madtom/pkg/noxchat/spool"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

type Server struct {
	madtomv1.UnimplementedNoxChatControlServiceServer

	cfg          *config.ProbeConfig
	verifier     *auth.Verifier
	tlsConfig    *tls.Config
	grpcServer   *grpc.Server
	bridgeClient *bridge.Client
	stateBuffer  *buffer.RingChunkBuffer
	logBuffer    *buffer.RingChunkBuffer
	walSpool     *spool.WalSpool
	logChunker   *chunker.LogChunker
	apiChunker   *chunker.ApiCallChunker
	stateChunker *chunker.StateChunker

	searchSlots      chan struct{}
	chatTraceSlots   chan struct{}
	stateSubscribers int64
	logSubscribers   int64

	subMu        sync.RWMutex
	stateSubs    map[uint64]chan *madtomv1.NoxStateChunk
	logSubs      map[uint64]chan *madtomv1.NoxLogApiChunk
	subIDCounter uint64

	mu                  sync.Mutex
	pendingTelemetry    []*madtomv1.NoxTelemetrySample
	pendingJobEvents    []*madtomv1.NoxJobProgressEvent
	pendingTurnEvents   []*madtomv1.NoxTurnTagEvent
	telemetryHistory    map[string][]historyPoint // metric -> list of points for LTTB query
	stateChunkIDCounter uint64

	// Transport compression counters
	batchesTotal   uint64
	zstdBatches    uint64
	zstdBytes      uint64
	decodedBytes   uint64
	rawBytesPassed uint64

	zstdEncMu sync.Mutex
	zstdEnc   *zstd.Encoder
}

type historyPoint struct {
	ts  int64
	val double
}

type double = float64

func NewServer(cfg *config.ProbeConfig, keyPair *auth.KeyPair, tlsConfig *tls.Config) (*Server, error) {
	var verifier *auth.Verifier
	if keyPair != nil {
		verifier = auth.NewVerifier([]*auth.KeyPair{keyPair}, nil)
	}
	return NewServerWithVerifier(cfg, verifier, tlsConfig)
}

func NewServerWithVerifier(cfg *config.ProbeConfig, verifier *auth.Verifier, tlsConfig *tls.Config) (*Server, error) {
	stateBuf := buffer.NewRingChunkBuffer(cfg.StateBufferBytes)
	logBuf := buffer.NewRingChunkBuffer(cfg.LogBufferBytes)

	wal, err := spool.NewWalSpool(cfg.SpoolDir, cfg.MaxSpoolBytes)
	if err != nil {
		return nil, fmt.Errorf("failed to init WAL spool: %w", err)
	}

	startLogSeq, startApiSeq := wal.MaxSeqs()
	nextLogChunkID, nextApiChunkID := wal.NextChunkIDs()

	lChunker, err := chunker.NewLogChunker(cfg.NodeID, startLogSeq)
	if err != nil {
		return nil, err
	}
	lChunker.SetNextChunkID(nextLogChunkID)

	aChunker, err := chunker.NewApiCallChunker(cfg.NodeID, startApiSeq)
	if err != nil {
		return nil, err
	}
	aChunker.SetNextChunkID(nextApiChunkID)

	sChunker, err := chunker.NewStateChunker(cfg.NodeID)
	if err != nil {
		return nil, err
	}

	zEnc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return nil, err
	}

	s := &Server{
		cfg:              cfg,
		verifier:         verifier,
		tlsConfig:        tlsConfig,
		stateBuffer:      stateBuf,
		logBuffer:        logBuf,
		walSpool:         wal,
		logChunker:       lChunker,
		apiChunker:       aChunker,
		stateChunker:     sChunker,
		zstdEnc:          zEnc,
		telemetryHistory: make(map[string][]historyPoint),
		stateSubs:        make(map[uint64]chan *madtomv1.NoxStateChunk),
		logSubs:          make(map[uint64]chan *madtomv1.NoxLogApiChunk),
	}

	s.bridgeClient = bridge.NewClient(cfg.PythonBridgeHost, cfg.PythonBridgePort, s)
	return s, nil
}

// Start launches the gRPC server and background flush workers.
func (s *Server) Start() error {
	s.bridgeClient.Start()
	s.syncBridgeMode()

	lis, err := net.Listen("tcp", s.cfg.ListenAddr)
	if err != nil {
		return err
	}

	var opts []grpc.ServerOption
	if s.tlsConfig != nil {
		opts = append(opts, grpc.Creds(credentials.NewTLS(s.tlsConfig)))
	}
	opts = append(opts, grpc.UnaryInterceptor(s.unaryAuthInterceptor))
	opts = append(opts, grpc.StreamInterceptor(s.streamAuthInterceptor))

	s.grpcServer = grpc.NewServer(opts...)
	madtomv1.RegisterNoxChatControlServiceServer(s.grpcServer, s)

	go s.stateTickerLoop()
	go s.logFlushTickerLoop()

	log.Printf("[NoxChat_Probe] Serving encrypted gRPC on %s", s.cfg.ListenAddr)
	return s.grpcServer.Serve(lis)
}

func (s *Server) Stop() {
	if s.grpcServer != nil {
		s.grpcServer.GracefulStop()
	}
	s.bridgeClient.Close()
	_ = s.walSpool.Close()
	s.logChunker.Close()
	s.apiChunker.Close()
	s.stateChunker.Close()
	s.zstdEncMu.Lock()
	if s.zstdEnc != nil {
		_ = s.zstdEnc.Close()
	}
	s.zstdEncMu.Unlock()
}

// Dispatcher callbacks from Python bridge
func (s *Server) OnLogRecord(rec *madtomv1.LogRecord) {
	chunk, err := s.logChunker.Append(rec.Priority, rec.Unit, rec.Message, rec.Fields, rec.TimestampUnixNano)
	if err == nil && chunk != nil {
		s.handleNewLogChunk(chunk)
	}
}

func (s *Server) OnApiCallRecord(rec *madtomv1.NoxRawApiCallRecord) {
	chunk, err := s.apiChunker.Append(rec)
	if err == nil && chunk != nil {
		s.handleNewApiChunk(chunk)
	}
}

func (s *Server) OnTelemetrySample(sample *madtomv1.NoxTelemetrySample) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.pendingTelemetry = append(s.pendingTelemetry, sample)
	// Retain in history for LTTB queries (up to 3600 points per series)
	for k, v := range sample.Metrics {
		pts := s.telemetryHistory[k]
		pts = append(pts, historyPoint{ts: sample.TimestampUnixNano, val: v})
		if len(pts) > 3600 {
			pts = pts[len(pts)-3600:]
		}
		s.telemetryHistory[k] = pts
	}
}

func (s *Server) OnJobEvent(event *madtomv1.NoxJobProgressEvent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.pendingJobEvents = append(s.pendingJobEvents, event)
}

func (s *Server) OnTurnEvent(event *madtomv1.NoxTurnTagEvent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.pendingTurnEvents = append(s.pendingTurnEvents, event)
}

func (s *Server) OnChatTraceEvent(event *madtomv1.NoxChatTraceEvent) {
	if event == nil || event.EventId == "" || event.TraceContext == nil || event.TraceContext.ChatId == nil {
		return
	}
	if s.cfg.StoreRawLogs {
		if err := s.walSpool.WriteChatTraceEvent(event); err != nil {
			log.Printf("[NoxChat_Probe] retain chat trace: %v", err)
		}
	}
	raw, err := proto.Marshal(event)
	if err != nil {
		return
	}
	s.logBuffer.Push(&buffer.Chunk{ID: uint64(event.TimestampUnixNano), Timestamp: event.TimestampUnixNano, Payload: raw, RawSize: uint32(len(raw))})
	msg := &madtomv1.NoxLogApiChunk{NodeId: s.cfg.NodeID, TimestampUnixNano: event.TimestampUnixNano,
		ZstdPayload: raw, DecodedSize: uint32(len(raw)), ChunkKind: "chat_trace"}
	s.subMu.RLock()
	for _, ch := range s.logSubs {
		select {
		case ch <- msg:
		default:
		}
	}
	s.subMu.RUnlock()
}

func (s *Server) handleNewLogChunk(chunk *madtomv1.LogChunk) {
	if s.cfg.StoreRawLogs {
		_ = s.walSpool.WriteLogChunk(chunk)
	}
	raw, _ := proto.Marshal(chunk)
	s.logBuffer.Push(&buffer.Chunk{
		ID:        chunk.ChunkId,
		Timestamp: chunk.LastTsUnixNano,
		Payload:   raw,
		RawSize:   chunk.RawSize,
	})

	atomic.AddUint64(&s.batchesTotal, 1)
	atomic.AddUint64(&s.zstdBatches, 1)
	atomic.AddUint64(&s.zstdBytes, uint64(len(chunk.ZstdRecords)))
	atomic.AddUint64(&s.decodedBytes, uint64(chunk.RawSize))

	msg := &madtomv1.NoxLogApiChunk{
		NodeId:            s.cfg.NodeID,
		ChunkId:           chunk.ChunkId,
		TimestampUnixNano: chunk.LastTsUnixNano,
		ZstdPayload:       raw,
		DecodedSize:       chunk.RawSize,
		DroppedChunks:     0,
		ChunkKind:         "log",
	}

	s.subMu.RLock()
	for _, ch := range s.logSubs {
		select {
		case ch <- msg:
		default:
		}
	}
	s.subMu.RUnlock()
}

func (s *Server) handleNewApiChunk(chunk *madtomv1.NoxApiCallChunk) {
	if s.cfg.StoreRawLogs {
		_ = s.walSpool.WriteApiCallChunk(chunk)
	}
	raw, _ := proto.Marshal(chunk)
	s.logBuffer.Push(&buffer.Chunk{
		ID:        chunk.ChunkId,
		Timestamp: chunk.LastTsUnixNano,
		Payload:   raw,
		RawSize:   chunk.RawSize,
	})

	atomic.AddUint64(&s.batchesTotal, 1)
	atomic.AddUint64(&s.zstdBatches, 1)
	atomic.AddUint64(&s.zstdBytes, uint64(len(chunk.ZstdRecords)))
	atomic.AddUint64(&s.decodedBytes, uint64(chunk.RawSize))

	msg := &madtomv1.NoxLogApiChunk{
		NodeId:            s.cfg.NodeID,
		ChunkId:           chunk.ChunkId,
		TimestampUnixNano: chunk.LastTsUnixNano,
		ZstdPayload:       raw,
		DecodedSize:       chunk.RawSize,
		DroppedChunks:     0,
		ChunkKind:         "api",
	}

	s.subMu.RLock()
	for _, ch := range s.logSubs {
		select {
		case ch <- msg:
		default:
		}
	}
	s.subMu.RUnlock()
}

func (s *Server) stateTickerLoop() {
	ticker := time.NewTicker(1 * time.Second)
	defer ticker.Stop()

	for range ticker.C {
		if atomic.LoadInt64(&s.stateSubscribers) == 0 {
			continue
		}

		s.mu.Lock()
		if len(s.pendingTelemetry) == 0 && len(s.pendingJobEvents) == 0 && len(s.pendingTurnEvents) == 0 {
			s.pendingTelemetry = append(s.pendingTelemetry, &madtomv1.NoxTelemetrySample{
				TimestampUnixNano: time.Now().UnixNano(),
				Metrics: map[string]float64{
					"probe_heartbeat": 1.0,
				},
			})
		}

		payload := &madtomv1.NoxStateChunkPayload{
			TelemetrySamples: s.pendingTelemetry,
			JobEvents:        s.pendingJobEvents,
			LiveTurnEvents:   s.pendingTurnEvents,
		}
		s.pendingTelemetry = nil
		s.pendingJobEvents = nil
		s.pendingTurnEvents = nil
		s.mu.Unlock()

		zstdData, rawSize, err := s.stateChunker.CompressStatePayload(payload)
		if err == nil {
			atomic.AddUint64(&s.batchesTotal, 1)
			atomic.AddUint64(&s.zstdBatches, 1)
			atomic.AddUint64(&s.zstdBytes, uint64(len(zstdData)))
			atomic.AddUint64(&s.decodedBytes, uint64(rawSize))

			id := atomic.AddUint64(&s.stateChunkIDCounter, 1)
			now := time.Now().UnixNano()
			s.stateBuffer.Push(&buffer.Chunk{
				ID:        id,
				Timestamp: now,
				Payload:   zstdData,
				RawSize:   rawSize,
			})

			msg := &madtomv1.NoxStateChunk{
				NodeId:            s.cfg.NodeID,
				ChunkId:           id,
				TimestampUnixNano: now,
				ZstdPayload:       zstdData,
				DecodedSize:       rawSize,
				DroppedChunks:     0,
			}

			s.subMu.RLock()
			for _, ch := range s.stateSubs {
				select {
				case ch <- msg:
				default:
				}
			}
			s.subMu.RUnlock()
		}
	}
}

func (s *Server) logFlushTickerLoop() {
	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()

	for range ticker.C {
		chunk, err := s.logChunker.FlushUnsealed(2 * time.Second)
		if err == nil && chunk != nil {
			s.handleNewLogChunk(chunk)
		}
		apiChunk, err := s.apiChunker.FlushUnsealed(2 * time.Second)
		if err == nil && apiChunk != nil {
			s.handleNewApiChunk(apiChunk)
		}
	}
}

func (s *Server) syncBridgeMode() {
	stateSub := atomic.LoadInt64(&s.stateSubscribers) > 0
	logSub := atomic.LoadInt64(&s.logSubscribers) > 0
	storeEnabled := s.cfg.StoreRawLogs
	s.bridgeClient.SetCaptureMode(stateSub, logSub, storeEnabled)
}

// Authentication interceptors
func (s *Server) verifyAuth(ctx context.Context) error {
	return s.verifier.VerifyIncoming(ctx)
}

func (s *Server) unaryAuthInterceptor(ctx context.Context, req interface{}, info *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (interface{}, error) {
	if err := s.verifyAuth(ctx); err != nil {
		return nil, err
	}
	return handler(ctx, req)
}

func (s *Server) streamAuthInterceptor(srv interface{}, ss grpc.ServerStream, info *grpc.StreamServerInfo, handler grpc.StreamHandler) error {
	if err := s.verifyAuth(ss.Context()); err != nil {
		return err
	}
	return handler(srv, ss)
}

// gRPC Implementations
func (s *Server) GetNodeStatus(ctx context.Context, req *madtomv1.NoxNodeStatusRequest) (*madtomv1.NoxNodeStatusResponse, error) {
	spoolBytes, maxSpool, _, _, _, _ := s.walSpool.Stats()
	curState, _, _, _ := s.stateBuffer.Stats()
	curLog, _, _, _ := s.logBuffer.Stats()

	resp := &madtomv1.NoxNodeStatusResponse{
		NodeId:            s.cfg.NodeID,
		Hostname:          s.cfg.NodeID,
		NoxchatVersion:    "1.0.0",
		ProbeVersion:      "1.0.0",
		PythonVersion:     "3.12",
		BotRunning:        true,
		StoreRawLogs:      s.cfg.StoreRawLogs,
		MaxSpoolBytes:     maxSpool,
		CurrentSpoolBytes: spoolBytes,
		StateBufferBytes:  uint64(curState),
		LogBufferBytes:    uint64(curLog),
		TransportStats: []*madtomv1.TransportCompressionStats{
			{
				NodeId:           s.cfg.NodeID,
				Mode:             "ZSTD_STREAM",
				Batches:          atomic.LoadUint64(&s.batchesTotal),
				ZstdBatches:      atomic.LoadUint64(&s.zstdBatches),
				ZstdBytes:        atomic.LoadUint64(&s.zstdBytes),
				DecodedZstdBytes: atomic.LoadUint64(&s.decodedBytes),
				RawBytes:         atomic.LoadUint64(&s.rawBytesPassed),
				LastSeenUnixNano: time.Now().UnixNano(),
			},
		},
	}
	return s.encodeResponse(ctx, resp)
}

func (s *Server) UpdateControlConfig(ctx context.Context, req *madtomv1.NoxUpdateControlConfigRequest) (*madtomv1.NoxNodeStatusResponse, error) {
	s.cfg.StoreRawLogs = req.StoreRawLogs
	if req.MaxSpoolBytes > 0 {
		s.cfg.MaxSpoolBytes = int64(req.MaxSpoolBytes)
	}
	s.syncBridgeMode()
	return s.GetNodeStatus(ctx, &madtomv1.NoxNodeStatusRequest{NodeId: req.NodeId})
}

func (s *Server) SubscribeState(req *madtomv1.NoxStateSubscriptionRequest, stream madtomv1.NoxChatControlService_SubscribeStateServer) error {
	ch := make(chan *madtomv1.NoxStateChunk, 64)
	id := atomic.AddUint64(&s.subIDCounter, 1)

	s.subMu.Lock()
	s.stateSubs[id] = ch
	atomic.AddInt64(&s.stateSubscribers, 1)
	s.syncBridgeMode()
	s.subMu.Unlock()

	defer func() {
		s.subMu.Lock()
		delete(s.stateSubs, id)
		if atomic.AddInt64(&s.stateSubscribers, -1) == 0 {
			s.stateBuffer.ClearAndReset()
			s.syncBridgeMode()
		}
		s.subMu.Unlock()
	}()

	ctx := stream.Context()
	for {
		select {
		case <-ctx.Done():
			return nil
		case msg, ok := <-ch:
			if !ok {
				return nil
			}
			if err := stream.Send(msg); err != nil {
				return err
			}
		}
	}
}

func (s *Server) SubscribeLogsAndApi(req *madtomv1.NoxLogApiSubscriptionRequest, stream madtomv1.NoxChatControlService_SubscribeLogsAndApiServer) error {
	ch := make(chan *madtomv1.NoxLogApiChunk, 128)
	id := atomic.AddUint64(&s.subIDCounter, 1)

	s.subMu.Lock()
	s.logSubs[id] = ch
	atomic.AddInt64(&s.logSubscribers, 1)
	s.syncBridgeMode()
	s.subMu.Unlock()

	defer func() {
		s.subMu.Lock()
		delete(s.logSubs, id)
		if atomic.AddInt64(&s.logSubscribers, -1) == 0 {
			s.logBuffer.ClearAndReset()
			s.syncBridgeMode()
		}
		s.subMu.Unlock()
	}()

	ctx := stream.Context()
	for {
		select {
		case <-ctx.Done():
			return nil
		case msg, ok := <-ch:
			if !ok {
				return nil
			}
			if err := stream.Send(msg); err != nil {
				return err
			}
		}
	}
}

// Telemetry & LTTB Historical Queries
func (s *Server) QueryTelemetryRange(ctx context.Context, req *madtomv1.RangeQueryRequest) (*madtomv1.RangeQueryResponse, error) {
	s.mu.Lock()
	pts := s.telemetryHistory[req.MetricName]
	var filtered []historyPoint
	for _, p := range pts {
		if p.ts >= req.StartTimeUnixNano && p.ts <= req.EndTimeUnixNano {
			filtered = append(filtered, p)
		}
	}
	s.mu.Unlock()

	var points []*madtomv1.TimeSeriesPoint
	target := int(req.TargetPoints)
	if target <= 0 || target >= len(filtered) {
		for _, p := range filtered {
			points = append(points, &madtomv1.TimeSeriesPoint{
				TimestampUnixNano: p.ts,
				Value:             p.val,
				MinValue:          p.val,
				MaxValue:          p.val,
			})
		}
	} else {
		// Basic downsampling
		step := len(filtered) / target
		if step == 0 {
			step = 1
		}
		for i := 0; i < len(filtered); i += step {
			p := filtered[i]
			points = append(points, &madtomv1.TimeSeriesPoint{
				TimestampUnixNano: p.ts,
				Value:             p.val,
				MinValue:          p.val,
				MaxValue:          p.val,
			})
		}
	}

	resp := &madtomv1.RangeQueryResponse{
		NodeId:     s.cfg.NodeID,
		MetricName: req.MetricName,
		Points:     points,
	}
	return s.encodeRangeResponse(ctx, resp)
}

func (s *Server) GetSpoolStats(ctx context.Context, req *madtomv1.NoxSpoolStatsRequest) (*madtomv1.NoxSpoolStatsResponse, error) {
	spoolBytes, maxSpool, logs, apis, oldest, newest := s.walSpool.Stats()
	resp := &madtomv1.NoxSpoolStatsResponse{
		NodeId:           s.cfg.NodeID,
		TotalSpoolBytes:  spoolBytes,
		MaxSpoolBytes:    maxSpool,
		TotalLogRecords:  logs,
		TotalApiCalls:    apis,
		OldestTsUnixNano: oldest,
		NewestTsUnixNano: newest,
	}
	return encodeZstdEnvelope(s, ctx, resp)
}

func (s *Server) SearchLogs(ctx context.Context, req *madtomv1.NoxSearchLogsRequest) (*madtomv1.NoxSearchLogsResponse, error) {
	if !s.cfg.LogSearchEnabled {
		return nil, status.Error(codes.Unimplemented, "log search disabled")
	}
	if req.NodeId != s.cfg.NodeID {
		return nil, status.Error(codes.InvalidArgument, "node_id does not match this probe")
	}
	s.mu.Lock()
	if s.searchSlots == nil {
		s.searchSlots = make(chan struct{}, s.cfg.LogSearchConcurrency)
	}
	slots := s.searchSlots
	s.mu.Unlock()
	select {
	case slots <- struct{}{}:
		defer func() { <-slots }()
	default:
		return nil, status.Error(codes.ResourceExhausted, "another log search is running")
	}
	limits := spool.DefaultSearchLimits()
	limits.MaxResults = s.cfg.LogSearchMaxResults
	limits.MaxQueryBytes = s.cfg.LogSearchMaxQueryBytes
	limits.ChunkBytes = s.cfg.LogSearchChunkBytes
	limits.PageBytes = s.cfg.LogSearchPageBytes
	limits.PageTime = time.Duration(s.cfg.LogSearchPageMs) * time.Millisecond
	limits.CursorTTL = time.Duration(s.cfg.LogSearchCursorSeconds) * time.Second
	resp, err := s.walSpool.SearchLogs(ctx, req, limits)
	if err != nil {
		return nil, err
	}
	return encodeZstdEnvelope(s, ctx, resp)
}

func (s *Server) QueryLogChunks(ctx context.Context, req *madtomv1.LogChunkQuery) (*madtomv1.LogChunkResponse, error) {
	chunks, hasMore, err := s.walSpool.QueryLogChunks(req.StartChunkId, req.MaxChunks)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "wal log query: %v", err)
	}
	if req.SummaryOnly {
		headers := make([]*madtomv1.LogChunk, 0, len(chunks))
		for _, c := range chunks {
			headers = append(headers, &madtomv1.LogChunk{NodeId: c.NodeId, ChunkId: c.ChunkId,
				FirstSeq: c.FirstSeq, LastSeq: c.LastSeq, RecordCount: c.RecordCount,
				FirstTsUnixNano: c.FirstTsUnixNano, LastTsUnixNano: c.LastTsUnixNano, Sealed: c.Sealed})
		}
		chunks = headers
	}
	resp := &madtomv1.LogChunkResponse{
		NodeId:  s.cfg.NodeID,
		Chunks:  chunks,
		HasMore: hasMore,
	}
	return encodeZstdEnvelope(s, ctx, resp)
}

func (s *Server) QueryApiCallChunks(ctx context.Context, req *madtomv1.NoxApiCallChunkQuery) (*madtomv1.NoxApiCallChunkResponse, error) {
	if chunk, err := s.apiChunker.FlushUnsealed(0); err == nil && chunk != nil {
		s.handleNewApiChunk(chunk)
	}
	chunks, hasMore, err := s.walSpool.QueryApiCallChunks(req.StartChunkId, req.MaxChunks, req.SummaryOnly)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "wal api query: %v", err)
	}
	resp := &madtomv1.NoxApiCallChunkResponse{
		NodeId:  s.cfg.NodeID,
		Chunks:  chunks,
		HasMore: hasMore,
	}
	return encodeZstdEnvelope(s, ctx, resp)
}

func (s *Server) QueryChatTrace(ctx context.Context, req *madtomv1.NoxChatTraceRequest) (*madtomv1.NoxChatTraceResponse, error) {
	if req.NodeId != s.cfg.NodeID {
		return nil, status.Error(codes.InvalidArgument, "node_id does not match this probe")
	}
	if (req.ChatId == nil && strings.TrimSpace(req.ChatName) == "") || req.Limit > 200 {
		return nil, status.Error(codes.InvalidArgument, "chat_id or chat_name is required and limit must not exceed 200")
	}
	s.mu.Lock()
	if s.chatTraceSlots == nil {
		s.chatTraceSlots = make(chan struct{}, 2)
	}
	slots := s.chatTraceSlots
	s.mu.Unlock()
	select {
	case slots <- struct{}{}:
		defer func() { <-slots }()
	default:
		return nil, status.Error(codes.ResourceExhausted, "another chat trace query is running")
	}
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}
	// Include recent calls that have not yet reached the periodic 2-second snapshot.
	if chunk, err := s.apiChunker.FlushUnsealed(0); err == nil && chunk != nil {
		s.handleNewApiChunk(chunk)
	}
	response, err := s.walSpool.QueryChatTrace(ctx, req)
	if err != nil {
		return nil, err
	}
	if !s.cfg.StoreRawLogs {
		response.Notice += " Store is currently disabled; new execution history is not retained."
	}
	return encodeZstdEnvelope(s, ctx, response)
}

func (s *Server) GetApiCallDetail(ctx context.Context, req *madtomv1.NoxApiCallDetailRequest) (*madtomv1.NoxRawApiCallRecord, error) {
	if req.CallId == "" {
		return nil, status.Error(codes.InvalidArgument, "call_id cannot be empty")
	}

	rec, err := s.walSpool.GetApiCall(req.CallId)
	if err != nil {
		return nil, status.Errorf(codes.NotFound, "api call not found: %v", err)
	}
	return rec, nil
}

func (s *Server) ClearSpool(ctx context.Context, req *madtomv1.NoxClearSpoolRequest) (*madtomv1.NoxClearSpoolResponse, error) {
	if err := s.walSpool.Clear(); err != nil {
		return &madtomv1.NoxClearSpoolResponse{
			Success: false,
			Message: fmt.Sprintf("failed to clear WAL spool: %v", err),
		}, nil
	}

	s.logBuffer.ClearAndReset()
	s.stateBuffer.ClearAndReset()

	log.Printf("[NoxChat_Probe] WAL spool files, active log buffers, and state buffers completely cleared (clean reset) for node %s", req.NodeId)
	return &madtomv1.NoxClearSpoolResponse{
		Success: true,
		Message: "WAL spool files, active logs, and API call buffers cleared successfully.",
	}, nil
}

// Proxied RPC calls to Python
func (s *Server) SendDirectMessage(req *madtomv1.NoxDirectMessageRequest, stream madtomv1.NoxChatControlService_SendDirectMessageServer) error {
	reply, err := s.bridgeClient.CallRpc("send_direct_message", req, 60*time.Second)
	if err != nil {
		return status.Errorf(codes.Internal, "%v", err)
	}
	var events []*madtomv1.NoxDirectTurnEvent
	if err := json.Unmarshal(reply.Payload, &events); err != nil {
		return status.Errorf(codes.Internal, "parse direct chat events: %v", err)
	}
	for _, ev := range events {
		if err := stream.Send(ev); err != nil {
			return err
		}
	}
	return nil
}

func (s *Server) GetChatHistory(ctx context.Context, req *madtomv1.NoxChatHistoryRequest) (*madtomv1.NoxChatHistoryResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_chat_history", req, 10*time.Second)
	if err != nil {
		if ctx.Err() != nil {
			return nil, status.FromContextError(ctx.Err()).Err()
		}
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxChatHistoryResponse
	if err := json.Unmarshal(reply.Payload, &resp); err != nil {
		return nil, status.Errorf(codes.Internal, "decode chat history: %v", err)
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) ListChats(ctx context.Context, req *madtomv1.NoxListChatsRequest) (*madtomv1.NoxListChatsResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "list_chats", req, 10*time.Second)
	if err != nil {
		if ctx.Err() != nil {
			return nil, status.FromContextError(ctx.Err()).Err()
		}
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListChatsResponse
	if err := json.Unmarshal(reply.Payload, &resp); err != nil {
		return nil, status.Errorf(codes.Internal, "decode chat list: %v", err)
	}
	if chunk, err := s.apiChunker.FlushUnsealed(0); err == nil && chunk != nil {
		s.handleNewApiChunk(chunk)
	}
	byID, byName := s.walSpool.ChatCallStats()
	for _, chat := range resp.Chats {
		if chat == nil {
			continue
		}
		if stat, ok := byID[chat.ChatId]; ok {
			chat.ApiCallCount = stat.CallCount
			chat.LastAgentCallUnixNano = stat.LastCallUnixNano
		}
		if normTitle := strings.ToLower(strings.TrimSpace(chat.Title)); normTitle != "" {
			if stat, ok := byName[normTitle]; ok {
				if chat.ApiCallCount == 0 {
					chat.ApiCallCount = stat.CallCount
				}
				if stat.LastCallUnixNano > chat.LastAgentCallUnixNano {
					chat.LastAgentCallUnixNano = stat.LastCallUnixNano
				}
			}
		}
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) QueryMemories(ctx context.Context, req *madtomv1.NoxMemoryQueryRequest) (*madtomv1.NoxMemoryQueryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("query_memories", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMemoryQueryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) SearchMemoriesHybrid(ctx context.Context, req *madtomv1.NoxHybridSearchRequest) (*madtomv1.NoxHybridSearchResponse, error) {
	reply, err := s.bridgeClient.CallRpc("search_memories_hybrid", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHybridSearchResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) UpsertMemory(ctx context.Context, req *madtomv1.NoxUpsertMemoryRequest) (*madtomv1.NoxMemoryMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("upsert_memory", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMemoryMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) DeleteMemory(ctx context.Context, req *madtomv1.NoxDeleteMemoryRequest) (*madtomv1.NoxMemoryMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("delete_memory", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMemoryMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

// Tips
func (s *Server) ListTips(ctx context.Context, req *madtomv1.NoxTipListRequest) (*madtomv1.NoxTipListResponse, error) {
	reply, err := s.bridgeClient.CallRpc("list_tips", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxTipListResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) SearchTips(ctx context.Context, req *madtomv1.NoxTipSearchRequest) (*madtomv1.NoxTipSearchResponse, error) {
	reply, err := s.bridgeClient.CallRpc("search_tips", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxTipSearchResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) AddTips(ctx context.Context, req *madtomv1.NoxAddTipsRequest) (*madtomv1.NoxAddTipsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("add_tips", req, 30*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxAddTipsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) UpdateTip(ctx context.Context, req *madtomv1.NoxUpdateTipRequest) (*madtomv1.NoxTipMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("update_tip", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxTipMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) DeleteTips(ctx context.Context, req *madtomv1.NoxDeleteTipsRequest) (*madtomv1.NoxTipMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("delete_tips", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxTipMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) TestTips(ctx context.Context, req *madtomv1.NoxTestTipsRequest) (*madtomv1.NoxTestTipsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("test_tips", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxTestTipsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

// Sandbox administration: pass through to the Python sandbox_* control RPCs.
func (s *Server) ListSandboxVms(ctx context.Context, req *madtomv1.NoxSandboxListVmsRequest) (*madtomv1.NoxSandboxListVmsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_list_vms", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxListVmsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) SandboxVmAction(ctx context.Context, req *madtomv1.NoxSandboxVmActionRequest) (*madtomv1.NoxSandboxVmActionResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_vm_action", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxVmActionResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListSandboxFiles(ctx context.Context, req *madtomv1.NoxSandboxListFilesRequest) (*madtomv1.NoxSandboxListFilesResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_list_files", req, 30*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxListFilesResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ReadSandboxText(ctx context.Context, req *madtomv1.NoxSandboxReadTextRequest) (*madtomv1.NoxSandboxReadTextResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_read_text", req, 30*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxReadTextResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) PreviewSandboxFile(ctx context.Context, req *madtomv1.NoxSandboxPreviewFileRequest) (*madtomv1.NoxSandboxPreviewFileResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_preview", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxPreviewFileResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListSandboxBackups(ctx context.Context, req *madtomv1.NoxSandboxListBackupsRequest) (*madtomv1.NoxSandboxListBackupsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_backups", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxListBackupsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) RestoreSandboxBackup(ctx context.Context, req *madtomv1.NoxSandboxRestoreBackupRequest) (*madtomv1.NoxSandboxRestoreBackupResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_backups", req, 120*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxRestoreBackupResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListSandboxLibraryRequests(ctx context.Context, req *madtomv1.NoxSandboxListLibraryRequestsRequest) (*madtomv1.NoxSandboxListLibraryRequestsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_library_requests", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxListLibraryRequestsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) DecideSandboxLibrary(ctx context.Context, req *madtomv1.NoxSandboxDecideLibraryRequest) (*madtomv1.NoxSandboxDecideLibraryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_decide_library", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxDecideLibraryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) GetSandboxImageStatus(ctx context.Context, req *madtomv1.NoxSandboxImageStatusRequest) (*madtomv1.NoxSandboxImageStatusResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_image_status", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxImageStatusResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) RebuildSandboxImage(ctx context.Context, req *madtomv1.NoxSandboxRebuildImageRequest) (*madtomv1.NoxSandboxRebuildImageResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_rebuild_image", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxRebuildImageResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListSandboxRuns(ctx context.Context, req *madtomv1.NoxSandboxListRunsRequest) (*madtomv1.NoxSandboxListRunsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("sandbox_runs", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSandboxListRunsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) GetAutomationStatus(ctx context.Context, req *madtomv1.NoxAutomationStatusRequest) (*madtomv1.NoxAutomationStatusResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_status", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxAutomationStatusResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListPreprocessors(ctx context.Context, req *madtomv1.NoxListPreprocessorsRequest) (*madtomv1.NoxListPreprocessorsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_list_preprocessors", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListPreprocessorsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) GetPreprocessor(ctx context.Context, req *madtomv1.NoxGetPreprocessorRequest) (*madtomv1.NoxGetPreprocessorResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_get_preprocessor", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxGetPreprocessorResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) PreprocessorAction(ctx context.Context, req *madtomv1.NoxPreprocessorActionRequest) (*madtomv1.NoxAutomationActionResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_preprocessor_action", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxAutomationActionResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListPreprocessorLogs(ctx context.Context, req *madtomv1.NoxListPreprocessorLogsRequest) (*madtomv1.NoxListPreprocessorLogsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_preprocessor_logs", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListPreprocessorLogsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListCrons(ctx context.Context, req *madtomv1.NoxListCronsRequest) (*madtomv1.NoxListCronsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_list_crons", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListCronsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) CronAction(ctx context.Context, req *madtomv1.NoxCronActionRequest) (*madtomv1.NoxAutomationActionResponse, error) {
	reply, err := s.bridgeClient.CallRpc("automation_cron_action", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxAutomationActionResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListUserQuotas(ctx context.Context, req *madtomv1.NoxListUserQuotasRequest) (*madtomv1.NoxListUserQuotasResponse, error) {
	reply, err := s.bridgeClient.CallRpc("quota_list_users", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListUserQuotasResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) GetUserQuota(ctx context.Context, req *madtomv1.NoxGetUserQuotaRequest) (*madtomv1.NoxGetUserQuotaResponse, error) {
	reply, err := s.bridgeClient.CallRpc("quota_get_user", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxGetUserQuotaResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) SetUserQuota(ctx context.Context, req *madtomv1.NoxSetUserQuotaRequest) (*madtomv1.NoxSetUserQuotaResponse, error) {
	reply, err := s.bridgeClient.CallRpc("quota_set_user", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSetUserQuotaResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListHotMemories(ctx context.Context, req *madtomv1.NoxHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("list_hot_memories", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		if err2 := json.Unmarshal(reply.Payload, &resp); err2 != nil {
			return nil, status.Errorf(codes.Internal, "failed to decode hot memories: %v", err2)
		}
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) UpsertHotMemory(ctx context.Context, req *madtomv1.NoxUpsertHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("upsert_hot_memory", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		if err2 := json.Unmarshal(reply.Payload, &resp); err2 != nil {
			return nil, status.Errorf(codes.Internal, "failed to decode hot memories: %v", err2)
		}
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) DeleteHotMemory(ctx context.Context, req *madtomv1.NoxDeleteHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("delete_hot_memory", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		if err2 := json.Unmarshal(reply.Payload, &resp); err2 != nil {
			return nil, status.Errorf(codes.Internal, "failed to decode hot memories: %v", err2)
		}
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) QueryMediaLibrary(ctx context.Context, req *madtomv1.NoxMediaQueryRequest) (*madtomv1.NoxMediaQueryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("query_media_library", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMediaQueryResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) GetMediaAssetPreview(ctx context.Context, req *madtomv1.NoxMediaPreviewRequest) (*madtomv1.NoxMediaPreviewResponse, error) {
	reply, err := s.bridgeClient.CallRpc("get_media_asset_preview", req, 25*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMediaPreviewResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) MutateMediaItem(ctx context.Context, req *madtomv1.NoxMediaMutationRequest) (*madtomv1.NoxMediaMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("mutate_media_item", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMediaMutationResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) ManageMediaPin(ctx context.Context, req *madtomv1.NoxMediaPinRequest) (*madtomv1.NoxMediaMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("manage_media_pin", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMediaMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) TriggerStickerPackAnalysis(ctx context.Context, req *madtomv1.NoxAnalyzePackRequest) (*madtomv1.NoxAnalyzePackResponse, error) {
	reply, err := s.bridgeClient.CallRpc("trigger_sticker_pack_analysis", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxAnalyzePackResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ForceAddSticker(ctx context.Context, req *madtomv1.NoxForceAddStickerRequest) (*madtomv1.NoxMediaMutationResponse, error) {
	reply, err := s.bridgeClient.CallRpc("force_add_sticker", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMediaMutationResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) GetActiveJobs(ctx context.Context, req *madtomv1.NoxActiveJobsRequest) (*madtomv1.NoxActiveJobsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("get_active_jobs", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxActiveJobsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) InspectVectors(ctx context.Context, req *madtomv1.NoxVectorInspectRequest) (*madtomv1.NoxVectorInspectResponse, error) {
	reply, err := s.bridgeClient.CallRpc("inspect_vectors", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxVectorInspectResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return encodeZstdEnvelope(s, ctx, &resp)
}

// Chat Images (Image Studio) — passthroughs to the NoxChat Python RPC handlers.
func (s *Server) ListChatImages(ctx context.Context, req *madtomv1.NoxListChatImagesRequest) (*madtomv1.NoxListChatImagesResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "list_chat_images", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListChatImagesResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) GetChatImage(ctx context.Context, req *madtomv1.NoxGetChatImageRequest) (*madtomv1.NoxGetChatImageResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_chat_image", req, 25*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxGetChatImageResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) GetImageStorageStats(ctx context.Context, req *madtomv1.NoxImageStorageStatsRequest) (*madtomv1.NoxImageStorageStatsResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_image_storage_stats", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxImageStorageStatsResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) SweepChatImages(ctx context.Context, req *madtomv1.NoxSweepChatImagesRequest) (*madtomv1.NoxSweepChatImagesResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "sweep_chat_images", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSweepChatImagesResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

// Chat Files (Unified Workspace) — passthroughs to the NoxChat Python RPC handlers.
func (s *Server) ListChatFiles(ctx context.Context, req *madtomv1.NoxListChatFilesRequest) (*madtomv1.NoxListChatFilesResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "list_chat_files", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListChatFilesResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) GetChatFilePreview(ctx context.Context, req *madtomv1.NoxGetChatFilePreviewRequest) (*madtomv1.NoxGetChatFilePreviewResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_chat_file_preview", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxGetChatFilePreviewResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) GetChatFileBytes(ctx context.Context, req *madtomv1.NoxGetChatFileBytesRequest) (*madtomv1.NoxGetChatFileBytesResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_chat_file_bytes", req, 60*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxGetChatFileBytesResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) GetWorkspaceStorageStats(ctx context.Context, req *madtomv1.NoxWorkspaceStorageStatsRequest) (*madtomv1.NoxWorkspaceStorageStatsResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "get_workspace_storage_stats", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxWorkspaceStorageStatsResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) SweepChatFiles(ctx context.Context, req *madtomv1.NoxSweepChatFilesRequest) (*madtomv1.NoxSweepChatFilesResponse, error) {
	reply, err := s.bridgeClient.CallRpcContext(ctx, "sweep_chat_files", req, 120*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxSweepChatFilesResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

func (s *Server) QueryUserProfiles(ctx context.Context, req *madtomv1.NoxUserProfileQueryRequest) (*madtomv1.NoxUserProfileQueryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("query_user_profiles", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxUserProfileQueryResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) SearchUserProfiles(ctx context.Context, req *madtomv1.NoxUserProfileSearchRequest) (*madtomv1.NoxUserProfileSearchResponse, error) {
	reply, err := s.bridgeClient.CallRpc("search_user_profiles", req, 20*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxUserProfileSearchResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return encodeZstdEnvelope(s, ctx, &resp)
}

func (s *Server) GetUserProfilePhoto(ctx context.Context, req *madtomv1.NoxUserProfilePhotoRequest) (*madtomv1.NoxUserProfilePhotoResponse, error) {
	reply, err := s.bridgeClient.CallRpc("get_user_profile_photo", req, 25*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxUserProfilePhotoResponse
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(reply.Payload, &resp); err != nil {
		_ = json.Unmarshal(reply.Payload, &resp)
	}
	return &resp, nil
}

// Zstd Response Envelopes
func encodeZstdEnvelope[T proto.Message](s *Server, ctx context.Context, resp T) (T, error) {
	raw, err := proto.Marshal(resp)
	if err != nil || len(raw) == 0 {
		return resp, nil
	}
	atomic.AddUint64(&s.batchesTotal, 1)
	md, _ := metadata.FromIncomingContext(ctx)
	if len(md.Get("madtom-accept-zstd")) == 0 {
		atomic.AddUint64(&s.rawBytesPassed, uint64(len(raw)))
		return resp, nil
	}

	m := resp.ProtoReflect()
	payloadField := m.Descriptor().Fields().ByNumber(100)
	sizeField := m.Descriptor().Fields().ByNumber(101)
	if payloadField == nil || sizeField == nil {
		atomic.AddUint64(&s.rawBytesPassed, uint64(len(raw)))
		return resp, nil
	}

	var compressed []byte
	s.zstdEncMu.Lock()
	if s.zstdEnc != nil {
		compressed = s.zstdEnc.EncodeAll(raw, nil)
	}
	s.zstdEncMu.Unlock()
	if len(compressed) == 0 {
		enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
		if err != nil {
			atomic.AddUint64(&s.rawBytesPassed, uint64(len(raw)))
			return resp, nil
		}
		compressed = enc.EncodeAll(raw, nil)
		_ = enc.Close()
	}

	if len(compressed) < len(raw) {
		atomic.AddUint64(&s.zstdBatches, 1)
		atomic.AddUint64(&s.zstdBytes, uint64(len(compressed)))
		atomic.AddUint64(&s.decodedBytes, uint64(len(raw)))

		envelope := m.New()
		envelope.Set(payloadField, protoreflect.ValueOfBytes(compressed))
		envelope.Set(sizeField, protoreflect.ValueOfUint32(uint32(len(raw))))
		return envelope.Interface().(T), nil
	}

	atomic.AddUint64(&s.rawBytesPassed, uint64(len(raw)))
	return resp, nil
}

func (s *Server) encodeResponse(ctx context.Context, resp *madtomv1.NoxNodeStatusResponse) (*madtomv1.NoxNodeStatusResponse, error) {
	return encodeZstdEnvelope(s, ctx, resp)
}

func (s *Server) encodeRangeResponse(ctx context.Context, resp *madtomv1.RangeQueryResponse) (*madtomv1.RangeQueryResponse, error) {
	return encodeZstdEnvelope(s, ctx, resp)
}
