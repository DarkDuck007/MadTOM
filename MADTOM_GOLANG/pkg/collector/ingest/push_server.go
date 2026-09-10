package ingest

import (
	"context"
	"io"
	"log"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
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

		if err := s.pipeline.ProcessBatch(batch, "PUSH"); err != nil {
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
		}); err != nil {
			return err
		}
	}
}

// PollTelemetry is not handled on the collector; daemons implement it in pull mode.
func (s *PushServer) PollTelemetry(ctx context.Context, req *madtomv1.PollRequest) (*madtomv1.TelemetryBatch, error) {
	return nil, status.Error(codes.Unimplemented, "PollTelemetry is a daemon RPC")
}
