package spool

import (
	"encoding/binary"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

func legacyFrame(t *testing.T, samples []*madtomv1.SystemMetrics, compressed bool) []byte {
	t.Helper()
	batch := &madtomv1.TelemetryBatch{Samples: samples}
	if compressed {
		raw, err := proto.Marshal(batch)
		if err != nil {
			t.Fatal(err)
		}
		enc, err := zstd.NewWriter(nil)
		if err != nil {
			t.Fatal(err)
		}
		batch = &madtomv1.TelemetryBatch{IsCompressed: true, CompressedPayload: enc.EncodeAll(raw, nil)}
		enc.Close()
	}
	raw, err := proto.Marshal(batch)
	if err != nil {
		t.Fatal(err)
	}
	frame := make([]byte, 4+len(raw))
	binary.BigEndian.PutUint32(frame, uint32(len(raw)))
	copy(frame[4:], raw)
	return frame
}

func TestMigrationLegacyPendingOrderAndIdempotence(t *testing.T) {
	for _, compressed := range []bool{false, true} {
		t.Run(map[bool]string{false: "raw", true: "zstd"}[compressed], func(t *testing.T) {
			dir := t.TempDir()
			name := "segment-00000007.wal"
			prefix := legacyFrame(t, []*madtomv1.SystemMetrics{{TimestampUnixNano: 1}}, compressed)
			var samples []*madtomv1.SystemMetrics
			for i := int64(2); i <= 6; i++ {
				samples = append(samples, &madtomv1.SystemMetrics{TimestampUnixNano: i, NodeId: strings.Repeat("x", 1<<20)})
			}
			original := append(prefix, legacyFrame(t, samples, compressed)...)
			if err := os.WriteFile(filepath.Join(dir, name), original, 0600); err != nil {
				t.Fatal(err)
			}
			w := &WALManager{dir: dir}
			if err := w.persistState(walState{LastSequence: 7, Acked: map[string]int64{name: int64(len(prefix))}}); err != nil {
				t.Fatal(err)
			}
			if err := Migrate(dir, "n", compressed); err != nil {
				t.Fatal(err)
			}
			stateBefore, _ := os.ReadFile(filepath.Join(dir, stateFile))
			if err := Migrate(dir, "n", compressed); err != nil {
				t.Fatal(err)
			}
			stateAfter, _ := os.ReadFile(filepath.Join(dir, stateFile))
			if string(stateBefore) != string(stateAfter) {
				t.Fatal("rerun changed state")
			}
			live, err := NewWALManager(dir, "n", 64<<20, false)
			if err != nil {
				t.Fatal(err)
			}
			defer live.Close()
			var got []int64
			for live.HasPendingBacklog() {
				b := pending(t, live, 500)
				if proto.Size(b) > DefaultChunkMaxBytes {
					t.Fatal("oversized replay")
				}
				for _, s := range b.Samples {
					got = append(got, s.TimestampUnixNano)
				}
				ack(t, live, b)
			}
			if len(got) != 5 {
				t.Fatalf("samples: %v", got)
			}
			for i, n := range got {
				if n != int64(i+2) {
					t.Fatalf("order: %v", got)
				}
			}
			if live.state.Version != 1 || live.state.LastSequence <= 7 {
				t.Fatal("version/sequence not advanced")
			}
		})
	}
}

func TestMigrationOversizedSingleSampleRetainsOriginal(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "segment-00000001.wal")
	original := legacyFrame(t, []*madtomv1.SystemMetrics{{NodeId: strings.Repeat("x", 4<<20)}}, false)
	if err := os.WriteFile(path, original, 0600); err != nil {
		t.Fatal(err)
	}
	if err := Migrate(dir, "n", false); err == nil || !strings.Contains(err.Error(), "single sample") {
		t.Fatalf("error: %v", err)
	}
	after, err := os.ReadFile(path)
	if err != nil || string(after) != string(original) {
		t.Fatal("original changed")
	}
	if _, err := os.Stat(filepath.Join(dir, stateFile)); !os.IsNotExist(err) {
		t.Fatal("version committed on failure")
	}
}

