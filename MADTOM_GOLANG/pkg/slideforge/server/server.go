package server

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math"
	"net"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	"github.com/DarkDuck007/madtom/pkg/noxchat/bridge"
	"github.com/DarkDuck007/madtom/pkg/noxchat/chunker"
	"github.com/DarkDuck007/madtom/pkg/noxchat/spool"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/status"
)

const probeVersion = "1.0.0"

type Config struct {
	NodeID         string
	ListenAddr     string
	BotHost        string
	BotPort        int
	StateDir       string
	StoreRawLogs   bool
	MaxSpoolBytes  int64
	MdnsEnabled    bool
	AllowedMethods []string
}

func DefaultConfig() Config {
	nodeID, err := os.Hostname()
	if err != nil || nodeID == "" {
		nodeID = "slideforge-node"
	}
	return Config{
		NodeID:        nodeID,
		ListenAddr:    ":50057",
		BotHost:       "127.0.0.1",
		BotPort:       50058,
		StateDir:      "/var/lib/slideforge-probe",
		MaxSpoolBytes: 1 << 30,
		MdnsEnabled:   true,
		AllowedMethods: []string{
			"get_overview", "list_jobs", "get_job", "list_job_files", "cancel_job", "extend_job", "get_job_file", "get_job_transcript",
			"list_users", "get_user", "set_user_quota", "set_user_banned",
			"list_guides", "get_guide", "put_guide", "list_tips", "upsert_tip", "delete_tip", "preview_prompt",
			"get_config",
			"sandbox_list_vms", "sandbox_stats", "sandbox_vm_action", "sandbox_list_files", "sandbox_read_text", "sandbox_preview",
			"sandbox_backups", "sandbox_restore_backup", "sandbox_library_requests", "sandbox_decide_library",
			"sandbox_image_status", "sandbox_rebuild_image", "sandbox_runs",
		},
	}
}

type Server struct {
	madtomv1.UnimplementedSlideForgeControlServiceServer

	cfg           Config
	allowlist     map[string]struct{}
	hostname      string
	botVersion    string
	pythonVersion string
	fingerprint   string
	startedAt     time.Time
	grpcServer    *grpc.Server
	bridge        *bridge.Client
	wal           *spool.WalSpool
	logChunker    *chunker.LogChunker
	apiChunker    *chunker.ApiCallChunker
	compressor    *chunker.StateChunker
	done          chan struct{}
	flushed       chan struct{}
	stopOnce      sync.Once
	modeMu        sync.Mutex

	mu             sync.Mutex
	storeRawLogs   bool
	logSubscribers int
	apiSubscribers int
	telemetry      []*madtomv1.SfTelemetrySample
	jobs           []*madtomv1.SfJobEvent
	pendingState   madtomv1.SfStateChunkPayload
	pendingLogs    []*madtomv1.LogRecord
	pendingAPI     []*madtomv1.SfApiCallRecord
	stateChunks    liveChunks
	logChunks      liveChunks
	batches        uint64
	zstdBytes      uint64
	decodedBytes   uint64
	lastChunkAt    int64
}

