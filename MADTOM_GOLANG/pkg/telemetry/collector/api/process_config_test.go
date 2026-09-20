package api

import (
	"context"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/registry"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"testing"
)

func TestProcessSnapshotLimitConfigRoundTrip(t *testing.T) {
	reg := registry.NewRegistry("test")
	server := NewServer("test", nil, reg, nil)
	for _, limit := range []uint32{0, 1, 25, 1000} {
		cfg := &madtomv1.NodeConfig{NodeId: "node", ProcessSnapshotLimit: limit, TopNProcesses: 3}
		if _, err := server.UpdateNodeConfig(context.Background(), &madtomv1.UpdateNodeConfigRequest{NodeId: "node", Config: cfg}); err != nil {
			t.Fatal(err)
		}
		got, err := server.GetNodeConfig(context.Background(), &madtomv1.GetNodeConfigRequest{NodeId: "node"})
		if err != nil || got.ProcessSnapshotLimit != limit || got.TopNProcesses != 3 {
			t.Fatalf("limit %d: %v %v", limit, got, err)
		}
	}
	_, err := server.UpdateNodeConfig(context.Background(), &madtomv1.UpdateNodeConfigRequest{NodeId: "node", Config: &madtomv1.NodeConfig{NodeId: "node", ProcessSnapshotLimit: 1001}})
	if status.Code(err) != codes.InvalidArgument {
		t.Fatalf("expected invalid argument: %v", err)
	}
	if reg.GetConfig("node").ProcessSnapshotLimit != 1000 {
		t.Fatal("invalid update changed saved configuration")
	}
}
