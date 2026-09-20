package twamp

import (
	"encoding/binary"
	"net"
	"testing"
	"time"
)

func TestProbeReflector(t *testing.T) {
	conn, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	done := make(chan struct{})
	defer func() { conn.Close(); <-done }()
	go func() {
		defer close(done)
		b := make([]byte, 100)
		n, addr, err := conn.ReadFrom(b)
		if err != nil || n < 14 {
			return
		}
		reply := make([]byte, 41)
		copy(reply[16:24], timestamp(time.Now()))
		copy(reply[24:28], b[:4])
		copy(reply[28:36], b[4:12])
		copy(reply[36:38], b[12:14])
		copy(reply[4:12], timestamp(time.Now()))
		reply[12] = 0x80
		reply[13] = 1
		binary.BigEndian.PutUint32(reply, 0)
		conn.WriteTo(reply, addr)
	}()
	result := Probe(conn.LocalAddr().String(), true, 42)
	if !result.Available || !result.OneWayAvailable || result.RttMs < 0 {
		t.Fatalf("unexpected probe result: %v", result)
	}
}
func TestNoTargetIsUnavailable(t *testing.T) {
	if Probe("", false, 0).Available {
		t.Fatal("missing measurement reported as available")
	}
}
func TestTimestampEra(t *testing.T) {
	for _, year := range []int{2026, 2040} {
		v := time.Date(year, 1, 1, 1, 2, 3, 456789000, time.UTC)
		if d := decode(timestamp(v), v).Sub(v); d > time.Nanosecond || d < -time.Nanosecond {
			t.Fatal(d)
		}
	}
}