// New initializes the probe and starts connecting to the bot. Start serves gRPC.
func New(cfg Config, verifier *auth.Verifier, tlsCfg *tls.Config) (*Server, error) {
	if cfg.NodeID == "" || cfg.ListenAddr == "" || cfg.StateDir == "" {
		return nil, errors.New("node ID, listen address, and state directory are required")
	}
	if !strings.EqualFold(cfg.BotHost, "localhost") && !net.ParseIP(cfg.BotHost).IsLoopback() {
		return nil, errors.New("bot host must be a loopback address")
	}
	if cfg.BotPort < 1 || cfg.BotPort > 65535 || cfg.MaxSpoolBytes <= 0 {
		return nil, errors.New("invalid bot port or spool limit")
	}
	cfg.AllowedMethods = slices.Clone(cfg.AllowedMethods)
	allowed := make(map[string]struct{}, len(cfg.AllowedMethods))
	for _, method := range cfg.AllowedMethods {
		allowed[method] = struct{}{}
	}

	spoolDir := filepath.Join(cfg.StateDir, "spool")
	if err := os.MkdirAll(spoolDir, 0700); err != nil {
		return nil, fmt.Errorf("create spool directory: %w", err)
	}
	if info, err := os.Lstat(spoolDir); err != nil || !info.IsDir() {
		return nil, fmt.Errorf("invalid spool directory %q", spoolDir)
	}
	if err := os.Chmod(spoolDir, 0700); err != nil {
		return nil, fmt.Errorf("secure spool directory: %w", err)
	}
	wal, err := spool.NewWalSpool(spoolDir, cfg.MaxSpoolBytes)
	if err != nil {
		return nil, fmt.Errorf("open spool: %w", err)
	}
	if err := wal.SetMaxBytes(cfg.MaxSpoolBytes); err != nil {
		wal.Close()
		return nil, fmt.Errorf("apply spool size limit: %w", err)
	}

	logSeq, apiSeq := wal.MaxSeqs()
	logID, apiID := wal.NextChunkIDs()
	logChunker, err := chunker.NewLogChunker(cfg.NodeID, logSeq)
	if err != nil {
		wal.Close()
		return nil, err
	}
	apiChunker, err := chunker.NewApiCallChunker(cfg.NodeID, apiSeq)
	if err != nil {
		logChunker.Close()
		wal.Close()
		return nil, err
	}
	compressor, err := chunker.NewStateChunker(cfg.NodeID)
	if err != nil {
		apiChunker.Close()
		logChunker.Close()
		wal.Close()
		return nil, err
	}
	logChunker.SetNextChunkID(logID)
	apiChunker.SetNextChunkID(apiID)

	hostname, _ := os.Hostname()
	s := &Server{
		cfg: cfg, allowlist: allowed, hostname: hostname, fingerprint: auth.TLSFingerprint(tlsCfg),
		botVersion: os.Getenv("SLIDEFORGE_VERSION"), pythonVersion: os.Getenv("SLIDEFORGE_PYTHON_VERSION"),
		startedAt: time.Now(), wal: wal, logChunker: logChunker, apiChunker: apiChunker,
		compressor: compressor, storeRawLogs: cfg.StoreRawLogs,
		done: make(chan struct{}), flushed: make(chan struct{}),
		stateChunks: newLiveChunks(1 << 20), logChunks: newLiveChunks(8 << 20),
	}
	opts := []grpc.ServerOption{
		grpc.UnaryInterceptor(verifier.UnaryInterceptor),
		grpc.StreamInterceptor(verifier.StreamInterceptor),
		grpc.WaitForHandlers(true),
	}
	if tlsCfg != nil {
		tlsCfg = tlsCfg.Clone()
		tlsCfg.MinVersion = max(tlsCfg.MinVersion, tls.VersionTLS13)
		opts = append(opts, grpc.Creds(credentials.NewTLS(tlsCfg)))
	}
	s.grpcServer = grpc.NewServer(opts...)
	madtomv1.RegisterSlideForgeControlServiceServer(s.grpcServer, s)
	s.bridge = bridge.NewClient(cfg.BotHost, cfg.BotPort, s)
	s.syncBridgeMode()
	s.bridge.Start()
	go s.flushLoop()
	return s, nil
}

func (s *Server) Start() error {
	listener, err := net.Listen("tcp", s.cfg.ListenAddr)
	if err != nil {
		return err
	}
	defer listener.Close()
	log.Printf("[SlideForge_Probe] Serving gRPC on %s", listener.Addr())
	if err := s.grpcServer.Serve(listener); !errors.Is(err, grpc.ErrServerStopped) {
		return err
	}
	return nil
}

func (s *Server) Stop() {
	s.stopOnce.Do(func() {
		close(s.done)
		s.grpcServer.Stop()
		s.bridge.Close()
		<-s.flushed
		s.mu.Lock()
		defer s.mu.Unlock()
		s.flushSpoolLocked()
		s.logChunker.Close()
		s.apiChunker.Close()
		s.compressor.Close()
		if err := s.wal.Close(); err != nil {
			log.Printf("[SlideForge_Probe] Close spool: %v", err)
		}
	})
}

