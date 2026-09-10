package ingest

import (
	"context"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"time"
)

// ReceivePush connects outbound to a listening daemon and persists its stream.
// It retries until the owner cancels ctx.
func ReceivePush(ctx context.Context, pipeline *Pipeline, nodeID, address string) {
	for ctx.Err() == nil {
		receivePushSession(ctx, pipeline, nodeID, address)
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Second):
		}
	}
}
func receivePushSession(ctx context.Context, p *Pipeline, nodeID, address string) {
	conn, err := grpc.NewClient(address, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return
	}
	defer conn.Close()
	stream, err := madtomv1.NewIngestServiceClient(conn).ReceiveBatchStream(ctx)
	if err != nil {
		return
	}
	if err = stream.Send(&madtomv1.BatchAck{NodeId: nodeID, Config: p.reg.TransportConfig(nodeID)}); err != nil {
		return
	}
	for {
		batch, err := stream.Recv()
		if err != nil {
			return
		}
		if batch.NodeId != nodeID {
			return
		}
		if err = p.ProcessBatch(batch, "REVERSE_PUSH"); err != nil {
			return
		}
		if err = stream.Send(&madtomv1.BatchAck{NodeId: nodeID, SegmentId: batch.SegmentId, SegmentOffset: batch.SegmentOffset, Success: true, Config: p.reg.TransportConfig(nodeID)}); err != nil {
			return
		}
	}
}
