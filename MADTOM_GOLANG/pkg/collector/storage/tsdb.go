package storage

import (
	"encoding/binary"
	"fmt"
	"math"
	"os"
	"sync"

	"github.com/cockroachdb/pebble"
)

type Point struct {
	TimestampUnixNano int64
	Value             float64
}

// TSDB manages the embedded Pebble time-series storage engine.
type TSDB struct {
	mu sync.RWMutex
	db *pebble.DB
}

// OpenTSDB opens or creates a Pebble database at the specified directory.
func OpenTSDB(dir string) (*TSDB, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, fmt.Errorf("failed to create TSDB directory: %w", err)
	}

	opts := &pebble.Options{}
	db, err := pebble.Open(dir, opts)
	if err != nil {
		return nil, fmt.Errorf("failed to open Pebble database: %w", err)
	}

	return &TSDB{db: db}, nil
}

// PutMetric stores a single time-series metric value.
func (t *TSDB) PutMetric(nodeID string, metricName string, timestampNano int64, value float64) error {
	t.mu.RLock()
	defer t.mu.RUnlock()

	key := makeKey(nodeID, metricName, timestampNano)
	valBytes := make([]byte, 8)
	binary.BigEndian.PutUint64(valBytes, math.Float64bits(value))

	return t.db.Set(key, valBytes, pebble.Sync)
}

// PutMetricsBatch inserts multiple points in a single atomic Pebble batch.
func (t *TSDB) PutMetricsBatch(nodeID string, metricName string, points []Point) error {
	t.mu.RLock()
	defer t.mu.RUnlock()

	batch := t.db.NewBatch()
	defer batch.Close()

	valBytes := make([]byte, 8)
	for _, pt := range points {
		key := makeKey(nodeID, metricName, pt.TimestampUnixNano)
		binary.BigEndian.PutUint64(valBytes, math.Float64bits(pt.Value))
		if err := batch.Set(key, valBytes, nil); err != nil {
			return err
		}
	}

	return batch.Commit(pebble.Sync)
}

// QueryRange scans points for a specific node and metric within [startNano, endNano].
func (t *TSDB) QueryRange(nodeID string, metricName string, startNano, endNano int64) ([]Point, error) {
	t.mu.RLock()
	defer t.mu.RUnlock()

	startKey := makeKey(nodeID, metricName, startNano)
	endKey := makeKey(nodeID, metricName, endNano+1)

	iter, err := t.db.NewIter(&pebble.IterOptions{
		LowerBound: startKey,
		UpperBound: endKey,
	})
	if err != nil {
		return nil, err
	}
	defer iter.Close()

	var points []Point
	for iter.First(); iter.Valid(); iter.Next() {
		key := iter.Key()
		valBytes := iter.Value()

		if len(key) < 8 || len(valBytes) < 8 {
			continue
		}

		timeBytes := key[len(key)-8:]
		ts := int64(binary.BigEndian.Uint64(timeBytes))
		bits := binary.BigEndian.Uint64(valBytes)
		val := math.Float64frombits(bits)

		points = append(points, Point{
			TimestampUnixNano: ts,
			Value:             val,
		})
	}

	return points, iter.Error()
}

// Close gracefully closes the Pebble DB.
func (t *TSDB) Close() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.db != nil {
		return t.db.Close()
	}
	return nil
}

func makeKey(nodeID string, metricName string, timestampNano int64) []byte {
	prefix := fmt.Sprintf("%s/%s/", nodeID, metricName)
	key := make([]byte, len(prefix)+8)
	copy(key, prefix)
	binary.BigEndian.PutUint64(key[len(prefix):], uint64(timestampNano))
	return key
}
