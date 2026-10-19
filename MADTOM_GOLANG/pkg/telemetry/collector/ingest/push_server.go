package ingest

import (
	"context"
	"io"
	"log"
	"net"
	"os"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/peer"
	"google.golang.org/grpc/status"
)

// PushServer implements IngestService for inbound daemon push streams.
type PushServer struct {
	madtomv1.UnimplementedIngestServiceServer
	pipeline *Pipeline
}

// NewPushServer constructs an IngestService server wrapper.
func NewPushServer(pipeline *Pipeline) *PushServer {
	return &PushServer{pipeline: pipeline}
}

// PushBatchStream handles streaming batches from monitored node daemons.
func (s *PushServer) PushBatchStream(stream madtomv1.IngestService_PushBatchStreamServer) error {
	for {
		batch, err := stream.Recv()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			return err
		}

		if p, ok := peer.FromContext(stream.Context()); ok && p.Addr != nil {
			host, _, err := net.SplitHostPort(p.Addr.String())
			if err == nil && host != "" {
				ip := net.ParseIP(host)
				isLoopback := ip != nil && ip.IsLoopback()
				// Only associate loopback (127.0.0.1) with the node running locally on the same host as the collector.
				// Remote nodes connecting via SSH tunnels, port forwarding, or loopback proxies must NOT be routed
				// to the collector's local daemon.
				if !isLoopback || isLocalCollectorNode(batch.NodeId) {
					s.pipeline.Reg().SetNodeAddress(batch.NodeId, net.JoinHostPort(host, "50052"))
				}
			}
		}

		summary, err := s.pipeline.ProcessBatchWithSummary(batch, "PUSH")
		if err != nil {
			log.Printf("[PushServer] Error processing batch from %s: %v", batch.NodeId, err)
			_ = stream.Send(&madtomv1.BatchAck{
				NodeId:        batch.NodeId,
				SegmentId:     batch.SegmentId,
				SegmentOffset: batch.SegmentOffset,
				Success:       false,
				ErrorMessage:  err.Error(),
			})
			continue
		}

		// Send success ACK so daemon can delete acknowledged segment
		if err := stream.Send(&madtomv1.BatchAck{
			NodeId:        batch.NodeId,
			SegmentId:     batch.SegmentId,
			SegmentOffset: batch.SegmentOffset,
			Success:       true,
			Config:        s.pipeline.reg.TransportConfig(batch.NodeId),
			LogAckedSeq:   summary.lastAckedLogSeq,
		}); err != nil {
			return err
		}
	}
}

// PollTelemetry is not handled on the collector; daemons implement it in pull mode.
func (s *PushServer) PollTelemetry(ctx context.Context, req *madtomv1.PollRequest) (*madtomv1.TelemetryBatch, error) {
	return nil, status.Error(codes.Unimplemented, "PollTelemetry is a daemon RPC")
}

// IsLocalCollectorNode returns true if the node ID corresponds to the local collector host
// (accounting for case differences, loopback addresses, or short/FQDN hostname substrings like 'lbw' and 'dani-lbw').
func isLocalCollectorNode(nodeID string) bool {
	h, err := os.Hostname()
	if err == nil && h != "" {
		if strings.EqualFold(nodeID, h) ||
			strings.Contains(strings.ToLower(h), strings.ToLower(nodeID)) ||
			strings.Contains(strings.ToLower(nodeID), strings.ToLower(h)) {
			return true
		}
	}
	return strings.EqualFold(nodeID, "localhost") || nodeID == "127.0.0.1" || nodeID == "::1"
}
