package main

import (
	"errors"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"syscall"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
	"github.com/DarkDuck007/madtom/pkg/noxchat/server"
	"github.com/grandcat/zeroconf"
)

func main() {
	cfgPath := flag.String("config", "", "Path to NoxChat config.yaml")
	nodeID := flag.String("node-id", "", "Unique Node ID")
	listenAddr := flag.String("listen", ":50055", "gRPC listen address (e.g. :50055)")
	bridgeHost := flag.String("bridge-host", "127.0.0.1", "Local Python loopback host (must be 127.0.0.1)")
	bridgePort := flag.Int("bridge-port", 50056, "Local Python loopback port")
	authKey := flag.String("auth-key", "", "SSH-style client authorization key")
	authKeyFile := flag.String("auth-key-file", "", "Path to file containing auth key")
	authorizedKeys := flag.String("authorized-keys", "", "Path to an Ed25519 public-key allowlist")
	insecureDev := flag.Bool("insecure-dev", false, "Allow startup without authentication keys")
	stateDir := flag.String("state-dir", "/var/lib/noxchat-probe", "Directory for the persisted TLS identity")
	storeRawLogs := flag.Bool("store-raw-logs", false, "Store raw logs + API calls in WAL spool")
	maxSpoolBytes := flag.Int64("max-spool-bytes", 1024*1024*1024, "Max WAL spool size on disk (default 1GB)")
	genKey := flag.Bool("gen-key", false, "Generate a new client auth key and exit")
	enableMdns := flag.Bool("mdns", true, "Advertise via mDNS (_noxchat-probe._tcp.local)")
	searchDefaults := config.DefaultConfig()
	flag.BoolVar(&searchDefaults.LogSearchEnabled, "log-search", true, "Enable retained-log literal search")
	flag.IntVar(&searchDefaults.LogSearchConcurrency, "log-search-concurrency", 1, "Concurrent retained-log searches")
	flag.IntVar(&searchDefaults.LogSearchMaxResults, "log-search-max-results", 200, "Maximum hits per page")
	flag.IntVar(&searchDefaults.LogSearchMaxQueryBytes, "log-search-max-query-bytes", 4096, "Maximum literal query bytes")
	flag.Uint64Var(&searchDefaults.LogSearchChunkBytes, "log-search-chunk-bytes", 16<<20, "Maximum decoded chunk bytes")
	flag.Uint64Var(&searchDefaults.LogSearchPageBytes, "log-search-page-bytes", 64<<20, "Decoded bytes per page (plus at most one chunk)")
	flag.IntVar(&searchDefaults.LogSearchPageMs, "log-search-page-ms", 2000, "Cooperative page time budget")
	flag.IntVar(&searchDefaults.LogSearchCursorSeconds, "log-search-cursor-seconds", 300, "Search cursor lifetime")
	flag.Parse()

	if *genKey {
		key, err := auth.GenerateClientKey()
		if err != nil {
			log.Fatalf("Failed to generate key: %v", err)
		}
		fmt.Printf("Generated NoxChat Client Key:\n%s\n\nAdd this to your NoxChat .env file:\nNOXCHAT_CONTROL_KEY=%s\n", key, key)
		return
	}

	cfg := searchDefaults
	if *cfgPath != "" {
		if err := cfg.LoadFromNoxChatYaml(*cfgPath); err != nil {
			log.Printf("[NoxChat_Probe] Warning: Could not read config from %s: %v", *cfgPath, err)
		}
	}

	if *nodeID != "" {
		cfg.NodeID = *nodeID
	}
	if *listenAddr != "" {
		cfg.ListenAddr = *listenAddr
	}
	if *bridgeHost != "" {
		cfg.PythonBridgeHost = *bridgeHost
	}
	if *bridgePort != 0 {
		cfg.PythonBridgePort = *bridgePort
	}
	if *storeRawLogs {
		cfg.StoreRawLogs = true
	}
	if *maxSpoolBytes > 0 {
		cfg.MaxSpoolBytes = *maxSpoolBytes
	}
	cfg.MdnsEnabled = *enableMdns

	if err := cfg.Validate(); err != nil {
		log.Fatalf("[NoxChat_Probe] Config validation failed: %v", err)
	}

	verifier, err := auth.LoadProbeVerifier(*authKey, *authKeyFile, "NOXCHAT_CONTROL_KEY", "./NoxChat/.env", *authorizedKeys, *insecureDev)
	if err != nil {
		log.Fatalf("[NoxChat_Probe] Authentication setup failed: %v", err)
	}
	if verifier == nil {
		log.Print("[NoxChat_Probe] WARNING: authentication disabled by -insecure-dev")
	}

	tlsCfg, fingerprint, err := auth.LoadOrCreateTLS(*stateDir, cfg.NodeID)
	if errors.Is(err, os.ErrPermission) || errors.Is(err, syscall.EROFS) {
		log.Printf("[NoxChat_Probe] WARNING: TLS state is unwritable, using a temporary certificate: %v", err)
		tlsCfg, fingerprint, err = auth.NewEphemeralTLS(cfg.NodeID)
	}
	if err != nil {
		log.Fatalf("TLS setup failed: %v", err)
	}
	log.Printf("[NoxChat_Probe] TLS certificate SHA-256: %s", fingerprint)

	srv, err := server.NewServerWithVerifier(cfg, verifier, tlsCfg)
	if err != nil {
		log.Fatalf("Failed to initialize server: %v", err)
	}

	if cfg.MdnsEnabled {
		_, portStr, _ := net.SplitHostPort(cfg.ListenAddr)
		port := 50055
		_, _ = fmt.Sscanf(portStr, "%d", &port)
		go startMdns(cfg.NodeID, port)
	}

	go func() {
		if err := srv.Start(); err != nil {
			log.Fatalf("Server terminated: %v", err)
		}
	}()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	<-sigCh

	log.Println("[NoxChat_Probe] Shutting down gracefully...")
	srv.Stop()
}

func startMdns(nodeID string, port int) {
	txt := []string{
		"node_id=" + nodeID,
		"version=1.0.0",
		"auth=ssh-key",
		"service=noxchat-probe",
	}
	server, err := zeroconf.Register(
		nodeID,
		"_noxchat-probe._tcp",
		"local.",
		port,
		txt,
		nil,
	)
	if err != nil {
		log.Printf("[mDNS] Register error: %v", err)
		return
	}
	defer server.Shutdown()
	log.Printf("[mDNS] Advertised _noxchat-probe._tcp.local for %s on port %d", nodeID, port)
	select {}
}
