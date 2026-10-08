package storage

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/cockroachdb/pebble"
	"google.golang.org/protobuf/proto"
)

// LogStats stores summary metrics for a node's log store.
type LogStats struct {
	FirstSeq     uint64 `json:"first_seq"`
	LastSeq      uint64 `json:"last_seq"`
	TotalRecords uint64 `json:"total_records"`
	OldestTs     int64  `json:"oldest_ts"`
	NewestTs     int64  `json:"newest_ts"`
	TotalBytes   uint64 `json:"total_bytes"`
}

// LogStore manages Pebble-backed storage for zstd-compressed LogChunks.
type LogStore struct {
	db    *pebble.DB
	mu    sync.RWMutex
	stats map[string]*LogStats
}

// NewLogStore creates a new LogStore wrapping an existing Pebble database.
func NewLogStore(db *pebble.DB) *LogStore {
	ls := &LogStore{
		db:    db,
		stats: make(map[string]*LogStats),
	}
	ls.loadAllStats()
	return ls
}

func logChunkKey(nodeID string, chunkID uint64) []byte {
	k := make([]byte, 2+len(nodeID)+1+8)
	k[0] = 'L'
	k[1] = 0
	copy(k[2:], nodeID)
	k[2+len(nodeID)] = 0
	binary.BigEndian.PutUint64(k[3+len(nodeID):], chunkID)
	return k
}

func logTimeKey(nodeID string, ts int64, chunkID uint64) []byte {
	k := make([]byte, 2+len(nodeID)+1+8+1+8)
	k[0] = 'T'
	k[1] = 0
	copy(k[2:], nodeID)
	k[2+len(nodeID)] = 0
	binary.BigEndian.PutUint64(k[3+len(nodeID):], uint64(ts))
	k[3+len(nodeID)+8] = 0
	binary.BigEndian.PutUint64(k[4+len(nodeID)+8:], chunkID)
	return k
}

func logStatsKey(nodeID string) []byte {
	return []byte(fmt.Sprintf("M\x00%s\x00stats", nodeID))
}

func (s *LogStore) loadAllStats() {
	prefix := []byte("M\x00")
	upper := []byte("M\x01")
	iter, err := s.db.NewIter(&pebble.IterOptions{
		LowerBound: prefix,
		UpperBound: upper,
	})
	if err != nil {
		return
	}
	defer iter.Close()

	for iter.First(); iter.Valid(); iter.Next() {
		var stats LogStats
		if err := json.Unmarshal(iter.Value(), &stats); err == nil {
			parts := bytes.Split(iter.Key(), []byte{0})
			if len(parts) == 3 && string(parts[0]) == "M" && string(parts[2]) == "stats" {
				nodeID := string(parts[1])
				if nodeID != "" {
					s.stats[nodeID] = &stats
				}
			}
		}
	}
}

