//go:build linux

package logs

import (
	"bufio"
	"context"
	"fmt"
	"log"
	"os/exec"
	"strconv"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// JournalTailer streams journalctl logs using a child process.
type JournalTailer struct {
	mu          sync.Mutex
	cancel      context.CancelFunc
	running     bool
	cursor      string
	rateLimiter *RateLimiter
	recordCb    func(*madtomv1.LogRecord, string)
	units       []string
	maxPriority uint32
	rateLimit   uint32
}

// NewJournalTailer creates a new tailer.
func NewJournalTailer(recordCb func(*madtomv1.LogRecord, string)) *JournalTailer {
	return &JournalTailer{
		recordCb: recordCb,
	}
}

// Start begins tailing systemd journal logs according to config.
func (t *JournalTailer) Start(cfg *madtomv1.NodeConfig, cursor string) {
	t.mu.Lock()
	defer t.mu.Unlock()

	if t.running {
		t.stopLocked()
	}

	if cfg == nil || cfg.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		return
	}

	t.cursor = cursor
	t.units = cfg.LogUnits
	t.maxPriority = cfg.LogMaxPriority
	t.rateLimit = cfg.LogRateLimitPerSec
	if t.rateLimit == 0 {
		t.rateLimit = 500 // default 500 records/sec
	}
	t.rateLimiter = NewRateLimiter(float64(t.rateLimit))

	ctx, cancel := context.WithCancel(context.Background())
	t.cancel = cancel
	t.running = true

	go t.runLoop(ctx)
}

// NeedsRestart reports whether the active tailer subprocess needs to be started or restarted for cfg.
func (t *JournalTailer) NeedsRestart(cfg *madtomv1.NodeConfig) bool {
	t.mu.Lock()
	defer t.mu.Unlock()

	if cfg == nil || cfg.LogMode == madtomv1.TelemetryOptInMode_OPT_IN_OFF {
		return t.running
	}
	if !t.running {
		return true
	}
	effectiveLimit := cfg.LogRateLimitPerSec
	if effectiveLimit == 0 {
		effectiveLimit = 500
	}
	if t.rateLimit != effectiveLimit {
		return true
	}
	if t.maxPriority != cfg.LogMaxPriority {
		return true
	}
	if len(t.units) != len(cfg.LogUnits) {
		return true
	}
	for i := range t.units {
		if t.units[i] != cfg.LogUnits[i] {
			return true
		}
	}
	return false
}

// Stop halts the tailer subprocess.
func (t *JournalTailer) Stop() {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.stopLocked()
}

func (t *JournalTailer) stopLocked() {
	if t.cancel != nil {
		t.cancel()
		t.cancel = nil
	}
	t.running = false
}

func (t *JournalTailer) runLoop(ctx context.Context) {
	for ctx.Err() == nil {
		t.stream(ctx)
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
	}
}

func (t *JournalTailer) stream(ctx context.Context) {
	args := []string{"-o", "json", "-f"}
	if t.cursor != "" {
		args = append(args, "--after-cursor="+t.cursor)
	} else {
		args = append(args, "-n", "0")
	}

	for _, u := range t.units {
		if u != "" {
			args = append(args, "-u", u)
		}
	}
	if t.maxPriority > 0 {
		args = append(args, "-p", strconv.Itoa(int(t.maxPriority)))
	}

	cmd := exec.CommandContext(ctx, "journalctl", args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		log.Printf("[JournalTailer] Failed to open stdout: %v", err)
		return
	}

	if err := cmd.Start(); err != nil {
		log.Printf("[JournalTailer] Failed to start journalctl: %v", err)
		return
	}

	scanner := bufio.NewScanner(stdout)
	buf := make([]byte, 1024*1024)
	scanner.Buffer(buf, 4*1024*1024)

	dropTicker := time.NewTicker(2 * time.Second)
	defer dropTicker.Stop()

	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case <-dropTicker.C:
				if d := t.rateLimiter.ResetDropped(); d > 0 {
					if t.recordCb != nil {
						t.recordCb(&madtomv1.LogRecord{
							TimestampUnixNano: time.Now().UnixNano(),
							Priority:          4, // Warning
							Unit:              "madtom-daemon",
							Message:           fmt.Sprintf("[RATE LIMIT] Dropped %d journal entries exceeding rate limit (%d/s)", d, t.rateLimit),
						}, t.cursor)
					}
				}
			}
		}
	}()

	for scanner.Scan() {
		line := scanner.Bytes()
		if len(line) == 0 {
			continue
		}

		rec, cur, err := ParseJournalEntry(line)
		if err != nil {
			continue
		}
		if cur != "" {
			t.cursor = cur
		}

		if !t.rateLimiter.Allow() {
			continue
		}

		if t.recordCb != nil {
			t.recordCb(rec, cur)
		}
	}

	_ = cmd.Wait()
}
