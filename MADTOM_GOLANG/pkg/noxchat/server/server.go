package server

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"sync"
	"sync/atomic"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	"github.com/DarkDuck007/madtom/pkg/noxchat/bridge"
	"github.com/DarkDuck007/madtom/pkg/noxchat/buffer"
	"github.com/DarkDuck007/madtom/pkg/noxchat/chunker"
	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	"github.com/DarkDuck007/madtom/pkg/noxchat/spool"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type Server struct {
	madtomv1.UnimplementedNoxChatControlServiceServer

	cfg          *config.ProbeConfig
	keyPair      *auth.KeyPair
	tlsConfig    *tls.Config
	grpcServer   *grpc.Server
	bridgeClient *bridge.Client
	stateBuffer  *buffer.RingChunkBuffer
	logBuffer    *buffer.RingChunkBuffer
	walSpool     *spool.WalSpool
	logChunker   *chunker.LogChunker
	apiChunker   *chunker.ApiCallChunker
	stateChunker *chunker.StateChunker

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
}

type historyPoint struct {
	ts    int64
	val   double
}

type double = float64

func NewServer(cfg *config.ProbeConfig, keyPair *auth.KeyPair, tlsConfig *tls.Config) (*Server, error) {
	stateBuf := buffer.NewRingChunkBuffer(cfg.StateBufferBytes)
	logBuf := buffer.NewRingChunkBuffer(cfg.LogBufferBytes)

	wal, err := spool.NewWalSpool(cfg.SpoolDir, cfg.MaxSpoolBytes)
	if err != nil {
		return nil, fmt.Errorf("failed to init WAL spool: %w", err)
	}

	startLogSeq, startApiSeq := wal.MaxSeqs()

	lChunker, err := chunker.NewLogChunker(cfg.NodeID, startLogSeq)
	if err != nil {
		return nil, err
	}

	aChunker, err := chunker.NewApiCallChunker(cfg.NodeID, startApiSeq)
	if err != nil {
		return nil, err
	}

	sChunker, err := chunker.NewStateChunker(cfg.NodeID)
	if err != nil {
		return nil, err
	}

	s := &Server{
		cfg:              cfg,
		keyPair:          keyPair,
		tlsConfig:        tlsConfig,
		stateBuffer:      stateBuf,
		logBuffer:        logBuf,
		walSpool:         wal,
		logChunker:       lChunker,
		apiChunker:       aChunker,
		stateChunker:     sChunker,
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
	if s.keyPair == nil {
		return nil
	}
	md, ok := metadata.FromIncomingContext(ctx)
	if !ok {
		return status.Errorf(codes.Unauthenticated, "missing metadata")
	}

	ts := firstMd(md, "x-nox-auth-ts")
	nonce := firstMd(md, "x-nox-auth-nonce")
	sig := firstMd(md, "x-nox-auth-sig")

	if ts == "" || nonce == "" || sig == "" {
		// Fallback to bearer token check
		bearer := firstMd(md, "authorization")
		if bearer != "" && s.keyPair.VerifySignature(ts, nonce, bearer) {
			return nil
		}
		return status.Errorf(codes.Unauthenticated, "invalid or missing authentication signature")
	}

	if !s.keyPair.VerifySignature(ts, nonce, sig) {
		return status.Errorf(codes.Unauthenticated, "cryptographic signature verification failed")
	}
	return nil
}

func firstMd(md metadata.MD, key string) string {
	vals := md.Get(key)
	if len(vals) > 0 {
		return vals[0]
	}
	return ""
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
	return &madtomv1.NoxSpoolStatsResponse{
		NodeId:            s.cfg.NodeID,
		TotalSpoolBytes:   spoolBytes,
		MaxSpoolBytes:     maxSpool,
		TotalLogRecords:   logs,
		TotalApiCalls:     apis,
		OldestTsUnixNano:  oldest,
		NewestTsUnixNano:  newest,
	}, nil
}

func (s *Server) QueryLogChunks(ctx context.Context, req *madtomv1.LogChunkQuery) (*madtomv1.LogChunkResponse, error) {
	chunks, hasMore, err := s.walSpool.QueryLogChunks(req.StartChunkId, req.MaxChunks)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "wal log query: %v", err)
	}
	return &madtomv1.LogChunkResponse{
		NodeId:  s.cfg.NodeID,
		Chunks:  chunks,
		HasMore: hasMore,
	}, nil
}

