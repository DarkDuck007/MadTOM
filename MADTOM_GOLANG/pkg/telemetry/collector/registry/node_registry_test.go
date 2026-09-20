package registry

import (
	"os"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestRegistry_PersistenceAcrossRestarts(t *testing.T) {
	tempDir, err := os.MkdirTemp("", "madtom_registry_test_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tempDir)

	// Instance 1: configure a node
	reg1 := NewRegistry("Collector-1", tempDir)
	customCfg := &madtomv1.NodeConfig{
		NodeId:                  "node-persist-1",
		TwampTarget:             "collector",
		TwampMode:               madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE,
		TwampClocksSynchronized: true,
		CpuPerCoreMode:          madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY,
	}
	reg1.SetConfig("node-persist-1", customCfg)

	// Instance 2: simulates collector restart with same storageDir
	reg2 := NewRegistry("Collector-1", tempDir)

	loadedCfg := reg2.GetConfig("node-persist-1")
	if loadedCfg == nil {
		t.Fatal("expected config to be loaded on restarted registry")
	}
	if loadedCfg.TwampTarget != "collector" {
		t.Errorf("expected twamp target 'collector', got %s", loadedCfg.TwampTarget)
	}
	if loadedCfg.TwampMode != madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_AND_STORE {
		t.Errorf("expected twamp mode MONITOR_AND_STORE, got %v", loadedCfg.TwampMode)
	}
	if !loadedCfg.TwampClocksSynchronized {
		t.Errorf("expected TwampClocksSynchronized to be true")
	}

	transportCfg := reg2.TransportConfig("node-persist-1")
	if transportCfg == nil {
		t.Fatal("expected TransportConfig to return non-nil for configured node")
	}

	// Node connects and reports telemetry
	reg2.RegisterOrTouch("node-persist-1", "PUSH", nil)
	nodes := reg2.ListNodes()
	if len(nodes) != 1 || nodes[0].Status != "ONLINE" {
		t.Fatalf("expected 1 online node, got %v", nodes)
	}

	// Re-check config after touch
	afterTouchCfg := reg2.GetConfig("node-persist-1")
	if afterTouchCfg.TwampTarget != "collector" {
		t.Errorf("expected twamp target to remain 'collector' after touch, got %s", afterTouchCfg.TwampTarget)
	}
}
