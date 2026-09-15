package transport

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	"github.com/DarkDuck007/madtom/pkg/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/daemon/spool"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
)

func TestListeningDaemonModes(t *testing.T) {
	for _, mode := range []string{"pull", "reverse-push"} {
		t.Run(mode, func(t *testing.T) {
			wal, err := spool.NewWALManager(t.TempDir(), "test-node", 1<<20, true)
			if err != nil {
				t.Fatal(err)
			}
			defer wal.Close()
			cfg := collector.DefaultConfig("test-node")
			cfg.FastPollIntervalMs = 100
			server := NewPullServer(0, "test-node", wal, collector.NewEngine("test-node"), cfg)
			if err := server.Start(); err != nil {
				t.Fatal(err)
			}
			defer server.Stop()
			address := server.listener.Addr().String()
			db, err := storage.OpenTSDB(t.TempDir())
			if err != nil {
				t.Fatal(err)
			}
			defer db.Close()
			reg := registry.NewRegistry("test")
			pipeline := ingest.NewPipeline(db, reg, "test")
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			if mode == "pull" {
				conn, err := grpc.NewClient(address, grpc.WithTransportCredentials(insecure.NewCredentials()))
				if err != nil {
					t.Fatal(err)
				}
				defer conn.Close()
				client := madtomv1.NewIngestServiceClient(conn)
				req := &madtomv1.PollRequest{NodeId: "test-node"}
				batch, err := client.PollTelemetry(ctx, req)
				if err != nil {
					t.Fatal(err)
				}
				if err := pipeline.ProcessBatch(batch, "PULL"); err != nil {
					t.Fatal(err)
				}
				req.LastAckedSegmentId = batch.SegmentId
				req.LastAckedOffset = batch.SegmentOffset
				next, err := client.PollTelemetry(ctx, req)
				if err != nil {
					t.Fatal(err)
				}
				if next.SegmentId == batch.SegmentId {
					t.Fatal("acknowledged batch replayed")
				}
			} else {
				done := make(chan struct{})
				go func() { defer close(done); ingest.ReceivePush(ctx, pipeline, "test-node", address) }()
				defer func() { cancel(); <-done }()
			}
			for {
				points, err := db.QueryRange("test-node", "cpu.total", 0, time.Now().UnixNano())
				if err != nil {
					t.Fatal(err)
				}
				if len(points) > 0 {
					break
				}
				select {
				case <-ctx.Done():
					t.Fatal("no stored telemetry")
				case <-time.After(20 * time.Millisecond):
				}
			}
			live, unsub := pipeline.Subscribe("test-node")
			defer unsub()
			select {
			case event := <-live:
				if event.Metrics.CpuModel == "" || !event.Metrics.ProcessesAvailable || len(event.Metrics.Processes) == 0 {
					t.Fatal("missing hardware/process snapshot")
				}
			case <-ctx.Done():
				t.Fatal("no initial live snapshot")
			}
		})
	}
}

func TestNodeIDMismatchExplainsConfiguration(t *testing.T) {
	server := NewPullServer(0, "actual-hostname", nil, nil, nil)
	_, err := server.PollTelemetry(context.Background(), &madtomv1.PollRequest{NodeId: "sakura1"})
	if status.Code(err) != codes.InvalidArgument {
		t.Fatalf("expected InvalidArgument, got %v", err)
	}
	for _, want := range []string{"sakura1", "actual-hostname", "configure the collector target"} {
		if !strings.Contains(err.Error(), want) {
			t.Fatalf("error %q does not contain %q", err, want)
		}
	}
}

func TestPushClientOfflineProcessSuppression(t *testing.T) {
	wal, err := spool.NewWALManager(t.TempDir(), "offline-node", 1<<20, false)
	if err != nil {
		t.Fatal(err)
	}
	defer wal.Close()

	cfg := collector.DefaultConfig("offline-node")
	cfg.FastPollIntervalMs = 50
	cfg.ProcessMode = madtomv1.ProcessTelemetryMode_PROCESS_MODE_LIVE_ONLY

	// Point to an address where no collector is running
	client := NewPushClient("127.0.0.1:59999", "offline-node", wal, collector.NewEngine("offline-node"), cfg)
	client.Start()

	// Let it sample while disconnected
	time.Sleep(200 * time.Millisecond)
	client.Stop()

	if client.IsConnected() {
		t.Fatal("expected IsConnected to be false for unreachable collector")
	}

	// Read spooled batches from WAL
	batch, err := wal.ReadBatchChunk(10)
	if err != nil {
		t.Fatalf("failed to read backlog: %v", err)
	}
	if batch == nil || len(batch.Samples) == 0 {
		t.Fatal("expected spooled batches in WAL while offline")
	}

	// In LIVE_ONLY mode, processes must be omitted while offline to conserve RAM and disk
	for _, sample := range batch.Samples {
		if len(sample.Processes) > 0 || sample.ProcessesAvailable {
			t.Fatalf("expected nil processes in offline LIVE_ONLY spool, got %d processes", len(sample.Processes))
		}
		if sample.Cpu == nil {
			t.Fatal("expected CPU metrics to be preserved in offline spool")
		}
		if sample.Memory == nil {
			t.Fatal("expected Memory metrics to be preserved in offline spool")
		}
	}
}