func (s *Server) QueryApiCallChunks(ctx context.Context, req *madtomv1.NoxApiCallChunkQuery) (*madtomv1.NoxApiCallChunkResponse, error) {
	chunks, hasMore, err := s.walSpool.QueryApiCallChunks(req.StartChunkId, req.MaxChunks, req.SummaryOnly)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "wal api query: %v", err)
	}
	return &madtomv1.NoxApiCallChunkResponse{
		NodeId:  s.cfg.NodeID,
		Chunks:  chunks,
		HasMore: hasMore,
	}, nil
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
	reply, err := s.bridgeClient.CallRpc("get_chat_history", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxChatHistoryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) ListChats(ctx context.Context, req *madtomv1.NoxListChatsRequest) (*madtomv1.NoxListChatsResponse, error) {
	reply, err := s.bridgeClient.CallRpc("list_chats", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxListChatsResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) QueryMemories(ctx context.Context, req *madtomv1.NoxMemoryQueryRequest) (*madtomv1.NoxMemoryQueryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("query_memories", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxMemoryQueryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) SearchMemoriesHybrid(ctx context.Context, req *madtomv1.NoxHybridSearchRequest) (*madtomv1.NoxHybridSearchResponse, error) {
	reply, err := s.bridgeClient.CallRpc("search_memories_hybrid", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHybridSearchResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
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

func (s *Server) ListHotMemories(ctx context.Context, req *madtomv1.NoxHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("list_hot_memories", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) UpsertHotMemory(ctx context.Context, req *madtomv1.NoxUpsertHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("upsert_hot_memory", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

func (s *Server) DeleteHotMemory(ctx context.Context, req *madtomv1.NoxDeleteHotMemoryRequest) (*madtomv1.NoxHotMemoryResponse, error) {
	reply, err := s.bridgeClient.CallRpc("delete_hot_memory", req, 10*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxHotMemoryResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
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
	return &resp, nil
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
	return &resp, nil
}

func (s *Server) InspectVectors(ctx context.Context, req *madtomv1.NoxVectorInspectRequest) (*madtomv1.NoxVectorInspectResponse, error) {
	reply, err := s.bridgeClient.CallRpc("inspect_vectors", req, 15*time.Second)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "%v", err)
	}
	var resp madtomv1.NoxVectorInspectResponse
	_ = json.Unmarshal(reply.Payload, &resp)
	return &resp, nil
}

// Zstd Response Envelopes
func (s *Server) encodeResponse(ctx context.Context, resp *madtomv1.NoxNodeStatusResponse) (*madtomv1.NoxNodeStatusResponse, error) {
	md, _ := metadata.FromIncomingContext(ctx)
	if len(md.Get("madtom-accept-zstd")) == 0 {
		return resp, nil
	}
	raw, err := proto.Marshal(resp)
	if err != nil {
		return resp, nil
	}
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return resp, nil
	}
	defer enc.Close()
	compressed := enc.EncodeAll(raw, nil)
	if len(compressed) < len(raw) {
		return &madtomv1.NoxNodeStatusResponse{
			ZstdPayload: compressed,
			DecodedSize: uint32(len(raw)),
		}, nil
	}
	return resp, nil
}

func (s *Server) encodeRangeResponse(ctx context.Context, resp *madtomv1.RangeQueryResponse) (*madtomv1.RangeQueryResponse, error) {
	md, _ := metadata.FromIncomingContext(ctx)
	if len(md.Get("madtom-accept-zstd")) == 0 {
		return resp, nil
	}
	raw, err := proto.Marshal(resp)
	if err != nil {
		return resp, nil
	}
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderLevel(zstd.SpeedDefault))
	if err != nil {
		return resp, nil
	}
	defer enc.Close()
	compressed := enc.EncodeAll(raw, nil)
	if len(compressed) < len(raw) {
		return &madtomv1.RangeQueryResponse{
			ZstdPayload: compressed,
			DecodedSize: uint32(len(raw)),
		}, nil
	}
	return resp, nil
}
