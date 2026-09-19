package spool

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

const stateFile = "wal-state.json"

type walState struct {
	Version      int              `json:"version"`
	LastSequence int64            `json:"last_sequence"`
	Acked        map[string]int64 `json:"acked_offsets"`
}

func (s walState) clone() walState {
	next := walState{Version: s.Version, LastSequence: s.LastSequence, Acked: make(map[string]int64, len(s.Acked))}
	for name, offset := range s.Acked {
		next.Acked[name] = offset
	}
	return next
}
func syncDirectory(path string) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	return f.Sync()
}
func (w *WALManager) persistState(state walState) error {
	raw, err := json.Marshal(state)
	if err != nil {
		return err
	}
	tmp := filepath.Join(w.dir, stateFile+".tmp")
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	defer os.Remove(tmp)
	n, err := f.Write(raw)
	if err == nil && n != len(raw) {
		err = io.ErrShortWrite
	}
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	if err := os.Rename(tmp, filepath.Join(w.dir, stateFile)); err != nil {
		return err
	}
	return syncDirectory(w.dir)
}

func (w *WALManager) recover() error {
	w.state = walState{Acked: make(map[string]int64)}
	raw, err := os.ReadFile(filepath.Join(w.dir, stateFile))
	if err == nil {
		if err := json.Unmarshal(raw, &w.state); err != nil {
			return fmt.Errorf("read WAL state: %w", err)
		}
		if w.state.Version < 0 || w.state.Version > currentWALVersion {
			return fmt.Errorf("unsupported WAL version %d", w.state.Version)
		}
		if w.state.LastSequence < 0 {
			return fmt.Errorf("invalid WAL sequence state")
		}
		if w.state.Acked == nil {
			w.state.Acked = make(map[string]int64)
		}
	} else if !os.IsNotExist(err) {
		return err
	}
	w.segments, w.totalSpoolBytes, err = w.scanDirectory()
	if err != nil {
		return err
	}
	present := make(map[string]bool)
	for i := range w.segments {
		seg := &w.segments[i]
		present[seg.name] = true
		if seg.seq > w.state.LastSequence {
			w.state.LastSequence = seg.seq
		}
		valid, torn, err := scanRecordEnds(seg.path, seg.size)
		if err != nil {
			return fmt.Errorf("recover %s: %w", seg.name, err)
		}
		if torn {
			if i != len(w.segments)-1 {
				return fmt.Errorf("incomplete record in sealed WAL segment %s", seg.name)
			}
			if w.state.Acked[seg.name] > valid {
				return fmt.Errorf("acknowledged WAL prefix is incomplete")
			}
			f, err := os.OpenFile(seg.path, os.O_WRONLY, 0)
			if err != nil {
				return err
			}
			err = f.Truncate(valid)
			if err == nil {
				err = f.Sync()
			}
			closeErr := f.Close()
			if err != nil {
				return err
			}
			if closeErr != nil {
				return closeErr
			}
			w.totalSpoolBytes -= seg.size - valid
			seg.size = valid
		}
		ack := w.state.Acked[seg.name]
		if ack < 0 || ack > seg.size {
			return fmt.Errorf("invalid persisted WAL offset")
		}
		if ack > 0 {
			ok, err := recordBoundary(seg.path, 0, ack)
			if err != nil {
				return err
			}
			if !ok {
				return fmt.Errorf("persisted WAL offset is not a record boundary")
			}
		}
	}
	for name := range w.state.Acked {
		if !present[name] {
			delete(w.state.Acked, name)
		}
	}
	w.currentSeq = w.state.LastSequence
	if len(w.segments) > 0 {
		last := w.segments[len(w.segments)-1]
		w.currentFile, err = os.OpenFile(last.path, os.O_WRONLY|os.O_APPEND, 0)
		if err != nil {
			return err
		}
		w.currentBytes = last.size
		w.currentOffset = last.size
	}
	if err := w.removeAcknowledgedLocked(); err != nil {
		if w.currentFile != nil {
			w.currentFile.Close()
		}
		return err
	}
	return nil
}

// Structural recovery never skips corrupt records. Only an incomplete final frame
// in the newest segment may be truncated; complete bad protobuf/zstd data remains an error.
func scanRecordEnds(path string, size int64) (int64, bool, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, false, err
	}
	defer f.Close()
	var header [4]byte
	for offset := int64(0); offset < size; {
		if size-offset < 4 {
			return offset, true, nil
		}
		if _, err := f.ReadAt(header[:], offset); err != nil {
			return offset, false, err
		}
		length := int64(binary.BigEndian.Uint32(header[:]))
		if length <= 0 || length > MaxRecordBytes {
			return offset, false, fmt.Errorf("invalid WAL record length")
		}
		if length > size-offset-4 {
			return offset, true, nil
		}
		offset += 4 + length
	}
	return size, false, nil
}
func recordBoundary(path string, start, end int64) (bool, error) {
	f, err := os.Open(path)
	if err != nil {
		return false, err
	}
	defer f.Close()
	var header [4]byte
	for start < end {
		if _, err := f.ReadAt(header[:], start); err != nil {
			return false, err
		}
		length := int64(binary.BigEndian.Uint32(header[:]))
		if length <= 0 || length > MaxRecordBytes {
			return false, fmt.Errorf("invalid WAL record length")
		}
		start += 4 + length
	}
	return start == end, nil
}
