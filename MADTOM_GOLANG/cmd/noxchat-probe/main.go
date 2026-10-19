package main

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"flag"
	"fmt"
	"log"
	"math/big"
	"net"
	"os"
	"os/signal"
	"syscall"
	"time"

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
	storeRawLogs := flag.Bool("store-raw-logs", false, "Store raw logs + API calls in WAL spool")
	maxSpoolBytes := flag.Int64("max-spool-bytes", 1024*1024*1024, "Max WAL spool size on disk (default 1GB)")
	genKey := flag.Bool("gen-key", false, "Generate a new client auth key and exit")
	enableMdns := flag.Bool("mdns", true, "Advertise via mDNS (_noxchat-probe._tcp.local)")
	flag.Parse()

	if *genKey {
		key, err := auth.GenerateClientKey()
		if err != nil {
			log.Fatalf("Failed to generate key: %v", err)
		}
		fmt.Printf("Generated NoxChat Client Key:\n%s\n\nAdd this to your NoxChat .env file:\nNOXCHAT_CONTROL_KEY=%s\n", key, key)
		return
	}

	cfg := config.DefaultConfig()
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

	keyPair, err := auth.LoadKey(*authKey, *authKeyFile, "NOXCHAT_CONTROL_KEY", "./NoxChat/.env")
	if err != nil {
		log.Printf("[NoxChat_Probe] Auth key warning: %v (running in token-less development mode if not set)", err)
	} else {
		log.Printf("[NoxChat_Probe] Authenticated control plane enabled with SSH-style token")
	}

	tlsCfg, err := generateSelfSignedTLS(cfg.NodeID)
	if err != nil {
		log.Fatalf("TLS certificate generation failed: %v", err)
	}

	srv, err := server.NewServer(cfg, keyPair, tlsCfg)
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

func generateSelfSignedTLS(nodeID string) (*tls.Config, error) {
	priv, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, err
	}

	notBefore := time.Now()
	notAfter := notBefore.Add(365 * 24 * time.Hour)

	serialNumberLimit := new(big.Int).Lsh(big.NewInt(1), 128)
	serialNumber, err := rand.Int(rand.Reader, serialNumberLimit)
	if err != nil {
		return nil, err
	}

	template := x509.Certificate{
		SerialNumber: serialNumber,
		Subject: pkix.Name{
			Organization: []string{"MADTOM NoxChat Probe"},
			CommonName:   nodeID + ".local",
		},
		NotBefore:             notBefore,
		NotAfter:              notAfter,
		KeyUsage:              x509.KeyUsageKeyEncipherment | x509.KeyUsageDigitalSignature,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		BasicConstraintsValid: true,
		DNSNames:              []string{nodeID, nodeID + ".local", "localhost"},
		IPAddresses:           []net.IP{net.ParseIP("127.0.0.1"), net.ParseIP("::1")},
	}

	derBytes, err := x509.CreateCertificate(rand.Reader, &template, &template, &priv.PublicKey, priv)
	if err != nil {
		return nil, err
	}

	cert := tls.Certificate{
		Certificate: [][]byte{derBytes},
		PrivateKey:  priv,
	}

	return &tls.Config{
		Certificates: []tls.Certificate{cert},
		MinVersion:   tls.VersionTLS13,
	}, nil
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
