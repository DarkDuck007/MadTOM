package storage

import (
	"context"
	"encoding/binary"
	"errors"
	"math"

	"github.com/cockroachdb/pebble"
)

var ErrQueryScanLimit = errors.New("historical query scan limit exceeded; narrow the time range")

// QueryRangeSampled retains O(target) points while scanning. Absolute-time buckets
// preserve original endpoints/extrema and do not shift when a fixed-width window moves.
// Unlike LTTB this is an extrema envelope, not triangle-area selection.
func (t *TSDB) QueryRangeSampled(ctx context.Context, node, metric string, start, end int64, target, maxScan int) ([]Point, error) {
	if start < 0 || end <= start || (target != 0 && target < 4) || maxScan < 1 {
		return nil, errors.New("invalid sampled query limits")
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	t.mu.RLock()
	defer t.mu.RUnlock()
	// Appending a zero includes the exact end key without overflowing MaxInt64.
	upper := append(makeKey(node, metric, end), 0)
	iter, err := t.db.NewIter(&pebble.IterOptions{LowerBound: makeKey(node, metric, start), UpperBound: upper})
	if err != nil {
		return nil, err
	}
	defer iter.Close()

	if !iter.First() {
		if err := iter.Error(); err != nil {
			return nil, err
		}
		return nil, nil
	}

	key := iter.Key()
	if len(key) >= 8 {
		firstTs := int64(binary.BigEndian.Uint64(key[len(key)-8:]))
		// If start is 0 (Unix epoch) or far before the earliest recorded data, clamp start to firstTs
		// to avoid degenerate bucket spans (e.g. 56-year buckets returning only 4-8 points).
		if firstTs > 1_000_000_000_000_000_000 && (start <= 0 || (start < firstTs && (end-start) > 2*(end-firstTs))) {
			start = firstTs
		}
	}

	if target <= 0 {
		raw := make([]Point, 0, 1024)
		scanned := 0
		for ; iter.Valid(); iter.Next() {
			if scanned%256 == 0 {
				if err := ctx.Err(); err != nil {
					return nil, err
				}
			}
			scanned++
			if scanned > maxScan {
				return nil, ErrQueryScanLimit
			}
			k, val := iter.Key(), iter.Value()
			if len(k) < 8 || len(val) != 8 {
				continue
			}
			p := Point{int64(binary.BigEndian.Uint64(k[len(k)-8:])), math.Float64frombits(binary.BigEndian.Uint64(val))}
			raw = append(raw, p)
		}
		if err := iter.Error(); err != nil {
			return nil, err
		}
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		return raw, nil
	}

	buckets := target/4 - 1
	if buckets < 1 {
		buckets = 1
	}
	span := end - start
	if span < 1 {
		span = 1
	}
	width := span / int64(buckets)
	if span%int64(buckets) != 0 {
		width++
	}
	if width < 1 {
		width = 1
	}
	raw := make([]Point, 0, target)
	sampled := make([]Point, 0, target)
	var first, last, low, high Point
	var bucket int64
	active, dense := false, false
	flush := func() {
		if !active {
			return
		}
		points := [4]Point{first, low, high, last}
		// Four items: insertion sort avoids reflection/interface allocations per bucket.
		for i := 1; i < len(points); i++ {
			for j := i; j > 0 && points[j].TimestampUnixNano < points[j-1].TimestampUnixNano; j-- {
				points[j], points[j-1] = points[j-1], points[j]
			}
		}
		for _, p := range points {
			if len(sampled) == 0 || sampled[len(sampled)-1].TimestampUnixNano != p.TimestampUnixNano {
				sampled = append(sampled, p)
			}
		}
	}
	scanned := 0
	for ; iter.Valid(); iter.Next() {
		if scanned%256 == 0 {
			if err := ctx.Err(); err != nil {
				return nil, err
			}
		}
		scanned++
		if scanned > maxScan {
			return nil, ErrQueryScanLimit
		}
		key, val := iter.Key(), iter.Value()
		if len(key) < 8 || len(val) != 8 {
			continue
		}
		p := Point{int64(binary.BigEndian.Uint64(key[len(key)-8:])), math.Float64frombits(binary.BigEndian.Uint64(val))}
		if !dense {
			if len(raw) < target {
				raw = append(raw, p)
			} else {
				dense = true
				raw = nil
			}
		}
		id := p.TimestampUnixNano / width
		if target < 8 {
			id = 0
		}
		if !active || id != bucket {
			flush()
			first, low, high, bucket, active = p, p, p, id, true
		}
		last = p
		if p.Value < low.Value {
			low = p
		}
		if p.Value > high.Value {
			high = p
		}
	}
	if err := iter.Error(); err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if !dense {
		return raw, nil
	}
	flush()
	return sampled, nil
}
