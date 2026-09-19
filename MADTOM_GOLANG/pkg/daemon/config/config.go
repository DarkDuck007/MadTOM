package config

import (
	"fmt"
	"os"
	"path/filepath"
	"strconv"

	"github.com/DarkDuck007/madtom/pkg/daemon/collector"
	"github.com/DarkDuck007/madtom/pkg/daemon/twamp"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/encoding/protojson"
)

const ConfigFileName = "node-config.json"

// ConfigPath returns the path to the node configuration file inside the spool directory.
func ConfigPath(spoolDir string) string {
	return filepath.Join(spoolDir, ConfigFileName)
}

// LoadConfig loads the node configuration from disk if it exists.
// If the file does not exist, it initializes with collector.DefaultConfig(nodeID).
// Returns the configuration, whether it was loaded from disk, and any error.
func LoadConfig(spoolDir, nodeID string) (*madtomv1.NodeConfig, bool, error) {
	path := ConfigPath(spoolDir)
	raw, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return collector.DefaultConfig(nodeID), false, nil
		}
		return nil, false, fmt.Errorf("failed to read node config from %s: %w", path, err)
	}

	cfg := &madtomv1.NodeConfig{}
	if err := protojson.Unmarshal(raw, cfg); err != nil {
		return nil, false, fmt.Errorf("failed to parse node config from %s: %w", path, err)
	}

	// Ensure node ID is populated
	if cfg.NodeId == "" {
		cfg.NodeId = nodeID
	}
	return cfg, true, nil
}

// SaveConfig atomically writes the node configuration to disk inside the spool directory.
func SaveConfig(spoolDir string, cfg *madtomv1.NodeConfig) error {
	if spoolDir == "" || cfg == nil {
		return nil
	}

	if err := os.MkdirAll(spoolDir, 0755); err != nil {
		return fmt.Errorf("failed to create spool directory %s: %w", spoolDir, err)
	}

	opts := protojson.MarshalOptions{
		Multiline:       true,
		Indent:          "  ",
		EmitUnpopulated: false,
	}
	data, err := opts.Marshal(cfg)
	if err != nil {
		return fmt.Errorf("failed to marshal node config: %w", err)
	}

	targetPath := ConfigPath(spoolDir)
	tmpPath := targetPath + ".tmp"

	f, err := os.OpenFile(tmpPath, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0644)
	if err != nil {
		return fmt.Errorf("failed to create temp config file %s: %w", tmpPath, err)
	}

	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to write temp config file: %w", err)
	}

	if err := f.Sync(); err != nil {
		_ = f.Close()
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to sync temp config file: %w", err)
	}

	if err := f.Close(); err != nil {
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to close temp config file: %w", err)
	}

	if err := os.Rename(tmpPath, targetPath); err != nil {
		_ = os.Remove(tmpPath)
		return fmt.Errorf("failed to commit config file: %w", err)
	}

	return nil
}

// ReconcileExplicitFlags reconciles explicitly supplied CLI flags against a configuration.
// If isFromDisk is true, any setting that conflicts with the CLI will generate a warning,
// and the CLI argument overrides the setting (CLI takes priority).
func ReconcileExplicitFlags(cfg *madtomv1.NodeConfig, isFromDisk bool, explicitFlags map[string]string, twampPort int, collectorAddr string) []string {
	var warnings []string

	// 1. TWAMP Target
	if val, ok := explicitFlags["twamp-target"]; ok {
		normalized := twamp.ResolveTarget(val, twampPort, collectorAddr)
		if isFromDisk && cfg.TwampTarget != normalized {
			warnings = append(warnings, fmt.Sprintf("[Config] WARNING: CLI argument -twamp-target=%q overrides UI-configured value %q (CLI takes priority)", normalized, cfg.TwampTarget))
		}
		cfg.TwampTarget = normalized

		// If a target was explicitly given on CLI and twamp mode was OFF, enable it
		if normalized != "" && cfg.TwampMode == madtomv1.TelemetryOptInMode_OPT_IN_OFF {
			if isFromDisk {
				warnings = append(warnings, "[Config] WARNING: CLI argument -twamp-target overrides TwampMode from OFF to MONITOR_AND_STORE (CLI takes priority)")
			}
			cfg.TwampMode = madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
		}
	} else if cfg.TwampTarget != "" {
		// If twamp-target was NOT passed on CLI but existed from UI, re-resolve with current port/collector
		cfg.TwampTarget = twamp.ResolveTarget(cfg.TwampTarget, twampPort, collectorAddr)
	}

	// 2. TWAMP Clocks Synchronized
	if val, ok := explicitFlags["twamp-clocks-synchronized"]; ok {
		synced, _ := strconv.ParseBool(val)
		if isFromDisk && cfg.TwampClocksSynchronized != synced {
			warnings = append(warnings, fmt.Sprintf("[Config] WARNING: CLI argument -twamp-clocks-synchronized=%v overrides UI-configured value %v (CLI takes priority)", synced, cfg.TwampClocksSynchronized))
		}
		cfg.TwampClocksSynchronized = synced
	}

	// 3. Zstd Compression
	if val, ok := explicitFlags["zstd"]; ok {
		zstd, _ := strconv.ParseBool(val)
		if isFromDisk && cfg.EnableZstdCompression != zstd {
			warnings = append(warnings, fmt.Sprintf("[Config] WARNING: CLI argument -zstd=%v overrides UI-configured value %v (CLI takes priority)", zstd, cfg.EnableZstdCompression))
		}
		cfg.EnableZstdCompression = zstd
	}

	// 4. Max Spool Bytes
	if val, ok := explicitFlags["max-spool-mb"]; ok {
		mb, err := strconv.ParseInt(val, 10, 64)
		if err == nil && mb > 0 {
			bytes := uint64(mb * 1024 * 1024)
			if isFromDisk && cfg.MaxSpoolBytes != bytes {
				warnings = append(warnings, fmt.Sprintf("[Config] WARNING: CLI argument -max-spool-mb=%d overrides UI-configured value %d (CLI takes priority)", mb, cfg.MaxSpoolBytes/(1024*1024)))
			}
			cfg.MaxSpoolBytes = bytes
		}
	}

	return warnings
}
