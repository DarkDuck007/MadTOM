package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"strconv"
	"syscall"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
	"github.com/DarkDuck007/madtom/pkg/slideforge/server"
	"github.com/grandcat/zeroconf"
)

func main() {
	if err := run(); err != nil {
		log.Fatal(err)
	}
}

func run() error {
	cfg := server.DefaultConfig()
	flag.StringVar(&cfg.NodeID, "node-id", cfg.NodeID, "Unique node ID")
	flag.StringVar(&cfg.ListenAddr, "listen", cfg.ListenAddr, "gRPC listen address")
	flag.StringVar(&cfg.BotHost, "bot-host", cfg.BotHost, "Bot control-channel loopback host")
	flag.IntVar(&cfg.BotPort, "bot-port", cfg.BotPort, "Bot control-channel port")
	flag.StringVar(&cfg.StateDir, "state-dir", cfg.StateDir, "Directory for TLS identity and WAL spool")
	flag.BoolVar(&cfg.MdnsEnabled, "mdns", cfg.MdnsEnabled, "Advertise via mDNS (_slideforge-probe._tcp.local)")
	flag.BoolVar(&cfg.StoreRawLogs, "store-raw-logs", cfg.StoreRawLogs, "Store logs and API calls in the WAL spool")
	flag.Int64Var(&cfg.MaxSpoolBytes, "max-spool-bytes", cfg.MaxSpoolBytes, "WAL retention quota in bytes")
	authKey := flag.String("auth-key", "", "Client authorization key (or SLIDEFORGE_CONTROL_KEY)")
	authKeyFile := flag.String("auth-key-file", "", "Key file or .env file (default lookup ./SlideForge/.env)")
	authorizedKeys := flag.String("authorized-keys", "", "Path to an Ed25519 public-key allowlist")
	insecureDev := flag.Bool("insecure-dev", false, "Allow startup without authentication keys")
	genKey := flag.Bool("gen-key", false, "Generate a new client authorization key and exit")
	flag.Parse()

	if *genKey {
		key, err := auth.GenerateClientKey()
		if err != nil {
			return fmt.Errorf("generate client key: %w", err)
		}
		fmt.Printf("SLIDEFORGE_CONTROL_KEY=%s\n", key)
		return nil
	}
	verifier, err := auth.LoadProbeVerifier(*authKey, *authKeyFile, "SLIDEFORGE_CONTROL_KEY", "./SlideForge/.env", *authorizedKeys, *insecureDev)
	if err != nil {
		return fmt.Errorf("authentication setup: %w", err)
	}
	if verifier == nil {
		log.Print("[SlideForge_Probe] WARNING: authentication disabled by -insecure-dev")
	}
	tlsCfg, fingerprint, err := auth.LoadOrCreateTLS(cfg.StateDir, cfg.NodeID)
	if err != nil {
		return fmt.Errorf("TLS setup: %w", err)
	}
	log.Printf("[SlideForge_Probe] TLS certificate SHA-256: %s", fingerprint)

	srv, err := server.New(cfg, verifier, tlsCfg)
	if err != nil {
		return fmt.Errorf("initialize server: %w", err)
	}
	defer srv.Stop()
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	result := make(chan error, 1)
	go func() { result <- srv.Start() }()
	if cfg.MdnsEnabled {
		if service := advertise(cfg.NodeID, cfg.ListenAddr); service != nil {
			defer service.Shutdown()
		}
	}
	select {
	case <-ctx.Done():
		return nil
	case err := <-result:
		return err
	}
}

func advertise(nodeID, listenAddr string) *zeroconf.Server {
	_, portString, err := net.SplitHostPort(listenAddr)
	port, portErr := strconv.Atoi(portString)
	if err != nil || portErr != nil || port <= 0 || port > 65535 {
		log.Printf("[mDNS] Cannot advertise listen address %q", listenAddr)
		return nil
	}
	txt := []string{"node_id=" + nodeID, "auth=ssh-key", "service=slideforge-probe"}
	service, err := zeroconf.Register(nodeID, "_slideforge-probe._tcp", "local.", port, txt, nil)
	if err != nil {
		log.Printf("[mDNS] Register error: %v", err)
		return nil
	}
	log.Printf("[mDNS] Advertised _slideforge-probe._tcp.local for %s on port %d", nodeID, port)
	return service
}
