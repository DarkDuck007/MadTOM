package storage

import (
	"context"
	"errors"
	"math"
	"reflect"
	"testing"
)

func TestSampledQueryBoundsPeaksAndStableBuckets(t *testing.T) {
	db, err := OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	points := make([]Point, 20000)
	for i := range points {
		points[i] = Point{int64(i+1) * 100000000, math.Sin(float64(i) * 17.13)}
	}
	points[501].Value = 999
	points[502].Value = -999
	if err := db.PutMetricsBatch("n", "m", points); err != nil {
		t.Fatal(err)
	}
	var baseline []Point
	for tick := int64(0); tick < 10; tick++ {
		got, err := db.QueryRangeSampled(context.Background(), "n", "m", tick*1000000000, (1800+tick)*1000000000, 750, 30000)
		if err != nil {
			t.Fatal(err)
		}
		if len(got) > 750 {
			t.Fatalf("over budget: %d", len(got))
		}
		high, low := false, false
		var interior []Point
		for i, p := range got {
			if i > 0 && p.TimestampUnixNano <= got[i-1].TimestampUnixNano {
				t.Fatal("not ordered")
			}
			high = high || p.Value == 999
			low = low || p.Value == -999
			if p.TimestampUnixNano > 100000000000 && p.TimestampUnixNano < 1700000000000 {
				interior = append(interior, p)
			}
		}
		if !high || !low {
			t.Fatal("lost extrema")
		}
		if tick == 0 {
			baseline = interior
		} else if !reflect.DeepEqual(baseline, interior) {
			t.Fatal("moving window changed interior buckets")
		}
	}
	sparse, err := db.QueryRangeSampled(context.Background(), "n", "m", 0, 1000000000, 750, 30000)
	if err != nil || !reflect.DeepEqual(sparse, points[:10]) {
		t.Fatalf("sparse changed: %v", err)
	}
	_, err = db.QueryRangeSampled(context.Background(), "n", "m", 0, 2000000000000, 750, 10)
	if !errors.Is(err, ErrQueryScanLimit) {
		t.Fatalf("expected scan limit: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err = db.QueryRangeSampled(ctx, "n", "m", 0, 2000000000000, 750, 30000)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("expected cancellation: %v", err)
	}
}

func TestSampledQueryInclusiveMaxTimestampAndSmallBudgets(t *testing.T) {
	db, err := OpenTSDB(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	points := make([]Point, 30)
	for i := range points {
		points[i] = Point{math.MaxInt64 - int64(29-i), float64(i)}
	}
	if err := db.PutMetricsBatch("n", "m", points); err != nil {
		t.Fatal(err)
	}
	for budget := 4; budget < 16; budget++ {
		got, err := db.QueryRangeSampled(context.Background(), "n", "m", math.MaxInt64-30, math.MaxInt64, budget, 100)
		if err != nil || len(got) > budget {
			t.Fatalf("budget %d: %d %v", budget, len(got), err)
		}
		if got[0] != points[0] || got[len(got)-1] != points[29] {
			t.Fatal("lost endpoints")
		}
	}
}

func BenchmarkHistoryQueryMemory(b *testing.B) {
	db, err := OpenTSDB(b.TempDir())
	if err != nil {
		b.Fatal(err)
	}
	defer db.Close()
	points := make([]Point, 100000)
	for i := range points {
		points[i] = Point{int64(i + 1), float64(i % 100)}
	}
	if err := db.PutMetricsBatch("n", "m", points); err != nil {
		b.Fatal(err)
	}
	b.Run("raw_100k", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := db.QueryRange("n", "m", 0, 100001); err != nil {
				b.Fatal(err)
			}
		}
	})
	b.Run("bounded_750", func(b *testing.B) {
		b.ReportAllocs()
		for i := 0; i < b.N; i++ {
			if _, err := db.QueryRangeSampled(context.Background(), "n", "m", 0, 100001, 750, 200000); err != nil {
				b.Fatal(err)
			}
		}
	})
}