func TestMigrationResumeCutover(t *testing.T) {
	for _, phase := range []string{"staged", "installed", "committed", "removed"} {
		t.Run(phase, func(t *testing.T) {
			dir := t.TempDir()
			stage := filepath.Join(dir, migrationDir)
			if err := os.Mkdir(stage, 0700); err != nil {
				t.Fatal(err)
			}
			old, newName := "segment-00000001.wal", "segment-00000002.wal"
			frame := legacyFrame(t, []*madtomv1.SystemMetrics{{TimestampUnixNano: 42}}, false)
			if err := os.WriteFile(filepath.Join(dir, old), frame, 0600); err != nil {
				t.Fatal(err)
			}
			dest := stage
			if phase != "staged" {
				dest = dir
			}
			if err := os.WriteFile(filepath.Join(dest, newName), frame, 0600); err != nil {
				t.Fatal(err)
			}
			plan := migrationPlan{State: walState{Version: 1, LastSequence: 2, Acked: map[string]int64{old: int64(len(frame))}}, Originals: []string{old}, Replacements: []string{newName}}
			raw, _ := json.Marshal(plan)
			if err := os.WriteFile(filepath.Join(stage, "ready.json"), raw, 0600); err != nil {
				t.Fatal(err)
			}
			if phase == "committed" || phase == "removed" {
				w := &WALManager{dir: dir}
				if err := w.persistState(plan.State); err != nil {
					t.Fatal(err)
				}
			}
			if phase == "removed" {
				if err := os.Remove(filepath.Join(dir, old)); err != nil {
					t.Fatal(err)
				}
			}
			if w, err := NewWALManager(dir, "n", 0, false); err == nil {
				w.Close()
				t.Fatal("normal startup accepted unfinished migration")
			}
			if err := Migrate(dir, "n", false); err != nil {
				t.Fatal(err)
			}
			w := openWAL(t, dir, false)
			defer w.Close()
			b := pending(t, w, 500)
			if len(b.Samples) != 1 || b.Samples[0].TimestampUnixNano != 42 {
				t.Fatal("lost or duplicated sample")
			}
		})
	}
}

func TestMigrationVersionAndAbandonedStage(t *testing.T) {
	dir := t.TempDir()
	if err := os.Mkdir(filepath.Join(dir, migrationDir), 0700); err != nil {
		t.Fatal(err)
	}
	if err := Migrate(dir, "n", false); err != nil {
		t.Fatal(err)
	}
	w := &WALManager{dir: dir}
	if err := w.persistState(walState{Version: currentWALVersion + 1}); err != nil {
		t.Fatal(err)
	}
	if err := Migrate(dir, "n", false); err == nil {
		t.Fatal("accepted future version")
	}
	if live, err := NewWALManager(dir, "n", 0, false); err == nil {
		live.Close()
		t.Fatal("opened future version")
	}
}

func TestSpoolDirectoryExclusiveOwnership(t *testing.T) {
	dir := t.TempDir()
	first, err := LockDirectory(dir)
	if err != nil {
		t.Fatal(err)
	}
	if other, err := LockDirectory(dir); err == nil {
		other.Close()
		t.Fatal("concurrent owner admitted")
	}
	first.Close()
	next, err := LockDirectory(dir)
	if err != nil {
		t.Fatal(err)
	}
	next.Close()
}

func TestNormalStartupDoesNotMigrate(t *testing.T) {
	dir := t.TempDir()
	w := openWAL(t, dir, false)
	writeSample(t, w, 1)
	w.Close()
	raw, err := os.ReadFile(filepath.Join(dir, stateFile))
	if err != nil {
		t.Fatal(err)
	}
	var state walState
	if err := json.Unmarshal(raw, &state); err != nil {
		t.Fatal(err)
	}
	if state.Version != 0 {
		t.Fatal("normal startup advanced migration version")
	}
	w = openWAL(t, dir, false)
	defer w.Close()
	if w.state.Version != 0 {
		t.Fatal("reopen migrated")
	}
}
