package config

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestConfigEnvironmentAndFlags(t *testing.T) {
	t.Setenv("SQUEEZE_PORT", "9090")
	t.Setenv("SQUEEZE_WORKERS", "2")
	t.Setenv("SQUEEZE_MDNS", "false")
	c, err := Parse([]string{"--port=8081", "--host=::1", "--retention=2h"})
	if err != nil {
		t.Fatal(err)
	}
	if c.Address() != "[::1]:8081" || c.Workers != 2 || c.MDNS || c.Retention != 2*time.Hour {
		t.Fatal(c)
	}
}

func TestInvalidConfig(t *testing.T) {
	for _, arg := range []string{"--workers=0", "--port=65536", "--max-upload=-1", "--retention=0s", "--upload-timeout=-1s", "--queue-size=0", "--node-id="} {
		if _, err := Parse([]string{arg}); err == nil {
			t.Fatal(arg)
		}
	}
	t.Setenv("SQUEEZE_WORKERS", "bad")
	if _, err := Parse(nil); err == nil {
		t.Fatal("invalid environment accepted")
	}
}

func TestYAMLConfigLoading(t *testing.T) {
	tmpDir := t.TempDir()
	yamlPath := filepath.Join(tmpDir, "test_config.yaml")
	content := `
server:
  host: "127.0.0.1"
  port: 8888
  workers: 4
  node_id: "TEST_NODE"
  mdns: false
presets:
  - key: "test_preset"
    title: "Test Preset 1080p"
    category: "Testing"
    description: "Preset used for unit tests"
    spec:
      video_codec: "libx264"
      crf: 23
      preset: "fast"
      container: "mp4"
      audio_codec: "aac"
`
	if err := os.WriteFile(yamlPath, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}

	c, err := Parse([]string{"--config", yamlPath, "--port=9999"})
	if err != nil {
		t.Fatal(err)
	}

	// CLI flag overrides YAML
	if c.Port != 9999 {
		t.Fatalf("expected port 9999 from flag override, got %d", c.Port)
	}
	// YAML values loaded
	if c.Host != "127.0.0.1" || c.Workers != 4 || c.NodeID != "TEST_NODE" || c.MDNS {
		t.Fatalf("unexpected config values from YAML: %+v", c)
	}
	// Presets loaded
	if len(c.Presets) != 1 {
		t.Fatalf("expected 1 preset, got %d", len(c.Presets))
	}
	if c.Presets[0].Key != "test_preset" || c.Presets[0].Title != "Test Preset 1080p" {
		t.Fatalf("unexpected preset content: %+v", c.Presets[0])
	}
}
