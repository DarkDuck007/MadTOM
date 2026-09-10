package api

import (
	"context"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"net"
	"strings"

	"github.com/DarkDuck007/madtom/pkg/collector/downsample"
	"github.com/DarkDuck007/madtom/pkg/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// Server implements QueryService and ConfigService for client UIs.
type Server struct {
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
		collectorName: collectorName,
		tsdb:          tsdb,
		reg:           reg,
		pipeline:      pipeline,
	}
}

// ListNodes returns all monitored nodes registered with this collector.
func (s *Server) ListNodes(ctx context.Context, req *madtomv1.ListNodesRequest) (*madtomv1.ListNodesResponse, error) {
	nodes := s.reg.ListNodes()
	return &madtomv1.ListNodesResponse{
		CollectorName: s.collectorName,
		Nodes:         nodes,
	}, nil
}

// QueryRange performs a resolution-adaptive downsampled query over a historical range.
func (s *Server) QueryRange(ctx context.Context, req *madtomv1.RangeQueryRequest) (*madtomv1.RangeQueryResponse, error) {
	if req.StartTimeUnixNano < 0 || req.EndTimeUnixNano <= req.StartTimeUnixNano || req.TargetPoints > 100000 {
		return nil, status.Error(codes.InvalidArgument, "invalid time range or point budget")
	}
	rawPoints, err := s.tsdb.QueryRange(req.NodeId, req.MetricName, req.StartTimeUnixNano, req.EndTimeUnixNano)
	if err != nil {
		return nil, err
	}

	targetPoints := int(req.TargetPoints)
	if targetPoints <= 0 {
		targetPoints = 1200 // default resolution budget
	}

	sampled := downsample.DownsampleLTTB(rawPoints, targetPoints)

	protoPoints := make([]*madtomv1.TimeSeriesPoint, len(sampled))
	for i, pt := range sampled {
		protoPoints[i] = &madtomv1.TimeSeriesPoint{
			TimestampUnixNano: pt.TimestampUnixNano,
			Value:             pt.Value,
			MinValue:          pt.Value, MaxValue: pt.Value,
		}
	}

	return &madtomv1.RangeQueryResponse{
		NodeId:     req.NodeId,
		MetricName: req.MetricName,
		Points:     protoPoints,
	}, nil
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
			if err := stream.Send(event); err != nil {
				return err
			}
		}
	}
}

// GetNodeConfig retrieves active opt-in settings for a node.
func (s *Server) GetNodeConfig(ctx context.Context, req *madtomv1.GetNodeConfigRequest) (*madtomv1.NodeConfig, error) {
	return s.reg.GetConfig(req.NodeId), nil
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
		if _, _, err := net.SplitHostPort(req.Config.TwampTarget); err != nil {
			return nil, status.Error(codes.InvalidArgument, "TWAMP target must be host:port")
		}
	}
	s.reg.SetConfig(req.NodeId, req.Config)
	return &madtomv1.ConfigAck{
		Success: true,
		Message: "Node configuration updated",
	}, nil
}