// PutChunk persists a sealed LogChunk and updates the time index and stats.
func (s *LogStore) PutChunk(chunk *madtomv1.LogChunk) error {
	if chunk == nil || chunk.NodeId == "" {
		return nil
	}

	payload, err := proto.Marshal(chunk)
	if err != nil {
		return fmt.Errorf("marshal chunk: %w", err)
	}

	cKey := logChunkKey(chunk.NodeId, chunk.ChunkId)
	tKey := logTimeKey(chunk.NodeId, chunk.LastTsUnixNano, chunk.ChunkId)

	s.mu.Lock()
	defer s.mu.Unlock()

	var isDuplicate bool
	if _, closer, err := s.db.Get(cKey); err == nil {
		isDuplicate = true
		_ = closer.Close()
	}

	batch := s.db.NewBatch()
	defer batch.Close()

	if err := batch.Set(cKey, payload, nil); err != nil {
		return err
	}
	if err := batch.Set(tKey, []byte{}, nil); err != nil {
		return err
	}

	st, ok := s.stats[chunk.NodeId]
	if !ok {
		st = &LogStats{
			FirstSeq: chunk.FirstSeq,
			OldestTs: chunk.FirstTsUnixNano,
		}
		s.stats[chunk.NodeId] = st
	}

	if chunk.FirstSeq < st.FirstSeq || st.TotalRecords == 0 {
		st.FirstSeq = chunk.FirstSeq
	}
	if chunk.LastSeq >= st.LastSeq {
		st.LastSeq = chunk.LastSeq
	}
	if chunk.LastTsUnixNano > st.NewestTs {
		st.NewestTs = chunk.LastTsUnixNano
	}
	if st.OldestTs == 0 || chunk.FirstTsUnixNano < st.OldestTs {
		st.OldestTs = chunk.FirstTsUnixNano
	}
	if !isDuplicate {
		st.TotalRecords += uint64(chunk.RecordCount)
		st.TotalBytes += uint64(len(payload))
	}

	statBytes, _ := json.Marshal(st)
	if err := batch.Set(logStatsKey(chunk.NodeId), statBytes, nil); err != nil {
		return err
	}

	return batch.Commit(pebble.Sync)
}

// GetChunk loads a single LogChunk by ID.
func (s *LogStore) GetChunk(nodeID string, chunkID uint64) (*madtomv1.LogChunk, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	cKey := logChunkKey(nodeID, chunkID)
	val, closer, err := s.db.Get(cKey)
	if err != nil {
		return nil, err
	}
	defer closer.Close()

	var chunk madtomv1.LogChunk
	if err := proto.Unmarshal(val, &chunk); err != nil {
		return nil, err
	}
	return &chunk, nil
}

// QueryChunks scans up to maxChunks LogChunk entries starting at startChunkID or closest seek timestamp.
func (s *LogStore) QueryChunks(nodeID string, startChunkID uint64, maxChunks int, seekTs int64, forward bool) ([]*madtomv1.LogChunk, bool, error) {
	if maxChunks <= 0 {
		maxChunks = 8
	}
	if maxChunks > 32 {
		maxChunks = 32
	}

	s.mu.RLock()
	defer s.mu.RUnlock()

	if seekTs > 0 {
		// Seek by time index
		tPrefix := logTimeKey(nodeID, seekTs, 0)
		tUpper := []byte(fmt.Sprintf("T\x00%s\x01", nodeID))
		tIter, err := s.db.NewIter(&pebble.IterOptions{
			LowerBound: tPrefix,
			UpperBound: tUpper,
		})
		if err == nil {
			if tIter.First() && tIter.Valid() {
				k := tIter.Key()
				if len(k) >= 8 {
					startChunkID = binary.BigEndian.Uint64(k[len(k)-8:])
				}
			}
			tIter.Close()
		}
	}

	prefix := []byte(fmt.Sprintf("L\x00%s\x00", nodeID))
	upper := []byte(fmt.Sprintf("L\x00%s\x01", nodeID))
	startKey := logChunkKey(nodeID, startChunkID)

	iter, err := s.db.NewIter(&pebble.IterOptions{
		LowerBound: prefix,
		UpperBound: upper,
	})
	if err != nil {
		return nil, false, err
	}
	defer iter.Close()

	var chunks []*madtomv1.LogChunk
	hasMore := false

	if forward {
		if !iter.SeekGE(startKey) {
			return chunks, false, nil
		}
		for ; iter.Valid(); iter.Next() {
			if len(chunks) >= maxChunks {
				hasMore = true
				break
			}
			var chunk madtomv1.LogChunk
			if err := proto.Unmarshal(iter.Value(), &chunk); err == nil {
				chunks = append(chunks, &chunk)
			}
		}
	} else {
		if !iter.SeekLT(startKey) {
			return chunks, false, nil
		}
		for ; iter.Valid(); iter.Prev() {
			if len(chunks) >= maxChunks {
				hasMore = true
				break
			}
			var chunk madtomv1.LogChunk
			if err := proto.Unmarshal(iter.Value(), &chunk); err == nil {
				chunks = append(chunks, &chunk)
			}
		}
		for i, j := 0, len(chunks)-1; i < j; i, j = i+1, j-1 {
			chunks[i], chunks[j] = chunks[j], chunks[i]
		}
	}

	return chunks, hasMore, nil
}

