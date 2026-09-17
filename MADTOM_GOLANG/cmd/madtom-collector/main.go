package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/DarkDuck007/madtom/pkg/collector/api"
	"github.com/DarkDuck007/madtom/pkg/collector/ingest"
	"github.com/DarkDuck007/madtom/pkg/collector/registry"
	"github.com/DarkDuck007/madtom/pkg/collector/storage"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/keepalive"
)

func main() {
	collectorName := flag.String("name", "Local Collector", "Display name for this collector (shown on UI node cards)")
	port := flag.Int("port", 50051, "TCP port for gRPC server (both ingestion and UI queries)")
	dataDir := flag.String("data-dir", filepath.Join(os.TempDir(), "madtom", "collector_data"), "Directory for Pebble TSDB time-series storage")
	pullTargets := flag.String("pull-targets", "", "Comma-separated list of pull-mode daemon targets (e.g. 'node-1@127.0.0.1:50052')")
	pullInterval := flag.Duration("pull-interval", 1*time.Second, "Polling interval for pull-mode daemon targets (default: 1s for full 1Hz resolution)")

	reverseTargets := flag.String("reverse-push-targets", "", "Comma-separated node-id@address targets: collector connects, daemon pushes")
	flag.Parse()

	log.Printf("Starting MADTOM Collector [%s] on port :%d (Storage: %s, Pull Interval: %v)", *collectorName, *port, *dataDir, *pullInterval)

	// 1. Initialize Pebble TSDB
	tsdb, err := storage.OpenTSDB(*dataDir)
	if err != nil {
		log.Fatalf("Failed to open Pebble TSDB: %v", err)
	}
	defer tsdb.Close()

	// 2. Initialize Node Registry & Ingest Pipeline
	reg := registry.NewRegistry(*collectorName)
	pipeline := ingest.NewPipeline(tsdb, reg, *collectorName)

	ctx, cancel := context.WithCancel(context.Background())
	var receivers sync.WaitGroup
	defer func() { cancel(); receivers.Wait() }()
	if *reverseTargets != "" {
		for _, target := range strings.Split(*reverseTargets, ",") {
			node, address, ok := strings.Cut(strings.TrimSpace(target), "@")
			if !ok || node == "" || address == "" {
				log.Fatalf("Invalid reverse push target %q", target)
			}
			receivers.Add(1)
			go func() { defer receivers.Done(); ingest.ReceivePush(ctx, pipeline, node, address) }()
		}
	}
	// 3. Optional Pull Scraper
	scraper := ingest.NewPullScraper(pipeline)
	if *pullTargets != "" {
		for _, target := range strings.Split(*pullTargets, ",") {
			parts := strings.Split(target, "@")
			if len(parts) == 2 {
				scraper.AddTarget(parts[0], parts[1], *pullInterval)
				log.Printf("Configured pull target: %s -> %s (interval: %v)", parts[0], parts[1], *pullInterval)
			}
		}
		scraper.Start()
		defer scraper.Stop()
	}

	// 4. Start Unified gRPC Server
	lis, err := net.Listen("tcp", fmt.Sprintf(":%d", *port))
	if err != nil {
		log.Fatalf("Failed to listen on TCP port %d: %v", *port, err)
	}
	defer lis.Close()

	grpcServer := grpc.NewServer(
		grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{
			MinTime:             3 * time.Second, // Allow clients to ping as frequently as every 3s
			PermitWithoutStream: true,            // Allow pings even when there are no active streams
		}),
		grpc.KeepaliveParams(keepalive.ServerParameters{
			MaxConnectionIdle:     15 * time.Minute,
			MaxConnectionAge:      30 * time.Minute,
			MaxConnectionAgeGrace: 5 * time.Second,
			Time:                  10 * time.Second, // Ping client if idle for 10s
			Timeout:               3 * time.Second,  // Wait 3s for client response
		}),
	)

	// Ingestion Push service
	pushServer := ingest.NewPushServer(pipeline)
	madtomv1.RegisterIngestServiceServer(grpcServer, pushServer)

	// UI Query & Config service
	apiServer := api.NewServer(*collectorName, tsdb, reg, pipeline)
	madtomv1.RegisterQueryServiceServer(grpcServer, apiServer)
	madtomv1.RegisterConfigServiceServer(grpcServer, apiServer)

	go func() {
		log.Printf("gRPC services listening on :%d", *port)
		if err := grpcServer.Serve(lis); err != nil {
			log.Fatalf("gRPC server error: %v", err)
		}
	}()

	// 5. Graceful shutdown
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
	<-sigChan

	log.Println("Shutting down MADTOM Collector...")
	cancel()
	grpcServer.Stop()
}
