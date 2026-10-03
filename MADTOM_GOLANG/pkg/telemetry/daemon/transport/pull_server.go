package transport

import (
	"context"
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/config"
	"github.com/DarkDuck007/madtom/pkg/telemetry/daemon/spool"
	"github.com/DarkDuck007/madtom/pkg/telemetry/optin"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/grpc/status"
)

// PullServer provides a gRPC listener for the collector to scrape pending telemetry batches.
type PullServer struct {
	madtomv1.UnimplementedIngestServiceServer
	mu                 sync.RWMutex
	port               int
	nodeID             string
	spoolDir           string
	wal                *spool.WALManager
	engine             *collector.Engine
	config             *madtomv1.NodeConfig
	server             *grpc.Server
	listener           net.Listener
	cancel             context.CancelFunc
	wg                 sync.WaitGroup
	streamMu           sync.Mutex
	activeStreamCancel context.CancelFunc
	activeStreamDone   chan struct{}
	hasActiveStream    bool
	lastActive         time.Time
}

// NewPullServer creates a new PullServer instance.
func NewPullServer(port int, nodeID string, wal *spool.WALManager, engine *collector.Engine, cfg *madtomv1.NodeConfig, spoolDir ...string) *PullServer {
	if cfg == nil {
		cfg = collector.DefaultConfig(nodeID)
	}
	var sDir string
	if len(spoolDir) > 0 {
		sDir = spoolDir[0]
	}
	return &PullServer{
		port:       port,
		nodeID:     nodeID,
		spoolDir:   sDir,
		wal:        wal,
		engine:     engine,
		config:     cfg,
		lastActive: time.Now(),
	}
}

// Start launches the pull gRPC listener on the configured port.
func (s *PullServer) Start() error {
	lis, err := net.Listen("tcp", fmt.Sprintf(":%d", s.port))
	if err != nil {
		return fmt.Errorf("failed to listen on port %d: %w", s.port, err)
	}
	s.listener = lis
	ctx, cancel := context.WithCancel(context.Background())
	s.cancel = cancel
	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		for {
			s.mu.Lock()
			cfg := s.config
			hasActiveClient := s.hasActiveStream || time.Since(s.lastActive) < 15*time.Second
			s.mu.Unlock()

			sample := s.engine.Collect(cfg)
			if !hasActiveClient && cfg != nil {
				optin.StripMonitorOnlyMetrics(sample, cfg)
			}

			if _, err := s.wal.WriteMetrics([]*madtomv1.SystemMetrics{sample}); err != nil {
				log.Printf("[PullServer] WAL append failed: %v", err)
			}
			if !wait(ctx, pollInterval(cfg)) {
				return
			}
		}
	}()
	s.server = grpc.NewServer(
		grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{
			MinTime:             2 * time.Second,
			PermitWithoutStream: true,
		}),
		grpc.KeepaliveParams(keepalive.ServerParameters{
			MaxConnectionIdle:     15 * time.Minute,
			MaxConnectionAge:      30 * time.Minute,
			MaxConnectionAgeGrace: 5 * time.Second,
			Time:                  5 * time.Second,
			Timeout:               3 * time.Second,
		}),
	)
	madtomv1.RegisterIngestServiceServer(s.server, s)

	go func() {
		_ = s.server.Serve(lis)
	}()
	return nil
}

// Stop terminates the pull gRPC server.
func (s *PullServer) Stop() {
	if s.cancel != nil {
		s.cancel()
	}
	s.wg.Wait()
	if s.server != nil {
		s.server.Stop()
	}
	if s.listener != nil {
		_ = s.listener.Close()
	}
}

