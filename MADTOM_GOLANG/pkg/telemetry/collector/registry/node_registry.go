package registry

import (
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"sync"
	"time"

	"fmt"
	"strings"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/DarkDuck007/madtom/pkg/telemetry/optin"
)

const ConfigsFileName = "node_configs.json"

type NodeEntry struct {
	Info            *madtomv1.NodeInfo
	Config          *madtomv1.NodeConfig
	Configured      bool
	DaemonVersion   string
	DaemonFeatures  []string
	DaemonAckedHash string
	LastAckUnixNano int64
	AckListeners    []chan struct{}
}

// Registry maintains device topology, live heartbeats, and persistent opt-in configurations.
type Registry struct {
	mu            sync.RWMutex
	collectorName string
	storageDir    string
	nodes         map[string]*NodeEntry
}

// NewRegistry creates a new Node Registry with a designated collector name and optional persistent storage directory.
func NewRegistry(collectorName string, storageDirs ...string) *Registry {
	r := &Registry{
		collectorName: collectorName,
		nodes:         make(map[string]*NodeEntry),
	}
	if len(storageDirs) > 0 && storageDirs[0] != "" {
		r.storageDir = storageDirs[0]
		r.loadFromDisk()
	}
	return r
}

// RegisterOrTouch updates or registers a node upon telemetry or handshake receipt.
func (r *Registry) RegisterOrTouch(nodeID string, mode string, optInfo *madtomv1.NodeInfo) {
	r.mu.Lock()
	defer r.mu.Unlock()

	now := time.Now().UnixNano()
	entry, exists := r.nodes[nodeID]
	if !exists {
		info := &madtomv1.NodeInfo{
			NodeId:           nodeID,
			Hostname:         nodeID,
			Os:               "Linux",
			Arch:             "amd64",
			Status:           "ONLINE",
			LastSeenUnixNano: now,
			ConnectionMode:   mode,
		}
		if optInfo != nil {
			if optInfo.Hostname != "" {
				info.Hostname = optInfo.Hostname
			}
			if optInfo.Os != "" {
				info.Os = optInfo.Os
			}
			if optInfo.Arch != "" {
				info.Arch = optInfo.Arch
			}
		}

		r.nodes[nodeID] = &NodeEntry{
			Info:   info,
			Config: defaultConfig(nodeID),
		}
		return
	}

	entry.Info.LastSeenUnixNano = now
	entry.Info.Status = "ONLINE"
	if mode != "" {
		entry.Info.ConnectionMode = mode
	}
}

