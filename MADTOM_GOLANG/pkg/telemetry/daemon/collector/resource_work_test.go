package collector

import (
	"fmt"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

func TestProcessDetailsOnlyReadForRetainedLeaders(t *testing.T) {
	root := t.TempDir()
	for i := 1; i <= 1050; i++ {
		if err := os.Mkdir(filepath.Join(root, strconv.Itoa(i)), 0700); err != nil {
			t.Fatal(err)
		}
	}
	p := NewProcessCollector()
	p.procRoot = root
	statusReads, statReads := 0, 0
	p.readFile = func(path string) ([]byte, error) {
		if path == filepath.Join(root, "stat") {
			return []byte("cpu 100000 0 0 0 0 0 0 0\n"), nil
		}
		pid, _ := strconv.Atoi(filepath.Base(filepath.Dir(path)))
		if filepath.Base(path) == "status" {
			statusReads++
			return []byte("Uid:\t1000\t1000\n"), nil
		}
		statReads++
		fields := make([]string, 22)
		for i := range fields {
			fields[i] = "0"
		}
		fields[0] = "R"
		fields[17] = "2"
		fields[21] = strconv.Itoa(pid)
		return []byte(fmt.Sprintf("%d (worker) %s", pid, strings.Join(fields, " "))), nil
	}
	got, available := p.Collect()
	if !available || len(got) != 1000 {
		t.Fatalf("snapshot: available %v count %d", available, len(got))
	}
	if statReads != 1050 || statusReads != 1000 || len(p.previous) != 1050 {
		t.Fatalf("work counts: %d stats %d details %d baselines", statReads, statusReads, len(p.previous))
	}
	if got[0].Pid != 1050 || got[999].Pid != 51 {
		t.Fatal("wrong retained leaders")
	}
	for _, entry := range got {
		if entry.User != "1000" || entry.Threads != 2 {
			t.Fatal("lost process details")
		}
	}
	for _, test := range []struct {
		configured uint32
		want       int
	}{{0, 1000}, {1, 1}, {7, 7}, {1000, 1000}, {1001, 1000}} {
		statusReads, statReads = 0, 0
		got, available := p.CollectTop(test.configured)
		if !available || len(got) != test.want || statusReads != test.want || statReads != 1050 {
			t.Fatalf("limit %d: available=%v points=%d details=%d counters=%d", test.configured, available, len(got), statusReads, statReads)
		}
		if got[0].Pid != 1050 || got[len(got)-1].Pid != int32(1051-test.want) {
			t.Fatalf("limit %d lost ranked leaders", test.configured)
		}
	}

}

func TestIndependentSwapZramProbes(t *testing.T) {
	for _, test := range []struct{ swap, zram bool }{{false, false}, {true, false}, {false, true}, {true, true}} {
		m := NewMemoryCollector()
		swaps, zrams := 0, 0
		m.readSwapDevices = func(*madtomv1.MemoryMetrics) { swaps++ }
		m.readZramDevices = func(*madtomv1.MemoryMetrics) { zrams++ }
		m.CollectSelected(test.swap, test.zram)
		if (swaps == 1) != test.swap || (zrams == 1) != test.zram {
			t.Fatalf("unexpected probe work for %+v", test)
		}
	}
}

func TestNetworkAllOffOverridesSkipProbe(t *testing.T) {
	e := NewEngine("test")
	e.netCollector = nil
	cfg := DefaultConfig("test")
	cfg.CollectNetworkInterfaces = false
	cfg.NetworkMode = madtomv1.TelemetryOptInMode_OPT_IN_OFF
	cfg.NicModes = map[string]madtomv1.TelemetryOptInMode{"eth0": madtomv1.TelemetryOptInMode_OPT_IN_OFF}
	cfg.ProcessMode = madtomv1.ProcessTelemetryMode_PROCESS_MODE_DISABLED
	if got := e.Collect(cfg); got.Network != nil {
		t.Fatal("network disabled")
	}
	cfg.NicModes["eth0"] = madtomv1.TelemetryOptInMode_OPT_IN_MONITOR_ONLY
	if !hasEnabledDevice(cfg.NicModes) {
		t.Fatal("monitor override must enable probing")
	}
}
