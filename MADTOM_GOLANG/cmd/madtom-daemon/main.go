package main

import (
	"flag"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/DarkDuck007/madtom/pkg/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/daemon/spool"
	"github.com/DarkDuck007/madtom/pkg/daemon/transport"
)

func main() {
	defaultNodeID, _ := os.Hostname()

	nodeID := flag.String("node-id", defaultNodeID, "Unique identifier for this monitored node")
	mode := flag.String("mode", "push", "Ingestion mode: 'push' (stream to collector) 'pull' (collector scrapes daemon), or 'reverse-push' (collector connects, daemon streams)")
	collectorAddr := flag.String("collector", "127.0.0.1:50051", "Target collector TCP gRPC address (for push mode)")
	listenPort := flag.Int("listen-port", 50052, "Listening port for collector scraper (for pull mode)")
	spoolDir := flag.String("spool-dir", filepath.Join(os.TempDir(), "madtom", "wal"), "Directory for local bounded disk spooling")
	maxSpoolMB := flag.Int64("max-spool-mb", 1024, "Maximum disk spool capacity in megabytes (default 1024 MB = 1 GB)")
	enableZstd := flag.Bool("zstd", false, "Enable zstd compression on disk and in-flight batches")

	twampTarget := flag.String("twamp-target", "", "TWAMP Light UDP reflector host:port")
	twampSynced := flag.Bool("twamp-clocks-synchronized", false, "Enable one-way measurements when both endpoint clocks are synchronized")
	flag.Parse()
	if *mode != "push" && *mode != "pull" && *mode != "reverse-push" {
		log.Fatalf("Unknown mode: %s", *mode)
	}

	log.Printf("Starting MADTOM Node Daemon [Node: %s, Mode: %s, Spool: %s (Max %d MB)]",
		*nodeID, *mode, *spoolDir, *maxSpoolMB)

	// 1. Initialize Disk WAL Spooler
	wal, err := spool.NewWALManager(*spoolDir, *nodeID, *maxSpoolMB*1024*1024, *enableZstd)
	if err != nil {
		log.Fatalf("Failed to initialize disk spooler: %v", err)
	}
	defer wal.Close()

	// 2. Initialize Subsystem Collectors
	engine := collector.NewEngine(*nodeID)
	cfg := collector.DefaultConfig(*nodeID)
	cfg.TwampTarget = *twampTarget
	cfg.TwampClocksSynchronized = *twampSynced

	// 3. Start Transport Layer
	var pushClient *transport.PushClient
	var pullServer *transport.PullServer

	if *mode == "push" {
		pushClient = transport.NewPushClient(*collectorAddr, *nodeID, wal, engine, cfg)
		pushClient.Start()
		defer pushClient.Stop()
	} else {
		pullServer = transport.NewPullServer(*listenPort, *nodeID, wal, engine, cfg)
		if err := pullServer.Start(); err != nil {
			log.Fatalf("Failed to start pull server on port %d: %v", *listenPort, err)
		}
		defer pullServer.Stop()
		log.Printf("Pull server listening on port :%d", *listenPort)
	}

	// 4. Wait for OS termination signals
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
	<-sigChan

	log.Println("Shutting down MADTOM Node Daemon...")
}
