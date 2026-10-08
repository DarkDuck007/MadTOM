package logs

import (
	"encoding/json"
	"fmt"
	"strconv"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// RateLimiter enforces a maximum records per second rate using a token bucket.
type RateLimiter struct {
	mu           sync.Mutex
	rate         float64 // tokens per sec
	capacity     float64
	tokens       float64
	lastRefill   time.Time
	droppedCount uint64
}

// NewRateLimiter creates a RateLimiter. If rate <= 0, no rate limiting is enforced.
func NewRateLimiter(rate float64) *RateLimiter {
	capVal := rate
	if capVal <= 0 {
		capVal = 0
	} else if capVal < 100 {
		capVal = 100
	}
	return &RateLimiter{
		rate:       rate,
		capacity:   capVal,
		tokens:     capVal,
		lastRefill: time.Now(),
	}
}

// Allow returns true if a record may pass; false if it must be dropped.
func (rl *RateLimiter) Allow() bool {
	if rl.rate <= 0 {
		return true
	}
	rl.mu.Lock()
	defer rl.mu.Unlock()

	now := time.Now()
	elapsed := now.Sub(rl.lastRefill).Seconds()
	rl.lastRefill = now

	rl.tokens += elapsed * rl.rate
	if rl.tokens > rl.capacity {
		rl.tokens = rl.capacity
	}

	if rl.tokens >= 1.0 {
		rl.tokens -= 1.0
		return true
	}

	rl.droppedCount++
	return false
}

// ResetDropped returns and clears the count of dropped records.
func (rl *RateLimiter) ResetDropped() uint64 {
	rl.mu.Lock()
	defer rl.mu.Unlock()
	d := rl.droppedCount
	rl.droppedCount = 0
	return d
}

// ParseJournalEntry parses a single line of journalctl -o json output.
// Returns the LogRecord, the cursor string, and any parse error.
func ParseJournalEntry(line []byte) (*madtomv1.LogRecord, string, error) {
	var raw map[string]interface{}
	if err := json.Unmarshal(line, &raw); err != nil {
		return nil, "", err
	}

	rec := &madtomv1.LogRecord{
		Fields: make(map[string]string),
	}
	cursor := ""

	for k, v := range raw {
		switch k {
		case "MESSAGE":
			rec.Message = extractStringOrBytes(v)
		case "__REALTIME_TIMESTAMP":
			rec.TimestampUnixNano = parseRealtimeNano(v)
		case "PRIORITY":
			rec.Priority = parsePriority(v)
		case "_SYSTEMD_UNIT":
			if str := extractStringOrBytes(v); str != "" {
				rec.Unit = str
			}
		case "SYSLOG_IDENTIFIER":
			if rec.Unit == "" {
				rec.Unit = extractStringOrBytes(v)
			}
		case "__CURSOR":
			cursor = extractStringOrBytes(v)
		default:
			// Retain useful identifiers: PID, COMM, EXE, HOSTNAME
			if k == "_PID" || k == "_COMM" || k == "_EXE" || k == "_HOSTNAME" {
				strVal := extractStringOrBytes(v)
				if strVal != "" {
					rec.Fields[k] = strVal
				}
			}
		}
	}

	if rec.Unit == "" {
		if comm, ok := rec.Fields["_COMM"]; ok {
			rec.Unit = comm
		} else {
			rec.Unit = "system"
		}
	}

	return rec, cursor, nil
}

func extractStringOrBytes(v interface{}) string {
	switch val := v.(type) {
	case string:
		return val
	case []interface{}:
		// Binary message serialized as integer byte array
		b := make([]byte, len(val))
		for i, item := range val {
			if num, ok := item.(float64); ok {
				b[i] = byte(num)
			}
		}
		return string(b)
	case float64:
		return strconv.FormatInt(int64(val), 10)
	default:
		return fmt.Sprintf("%v", val)
	}
}

func parseRealtimeNano(v interface{}) int64 {
	switch val := v.(type) {
	case string:
		if micros, err := strconv.ParseInt(val, 10, 64); err == nil {
			return micros * 1000
		}
	case float64:
		return int64(val) * 1000
	}
	return time.Now().UnixNano()
}

func parsePriority(v interface{}) uint32 {
	switch val := v.(type) {
	case string:
		if p, err := strconv.ParseUint(val, 10, 32); err == nil {
			return uint32(p)
		}
	case float64:
		return uint32(val)
	}
	return 6 // Default syslog INFO
}
