package transport

import (
	"context"
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	"github.com/DarkDuck007/madtom/pkg/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/daemon/spool"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// PullServer provides a gRPC listener for the collector to scrape pending telemetry batches.
type PullServer struct {
	madtomv1.UnimplementedIngestServiceServer
	mu       sync.RWMutex
	port     int
	nodeID   string
	wal      *spool.WALManager
	engine   *collector.Engine
	config   *madtomv1.NodeConfig
	server   *grpc.Server
	listener net.Listener
	cancel   context.CancelFunc
	wg       sync.WaitGroup
	streamMu sync.Mutex
}

// NewPullServer creates a new PullServer instance.
func NewPullServer(port int, nodeID string, wal *spool.WALManager, engine *collector.Engine, cfg *madtomv1.NodeConfig) *PullServer {
	if cfg == nil {
		cfg = collector.DefaultConfig(nodeID)
	}
	return &PullServer{
		port:   port,
		nodeID: nodeID,
		wal:    wal,
		engine: engine,
		config: cfg,
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
			s.mu.Unlock()
			_, _ = s.wal.WriteMetrics([]*madtomv1.SystemMetrics{s.engine.Collect(cfg)})
			if !wait(ctx, pollInterval(cfg)) {
				return
			}
		}
	}()
	s.server = grpc.NewServer()
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
		pending, err := s.wal.ReadOldestBatch()
		if err != nil {
			return nil, err
		}
		if pending != nil && pending.SegmentId == req.LastAckedSegmentId && pending.SegmentOffset == req.LastAckedOffset {
			if err := s.wal.AcknowledgeSegment(req.LastAckedSegmentId); err != nil {
				return nil, err
			}
		}
	}

	// 1. Drain oldest offline segment if available
	batch, err := s.wal.ReadOldestBatch()
	if err != nil {
		return nil, status.Errorf(codes.Internal, "read spool: %v", err)
	}
	if batch != nil {
		return batch, nil
	}

	// 2. Otherwise sample live metrics immediately
	sample := s.engine.Collect(s.config)
	batch, err = s.wal.WriteMetrics([]*madtomv1.SystemMetrics{sample})
	if err != nil {
		return &madtomv1.TelemetryBatch{
			NodeId:  s.nodeID,
			Samples: []*madtomv1.SystemMetrics{sample},
		}, nil
	}

	return batch, nil
}

// PushBatchStream is rejected in pull server mode.
func (s *PullServer) PushBatchStream(stream madtomv1.IngestService_PushBatchStreamServer) error {
	return status.Error(codes.Unimplemented, "PushBatchStream not supported in pull-mode daemon")
}

// ReceiveBatchStream reverses connection establishment without reversing data flow.
func (s *PullServer) ReceiveBatchStream(stream madtomv1.IngestService_ReceiveBatchStreamServer) error {
	if !s.streamMu.TryLock() {
		return status.Error(codes.AlreadyExists, "a collector is already connected")
	}
	defer s.streamMu.Unlock()
	hello, err := stream.Recv()
	if err != nil {
		return err
	}
	if hello.NodeId != s.nodeID {
		return status.Errorf(codes.InvalidArgument, "node ID mismatch: collector requested %q, daemon is %q; configure the collector target with the daemon node ID", hello.NodeId, s.nodeID)
	}
	s.mu.Lock()
	if hello.Config != nil {
		s.config = hello.Config
	}
	s.mu.Unlock()
	for {
		batch, err := s.wal.ReadOldestBatch()
		if err != nil {
			return err
		}
		if batch == nil {
			if !wait(stream.Context(), 100*time.Millisecond) {
				return stream.Context().Err()
			}
			continue
		}
		if err := stream.Send(batch); err != nil {
			return err
		}
		ack, err := stream.Recv()
		if err != nil {
			return err
		}
		if !ack.Success || ack.NodeId != s.nodeID || ack.SegmentId != batch.SegmentId || ack.SegmentOffset != batch.SegmentOffset {
			return status.Error(codes.FailedPrecondition, "batch was not acknowledged")
		}
		if err := s.wal.AcknowledgeSegment(ack.SegmentId); err != nil {
			return err
		}
		s.mu.Lock()
		if ack.Config != nil {
			s.config = ack.Config
		}
		s.mu.Unlock()
	}
}
