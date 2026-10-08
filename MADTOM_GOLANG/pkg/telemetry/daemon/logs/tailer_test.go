package logs

import (
	"testing"
)

func TestRateLimiter(t *testing.T) {
	rl := NewRateLimiter(10) // 10 per sec, capacity 100
	if !rl.Allow() {
		t.Fatalf("Expected first call to be allowed")
	}

	// Rapidly exhaust burst
	allowed := 0
	for i := 0; i < 200; i++ {
		if rl.Allow() {
			allowed++
		}
	}
	if allowed > 105 {
		t.Errorf("Allowed too many tokens: %d", allowed)
	}
	dropped := rl.ResetDropped()
	if dropped == 0 {
		t.Errorf("Expected dropped records count > 0")
	}
	if rl.ResetDropped() != 0 {
		t.Errorf("Expected reset dropped to be 0 after call")
	}
}

func TestParseJournalEntry(t *testing.T) {
	line := []byte(`{
		"__CURSOR": "s=abc;i=123",
		"__REALTIME_TIMESTAMP": "1791350381637031",
		"PRIORITY": "3",
		"_SYSTEMD_UNIT": "myapp.service",
		"_PID": "1234",
		"_COMM": "myapp",
		"MESSAGE": "Something failed"
	}`)

	rec, cur, err := ParseJournalEntry(line)
	if err != nil {
		t.Fatalf("ParseJournalEntry: %v", err)
	}
	if cur != "s=abc;i=123" {
		t.Errorf("Expected cursor 's=abc;i=123', got %q", cur)
	}
	if rec.Unit != "myapp.service" {
		t.Errorf("Expected unit 'myapp.service', got %q", rec.Unit)
	}
	if rec.Priority != 3 {
		t.Errorf("Expected priority 3, got %d", rec.Priority)
	}
	if rec.Message != "Something failed" {
		t.Errorf("Expected message 'Something failed', got %q", rec.Message)
	}
	if rec.TimestampUnixNano != 1791350381637031000 {
		t.Errorf("Expected timestamp nano 1791350381637031000, got %d", rec.TimestampUnixNano)
	}
	if rec.Fields["_PID"] != "1234" {
		t.Errorf("Expected _PID 1234, got %q", rec.Fields["_PID"])
	}
}

func TestParseJournalEntry_BinaryMessage(t *testing.T) {
	line := []byte(`{
		"SYSLOG_IDENTIFIER": "kernel",
		"MESSAGE": [72, 101, 108, 108, 111]
	}`)

	rec, _, err := ParseJournalEntry(line)
	if err != nil {
		t.Fatalf("ParseJournalEntry: %v", err)
	}
	if rec.Message != "Hello" {
		t.Errorf("Expected message 'Hello', got %q", rec.Message)
	}
	if rec.Unit != "kernel" {
		t.Errorf("Expected unit 'kernel', got %q", rec.Unit)
	}
}
