package config

import (
	"os"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestLoadConfig_DefaultWhenMissing(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom_cfg_test_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	cfg, isFromDisk, err := LoadConfig(tempDir, "test-node-1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if isFromDisk {
		t.Fatalf("expected isFromDisk to be false")
	}
	if cfg.NodeId != "test-node-1" {
		t.Fatalf("expected node-id to be test-node-1, got %s", cfg.NodeId)
	}
}

func TestSaveAndLoadConfig(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom_cfg_test_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	orig, _, _ := LoadConfig(tempDir, "test-node-2")
	orig.ProcessSnapshotLimit = 25
	orig.TwampTarget = "10.0.0.5:862"
	orig.TwampMode = madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE
	orig.TwampClocksSynchronized = true
	orig.CpuPerCoreMode = madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY

	if err := SaveConfig(tempDir, orig); err != nil {
		t.Fatalf("SaveConfig failed: %v", err)
	}

	loaded, isFromDisk, err := LoadConfig(tempDir, "test-node-2")
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if !isFromDisk {
		t.Fatalf("expected isFromDisk to be true")
	}
	if loaded.ProcessSnapshotLimit != 25 {
		t.Fatalf("snapshot limit did not survive restart: %d", loaded.ProcessSnapshotLimit)
	}
	if loaded.TwampTarget != "10.0.0.5:862" {
		t.Fatalf("expected TwampTarget to match, got %s", loaded.TwampTarget)
	}
	if loaded.TwampMode != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Fatalf("expected TwampMode to match, got %v", loaded.TwampMode)
	}
	if !loaded.TwampClocksSynchronized {
		t.Fatalf("expected TwampClocksSynchronized to be true")
	}
	if loaded.CpuPerCoreMode != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY {
		t.Fatalf("expected CpuPerCoreMode to match, got %v", loaded.CpuPerCoreMode)
	}
}

func TestReconcileExplicitFlags_OverrideAndWarnings(t *testing.T) {
	cfg := &madtomv1.NodeConfig{
		NodeId:                  "node-3",
		TwampTarget:             "10.0.0.1:862",
		TwampClocksSynchronized: false,
		EnableZstdCompression:   false,
		MaxSpoolBytes:           512 * 1024 * 1024,
	}

	explicitFlags := map[string]string{
		"twamp-target":              "192.168.1.1:8620",
		"twamp-clocks-synchronized": "true",
		"zstd":                      "true",
		"max-spool-mb":              "2048",
	}

	warnings := ReconcileExplicitFlags(cfg, true, explicitFlags, 862, "127.0.0.1:50051")

	if len(warnings) != 5 {
		t.Fatalf("expected 5 warnings, got %d: %v", len(warnings), warnings)
	}
	if cfg.TwampTarget != "192.168.1.1:8620" {
		t.Fatalf("expected twamp-target overridden to 192.168.1.1:8620, got %s", cfg.TwampTarget)
	}
	if !cfg.TwampClocksSynchronized {
		t.Fatalf("expected twamp-clocks-synchronized to be true")
	}
	if !cfg.EnableZstdCompression {
		t.Fatalf("expected zstd to be true")
	}
	if cfg.MaxSpoolBytes != 2048*1024*1024 {
		t.Fatalf("expected max-spool-bytes to be 2048 MB, got %d", cfg.MaxSpoolBytes)
	}
}

func TestReconcileExplicitFlags_OmittedFlagsPreserveUI(t *testing.T) {
	cfg := &madtomv1.NodeConfig{
		NodeId:                  "node-4",
		TwampTarget:             "collector",
		TwampMode:               madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		TwampClocksSynchronized: true,
	}

	// No flags explicitly passed on CLI
	explicitFlags := map[string]string{}

	warnings := ReconcileExplicitFlags(cfg, true, explicitFlags, 8620, "10.0.0.10:50051")

	if len(warnings) != 0 {
		t.Fatalf("expected 0 warnings when flags are omitted, got: %v", warnings)
	}
	// "collector" should be re-resolved to the connected collector address and port
	if cfg.TwampTarget != "10.0.0.10:8620" {
		t.Fatalf("expected twamp target resolved to 10.0.0.10:8620, got %s", cfg.TwampTarget)
	}
	if !cfg.TwampClocksSynchronized {
		t.Fatalf("expected TwampClocksSynchronized to remain true")
	}
}