// RecordBatch updates topology, staleness, and daemon capabilities from an incoming batch.
func (r *Registry) RecordBatch(batch *madtomv1.TelemetryBatch, mode string) {
	if batch == nil || batch.NodeId == "" {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()

	nodeID := batch.NodeId
	now := time.Now().UnixNano()
	entry, exists := r.nodes[nodeID]
	if !exists {
		info := &madtomv1.NodeInfo{
			NodeId:           nodeID,
			Hostname:         nodeID,
			Os:               "Linux",
			Arch:             "amd64",
			Status:           "ONLINE",
			LastSeenUnixNano: now,
			ConnectionMode:   mode,
		}
		entry = &NodeEntry{
			Info:   info,
			Config: defaultConfig(nodeID),
		}
		r.nodes[nodeID] = entry
	}

	entry.Info.LastSeenUnixNano = now
	entry.Info.Status = "ONLINE"
	if mode != "" {
		entry.Info.ConnectionMode = mode
	}
	if batch.DaemonVersion != "" {
		entry.DaemonVersion = batch.DaemonVersion
		entry.Info.DaemonVersion = batch.DaemonVersion
	}
	if len(batch.SupportedFeatures) > 0 {
		entry.DaemonFeatures = batch.SupportedFeatures
		entry.Info.SupportedFeatures = batch.SupportedFeatures
	}
	if batch.AckedConfigHash != "" {
		entry.DaemonAckedHash = batch.AckedConfigHash
		entry.LastAckUnixNano = now
	}

	targetHash := optin.ComputeConfigHash(entry.Config)
	if entry.DaemonAckedHash == targetHash {
		entry.Info.ConfigSyncStatus = "CONFIRMED"
	} else if entry.DaemonVersion == "" {
		entry.Info.ConfigSyncStatus = "LEGACY_DAEMON"
	} else {
		entry.Info.ConfigSyncStatus = "PENDING"
	}

	for _, ch := range entry.AckListeners {
		select {
		case ch <- struct{}{}:
		default:
		}
	}
	entry.AckListeners = nil
}

// WaitForConfigSync waits up to timeout for the node's daemon to confirm applying the config.
func (r *Registry) WaitForConfigSync(nodeID string, timeout time.Duration) (confirmed bool, daemonStatus string, version string, features []string, msg string) {
	r.mu.Lock()
	entry, exists := r.nodes[nodeID]
	if !exists {
		r.mu.Unlock()
		return false, "NOT_FOUND", "", nil, "Node is not registered"
	}

	targetHash := optin.ComputeConfigHash(entry.Config)
	now := time.Now().UnixNano()
	elapsedSec := float64(now-entry.Info.LastSeenUnixNano) / 1e9
	isOnline := elapsedSec <= 30 && entry.Info.Status != "OFFLINE"

	if !isOnline {
		v := entry.DaemonVersion
		feat := entry.DaemonFeatures
		r.mu.Unlock()
		return false, "DAEMON_OFFLINE", v, feat,
			"Configuration saved on collector. Node is currently OFFLINE; settings will apply automatically when daemon connects."
	}

	if entry.Config.LogMode != madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		hasLogSupport := false
		for _, f := range entry.DaemonFeatures {
			if strings.EqualFold(f, "logs") {
				hasLogSupport = true
				break
			}
		}
		if !hasLogSupport {
			v := entry.DaemonVersion
			if v == "" {
				v = "legacy (<1.2.0)"
			}
			feat := entry.DaemonFeatures
			r.mu.Unlock()
			return false, "UNSUPPORTED", v, feat,
				fmt.Sprintf("Saved on collector, but running daemon (%s) does NOT support system logs. Please upgrade madtom-daemon on this host.", v)
		}
	}

	if entry.DaemonVersion == "" {
		r.mu.Unlock()
		return false, "LEGACY_DAEMON", "legacy (<1.2.0)", nil,
			"Saved on collector. Running daemon is a legacy build (<1.2.0) that cannot confirm configuration sync. Please upgrade madtom-daemon on this host."
	}

	if entry.DaemonAckedHash == targetHash {
		v := entry.DaemonVersion
		feat := entry.DaemonFeatures
		r.mu.Unlock()
		featStr := strings.Join(feat, ", ")
		if featStr == "" {
			featStr = "metrics"
		}
		return true, "CONFIRMED", v, feat,
			fmt.Sprintf("Applied and confirmed by daemon (v%s, features: %s)", v, featStr)
	}

	ch := make(chan struct{}, 1)
	entry.AckListeners = append(entry.AckListeners, ch)
	r.mu.Unlock()

	select {
	case <-ch:
		r.mu.RLock()
		defer r.mu.RUnlock()
		if entry.DaemonAckedHash == targetHash {
			featStr := strings.Join(entry.DaemonFeatures, ", ")
			if featStr == "" {
				featStr = "metrics"
			}
			return true, "CONFIRMED", entry.DaemonVersion, entry.DaemonFeatures,
				fmt.Sprintf("Applied and confirmed by daemon (v%s, features: %s)", entry.DaemonVersion, featStr)
		}
		return false, "PENDING", entry.DaemonVersion, entry.DaemonFeatures,
			"Configuration delivered; awaiting daemon sync confirmation."
	case <-time.After(timeout):
		r.mu.RLock()
		defer r.mu.RUnlock()
		if entry.DaemonAckedHash == targetHash {
			featStr := strings.Join(entry.DaemonFeatures, ", ")
			if featStr == "" {
				featStr = "metrics"
			}
			return true, "CONFIRMED", entry.DaemonVersion, entry.DaemonFeatures,
				fmt.Sprintf("Applied and confirmed by daemon (v%s, features: %s)", entry.DaemonVersion, featStr)
		}
		if entry.DaemonVersion == "" {
			return false, "LEGACY_DAEMON", "", nil,
				"Saved on collector hub. Running daemon does not report version/capabilities; please update madtom-daemon on this host."
		}
		return false, "PENDING_DELIVERY", entry.DaemonVersion, entry.DaemonFeatures,
			fmt.Sprintf("Saved on collector hub; daemon (v%s) has not yet returned confirmation within %v.", entry.DaemonVersion, timeout)
	}
}

// ListNodes evaluates staleness and returns all registered nodes.
func (r *Registry) ListNodes() []*madtomv1.NodeInfo {
	r.mu.Lock()
	defer r.mu.Unlock()

	now := time.Now().UnixNano()
	list := make([]*madtomv1.NodeInfo, 0, len(r.nodes))

	for _, entry := range r.nodes {
		elapsedSec := float64(now-entry.Info.LastSeenUnixNano) / 1e9
		if elapsedSec > 90 {
			entry.Info.Status = "OFFLINE"
		} else if elapsedSec > 30 {
			entry.Info.Status = "STALE"
		} else {
			entry.Info.Status = "ONLINE"
		}
		targetHash := optin.ComputeConfigHash(entry.Config)
		if entry.DaemonAckedHash == targetHash {
			entry.Info.ConfigSyncStatus = "CONFIRMED"
		} else if entry.DaemonVersion == "" {
			entry.Info.ConfigSyncStatus = "LEGACY_DAEMON"
		} else {
			entry.Info.ConfigSyncStatus = "PENDING"
		}
		list = append(list, proto.Clone(entry.Info).(*madtomv1.NodeInfo))
	}

	return list
}

