// Package twamp implements the unauthenticated TWAMP Light test sender from
// RFC 5357 section 4 and Appendix I. Session targets are configured explicitly.
package twamp

import (
	"bytes"
	"encoding/binary"
	"fmt"
	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"net"
	"time"
)

func timestamp(t time.Time) []byte {
	b := make([]byte, 8)
	binary.BigEndian.PutUint32(b, uint32(t.Unix()+2208988800))
	binary.BigEndian.PutUint32(b[4:], uint32((uint64(t.Nanosecond())<<32)/1e9))
	return b
}
func decode(b []byte, near time.Time) time.Time {
	seconds := int64(binary.BigEndian.Uint32(b)) - 2208988800
	// Choose the NTP era nearest the current measurement.
	for seconds-near.Unix() > 1<<31 {
		seconds -= 1 << 32
	}
	for near.Unix()-seconds > 1<<31 {
		seconds += 1 << 32
	}
	nanos := (uint64(binary.BigEndian.Uint32(b[4:])) * 1e9) >> 32
	return time.Unix(seconds, int64(nanos))
}
func Probe(target string, synchronized bool, sequence uint32) *madtomv1.TwampMetrics {
	result := &madtomv1.TwampMetrics{Target: target}
	if target == "" {
		result.Error = "No TWAMP reflector configured"
		return result
	}
	conn, err := net.DialTimeout("udp", target, 500*time.Millisecond)
	if err != nil {
		result.Error = err.Error()
		return result
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(500 * time.Millisecond))
	packet := make([]byte, 41)
	binary.BigEndian.PutUint32(packet, sequence)
	t1 := time.Now()
	copy(packet[4:12], timestamp(t1))
	packet[13] = 1
	if synchronized {
		packet[12] = 0x80
	}
	if _, err = conn.Write(packet); err != nil {
		result.Error = err.Error()
		return result
	}
	reply := make([]byte, 2048)
	for {
		n, err := conn.Read(reply)
		t4 := time.Now()
		if err != nil {
			result.Error = err.Error()
			return result
		}
		if n < 41 || binary.BigEndian.Uint32(reply[24:28]) != sequence || !bytes.Equal(reply[28:36], packet[4:12]) {
			continue
		}
		t2, t3 := decode(reply[16:24], t4), decode(reply[4:12], t4)
		processing := t3.Sub(t2)
		rtt := t4.Sub(t1) - processing
		if processing < 0 || rtt < 0 {
			result.Error = "Invalid reflector timestamps"
			return result
		}
		result.Available = true
		result.RttMs = float64(rtt) / float64(time.Millisecond)
		if synchronized && reply[12]&0x80 != 0 {
			fwd, rev := t2.Sub(t1), t4.Sub(t3)
			if fwd >= 0 && rev >= 0 {
				result.OneWayAvailable = true
				result.ForwardMs = float64(fwd) / float64(time.Millisecond)
				result.ReverseMs = float64(rev) / float64(time.Millisecond)
			} else {
				result.Error = fmt.Sprint("Clock offset prevents one-way measurements")
			}
		}
		return result
	}
}