// PollTelemetry responds to a scraper poll with pending backlog or immediate telemetry.
func (s *PullServer) PollTelemetry(ctx context.Context, req *madtomv1.PollRequest) (*madtomv1.TelemetryBatch, error) {
	started := time.Now()
	log.Printf("[Poll] Request for %q", req.NodeId)
	defer func() { log.Printf("[Poll] Request for %q finished in %s", req.NodeId, time.Since(started)) }()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.lastActive = time.Now()
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}

	if req.NodeId != s.nodeID {
		return nil, status.Errorf(codes.InvalidArgument, "node ID mismatch: collector requested %q, daemon is %q; configure the collector target with the daemon node ID", req.NodeId, s.nodeID)
	}
	if req.Config != nil {
		s.config = req.Config
	}
	// Acknowledge previously scraped segment if requested
	if req.LastAckedSegmentId != "" {
		if err := s.wal.AcknowledgeSegment(req.LastAckedSegmentId, req.LastAckedOffset); err != nil {
			return nil, status.Errorf(codes.FailedPrecondition, "acknowledge spool: %v", err)
		}
	}

	// 1. Drain pending backlog chunk if available
	maxSamples := int(req.MaxSamples)
	if maxSamples <= 0 {
		maxSamples = spool.DefaultChunkMaxSamples
	}
	batch, err := s.wal.ReadBatchChunk(maxSamples)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "read spool: %v", err)
	}
	if batch != nil {
		return batch, nil
	}

	// 2. Otherwise sample live metrics immediately
	sample := s.engine.Collect(s.config)
	if _, err = s.wal.WriteMetrics([]*madtomv1.SystemMetrics{sample}); err != nil {
		return nil, status.Errorf(codes.Internal, "write spool: %v", err)
	}
	// A concurrent sampler may have appended first. Return the pending prefix,
	// never a newer standalone record whose ACK would skip an earlier sample.
	batch, err = s.wal.ReadBatchChunk(maxSamples)
	if err != nil {
		return nil, status.Errorf(codes.Internal, "read spool: %v", err)
	}
	if batch == nil {
		return &madtomv1.TelemetryBatch{NodeId: s.nodeID}, nil
	}
	return batch, nil
}

// PushBatchStream is rejected in pull server mode.
func (s *PullServer) PushBatchStream(stream madtomv1.IngestService_PushBatchStreamServer) error {
	return status.Error(codes.Unimplemented, "PushBatchStream not supported in pull-mode daemon")
}

// ReceiveBatchStream reverses connection establishment without reversing data flow.
func (s *PullServer) ReceiveBatchStream(stream madtomv1.IngestService_ReceiveBatchStreamServer) error {
	// If a previous stream is hanging (e.g. collector machine suspended and resumed),
	// supersede it gracefully so the new stream can immediately take over.
	s.mu.Lock()
	if s.activeStreamCancel != nil {
		log.Printf("[PullServer] Cancelling previous collector stream for node %s to allow new connection", s.nodeID)
		s.activeStreamCancel()
	}
	s.mu.Unlock()

	s.streamMu.Lock()
	defer s.streamMu.Unlock()

	streamCtx, cancel := context.WithCancel(stream.Context())
	defer cancel()

	doneCh := make(chan struct{})
	defer close(doneCh)

	s.mu.Lock()
	s.activeStreamCancel = cancel
	s.activeStreamDone = doneCh
	s.mu.Unlock()

	defer func() {
		s.mu.Lock()
		if s.activeStreamDone == doneCh {
			s.activeStreamCancel = nil
			s.activeStreamDone = nil
		}
		s.mu.Unlock()
	}()

	hello, err := stream.Recv()
	if err != nil {
		return err
	}
	if hello.NodeId != s.nodeID {
		return status.Errorf(codes.InvalidArgument, "node ID mismatch: collector requested %q, daemon is %q; configure the collector target with the daemon node ID", hello.NodeId, s.nodeID)
	}
	s.mu.Lock()
	s.hasActiveStream = true
	s.lastActive = time.Now()
	if hello.Config != nil {
		s.config = hello.Config
	}
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		s.hasActiveStream = false
		s.mu.Unlock()
	}()
	for {
		batch, err := s.wal.ReadBatchChunk(spool.DefaultChunkMaxSamples)
		if err != nil {
			return err
		}
		if batch == nil {
			if !wait(streamCtx, 100*time.Millisecond) {
				return streamCtx.Err()
			}
			continue
		}
		if err := stream.Send(batch); err != nil {
			return err
		}

		type ackResult struct {
			ack *madtomv1.BatchAck
			err error
		}
		ackCh := make(chan ackResult, 1)
		go func() {
			a, e := stream.Recv()
			ackCh <- ackResult{ack: a, err: e}
		}()

		var ack *madtomv1.BatchAck
		select {
		case <-streamCtx.Done():
			return streamCtx.Err()
		case <-time.After(10 * time.Second):
			return status.Error(codes.DeadlineExceeded, "timed out waiting for batch acknowledgement")
		case res := <-ackCh:
			if res.err != nil {
				return res.err
			}
			ack = res.ack
		}

		if !ack.Success || ack.NodeId != s.nodeID || ack.SegmentId != batch.SegmentId || ack.SegmentOffset != batch.SegmentOffset {
			return status.Error(codes.FailedPrecondition, "batch was not acknowledged")
		}
		if err := s.wal.AcknowledgeSegment(ack.SegmentId, ack.SegmentOffset); err != nil {
			return err
		}
		s.mu.Lock()
		s.lastActive = time.Now()
		if ack.Config != nil {
			s.config = ack.Config
			if s.spoolDir != "" {
				_ = config.SaveConfig(s.spoolDir, ack.Config)
			}
		}
		s.mu.Unlock()
	}
}
