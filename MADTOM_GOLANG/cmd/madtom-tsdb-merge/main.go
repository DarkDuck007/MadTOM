package main

import (
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/storage"
)

func main() {
	var srcFlag string
	var destFlag string
	var batchSize int
	var force bool

	flag.StringVar(&srcFlag, "src", "", "Comma-separated list of source database directories to merge")
	flag.StringVar(&destFlag, "dest", "", "Destination directory for the merged database")
	flag.IntVar(&batchSize, "batch-size", 10000, "Number of records per commit batch")
	flag.BoolVar(&force, "force", false, "Allow writing into an existing non-empty destination directory")

	flag.Usage = func() {
		fmt.Fprintf(os.Stderr, "MADTOM TSDB Merge Tool\n\n")
		fmt.Fprintf(os.Stderr, "Merges multiple CockroachDB Pebble TSDB directories into a single unified, deduplicated database.\n\n")
		fmt.Fprintf(os.Stderr, "Usage:\n")
		fmt.Fprintf(os.Stderr, "  madtom-tsdb-merge [flags] <source1> <source2> [source3...] <destination>\n")
		fmt.Fprintf(os.Stderr, "  madtom-tsdb-merge -src <dir1>,<dir2> -dest <destination>\n\n")
		fmt.Fprintf(os.Stderr, "Flags:\n")
		flag.PrintDefaults()
		fmt.Fprintf(os.Stderr, "\nExamples:\n")
		fmt.Fprintf(os.Stderr, "  madtom-tsdb-merge /var/lib/madtom-collector /var/lib/madtom_collector /var/lib/madtom_merged\n")
		fmt.Fprintf(os.Stderr, "  madtom-tsdb-merge -src ./madtom-collector,./madtom_collector -dest ./madtom_collector_merged\n")
	}

	flag.Parse()

	var srcDirs []string
	destDir := destFlag

	if srcFlag != "" {
		for _, s := range strings.Split(srcFlag, ",") {
			s = strings.TrimSpace(s)
			if s != "" {
				srcDirs = append(srcDirs, s)
			}
		}
	}

	// Positional arguments override or provide source / destination
	args := flag.Args()
	if len(args) >= 2 {
		if destDir == "" {
			destDir = args[len(args)-1]
			srcDirs = append(srcDirs, args[:len(args)-1]...)
		} else {
			srcDirs = append(srcDirs, args...)
		}
	} else if len(args) == 1 && destDir == "" {
		destDir = args[0]
	}

	if len(srcDirs) < 2 || destDir == "" {
		flag.Usage()
		os.Exit(1)
	}

	fmt.Println("================================================================")
	fmt.Println("             MADTOM Pebble TSDB Database Merger                 ")
	fmt.Println("================================================================")
	fmt.Printf("Sources to merge (%d):\n", len(srcDirs))
	for i, s := range srcDirs {
		fmt.Printf("  [%d] %s\n", i+1, s)
	}
	fmt.Printf("Target Destination:\n  -> %s\n", destDir)
	fmt.Println("----------------------------------------------------------------")

	start := time.Now()
	stats, err := storage.MergeDatabases(srcDirs, destDir, storage.MergeOptions{
		BatchSize: batchSize,
		Force:     force,
		LogWriter: os.Stdout,
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "\n[ERROR] Merge failed: %v\n", err)
		os.Exit(1)
	}

	elapsed := time.Since(start)

	fmt.Println("----------------------------------------------------------------")
	fmt.Println("✅ Merge Completed Successfully!")
	fmt.Printf("Elapsed Time:         %s\n", elapsed.Round(time.Millisecond))
	fmt.Printf("Total Source Records: %d\n", stats.TotalSourceKeys)
	fmt.Printf("Unique Merged Points: %d\n", stats.UniqueMergedKeys)
	if stats.TotalSourceKeys > stats.UniqueMergedKeys {
		fmt.Printf("Duplicates Resolved:  %d (overlapping metrics deduplicated)\n", stats.TotalSourceKeys-stats.UniqueMergedKeys)
	}
	if !stats.EarliestTimestamp.IsZero() {
		fmt.Printf("Time Range:           %s to %s\n",
			stats.EarliestTimestamp.UTC().Format("2006-01-02 15:04:05 UTC"),
			stats.LatestTimestamp.UTC().Format("2006-01-02 15:04:05 UTC"))
	}
	if len(stats.NodeIDs) > 0 {
		fmt.Printf("Nodes Discovered (%d): %s\n", len(stats.NodeIDs), strings.Join(stats.NodeIDs, ", "))
	}
	fmt.Println("================================================================")
	fmt.Println("Next steps to activate merged database:")
	fmt.Printf("  1. Stop your madtom-collector service.\n")
	fmt.Printf("  2. Backup original directory (e.g. mv %s %s.bak)\n", srcDirs[0], srcDirs[0])
	fmt.Printf("  3. Move merged data into place: mv %s %s\n", destDir, srcDirs[0])
	fmt.Printf("  4. Restart madtom-collector service.\n")
	fmt.Println("================================================================")
}
