package ffmpeg

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestComputeHardwareSignature(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	sig1, cpu1, _ := ComputeHardwareSignature(ctx, "ffmpeg")
	sig2, cpu2, _ := ComputeHardwareSignature(ctx, "ffmpeg")

	if sig1 == "" {
		t.Fatal("expected non-empty hardware signature")
	}
	if sig1 != sig2 {
		t.Fatalf("expected identical signatures for same hardware, got %s vs %s", sig1, sig2)
	}
	if cpu1 != cpu2 || cpu1 == "" {
		t.Fatalf("expected consistent non-empty CPU description, got %q", cpu1)
	}
}

func TestHardwareCache_ReadWriteAndInvalidate(t *testing.T) {
	tmpDir, err := os.MkdirTemp("", "squeeze_cache_test_*")
	if err != nil {
		t.Fatal(err)
	}
	defer os.RemoveAll(tmpDir)

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	sig, cpu, gpu := ComputeHardwareSignature(ctx, "ffmpeg")

	// Pre-populate mock cache in tmpDir
	mockCache := HardwareCache{
		Signature:        sig,
		CPU:              cpu,
		GPU:              gpu,
		AllEncoders:      []string{"libx264", "h264_vaapi", "h264_nvenc"},
		HardwareEncoders: []string{"h264_vaapi"},
		Status:           "active_hardware_verified (test)",
		ProbedAt:         time.Now().UTC(),
	}
	cachePath := GetCacheFilePath(tmpDir)
	data, _ := json.Marshal(mockCache)
	if err := os.WriteFile(cachePath, data, 0644); err != nil {
		t.Fatal(err)
	}

	// First probe should hit the valid cache
	all, hw, status, err := ProbeHardware(ctx, "ffmpeg", tmpDir, false)
	if err != nil {
		t.Fatalf("ProbeHardware failed: %v", err)
	}
	if !strings.Contains(status, "(cached)") {
		t.Fatalf("expected status to indicate (cached), got %q", status)
	}
	if len(all) != 3 || len(hw) != 1 || hw[0] != "h264_vaapi" {
		t.Fatalf("unexpected cached contents: all=%v, hw=%v", all, hw)
	}

	// Alter signature in file to simulate hardware change
	mockCache.Signature = "stale_signature_123"
	data, _ = json.Marshal(mockCache)
	_ = os.WriteFile(cachePath, data, 0644)

	// Probe should detect mismatch, invalidate, and re-probe fresh
	all, hw, status, err = ProbeHardware(ctx, "ffmpeg", tmpDir, false)
	if err != nil {
		t.Fatalf("re-probe after mismatch failed: %v", err)
	}
	if strings.Contains(status, "(cached)") {
		t.Fatalf("expected cache miss due to signature mismatch, got %q", status)
	}
	if len(all) == 0 {
		t.Fatal("expected discovered encoders after fresh probe")
	}

	// Test manual cache invalidation
	if err := InvalidateHardwareCache(tmpDir); err != nil {
		t.Fatalf("InvalidateHardwareCache failed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(tmpDir, "hardware_cache.json")); !os.IsNotExist(err) {
		t.Fatal("expected cache file to be deleted")
	}
}
