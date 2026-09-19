package api

import (
	"context"
	"errors"
	"net"
	"strings"
	"time"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/DarkDuck007/madtom/pkg/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// Server implements QueryService and ConfigService for client UIs.
const MaxConcurrentHistoryQueries = 4
const MaxHistoryScanPoints = 5_000_000
const HistoryQueryTimeout = 10 * time.Second

type Server struct {
	historySlots chan struct{}
	madtomv1.UnimplementedQueryServiceServer
	madtomv1.UnimplementedConfigServiceServer
	collectorName string
	tsdb          *storage.TSDB
	reg           *registry.Registry
	pipeline      *ingest.Pipeline
}

// NewServer constructs a new API server.
func NewServer(collectorName string, tsdb *storage.TSDB, reg *registry.Registry, pipeline *ingest.Pipeline) *Server {
	return &Server{
		historySlots:  make(chan struct{}, MaxConcurrentHistoryQueries),
		collectorName: collectorName,
		tsdb:          tsdb,
		reg:           reg,
		pipeline:      pipeline,
	}
}

// ListNodes returns all monitored nodes registered with this collector.
func (s *Server) ListNodes(ctx context.Context, req *madtomv1.ListNodesRequest) (*madtomv1.ListNodesResponse, error) {
	nodes := s.reg.ListNodes()
	return encodeResponse(ctx, &madtomv1.ListNodesResponse{
		CollectorName:           s.collectorName,
		Nodes:                   nodes,
		TransportStatsSupported: true,
		TransportStats:          s.pipeline.TransportStats(),
	})
}

// QueryRange performs a resolution-adaptive downsampled query over a historical range.
func (s *Server) QueryRange(ctx context.Context, req *madtomv1.RangeQueryRequest) (*madtomv1.RangeQueryResponse, error) {
	if req.StartTimeUnixNano < 0 || req.EndTimeUnixNano <= req.StartTimeUnixNano || req.TargetPoints > 100000 {
		return nil, status.Error(codes.InvalidArgument, "invalid time range or point budget")
	}
	targetPoints := int(req.TargetPoints)
	if targetPoints == 0 {
		targetPoints = 1200
	}
	if targetPoints < 4 {
		return nil, status.Error(codes.InvalidArgument, "point budget must be zero (default) or 4–100000")
	}
	ctx, cancel := context.WithTimeout(ctx, HistoryQueryTimeout)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return nil, status.FromContextError(err).Err()
	}
	select {
	case s.historySlots <- struct{}{}:
		defer func() { <-s.historySlots }()
	default:
		return nil, status.Error(codes.ResourceExhausted, "historical query capacity reached; retry later")
	}
	sampled, err := s.tsdb.QueryRangeSampled(ctx, req.NodeId, req.MetricName, req.StartTimeUnixNano, req.EndTimeUnixNano, targetPoints, MaxHistoryScanPoints)
	if err != nil {
		if ctx.Err() != nil {
			return nil, status.FromContextError(ctx.Err()).Err()
		}
		if errors.Is(err, storage.ErrQueryScanLimit) {
			return nil, status.Error(codes.ResourceExhausted, err.Error())
		}
		return nil, status.Error(codes.Internal, "historical storage query failed")
	}

	protoPoints := make([]*madtomv1.TimeSeriesPoint, len(sampled))
	for i, pt := range sampled {
		protoPoints[i] = &madtomv1.TimeSeriesPoint{
			TimestampUnixNano: pt.TimestampUnixNano,
			Value:             pt.Value,
			MinValue:          pt.Value, MaxValue: pt.Value,
		}
	}

	return encodeResponse(ctx, &madtomv1.RangeQueryResponse{
		NodeId:     req.NodeId,
		MetricName: req.MetricName,
		Points:     protoPoints,
	})
}

// SubscribeLive streams 1Hz real-time events for an active node card or detail view.
func (s *Server) SubscribeLive(req *madtomv1.LiveSubscriptionRequest, stream madtomv1.QueryService_SubscribeLiveServer) error {
	ch, unsubscribe := s.pipeline.Subscribe(req.NodeId)
	defer unsubscribe()

	ctx := stream.Context()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case event, ok := <-ch:
			if !ok {
				return nil
			}
			encoded, err := encodeResponse(ctx, event)
			if err != nil {
				return err
			}
			if err := stream.Send(encoded); err != nil {
				return err
			}
		}
	}
}

// GetNodeConfig retrieves active opt-in settings for a node.
func (s *Server) GetNodeConfig(ctx context.Context, req *madtomv1.GetNodeConfigRequest) (*madtomv1.NodeConfig, error) {
	return encodeResponse(ctx, s.reg.GetConfig(req.NodeId))
}

// UpdateNodeConfig updates active opt-in settings for a node.
func (s *Server) UpdateNodeConfig(ctx context.Context, req *madtomv1.UpdateNodeConfigRequest) (*madtomv1.ConfigAck, error) {
	if req.Config == nil || req.NodeId == "" || req.Config.NodeId != req.NodeId {
		return nil, status.Error(codes.InvalidArgument, "node config must match node ID")
	}
	if req.Config.FastPollIntervalMs < 100 {
		req.Config.FastPollIntervalMs = 1000
	}
	if strings.TrimSpace(req.Config.TwampTarget) != "" {
		target := strings.TrimSpace(req.Config.TwampTarget)
		if !strings.EqualFold(target, "collector") && !strings.EqualFold(target, "auto") {
			if strings.Contains(target, ":") {
				if _, _, err := net.SplitHostPort(target); err != nil {
					return nil, status.Error(codes.InvalidArgument, "TWAMP target must be 'collector', 'auto', host, or host:port")
				}
			}
		}
	}
	s.reg.SetConfig(req.NodeId, req.Config)
	return encodeResponse(ctx, &madtomv1.ConfigAck{
		Success: true,
		Message: "Node configuration updated",
	})
}
