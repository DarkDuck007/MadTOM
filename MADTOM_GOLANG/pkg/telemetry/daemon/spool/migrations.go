package spool

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/klauspost/compress/zstd"
	"google.golang.org/protobuf/proto"
)

const currentWALVersion = 1
const migrationDir = ".migration"

// Ordered, append-only migration registry. A step must durably commit its version.
var walMigrations = []func(string, string, bool) error{migrateReplayRecords}

// Migrate runs only when explicitly requested, before opening the WAL or transports.
// The daemon must have exclusive ownership of dir for the entire operation.
func Migrate(dir, nodeID string, compressed bool) error {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return err
	}
	stage := filepath.Join(dir, migrationDir)
	if _, err := os.Stat(stage); err == nil {
		if _, err := os.Stat(filepath.Join(stage, "ready.json")); err == nil {
			if err := finishMigration(dir); err != nil {
				return err
			}
		} else if os.IsNotExist(err) {
			// No durable commit intent: the original files have never been touched.
			if err := os.RemoveAll(stage); err != nil {
				return err
			}
			if err := syncDirectory(dir); err != nil {
				return err
			}
		} else {
			return err
		}
	} else if !os.IsNotExist(err) {
		return err
	}
	for {
		state := walState{}
		raw, err := os.ReadFile(filepath.Join(dir, stateFile))
		if err == nil {
			if err := json.Unmarshal(raw, &state); err != nil {
				return err
			}
		} else if !os.IsNotExist(err) {
			return err
		}
		if state.Version < 0 || state.Version > currentWALVersion {
			return fmt.Errorf("unsupported WAL version %d", state.Version)
		}
		if state.Version == currentWALVersion {
			return nil
		}
		if err := walMigrations[state.Version](dir, nodeID, compressed); err != nil {
			return fmt.Errorf("WAL migration %d -> %d: %w", state.Version, state.Version+1, err)
		}
	}
}

type migrationPlan struct {
	State        walState `json:"state"`
	Originals    []string `json:"originals"`
	Replacements []string `json:"replacements"`
}

func migrateReplayRecords(dir, nodeID string, compressed bool) (result error) {
	source := &WALManager{dir: dir}
	source.state = walState{Acked: map[string]int64{}}
	raw, err := os.ReadFile(filepath.Join(dir, stateFile))
	if err == nil {
		if err := json.Unmarshal(raw, &source.state); err != nil {
			return err
		}
	} else if !os.IsNotExist(err) {
		return err
	}
	source.segments, _, err = source.scanDirectory()
	if err != nil {
		return err
	}
	if source.state.LastSequence < 0 {
		return fmt.Errorf("invalid WAL sequence")
	}
	for _, seg := range source.segments {
		if seg.seq > source.state.LastSequence {
			source.state.LastSequence = seg.seq
		}
	}
	source.decoder, err = zstd.NewReader(nil, zstd.WithDecoderConcurrency(1), zstd.WithDecoderMaxMemory(MaxRecordBytes))
	if err != nil {
		return err
	}
	defer source.decoder.Close()
	stage := filepath.Join(dir, migrationDir)
	if err := os.Mkdir(stage, 0700); err != nil {
		return err
	}
	if err := syncDirectory(dir); err != nil {
		return err
	}
	ready := false
	defer func() {
		if !ready {
			if err := os.RemoveAll(stage); err != nil && result == nil {
				result = err
			}
		}
	}()
	target, err := NewWALManager(stage, nodeID, 1<<63-1, compressed)
	if err != nil {
		return err
	}
	defer target.Close()
	target.state.LastSequence = source.state.LastSequence
	target.currentSeq = source.state.LastSequence
	plan := migrationPlan{}
	for _, seg := range source.segments {
		plan.Originals = append(plan.Originals, seg.name)
		// Migration is stricter than normal torn-tail recovery: never modify originals.
		_, torn, err := scanRecordEnds(seg.path, seg.size)
		if err != nil {
			return err
		}
		if torn {
			return fmt.Errorf("incomplete record in %s; originals retained", seg.name)
		}
		offset := source.state.Acked[seg.name]
		if offset < 0 || offset > seg.size {
			return fmt.Errorf("invalid ACK in %s", seg.name)
		}
		ok, err := recordBoundary(seg.path, 0, offset)
		if err != nil {
			return err
		}
		if !ok {
			return fmt.Errorf("invalid ACK boundary in %s", seg.name)
		}
		if err := migrateSegment(source, target, seg, offset); err != nil {
			return err
		}
	}
	if err := target.Close(); err != nil {
		return err
	}
	plan.State = target.state.clone()
	plan.State.Version = 1
	for _, seg := range source.segments {
		plan.State.Acked[seg.name] = seg.size
	}
	for _, seg := range target.segments {
		plan.Replacements = append(plan.Replacements, seg.name)
	}
	// Atomic ready marker distinguishes safe-to-discard staging from resumable cutover.
	raw, err = json.Marshal(plan)
	if err != nil {
		return err
	}
	path := filepath.Join(stage, "ready.tmp")
	if err := writeSynced(path, raw); err != nil {
		return err
	}
	if err := os.Rename(path, filepath.Join(stage, "ready.json")); err != nil {
		return err
	}
	// Once renamed, retain the transaction even if directory fsync reports failure.
	ready = true
	if err := syncDirectory(stage); err != nil {
		return err
	}
	return finishMigration(dir)
}

