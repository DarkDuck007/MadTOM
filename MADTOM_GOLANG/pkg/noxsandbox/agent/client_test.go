package agent

import (
	"context"
	"encoding/binary"
	"io"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

// hostileMachine answers every Dial with a scripted (malicious) guest.
type hostileMachine struct {
	reply func(c net.Conn)
	done  chan struct{}
}

func (m *hostileMachine) Dial(context.Context) (net.Conn, error) {
	host, guest := net.Pipe()
	go func() {
		defer guest.Close()
		var hdr [4]byte
		if _, err := io.ReadFull(guest, hdr[:]); err != nil {
			return
		}
		if _, err := io.CopyN(io.Discard, guest, int64(binary.BigEndian.Uint32(hdr[:]))); err != nil {
			return
		}
		m.reply(guest)
	}()
	return host, nil
}
func (m *hostileMachine) ID() string                            { return "hostile" }
func (m *hostileMachine) Spec() vmm.Spec                        { return vmm.Spec{} }
func (m *hostileMachine) Pause(context.Context) error           { return nil }
func (m *hostileMachine) Resume(context.Context) error          { return nil }
func (m *hostileMachine) SetBalloon(context.Context, int) error { return nil }
func (m *hostileMachine) Snapshot(context.Context, string) (vmm.Snapshot, error) {
	return vmm.Snapshot{}, nil
}
func (m *hostileMachine) CPUSeconds() (float64, error) { return 0, nil }
func (m *hostileMachine) Stop(context.Context) error   { return nil }
func (m *hostileMachine) Done() <-chan struct{}        { return m.done }

func frame(payload string) []byte {
	b := make([]byte, 4+len(payload))
	binary.BigEndian.PutUint32(b, uint32(len(payload)))
	copy(b[4:], payload)
	return b
}

func TestHostileGuestRepliesAreRejectedWithoutPanicOrHang(t *testing.T) {
	cases := map[string]func(c net.Conn){
		"huge length":    func(c net.Conn) { c.Write([]byte{0xff, 0xff, 0xff, 0xff}) },
		"zero length":    func(c net.Conn) { c.Write([]byte{0, 0, 0, 0}) },
		"truncated":      func(c net.Conn) { c.Write(frame(`{"id":1,"ok":true}`)[:10]) },
		"not json":       func(c net.Conn) { c.Write(frame(`\x00\x01garbage`)) },
		"bad utf8":       func(c net.Conn) { c.Write(frame("{\"id\":1,\"ok\":true,\"load\":\"\xff\xfe\"}")) },
		"unknown field":  func(c net.Conn) { c.Write(frame(`{"id":1,"ok":true,"load":0,"mem_free_kb":1,"exec":"/bin/sh"}`)) },
		"trailing json":  func(c net.Conn) { c.Write(frame(`{"id":1,"ok":true,"load":0,"mem_free_kb":1}{"id":2}`)) },
		"wrong id":       func(c net.Conn) { c.Write(frame(`{"id":7,"ok":true,"load":0,"mem_free_kb":1}`)) },
		"missing ok":     func(c net.Conn) { c.Write(frame(`{"id":1,"load":0,"mem_free_kb":1}`)) },
		"silent guest":   func(c net.Conn) { time.Sleep(2 * time.Second) },
		"closes at once": func(c net.Conn) {},
	}
	for name, reply := range cases {
		t.Run(name, func(t *testing.T) {
			c := Client{Machine: &hostileMachine{reply: reply, done: make(chan struct{})}, MaxFrameBytes: 1 << 20}
			ctx, cancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
			defer cancel()
			start := time.Now()
			if _, err := c.Ping(ctx); err == nil {
				t.Fatal("hostile reply accepted")
			}
			if time.Since(start) > 1500*time.Millisecond {
				t.Fatal("client did not honour its deadline")
			}
		})
	}
}

func TestGuestErrorTextIsBounded(t *testing.T) {
	long := strings.Repeat("A", 100000)
	c := Client{Machine: &hostileMachine{reply: func(conn net.Conn) {
		conn.Write(frame(`{"id":1,"ok":false,"error":"` + long + `"}`))
	}, done: make(chan struct{})}, MaxFrameBytes: 1 << 20}
	_, err := c.Ping(context.Background())
	if err == nil || len(err.Error()) > 1100 {
		t.Fatalf("guest error not bounded: %d bytes", len(err.Error()))
	}
}