// GetConfig returns the active opt-in configuration for a node.
func (r *Registry) GetConfig(nodeID string) *madtomv1.NodeConfig {
	r.mu.RLock()
	defer r.mu.RUnlock()

	if entry, exists := r.nodes[nodeID]; exists {
		return proto.Clone(entry.Config).(*madtomv1.NodeConfig)
	}
	return defaultConfig(nodeID)
}

// SetConfig updates the opt-in configuration for a node and persists it to disk.
func (r *Registry) SetConfig(nodeID string, cfg *madtomv1.NodeConfig) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if entry, exists := r.nodes[nodeID]; exists {
		entry.Config = proto.Clone(cfg).(*madtomv1.NodeConfig)
		entry.Configured = true
	} else {
		r.nodes[nodeID] = &NodeEntry{
			Info: &madtomv1.NodeInfo{
				NodeId:           nodeID,
				Hostname:         nodeID,
				Status:           "OFFLINE",
				LastSeenUnixNano: time.Now().UnixNano(),
			},
			Config:     cfg,
			Configured: true,
		}
	}
	r.saveToDiskLocked()
}

func (r *Registry) loadFromDisk() {
	if r.storageDir == "" {
		return
	}
	path := filepath.Join(r.storageDir, ConfigsFileName)
	data, err := os.ReadFile(path)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[Registry] Warning: failed to read %s: %v", path, err)
		}
		return
	}

	var rawMap map[string]json.RawMessage
	if err := json.Unmarshal(data, &rawMap); err != nil {
		log.Printf("[Registry] Warning: failed to parse %s: %v", path, err)
		return
	}

	for nodeID, rawCfg := range rawMap {
		cfg := &madtomv1.NodeConfig{}
		if err := protojson.Unmarshal(rawCfg, cfg); err != nil {
			log.Printf("[Registry] Warning: failed to unmarshal config for node %s: %v", nodeID, err)
			continue
		}
		if cfg.NodeId == "" {
			cfg.NodeId = nodeID
		}
		r.nodes[nodeID] = &NodeEntry{
			Info: &madtomv1.NodeInfo{
				NodeId:           nodeID,
				Hostname:         nodeID,
				Status:           "OFFLINE",
				LastSeenUnixNano: 0,
			},
			Config:     cfg,
			Configured: true,
		}
	}
	log.Printf("[Registry] Restored %d node configuration(s) from %s", len(rawMap), path)
}

func (r *Registry) saveToDiskLocked() {
	if r.storageDir == "" {
		return
	}
	rawMap := make(map[string]json.RawMessage)
	opts := protojson.MarshalOptions{
		Multiline:       true,
		Indent:          "  ",
		EmitUnpopulated: false,
	}

	for nodeID, entry := range r.nodes {
		if entry.Configured && entry.Config != nil {
			b, err := opts.Marshal(entry.Config)
			if err != nil {
				log.Printf("[Registry] Warning: failed to marshal config for node %s: %v", nodeID, err)
				continue
			}
			rawMap[nodeID] = json.RawMessage(b)
		}
	}

	data, err := json.MarshalIndent(rawMap, "", "  ")
	if err != nil {
		log.Printf("[Registry] Warning: failed to encode %s: %v", ConfigsFileName, err)
		return
	}

	if err := os.MkdirAll(r.storageDir, 0755); err != nil {
		log.Printf("[Registry] Warning: failed to create storage dir %s: %v", r.storageDir, err)
		return
	}

	targetPath := filepath.Join(r.storageDir, ConfigsFileName)
	tmpPath := targetPath + ".tmp"

	if err := os.WriteFile(tmpPath, data, 0644); err != nil {
		log.Printf("[Registry] Warning: failed to write %s: %v", tmpPath, err)
		return
	}

	if err := os.Rename(tmpPath, targetPath); err != nil {
		_ = os.Remove(tmpPath)
		log.Printf("[Registry] Warning: failed to commit %s: %v", targetPath, err)
	}
}

func defaultConfig(nodeID string) *madtomv1.NodeConfig {
	return &madtomv1.NodeConfig{
		NodeId:                    nodeID,
		CollectCpuOverall:         true,
		CollectCpuPerCore:         true,
		CollectMemoryBasic:        true,
		CollectMemorySwapZram:     true,
		CollectPowerBattery:       true,
		CollectNetworkInterfaces:  true,
		CollectNetworkConnections: false,
		FastPollIntervalMs:        1000,
		NormalPollIntervalMs:      10000,
		SlowPollIntervalMs:        30000,
		EnableZstdCompression:     false,
		MaxSpoolBytes:             1024 * 1024 * 1024,
		ProcessMode:               madtomv1.ProcessTelemetryMode_PROCESS_MODE_LIVE_ONLY,
		TopNProcesses:             5,
	}
}

func (r *Registry) TransportConfig(nodeID string) *madtomv1.NodeConfig {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if entry, ok := r.nodes[nodeID]; ok && entry.Configured {
		return proto.Clone(entry.Config).(*madtomv1.NodeConfig)
	}
	return nil
}
