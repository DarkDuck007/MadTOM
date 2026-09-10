package storage

import (
	"os"
	"testing"
	"time"
)

func TestPebbleTSDB(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-tsdb-test-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	db, err := OpenTSDB(tempDir)
	if err != nil {
		t.Fatalf("failed to open TSDB: %v", err)
	}
	defer db.Close()

	baseTime := time.Now().UnixNano()
	var inserted []Point
	for i := 0; i < 100; i++ {
		ts := baseTime + int64(i)*1_000_000_000 // 1s increments
		val := float64(i * 2)
		inserted = append(inserted, Point{TimestampUnixNano: ts, Value: val})
	}

	if err := db.PutMetricsBatch("node-1", "cpu.total", inserted); err != nil {
		t.Fatalf("batch put failed: %v", err)
	}

	// Query range covering items 10 through 20
	start := baseTime + 10*1_000_000_000
	end := baseTime + 20*1_000_000_000

	points, err := db.QueryRange("node-1", "cpu.total", start, end)
	if err != nil {
		t.Fatalf("query range failed: %v", err)
	}

	if len(points) != 11 {
		t.Fatalf("expected 11 points in range [10..20], got %d", len(points))
	}

	if points[0].Value != 20.0 || points[10].Value != 40.0 {
		t.Fatalf("unexpected point values: first=%f, last=%f", points[0].Value, points[10].Value)
	}
}

func TestPutRecordsBatch(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom-tsdb-multitest-*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	db, err := OpenTSDB(tempDir)
	if err != nil {
		t.Fatalf("failed to open TSDB: %v", err)
	}
	defer db.Close()

	baseTime := time.Now().UnixNano()
	var records []MetricRecord
	for i := 0; i < 50; i++ {
		ts := baseTime + int64(i)*1_000_000_000
		records = append(records, MetricRecord{
			NodeID:        "node-A",
			MetricName:    "cpu.total",
			TimestampNano: ts,
			Value:         float64(i),
		})
		records = append(records, MetricRecord{
			NodeID:        "node-A",
			MetricName:    "memory.used",
			TimestampNano: ts,
			Value:         float64(i * 1024),
		})
	}

	if err := db.PutRecordsBatch(records); err != nil {
		t.Fatalf("PutRecordsBatch failed: %v", err)
	}

	cpuPts, err := db.QueryRange("node-A", "cpu.total", baseTime, baseTime+50*1_000_000_000)
	if err != nil || len(cpuPts) != 50 {
		t.Fatalf("expected 50 cpu points, got %d, err: %v", len(cpuPts), err)
	}

	memPts, err := db.QueryRange("node-A", "memory.used", baseTime, baseTime+50*1_000_000_000)
	if err != nil || len(memPts) != 50 {
		t.Fatalf("expected 50 mem points, got %d, err: %v", len(memPts), err)
	}
}
