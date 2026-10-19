package config

import (
	"fmt"
	"log"
	"os"
	"strings"

	"gopkg.in/yaml.v3"
)

// ProbeConfig holds configuration parsed from CLI flags, env vars, and NoxChat config.yaml.
type ProbeConfig struct {
	NodeID           string `yaml:"node_id"`
	ListenAddr       string `yaml:"listen_addr"`        // Public gRPC port (default ":50055")
	PythonBridgeHost string `yaml:"python_bridge_host"` // Loopback host (must be 127.0.0.1 or localhost)
	PythonBridgePort int    `yaml:"python_bridge_port"` // Loopback port (default 50056)
	StoreRawLogs     bool   `yaml:"store_raw_logs"`     // Persist raw logs + API calls to WAL spool
	MaxSpoolBytes    int64  `yaml:"max_spool_bytes"`    // Default 1GB (1073741824)
	StateBufferBytes int64  `yaml:"state_buffer_bytes"` // Default 1MB
	LogBufferBytes   int64  `yaml:"log_buffer_bytes"`   // Default 8MB
	SpoolDir         string `yaml:"spool_dir"`          // Default "/var/lib/noxchat-probe/spool" or "./data/spool"
	AuthKey          string `yaml:"auth_key"`
	AuthKeyFile      string `yaml:"auth_key_file"`
	MdnsEnabled      bool   `yaml:"mdns_enabled"`
}

// DefaultConfig returns safe fallback defaults.
func DefaultConfig() *ProbeConfig {
	return &ProbeConfig{
		NodeID:           "noxchat-node",
		ListenAddr:       ":50055",
		PythonBridgeHost: "127.0.0.1",
		PythonBridgePort: 50056,
		StoreRawLogs:     false,
		MaxSpoolBytes:    1024 * 1024 * 1024,
		StateBufferBytes: 1024 * 1024,
		LogBufferBytes:   8 * 1024 * 1024,
		SpoolDir:         "./data/noxchat_spool",
		MdnsEnabled:      true,
	}
}

// LoadFromNoxChatYaml loads control_center section from NoxChat's config.yaml.
func (c *ProbeConfig) LoadFromNoxChatYaml(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}

	var raw struct {
		App struct {
			Name    string `yaml:"name"`
			DataDir string `yaml:"data_dir"`
		} `yaml:"app"`
		ControlCenter struct {
			Enabled          bool   `yaml:"enabled"`
			ListenHost       string `yaml:"listen_host"`
			ListenPort       int    `yaml:"listen_port"`
			StoreRawLogs     bool   `yaml:"store_raw_logs"`
			MaxSpoolBytes    int64  `yaml:"max_spool_bytes"`
			StateBufferBytes int64  `yaml:"state_buffer_bytes"`
			LogBufferBytes   int64  `yaml:"log_buffer_bytes"`
		} `yaml:"control_center"`
	}

	if err := yaml.Unmarshal(data, &raw); err != nil {
		return err
	}

	if raw.ControlCenter.ListenHost != "" {
		c.PythonBridgeHost = raw.ControlCenter.ListenHost
	}
	if raw.ControlCenter.ListenPort != 0 {
		c.PythonBridgePort = raw.ControlCenter.ListenPort
	}
	c.StoreRawLogs = raw.ControlCenter.StoreRawLogs
	if raw.ControlCenter.MaxSpoolBytes > 0 {
		c.MaxSpoolBytes = raw.ControlCenter.MaxSpoolBytes
	}
	if raw.ControlCenter.StateBufferBytes > 0 {
		c.StateBufferBytes = raw.ControlCenter.StateBufferBytes
	}
	if raw.ControlCenter.LogBufferBytes > 0 {
		c.LogBufferBytes = raw.ControlCenter.LogBufferBytes
	}
	if raw.App.DataDir != "" {
		c.SpoolDir = raw.App.DataDir + "/noxchat_spool"
	}

	return nil
}

// Validate verifies security constraints, including the mandatory localhost check.
func (c *ProbeConfig) Validate() error {
	host := strings.TrimSpace(strings.ToLower(c.PythonBridgeHost))
	if host != "127.0.0.1" && host != "localhost" && host != "::1" {
		log.Printf("\n====================================================================\n"+
			"SECURITY WARNING: python_bridge_host is set to '%s'!\n"+
			"The local Python<->NoxChat_Probe bridge is unencrypted and must ONLY bind to localhost or 127.0.0.1.\n"+
			"Exposing this bridge to external interfaces is a critical security vulnerability!\n"+
			"====================================================================\n", c.PythonBridgeHost)
	}

	if c.ListenAddr == "" {
		return fmt.Errorf("listen_addr must not be empty")
	}
	return nil
}
