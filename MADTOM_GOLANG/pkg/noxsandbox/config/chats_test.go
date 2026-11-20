package config

import (
	"os"
	"path/filepath"
	"testing"
)

// NoxChat's sandbox section is decoded strictly; an unknown key would silently drop the whole
// section back to defaults, so `chats` must be a known field with the same meaning as in Python.
func TestSandboxChatsModeLoadsFromNoxChatConfig(t *testing.T) {
	cases := []struct{ yaml, want string }{
		{"sandbox:\n  enabled: true\n  chats: all\n  pre_vm:\n    pool_size: 4\n", "all"},
		{"sandbox:\n  enabled: true\n  chats: none\n", "none"},
		{"sandbox:\n  enabled: true\n  chats: whitelist\n  allow_all_chats: true\n", "whitelist"},
		{"sandbox:\n  enabled: true\n  allow_all_chats: true\n", "all"},
		{"sandbox:\n  enabled: true\n", "whitelist"},
	}
	for _, c := range cases {
		path := filepath.Join(t.TempDir(), "config.yaml")
		if err := os.WriteFile(path, []byte(c.yaml), 0o600); err != nil {
			t.Fatal(err)
		}
		sb := loadSandbox(Host{NoxChatConfigPath: path, Ceilings: Ceilings{VMMemMB: 4096, VMVCPUs: 4, DiskGBPerChat: 8}})
		if !sb.Enabled {
			t.Fatalf("%q: section was dropped to defaults", c.yaml)
		}
		if got := sb.ChatMode(); got != c.want {
			t.Fatalf("%q: mode %q, want %q", c.yaml, got, c.want)
		}
	}
}

func TestSandboxInvalidChatsModeIsRejected(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.yaml")
	if err := os.WriteFile(path, []byte("sandbox:\n  enabled: true\n  chats: everyone\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if sb := loadSandbox(Host{NoxChatConfigPath: path, Ceilings: Ceilings{VMMemMB: 4096, VMVCPUs: 4, DiskGBPerChat: 8}}); sb.Enabled {
		t.Fatal("invalid chats value must fall back to (disabled) defaults")
	}
}
