package api

import (
	"context"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"testing"
)

func TestQueryAdmissionCancellationAndBudget(t *testing.T) {
	db, err := storage.OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	s := NewServer("test", db, nil, nil)
	req := &madtomv1.RangeQueryRequest{NodeId: "n", MetricName: "m", EndTimeUnixNano: 100, TargetPoints: 4}
	for i := 0; i < MaxConcurrentHistoryQueries; i++ {
		s.historySlots <- struct{}{}
	}
	if _, err = s.QueryRange(context.Background(), req); status.Code(err) != codes.ResourceExhausted {
		t.Fatalf("overload: %v", err)
	}
	<-s.historySlots
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err = s.QueryRange(ctx, req); status.Code(err) != codes.Canceled {
		t.Fatalf("cancel: %v", err)
	}
	if _, err = s.QueryRange(context.Background(), req); err != nil {
		t.Fatal(err)
	}
	if len(s.historySlots) != MaxConcurrentHistoryQueries-1 {
		t.Fatal("slot leak")
	}
	req.TargetPoints = 2
	if _, err = s.QueryRange(context.Background(), req); status.Code(err) != codes.InvalidArgument {
		t.Fatalf("budget: %v", err)
	}
}
