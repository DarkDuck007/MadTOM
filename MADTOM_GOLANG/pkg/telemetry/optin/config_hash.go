package optin

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"sort"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// ComputeConfigHash generates a deterministic short hash representing the effective opt-in configuration.
func ComputeConfigHash(cfg *madtomv1.NodeConfig) string {
	if cfg == nil {
		return "none"
	}

	h := sha256.New()
	fmt.Fprintf(h, "id:%s;", cfg.NodeId)
	fmt.Fprintf(h, "poll:%d,%d,%d;", cfg.FastPollIntervalMs, cfg.NormalPollIntervalMs, cfg.SlowPollIntervalMs)
	fmt.Fprintf(h, "twamp:%s,%t,%d;", cfg.TwampTarget, cfg.TwampClocksSynchronized, cfg.TwampMode)
	fmt.Fprintf(h, "proc:%d,%d,%d;", cfg.ProcessMode, cfg.TopNProcesses, cfg.ProcessSnapshotLimit)
	fmt.Fprintf(h, "cpu:%d,%d;", cfg.CpuOverallMode, cfg.CpuPerCoreMode)
	writeSortedMap(h, "cores", cfg.CoreModes)
	fmt.Fprintf(h, "mem:%d,%d,%d;", cfg.MemoryBasicMode, cfg.MemorySwapMode, cfg.ZramMode)
	writeSortedMap(h, "swap", cfg.SwapDeviceModes)
	writeSortedMap(h, "zram", cfg.ZramDeviceModes)
	fmt.Fprintf(h, "net:%d;", cfg.NetworkMode)
	writeSortedMap(h, "nics", cfg.NicModes)
	fmt.Fprintf(h, "disk:%d;", cfg.DiskIoMode)
	writeSortedMap(h, "disks", cfg.DiskDeviceModes)
	fmt.Fprintf(h, "power:%d;", cfg.PowerMode)
	writeSortedMap(h, "pwrs", cfg.PowerMetricModes)
	fmt.Fprintf(h, "log:%d,%d,%d,%d;", cfg.LogMode, cfg.LogRateLimitPerSec, cfg.LogRetentionHours, cfg.LogRetentionBytes)
	units := make([]string, len(cfg.LogUnits))
	copy(units, cfg.LogUnits)
	sort.Strings(units)
	fmt.Fprintf(h, "units:%s;", strings.Join(units, ","))

	sum := h.Sum(nil)
	return hex.EncodeToString(sum[:8])
}

func writeSortedMap(h io.Writer, prefix string, m map[string]madtomv1.TelemetryOptInMode) {
	if len(m) == 0 {
		return
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	fmt.Fprintf(h, "%s:", prefix)
	for _, k := range keys {
		fmt.Fprintf(h, "%s=%d,", k, m[k])
	}
	fmt.Fprint(h, ";")
}