// GetStats returns current log statistics for the specified node.
func (s *LogStore) GetStats(nodeID string) (*madtomv1.LogStatsResponse, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()

	st, ok := s.stats[nodeID]
	if !ok || st.TotalRecords == 0 {
		return &madtomv1.LogStatsResponse{
			NodeId:       nodeID,
			TotalRecords: 0,
		}, nil
	}

	return &madtomv1.LogStatsResponse{
		NodeId:           nodeID,
		FirstSeq:         st.FirstSeq,
		LastSeq:          st.LastSeq,
		TotalRecords:     st.TotalRecords,
		OldestTsUnixNano: st.OldestTs,
		NewestTsUnixNano: st.NewestTs,
		TotalBytes:       st.TotalBytes,
	}, nil
}

// EnforceRetention prunes expired or excess log chunks for a given node.
func (s *LogStore) EnforceRetention(nodeID string, maxBytes uint64, maxAgeHours uint32) error {
	var cutoffNano int64
	if maxAgeHours > 0 {
		cutoffNano = time.Now().Add(-time.Duration(maxAgeHours) * time.Hour).UnixNano()
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	st, ok := s.stats[nodeID]
	if !ok || st.TotalRecords == 0 {
		return nil
	}

	prefix := []byte(fmt.Sprintf("L\x00%s\x00", nodeID))
	upper := []byte(fmt.Sprintf("L\x00%s\x01", nodeID))

	iter, err := s.db.NewIter(&pebble.IterOptions{
		LowerBound: prefix,
		UpperBound: upper,
	})
	if err != nil {
		return err
	}
	defer iter.Close()

	batch := s.db.NewBatch()
	defer batch.Close()

	var deletedBytes uint64
	var deletedRecords uint64
	var newOldestTs int64
	var newFirstSeq uint64

	for iter.First(); iter.Valid(); iter.Next() {
		var chunk madtomv1.LogChunk
		if err := proto.Unmarshal(iter.Value(), &chunk); err != nil {
			continue
		}

		shouldDelete := false
		if cutoffNano > 0 && chunk.LastTsUnixNano < cutoffNano {
			shouldDelete = true
		} else if maxBytes > 0 && st.TotalBytes-deletedBytes > maxBytes {
			shouldDelete = true
		}

		if shouldDelete {
			_ = batch.Delete(iter.Key(), nil)
			tKey := logTimeKey(nodeID, chunk.LastTsUnixNano, chunk.ChunkId)
			_ = batch.Delete(tKey, nil)
			deletedBytes += uint64(len(iter.Value()))
			deletedRecords += uint64(chunk.RecordCount)
		} else {
			if newFirstSeq == 0 {
				newFirstSeq = chunk.FirstSeq
				newOldestTs = chunk.FirstTsUnixNano
			}
			if st.TotalBytes-deletedBytes <= maxBytes {
				break
			}
		}
	}

	if deletedRecords > 0 {
		if st.TotalRecords >= deletedRecords {
			st.TotalRecords -= deletedRecords
		} else {
			st.TotalRecords = 0
		}
		if st.TotalBytes >= deletedBytes {
			st.TotalBytes -= deletedBytes
		} else {
			st.TotalBytes = 0
		}
		st.FirstSeq = newFirstSeq
		st.OldestTs = newOldestTs

		statBytes, _ := json.Marshal(st)
		_ = batch.Set(logStatsKey(nodeID), statBytes, nil)
		return batch.Commit(pebble.Sync)
	}

	return nil
}
