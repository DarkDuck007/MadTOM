package collector_test

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/api"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/storage"
	daemoncoll "github.com/DarkDuck007/madtom/pkg/telemetry/daemon/collector"
	daemonspool "github.com/DarkDuck007/madtom/pkg/telemetry/daemon/spool"
	daemontrans "github.com/DarkDuck007/madtom/pkg/telemetry/daemon/transport"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func TestEndToEndDaemonToCollector(t *testing.T) {
	tempBase, err := os.MkdirTemp("", "madtom-e2e-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempBase)

	collDataDir := filepath.Join(tempBase, "collector_tsdb")
	daemonSpoolDir := filepath.Join(tempBase, "daemon_spool")

	// 1. Setup and start Collector gRPC server
	tsdb, err := storage.OpenTSDB(collDataDir)
	if err != nil {
		t.Fatalf("failed to open TSDB: %v", err)
	}
	defer tsdb.Close()

	reg := registry.NewRegistry("Test-Collector-Primary")
	pipeline := ingest.NewPipeline(tsdb, reg, "Test-Collector-Primary")

	lis, err := net.Listen("tcp", "127.0.0.1:0") // Random available port
	if err != nil {
		t.Fatalf("failed to listen: %v", err)
	}
	defer lis.Close()
	serverAddr := lis.Addr().String()

	grpcServer := grpc.NewServer()
	madtomv1.RegisterIngestServiceServer(grpcServer, ingest.NewPushServer(pipeline))
	apiServer := api.NewServer("Test-Collector-Primary", tsdb, reg, pipeline)
	madtomv1.RegisterQueryServiceServer(grpcServer, apiServer)
	madtomv1.RegisterConfigServiceServer(grpcServer, apiServer)

	go func() {
		_ = grpcServer.Serve(lis)
	}()
	defer grpcServer.GracefulStop()

	// 2. Setup and start Node Daemon Push Client
	wal, err := daemonspool.NewWALManager(daemonSpoolDir, "node-alpha", 1024*1024, false)
	if err != nil {
		t.Fatalf("failed to open daemon WAL: %v", err)
	}
	defer wal.Close()

	engine := daemoncoll.NewEngine("node-alpha")
	cfg := daemoncoll.DefaultConfig("node-alpha")
	cfg.FastPollIntervalMs = 100 // fast 100ms ticks for test

	pushClient := daemontrans.NewPushClient(serverAddr, "node-alpha", wal, engine, cfg)
	pushClient.Start()
	defer pushClient.Stop()

	// 3. Connect UI client to QueryService
	conn, err := grpc.Dial(serverAddr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("failed to dial query service: %v", err)
	}
	defer conn.Close()

	queryClient := madtomv1.NewQueryServiceClient(conn)

	// Wait for connection and at least 2 samples to ingest
	time.Sleep(350 * time.Millisecond)

	// Verify ListNodes
	listResp, err := queryClient.ListNodes(context.Background(), &madtomv1.ListNodesRequest{})
	if err != nil {
		t.Fatalf("ListNodes failed: %v", err)
	}

	if listResp.CollectorName != "Test-Collector-Primary" {
		t.Fatalf("expected collector name 'Test-Collector-Primary', got '%s'", listResp.CollectorName)
	}

	if len(listResp.Nodes) != 1 {
		t.Fatalf("expected 1 registered node, got %d", len(listResp.Nodes))
	}
	node := listResp.Nodes[0]
	if node.NodeId != "node-alpha" || node.Status != "ONLINE" {
		t.Fatalf("unexpected node info: %+v", node)
	}

	// Verify QueryRange
	now := time.Now().UnixNano()
	queryResp, err := queryClient.QueryRange(context.Background(), &madtomv1.RangeQueryRequest{
		NodeId:            "node-alpha",
		MetricName:        "cpu.total",
		StartTimeUnixNano: now - int64(10*time.Second),
		EndTimeUnixNano:   now + int64(1*time.Second),
		TargetPoints:      100,
	})
	if err != nil {
		t.Fatalf("QueryRange failed: %v", err)
	}

	if len(queryResp.Points) == 0 {
		t.Fatal("expected at least 1 point in QueryRange response")
	}
	t.Logf("Successfully verified E2E flow! Received %d points for node %s via collector %s",
		len(queryResp.Points), node.NodeId, listResp.CollectorName)
}
