package twamp

import (
	"net"
	"strconv"
	"strings"
)

// DefaultPort is the standard IANA UDP port for TWAMP Light (RFC 5357).
const DefaultPort = 862

// ResolveTarget normalizes a TWAMP target string into a host:port UDP address.
// - If target is "collector" or "auto", it resolves to the collector's host and defaultPort.
// - If target is a bare host or IP (no port specified), it appends defaultPort.
// - If target already contains a valid host:port, it is returned as-is.
// - If target is empty, returns "".
func ResolveTarget(target string, defaultPort int, collectorAddr string) string {
	target = strings.TrimSpace(target)
	if target == "" {
		return ""
	}
	if defaultPort <= 0 {
		defaultPort = DefaultPort
	}
	portStr := strconv.Itoa(defaultPort)

	if strings.EqualFold(target, "collector") || strings.EqualFold(target, "auto") {
		if strings.TrimSpace(collectorAddr) == "" {
			return ""
		}
		host, _, err := net.SplitHostPort(collectorAddr)
		if err != nil {
			host = strings.TrimSpace(collectorAddr)
		}
		if host == "" {
			return ""
		}
		return net.JoinHostPort(host, portStr)
	}

	// Check if already contains host:port
	if _, _, err := net.SplitHostPort(target); err == nil {
		return target
	}

	// Bare host, IP, or hostname without port
	return net.JoinHostPort(target, portStr)
}
