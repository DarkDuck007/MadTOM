package storage

import (
	"os"
	"testing"
	"time"
)

func TestMergeDatabases(t *testing.T) {
	dir1 := t.TempDir()
	dir2 := t.TempDir()
	destDir := t.TempDir() + "/merged"

	// 1. Populate DB 1
	db1, err := OpenTSDB(dir1)
	if err != nil {
		t.Fatalf("failed to open db1: %v", err)
	}
	for i := 1; i <= 100; i++ {
		ts := int64(i * 1000)
		if err := db1.PutMetric("node-1", "cpu.total", ts, float64(i)); err != nil {
			t.Fatal(err)
		}
		if err := db1.PutMetric("node-2", "mem.used", ts, float64(i*10)); err != nil {
			t.Fatal(err)
		}
	}
	db1.Close()

	// 2. Populate DB 2 (with some overlapping timestamps and some new)
	db2, err := OpenTSDB(dir2)
	if err != nil {
		t.Fatalf("failed to open db2: %v", err)
	}
	for i := 50; i <= 150; i++ {
		ts := int64(i * 1000)
		// Overlapping node-1 metric
		if err := db2.PutMetric("node-1", "cpu.total", ts, float64(i)); err != nil {
			t.Fatal(err)
		}
		// New node-3 metric
		if err := db2.PutMetric("node-3", "disk.io", ts, float64(i*100)); err != nil {
			t.Fatal(err)
		}
	}
	db2.Close()

	// 3. Merge DB1 and DB2 into destDir
	stats, err := MergeDatabases([]string{dir1, dir2}, destDir, MergeOptions{
		BatchSize: 50,
		LogWriter: os.Stdout,
	})
	if err != nil {
		t.Fatalf("MergeDatabases failed: %v", err)
	}

	if stats.TotalSourceKeys != 200+202 { // db1: 100+100=200; db2: 101+101=202
		t.Errorf("expected 402 source keys, got %d", stats.TotalSourceKeys)
	}

	// Unique keys:
	// node-1 cpu.total: 1..150 = 150 keys
	// node-2 mem.used: 1..100 = 100 keys
	// node-3 disk.io: 50..150 = 101 keys
	// Total unique = 150 + 100 + 101 = 351 keys
	if stats.UniqueMergedKeys != 351 {
		t.Errorf("expected 351 unique keys, got %d", stats.UniqueMergedKeys)
	}

	if len(stats.NodeIDs) != 3 || stats.NodeIDs[0] != "node-1" || stats.NodeIDs[1] != "node-2" || stats.NodeIDs[2] != "node-3" {
		t.Errorf("unexpected nodeIDs: %+v", stats.NodeIDs)
	}

	if stats.EarliestTimestamp.UnixNano() != 1000 {
		t.Errorf("expected min ts 1000, got %v", stats.EarliestTimestamp.UnixNano())
	}
	if stats.LatestTimestamp.UnixNano() != 150000 {
		t.Errorf("expected max ts 150000, got %v", stats.LatestTimestamp.UnixNano())
	}

	// 4. Query merged database using OpenTSDB
	mergedDB, err := OpenTSDB(destDir)
	if err != nil {
		t.Fatalf("failed to open merged db: %v", err)
	}
	defer mergedDB.Close()

	pts, err := mergedDB.QueryRange("node-1", "cpu.total", 0, time.Now().UnixNano())
	if err != nil {
		t.Fatalf("QueryRange failed: %v", err)
	}
	if len(pts) != 150 {
		t.Errorf("expected 150 points for node-1 cpu.total, got %d", len(pts))
	}
}

func TestMergeDatabasesValidation(t *testing.T) {
	dir1 := t.TempDir()

	// Source equals dest
	_, err := MergeDatabases([]string{dir1}, dir1, MergeOptions{})
	if err == nil {
		t.Fatal("expected error when source == dest")
	}

	// Empty sources
	_, err = MergeDatabases([]string{}, t.TempDir(), MergeOptions{})
	if err == nil {
		t.Fatal("expected error when no sources provided")
	}
}