func (s *Server) GetNodeStatus(_ context.Context, _ *madtomv1.SfNodeStatusRequest) (*madtomv1.SfNodeStatusResponse, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	current, limit, _, _, _ := s.wal.ChunkStats()
	return &madtomv1.SfNodeStatusResponse{
		NodeId: s.cfg.NodeID, Hostname: s.hostname, ProbeVersion: probeVersion,
		SlideforgeVersion: s.botVersion, PythonVersion: s.pythonVersion,
		UptimeSeconds: int64(time.Since(s.startedAt) / time.Second), BotRunning: s.bridge.Connected(),
		StoreRawLogs: s.storeRawLogs, MaxSpoolBytes: limit, CurrentSpoolBytes: current,
		AllowedMethods: slices.Clone(s.cfg.AllowedMethods), TlsFingerprintSha256: s.fingerprint,
		TransportStats: []*madtomv1.TransportCompressionStats{{
			NodeId: s.cfg.NodeID, Mode: "ZSTD_STREAM", Batches: s.batches, ZstdBatches: s.batches,
			ZstdBytes: s.zstdBytes, DecodedZstdBytes: s.decodedBytes, LastSeenUnixNano: s.lastChunkAt,
		}},
	}, nil
}

func (s *Server) UpdateControlConfig(ctx context.Context, req *madtomv1.SfUpdateControlConfigRequest) (*madtomv1.SfNodeStatusResponse, error) {
	if req.MaxSpoolBytes > math.MaxInt64 {
		return nil, status.Error(codes.InvalidArgument, "spool limit out of range")
	}
	s.mu.Lock()
	if req.MaxSpoolBytes > 0 {
		if err := s.wal.SetMaxBytes(int64(req.MaxSpoolBytes)); err != nil {
			s.mu.Unlock()
			return nil, status.Errorf(codes.Internal, "update spool limit: %v", err)
		}
	}
	if s.storeRawLogs && !req.StoreRawLogs {
		s.flushSpoolLocked()
	}
	s.storeRawLogs = req.StoreRawLogs
	s.mu.Unlock()
	s.syncBridgeMode()
	return s.GetNodeStatus(ctx, nil)
}

func (s *Server) GetSpoolStats(_ context.Context, _ *madtomv1.SfSpoolStatsRequest) (*madtomv1.SfSpoolStatsResponse, error) {
	current, limit, logs, api, oldest := s.wal.ChunkStats()
	return &madtomv1.SfSpoolStatsResponse{
		CurrentBytes: current, MaxBytes: limit, LogChunks: logs, ApiChunks: api, OldestUnixNano: oldest,
	}, nil
}

func (s *Server) QueryLogChunks(_ context.Context, req *madtomv1.LogChunkQuery) (*madtomv1.LogChunkResponse, error) {
	chunks, hasMore := s.wal.QueryLogChunkPage(req)
	return &madtomv1.LogChunkResponse{NodeId: s.cfg.NodeID, Chunks: chunks, HasMore: hasMore}, nil
}

func effectiveTimeout(ms uint32) time.Duration {
	if ms == 0 {
		return 30 * time.Second
	}
	return time.Duration(min(ms, 120_000)) * time.Millisecond
}

func (s *Server) Call(ctx context.Context, req *madtomv1.SfCallRequest) (*madtomv1.SfCallResponse, error) {
	if _, allowed := s.allowlist[req.Method]; !allowed {
		return &madtomv1.SfCallResponse{Error: "method_not_allowed"}, nil
	}
	payload := req.PayloadJson
	if len(payload) > 1<<20 {
		return &madtomv1.SfCallResponse{Error: "payload_too_large"}, nil
	}
	if payload == "" {
		payload = "{}"
	}
	if !utf8.ValidString(payload) || !json.Valid([]byte(payload)) {
		return &madtomv1.SfCallResponse{Error: "bad_payload"}, nil
	}
	reply, err := s.bridge.CallRpcContext(ctx, req.Method, json.RawMessage(payload), effectiveTimeout(req.TimeoutMs))
	if err != nil {
		var botErr *bridge.RPCError
		switch {
		case errors.As(err, &botErr):
			return &madtomv1.SfCallResponse{Error: botErr.Message}, nil
		case errors.Is(err, bridge.ErrTimeout), errors.Is(err, context.DeadlineExceeded):
			return &madtomv1.SfCallResponse{Error: "timeout"}, nil
		default:
			return &madtomv1.SfCallResponse{Error: "bot_unavailable"}, nil
		}
	}
	return &madtomv1.SfCallResponse{Ok: true, PayloadJson: string(reply.Payload)}, nil
}
