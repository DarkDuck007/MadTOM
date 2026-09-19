package api

import (
	"context"
	"github.com/DarkDuck007/madtom/pkg/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
	"net"
	"strings"
	"testing"
	"time"
)

func TestNegotiatedUnaryAndLiveRPCs(t *testing.T) {
	reg := registry.NewRegistry("test")
	pipeline := ingest.NewPipeline(nil, reg, "test")
	id := strings.Repeat("node-", 500)
	if err := pipeline.ProcessBatch(&madtomv1.TelemetryBatch{NodeId: id, Samples: []*madtomv1.SystemMetrics{{TimestampUnixNano: 1}}}, "PUSH"); err != nil {
		t.Fatal(err)
	}
	listener := bufconn.Listen(1 << 20)
	server := grpc.NewServer()
	api := NewServer("test", nil, reg, pipeline)
	madtomv1.RegisterQueryServiceServer(server, api)
	madtomv1.RegisterConfigServiceServer(server, api)
	go server.Serve(listener)
	defer server.Stop()
	conn, err := grpc.NewClient("passthrough:///test", grpc.WithTransportCredentials(insecure.NewCredentials()), grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return listener.DialContext(ctx) }))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	query := madtomv1.NewQueryServiceClient(conn)
	legacy, err := query.ListNodes(ctx, &madtomv1.ListNodesRequest{})
	if err != nil || len(legacy.Nodes) != 1 || len(legacy.ZstdPayload) != 0 {
		t.Fatalf("legacy: %v %v", legacy, err)
	}
	compressedCtx := metadata.AppendToOutgoingContext(ctx, compressionHeader, "1")
	compressed, err := query.ListNodes(compressedCtx, &madtomv1.ListNodesRequest{})
	if err != nil {
		t.Fatal(err)
	}
	decoder, _ := zstd.NewReader(nil)
	defer decoder.Close()
	raw, err := decoder.DecodeAll(compressed.ZstdPayload, nil)
	if err != nil {
		t.Fatal(err)
	}
	restored := &madtomv1.ListNodesResponse{}
	if err := proto.Unmarshal(raw, restored); err != nil {
		t.Fatal(err)
	}
	if !proto.Equal(restored, legacy) {
		t.Fatal("unary data differs")
	}
	stream, err := query.SubscribeLive(compressedCtx, &madtomv1.LiveSubscriptionRequest{NodeId: id})
	if err != nil {
		t.Fatal(err)
	}
	event, err := stream.Recv()
	if err != nil {
		t.Fatal(err)
	}
	raw, err = decoder.DecodeAll(event.ZstdPayload, nil)
	if err != nil {
		t.Fatal(err)
	}
	decoded := &madtomv1.LiveTelemetryEvent{}
	if err := proto.Unmarshal(raw, decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.Metrics.TimestampUnixNano != 1 || decoded.NodeId != id {
		t.Fatal("stream data differs")
	}
	config, err := madtomv1.NewConfigServiceClient(conn).GetNodeConfig(compressedCtx, &madtomv1.GetNodeConfigRequest{NodeId: id})
	if err != nil {
		t.Fatal(err)
	}
	if len(config.ZstdPayload) == 0 {
		t.Fatal("config not compressed")
	}
}