func migrateSegment(source, target *WALManager, seg segmentMeta, offset int64) error {
	f, err := os.Open(seg.path)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := f.Seek(offset, io.SeekStart); err != nil {
		return err
	}
	for offset < seg.size {
		record, next, err := source.readRecord(f, offset, seg.size)
		if err != nil {
			return fmt.Errorf("%s at %d: %w", seg.name, offset, err)
		}
		var chunk []*madtomv1.SystemMetrics
		size := proto.Size(&madtomv1.TelemetryBatch{NodeId: target.nodeID})
		base := size
		for _, sample := range record.Samples {
			cost := proto.Size(&madtomv1.TelemetryBatch{Samples: []*madtomv1.SystemMetrics{sample}})
			if base+cost > DefaultChunkMaxBytes-1024 {
				return fmt.Errorf("%s at %d: single sample exceeds replay limit; originals retained", seg.name, offset)
			}
			if size+cost > DefaultChunkMaxBytes-1024 {
				if _, err := target.WriteMetrics(chunk); err != nil {
					return err
				}
				chunk = nil
				size = base
			}
			chunk = append(chunk, sample)
			size += cost
		}
		if len(chunk) > 0 {
			if _, err := target.WriteMetrics(chunk); err != nil {
				return err
			}
		}
		offset = next
	}
	return nil
}

func writeSynced(path string, data []byte) error {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	n, err := f.Write(data)
	if err == nil && n != len(data) {
		err = io.ErrShortWrite
	}
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	return closeErr
}

func finishMigration(dir string) error {
	stage := filepath.Join(dir, migrationDir)
	raw, err := os.ReadFile(filepath.Join(stage, "ready.json"))
	if err != nil {
		return err
	}
	var plan migrationPlan
	if err := json.Unmarshal(raw, &plan); err != nil {
		return err
	}
	if plan.State.Version != 1 {
		return fmt.Errorf("unsupported pending migration version")
	}
	for _, name := range append(append([]string{}, plan.Originals...), plan.Replacements...) {
		if _, err := parseSegment(name); err != nil {
			return err
		}
	}
	for _, name := range plan.Replacements {
		from, to := filepath.Join(stage, name), filepath.Join(dir, name)
		if _, err := os.Stat(from); err == nil {
			if err := os.Rename(from, to); err != nil {
				return err
			}
		} else if os.IsNotExist(err) {
			if _, err := os.Stat(to); err != nil {
				return err
			}
		} else {
			return err
		}
	}
	if err := syncDirectory(dir); err != nil {
		return err
	}
	if err := syncDirectory(stage); err != nil {
		return err
	}
	w := &WALManager{dir: dir}
	if err := w.persistState(plan.State); err != nil {
		return err
	}
	for _, name := range plan.Originals {
		if err := os.Remove(filepath.Join(dir, name)); err != nil && !os.IsNotExist(err) {
			return err
		}
	}
	if err := syncDirectory(dir); err != nil {
		return err
	}
	// Remove ready last: a crash during cleanup must retain enough information to resume.
	for _, name := range []string{stateFile, stateFile + ".tmp"} {
		if err := os.Remove(filepath.Join(stage, name)); err != nil && !os.IsNotExist(err) {
			return err
		}
	}
	if err := os.Remove(filepath.Join(stage, "ready.json")); err != nil {
		return err
	}
	if err := syncDirectory(stage); err != nil {
		return err
	}
	if err := os.Remove(stage); err != nil {
		return err
	}
	return syncDirectory(dir)
}
