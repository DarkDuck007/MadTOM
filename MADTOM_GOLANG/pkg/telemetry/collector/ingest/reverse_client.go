package ingest

import (
	"context"
	"log"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

// ReceivePush connects outbound to a listening daemon and persists its stream.
// It retries until the owner cancels ctx.
func ReceivePush(ctx context.Context, pipeline *Pipeline, nodeID, address string) {
	log.Printf("[ReversePush] Starting receiver worker for %s at %s", nodeID, address)
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
		log.Printf("[ReversePush] Failed to create client for %s at %s: %v", nodeID, address, err)
		return
	}
	defer conn.Close()

	stream, err := madtomv1.NewIngestServiceClient(conn).ReceiveBatchStream(ctx)
	if err != nil {
		log.Printf("[ReversePush] Failed to open stream for %s at %s: %v", nodeID, address, err)
		return
	}

	if err = stream.Send(&madtomv1.BatchAck{NodeId: nodeID, Config: p.reg.TransportConfig(nodeID)}); err != nil {
		log.Printf("[ReversePush] Handshake failed for %s at %s: %v", nodeID, address, err)
		return
	}

	log.Printf("[ReversePush] Connected and streaming from %s at %s", nodeID, address)
	for {
		batch, err := stream.Recv()
		if err != nil {
			log.Printf("[ReversePush] Stream ended for %s at %s: %v", nodeID, address, err)
			return
		}
		if batch.NodeId != nodeID {
			log.Printf("[ReversePush] Node ID mismatch from %s: got %q", nodeID, batch.NodeId)
			return
		}
		if err = p.ProcessBatch(batch, "REVERSE_PUSH"); err != nil {
			log.Printf("[ReversePush] Ingestion failed for %s segment %s: %v", nodeID, batch.SegmentId, err)
			return
		}
		if err = stream.Send(&madtomv1.BatchAck{NodeId: nodeID, SegmentId: batch.SegmentId, SegmentOffset: batch.SegmentOffset, Success: true, Config: p.reg.TransportConfig(nodeID)}); err != nil {
			log.Printf("[ReversePush] Ack send failed for %s: %v", nodeID, err)
			return
		}
	}
}
