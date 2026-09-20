package discovery

import (
	"fmt"
	"net"
	"github.com/DarkDuck007/madtom/pkg/squeeze/config"
	"strings"

	"github.com/grandcat/zeroconf"
)

type Advertisement interface{ Shutdown() }

func isPrivateIPv4(ip net.IP) bool {
	v4 := ip.To4()
	if v4 == nil || v4.IsLoopback() || v4.IsLinkLocalUnicast() {
		return false
	}
	// 10.0.0.0/8
	if v4[0] == 10 {
		return true
	}
	// 172.16.0.0/12
	if v4[0] == 172 && v4[1] >= 16 && v4[1] <= 31 {
		return true
	}
	// 192.168.0.0/16
	if v4[0] == 192 && v4[1] == 168 {
		return true
	}
	return false
}

func getPrimaryOutboundIP() net.IP {
	conn, err := net.DialUDP("udp4", nil, &net.UDPAddr{
		IP:   net.IPv4(8, 8, 8, 8),
		Port: 80,
	})
	if err == nil {
		defer conn.Close()
		if udpAddr, ok := conn.LocalAddr().(*net.UDPAddr); ok {
			if isPrivateIPv4(udpAddr.IP) {
				return udpAddr.IP
			}
		}
	}
	return nil
}

func selectLanInterfaces() ([]net.Interface, string, []string) {
	all, err := net.Interfaces()
	if err != nil {
		return nil, "", nil
	}

	primaryIP := getPrimaryOutboundIP()
	var primaryStr string
	if primaryIP != nil {
		primaryStr = primaryIP.String()
	}

	var physical []net.Interface
	var other []net.Interface
	var allLanIPs []string
	seenIPs := make(map[string]bool)

	for _, iface := range all {
		// Interface must be up, multicast-enabled, and not loopback or point-to-point (cellular/tunnels)
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 ||
			iface.Flags&net.FlagMulticast == 0 || iface.Flags&net.FlagPointToPoint != 0 {
			continue
		}
		name := strings.ToLower(iface.Name)
		// Ignore virtual container/docker/bridge interfaces
		if strings.HasPrefix(name, "docker") || strings.HasPrefix(name, "br-") || strings.HasPrefix(name, "lxc") ||
			strings.HasPrefix(name, "veth") || strings.HasPrefix(name, "virbr") || strings.HasPrefix(name, "tun") ||
			strings.HasPrefix(name, "tap") {
			continue
		}

		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}

		hasPrivateV4 := false
		isPrimaryIface := false
		for _, a := range addrs {
			var ip net.IP
			switch v := a.(type) {
			case *net.IPNet:
				ip = v.IP
			case *net.IPAddr:
				ip = v.IP
			}
			if isPrivateIPv4(ip) {
				hasPrivateV4 = true
				ipStr := ip.String()
				if !seenIPs[ipStr] {
					seenIPs[ipStr] = true
					allLanIPs = append(allLanIPs, ipStr)
				}
				if primaryIP != nil && ip.Equal(primaryIP) {
					isPrimaryIface = true
				}
			}
		}

		// Only include interfaces that actually have an assigned RFC 1918 private IPv4 address
		if !hasPrivateV4 {
			continue
		}

		if strings.HasPrefix(name, "wl") || strings.HasPrefix(name, "en") || strings.HasPrefix(name, "eth") {
			if isPrimaryIface {
				physical = append([]net.Interface{iface}, physical...)
			} else {
				physical = append(physical, iface)
			}
		} else {
			if isPrimaryIface {
				other = append([]net.Interface{iface}, other...)
			} else {
				other = append(other, iface)
			}
		}
	}

	if primaryStr == "" && len(allLanIPs) > 0 {
		primaryStr = allLanIPs[0]
	}

	if len(physical) > 0 {
		return physical, primaryStr, allLanIPs
	}
	return other, primaryStr, allLanIPs
}

func Advertise(c config.Config, hardware []string) (Advertisement, error) {
	gpu := "none"
	if len(hardware) > 0 {
		gpu = strings.Join(hardware, ",")
		if len(gpu) > 240 {
			gpu = gpu[:240]
		}
	}
	text := []string{
		"version=" + config.Version,
		"node_id=" + c.NodeID,
		"gpu=" + gpu,
		"path=/api/v1",
		"auth=" + fmt.Sprint(c.Token != ""),
		fmt.Sprintf("port=%d", c.Port),
	}

	ifaces, preferredIP, allLanIPs := selectLanInterfaces()
	if preferredIP != "" {
		text = append(text, "ip="+preferredIP)
	}
	if len(allLanIPs) > 0 {
		text = append(text, "ips="+strings.Join(allLanIPs, ","))
	}

	for _, v := range text {
		if len(v) > 255 {
			return nil, fmt.Errorf("mDNS TXT record exceeds 255 bytes")
		}
	}

	return zeroconf.Register("SQUEEZE "+c.NodeID, "_squeeze._tcp", "local.", c.Port, text, ifaces)
}
