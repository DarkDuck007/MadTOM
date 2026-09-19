// Package twamp implements a native, zero-dependency RFC 5357 TWAMP Light
// UDP Reflector for the MADTOM Collector.
package twamp

import (
	"encoding/binary"
	"errors"
	"fmt"
	"log"
	"net"
	"os"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// DefaultPort is the standard IANA UDP port for TWAMP Light (RFC 5357).
const DefaultPort = 862

// Reflector is an unauthenticated RFC 5357 TWAMP Light UDP reflector.
type Reflector struct {
	port     int
	conn     net.PacketConn
	stopChan chan struct{}
	wg       sync.WaitGroup
	seq      uint32
	running  atomic.Bool
}

// NewReflector creates a new TWAMP Light reflector instance for the specified UDP port.
// A port <= 0 disables the reflector.
func NewReflector(port int) *Reflector {
	return &Reflector{
		port:     port,
		stopChan: make(chan struct{}),
	}
}

// Start begins listening on the configured UDP port in a background goroutine.
// If port <= 0, Start returns immediately with nil (disabled).
func (r *Reflector) Start() error {
	if r.port <= 0 {
		log.Println("[TWAMP Reflector] Disabled (port <= 0)")
		return nil
	}

	addr := fmt.Sprintf(":%d", r.port)
	conn, err := net.ListenPacket("udp", addr)
	if err != nil {
		if errors.Is(err, syscall.EACCES) || errors.Is(err, os.ErrPermission) {
			log.Printf("[TWAMP Reflector] WARNING: Permission denied binding UDP port %d. "+
				"Standard port 862 requires root or CAP_NET_BIND_SERVICE (e.g. 'sudo setcap cap_net_bind_service=+ep /path/to/madtom-collector'). "+
				"Alternatively, start with a high port using -twamp-port=8620.", r.port)
		}
		return fmt.Errorf("failed to bind TWAMP UDP port %d: %w", r.port, err)
	}

	r.conn = conn
	r.running.Store(true)
	r.wg.Add(1)

	log.Printf("[TWAMP Reflector] Listening for RFC 5357 test probes on UDP :%d", r.port)
	go r.serve()
	return nil
}

// Addr returns the bound local network address, or nil if not running.
func (r *Reflector) Addr() net.Addr {
	if r.conn != nil {
		return r.conn.LocalAddr()
	}
	return nil
}

// Stop gracefully stops the UDP reflector and closes the socket.
func (r *Reflector) Stop() {
	if !r.running.Swap(false) {
		return
	}
	close(r.stopChan)
	if r.conn != nil {
		_ = r.conn.Close()
	}
	r.wg.Wait()
	log.Println("[TWAMP Reflector] Stopped")
}

func (r *Reflector) serve() {
	defer r.wg.Done()

	buf := make([]byte, 2048)
	for {
		n, remoteAddr, err := r.conn.ReadFrom(buf)
		if err != nil {
			select {
			case <-r.stopChan:
				return
			default:
				if !r.running.Load() {
					return
				}
				continue
			}
		}

		// T2: Receive timestamp
		t2 := time.Now()

		// A valid TWAMP Light test packet has at least 14 bytes (Seq + T1 + ErrorEstimate)
		if n < 14 {
			continue
		}

		reply := make([]byte, 41)

		// Reflector sequence number (offset 0..4)
		seq := atomic.AddUint32(&r.seq, 1)
		binary.BigEndian.PutUint32(reply[0:4], seq)

		// T3: Transmit timestamp (offset 4..12)
		t3 := time.Now()
		copy(reply[4:12], ntpTimestamp(t3))

		// Reflector Error Estimate (offset 12..14):
		// Bit 0 (0x80) = S bit (Synchronized clocks). Multiplier = 1 (0x01).
		reply[12] = 0x80
		reply[13] = 0x01

		// MBZ (offset 14..16) = 0

		// T2: Receive timestamp (offset 16..24)
		copy(reply[16:24], ntpTimestamp(t2))

		// Sender Sequence Number (offset 24..28, echoed from request 0..4)
		copy(reply[24:28], buf[0:4])

		// Sender Timestamp T1 (offset 28..36, echoed from request 4..12)
		copy(reply[28:36], buf[4:12])

		// Sender Error Estimate (offset 36..38, echoed from request 12..14)
		copy(reply[36:38], buf[12:14])

		// MBZ (offset 38..40) = 0

		// Sender TTL (offset 40..41, echoed from request 13 or default 1)
		if n > 13 && buf[13] != 0 {
			reply[40] = buf[13]
		} else {
			reply[40] = 1
		}

		_, _ = r.conn.WriteTo(reply, remoteAddr)
	}
}

// ntpTimestamp encodes a time.Time into an 8-byte NTP timestamp:
// 32 bits seconds since Jan 1 1900, 32 bits fractional seconds.
func ntpTimestamp(t time.Time) []byte {
	b := make([]byte, 8)
	binary.BigEndian.PutUint32(b[0:4], uint32(t.Unix()+2208988800))
	binary.BigEndian.PutUint32(b[4:8], uint32((uint64(t.Nanosecond())<<32)/1e9))
	return b
}
