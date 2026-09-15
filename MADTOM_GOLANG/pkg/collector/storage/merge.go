package storage

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/cockroachdb/pebble"
)

// MergeStats summarizes the outcome of a TSDB merge operation.
type MergeStats struct {
	TotalSourceKeys   int64
	UniqueMergedKeys  int64
	SourceCounts      map[string]int64
	EarliestTimestamp time.Time
	LatestTimestamp   time.Time
	NodeIDs           []string
}

// MergeOptions configures the database merge behavior.
type MergeOptions struct {
	BatchSize int  // Number of records per atomic batch write (default 10000)
	Force     bool // Overwrite or populate non-empty destination directory
	LogWriter io.Writer
}

// ParseKey extracts the node ID, metric name, and timestamp from a TSDB key.
func ParseKey(key []byte) (nodeID string, metricName string, timestampNano int64, ok bool) {
	if len(key) < 8 {
		return "", "", 0, false
	}
	timeBytes := key[len(key)-8:]
	ts := int64(binary.BigEndian.Uint64(timeBytes))
	prefix := string(key[:len(key)-8])
	prefix = strings.TrimSuffix(prefix, "/")
	nodeID, metricName, found := strings.Cut(prefix, "/")
	if !found {
		return prefix, "", ts, true
	}
	return nodeID, metricName, ts, true
}

// MergeDatabases copies all key-value entries from multiple source Pebble TSDBs into destDir.
func MergeDatabases(srcDirs []string, destDir string, opts MergeOptions) (*MergeStats, error) {
	if len(srcDirs) == 0 {
		return nil, fmt.Errorf("at least one source directory is required")
	}
	if destDir == "" {
		return nil, fmt.Errorf("destination directory cannot be empty")
	}

	absDest, err := filepath.Abs(destDir)
	if err != nil {
		return nil, fmt.Errorf("invalid destination directory: %w", err)
	}

	for _, src := range srcDirs {
		absSrc, err := filepath.Abs(src)
		if err != nil {
			return nil, fmt.Errorf("invalid source directory %q: %w", src, err)
		}
		if absSrc == absDest {
			return nil, fmt.Errorf("source directory %q cannot be identical to destination directory", src)
		}
	}

	// Verify destination directory
	if entries, err := os.ReadDir(absDest); err == nil && len(entries) > 0 && !opts.Force {
		return nil, fmt.Errorf("destination directory %q already exists and is not empty; use -force to proceed", destDir)
	}

	if opts.BatchSize <= 0 {
		opts.BatchSize = 10000
	}

	if err := os.MkdirAll(absDest, 0755); err != nil {
		return nil, fmt.Errorf("failed to create destination directory: %w", err)
	}

	destDB, err := pebble.Open(absDest, &pebble.Options{})
	if err != nil {
		return nil, fmt.Errorf("failed to open destination database: %w", err)
	}
	defer destDB.Close()

	stats := &MergeStats{
		SourceCounts: make(map[string]int64),
	}
	nodeSet := make(map[string]struct{})
	var minTs, maxTs int64

	batch := destDB.NewBatch()
	defer batch.Close()

	for idx, srcDir := range srcDirs {
		absSrc, _ := filepath.Abs(srcDir)
		if opts.LogWriter != nil {
			fmt.Fprintf(opts.LogWriter, "[%d/%d] Reading source: %s\n", idx+1, len(srcDirs), srcDir)
		}

		srcDB, err := pebble.Open(absSrc, &pebble.Options{ReadOnly: true})
		if err != nil {
			return nil, fmt.Errorf("failed to open source database %q: %w", srcDir, err)
		}

		iter, err := srcDB.NewIter(nil)
		if err != nil {
			srcDB.Close()
			return nil, fmt.Errorf("failed to create iterator for %q: %w", srcDir, err)
		}

		var count int64
		for iter.First(); iter.Valid(); iter.Next() {
			k := iter.Key()
			v := iter.Value()

			if err := batch.Set(k, v, nil); err != nil {
				iter.Close()
				srcDB.Close()
				return nil, fmt.Errorf("failed to stage record in batch: %w", err)
			}

			if nodeID, _, ts, ok := ParseKey(k); ok {
				if nodeID != "" {
					nodeSet[nodeID] = struct{}{}
				}
				if ts > 0 {
					if minTs == 0 || ts < minTs {
						minTs = ts
					}
					if ts > maxTs {
						maxTs = ts
					}
				}
			}

			count++
			if count%int64(opts.BatchSize) == 0 {
				if err := batch.Commit(pebble.Sync); err != nil {
					iter.Close()
					srcDB.Close()
					return nil, fmt.Errorf("failed to commit batch to destination: %w", err)
				}
				batch.Reset()
				if opts.LogWriter != nil {
					fmt.Fprintf(opts.LogWriter, "  -> Copied %d records from %s...\n", count, filepath.Base(srcDir))
				}
			}
		}

		if err := iter.Error(); err != nil {
			iter.Close()
			srcDB.Close()
			return nil, fmt.Errorf("iterator error on %q: %w", srcDir, err)
		}

		iter.Close()
		srcDB.Close()

		stats.SourceCounts[srcDir] = count
		stats.TotalSourceKeys += count
		if opts.LogWriter != nil {
			fmt.Fprintf(opts.LogWriter, "  -> Finished %s: %d records.\n", srcDir, count)
		}
	}

	// Commit any remaining staged records
	if batch.Count() > 0 {
		if err := batch.Commit(pebble.Sync); err != nil {
			return nil, fmt.Errorf("failed to commit final batch: %w", err)
		}
	}

	// Verify destination unique key count
	destIter, err := destDB.NewIter(nil)
	if err != nil {
		return nil, fmt.Errorf("failed to open verification iterator on destination: %w", err)
	}
	var uniqueKeys int64
	for destIter.First(); destIter.Valid(); destIter.Next() {
		uniqueKeys++
	}
	destIter.Close()
	stats.UniqueMergedKeys = uniqueKeys

	if minTs > 0 {
		stats.EarliestTimestamp = time.Unix(0, minTs)
	}
	if maxTs > 0 {
		stats.LatestTimestamp = time.Unix(0, maxTs)
	}

	for node := range nodeSet {
		stats.NodeIDs = append(stats.NodeIDs, node)
	}
	sort.Strings(stats.NodeIDs)

	return stats, nil
}
