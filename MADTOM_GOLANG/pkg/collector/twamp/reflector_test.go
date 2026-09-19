package twamp

import (
	"net"
	"testing"

	daemonTwamp "github.com/DarkDuck007/madtom/pkg/daemon/twamp"
)

func TestReflector_ProbeSuccess(t *testing.T) {
	// Bind to an ephemeral port for testing
	reflector := &Reflector{
		port:     0, // ephemeral
		stopChan: make(chan struct{}),
	}
	conn, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("failed to listen on udp: %v", err)
	}
	reflector.conn = conn
	reflector.running.Store(true)
	reflector.wg.Add(1)
	go reflector.serve()
	defer reflector.Stop()

	targetAddr := conn.LocalAddr().String()

	// Perform TWAMP Probe using the daemon's client
	metrics := daemonTwamp.Probe(targetAddr, true, 101)

	if !metrics.Available {
		t.Fatalf("expected metrics to be available, got error: %s", metrics.Error)
	}
	if metrics.RttMs < 0 || metrics.RttMs > 500 {
		t.Errorf("unexpected RttMs: %f", metrics.RttMs)
	}
	if !metrics.OneWayAvailable {
		t.Errorf("expected OneWayAvailable to be true")
	}
	if metrics.ForwardMs < 0 || metrics.ReverseMs < 0 {
		t.Errorf("expected non-negative forward/reverse ms, got fwd=%f rev=%f", metrics.ForwardMs, metrics.ReverseMs)
	}
}

func TestReflector_DisabledWhenPortZero(t *testing.T) {
	reflector := NewReflector(0)
	if err := reflector.Start(); err != nil {
		t.Fatalf("expected nil error for disabled reflector, got: %v", err)
	}
	if reflector.Addr() != nil {
		t.Fatalf("expected Addr() to be nil when disabled")
	}
	reflector.Stop()
}

func TestReflector_StopReentrant(t *testing.T) {
	conn, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("failed to listen on udp: %v", err)
	}
	reflector := &Reflector{
		conn:     conn,
		stopChan: make(chan struct{}),
	}
	reflector.running.Store(true)
	reflector.wg.Add(1)
	go reflector.serve()

	reflector.Stop()
	// Second call should not panic
	reflector.Stop()
}
