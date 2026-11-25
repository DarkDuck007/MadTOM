package server

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"

	"github.com/DarkDuck007/madtom/pkg/noxchat/config"
)

// The Studio's log mode (UpdateControlConfig) is a runtime decision. It is persisted next to the
// spool so a probe restart (deploy, reboot) keeps spooling instead of silently reverting to the
// config.yaml default until a Studio reconnects — calls made in that gap would never be retained.
const controlOverrideFile = "noxchat_probe_control.json"

type controlOverride struct {
	StoreRawLogs bool `json:"store_raw_logs"`
}

func controlOverridePath(cfg *config.ProbeConfig) string {
	if cfg.SpoolDir == "" {
		return ""
	}
	return filepath.Join(filepath.Dir(filepath.Clean(cfg.SpoolDir)), controlOverrideFile)
}

// applyControlOverride replaces config.yaml's store_raw_logs with the last value a Studio set.
func applyControlOverride(cfg *config.ProbeConfig) {
	path := controlOverridePath(cfg)
	if path == "" {
		return
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[NoxChat_Probe] WARNING: cannot read %s: %v", path, err)
		}
		return
	}
	var o controlOverride
	if err := json.Unmarshal(raw, &o); err != nil {
		log.Printf("[NoxChat_Probe] WARNING: ignoring malformed %s: %v", path, err)
		return
	}
	if o.StoreRawLogs != cfg.StoreRawLogs {
		log.Printf("[NoxChat_Probe] store_raw_logs=%v (Studio override from %s; config.yaml says %v)", o.StoreRawLogs, path, cfg.StoreRawLogs)
	}
	cfg.StoreRawLogs = o.StoreRawLogs
}

func saveControlOverride(cfg *config.ProbeConfig) {
	path := controlOverridePath(cfg)
	if path == "" {
		return
	}
	raw, _ := json.Marshal(controlOverride{StoreRawLogs: cfg.StoreRawLogs})
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, raw, 0o600); err != nil {
		log.Printf("[NoxChat_Probe] WARNING: cannot persist control override: %v", err)
		return
	}
	if err := os.Rename(tmp, path); err != nil {
		log.Printf("[NoxChat_Probe] WARNING: cannot persist control override: %v", err)
	}
}
