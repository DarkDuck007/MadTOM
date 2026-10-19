package network

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/vishvananda/netlink"
	"golang.org/x/sys/unix"
)

// NetworkProber encapsulates low-level Linux network interrogation:
// netlink (links, addrs, routes, neighs), sock_diag (tcp_info), nftables JSON, and diagnostics.
type NetworkProber struct {
	mu            sync.Mutex
	bootID        string
	lastLinkStats map[string]linkStatsSnapshot
	lastStatsTime time.Time
}

type linkStatsSnapshot struct {
	rxBytes   uint64
	txBytes   uint64
	rxPackets uint64
	txPackets uint64
	rxDrops   uint64
	txDrops   uint64
	rxErrors  uint64
	txErrors  uint64
}

func NewNetworkProber() *NetworkProber {
	bootID := readBootID()
	return &NetworkProber{
		bootID:        bootID,
		lastLinkStats: make(map[string]linkStatsSnapshot),
	}
}

func readBootID() string {
	b, err := os.ReadFile("/proc/sys/kernel/random/boot_id")
	if err == nil {
		return strings.TrimSpace(string(b))
	}
	return "unknown-boot-id"
}

func (p *NetworkProber) BootID() string {
	return p.bootID
}

// ---------------------------------------------------------------------
// STAGE: Capabilities
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeCapabilities(ctx context.Context) *madtomv1.CapabilitiesStagePayload {
	hasNetlink := true
	hasSockDiag := checkNetlinkSockDiag()
	hasNft := checkCommand("nft")
	hasIpt := checkCommand("iptables")

	limitations := make(map[string]string)
	if !hasNft {
		limitations["firewall"] = "nft executable not found in PATH"
	}
	if !hasSockDiag {
		limitations["sock_diag"] = "NETLINK_INET_DIAG socket permission restricted or unsupported"
	}

	payload := &madtomv1.CapabilitiesStagePayload{
		HasNetlink:                  hasNetlink,
		HasSockDiag:                 hasSockDiag,
		HasNftables:                 hasNft,
		HasLegacyIptables:           hasIpt,
		NftablesVersion:             readNftVersion(),
		DefaultCongestionControl:    readDefaultTcpCc(),
		AvailableCongestionControls: readAvailableTcpCc(),
		RootNamespace:               "root",
		AccessibleNamespaces:        []string{"root"},
		SectionLimitations:          limitations,
	}
	return payload
}

func readNftVersion() string {
	cmd := exec.Command("nft", "-v")
	out, err := cmd.Output()
	if err == nil {
		return strings.TrimSpace(string(out))
	}
	return ""
}

func readDefaultTcpCc() string {
	b, err := os.ReadFile("/proc/sys/net/ipv4/tcp_congestion_control")
	if err == nil {
		return strings.TrimSpace(string(b))
	}
	return "cubic"
}

func checkNetlinkSockDiag() bool {
	fd, err := unix.Socket(unix.AF_NETLINK, unix.SOCK_DGRAM, unix.NETLINK_INET_DIAG)
	if err == nil {
		unix.Close(fd)
		return true
	}
	return false
}

func checkCommand(name string) bool {
	_, err := exec.LookPath(name)
	return err == nil
}

func readAvailableTcpCc() []string {
	b, err := os.ReadFile("/proc/sys/net/ipv4/tcp_available_congestion_control")
	if err == nil {
		return strings.Fields(strings.TrimSpace(string(b)))
	}
	return []string{"cubic", "reno"}
}

// ---------------------------------------------------------------------
// STAGE: Interfaces
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeInterfaces(ctx context.Context, nodeID string) (*madtomv1.InterfaceStagePayload, error) {
	links, err := netlink.LinkList()
	if err != nil {
		return nil, fmt.Errorf("netlink link list: %w", err)
	}

	now := time.Now()
	p.mu.Lock()
	elapsedSec := now.Sub(p.lastStatsTime).Seconds()
	if elapsedSec <= 0 {
		elapsedSec = 1.0
	}
	newStats := make(map[string]linkStatsSnapshot)

	payload := &madtomv1.InterfaceStagePayload{
		Interfaces: make([]*madtomv1.InterfaceNode, 0, len(links)),
	}

	for _, link := range links {
		attrs := link.Attrs()
		if attrs == nil {
			continue
		}

		speed := readLinkSpeed(attrs.Name)
		duplex := readLinkDuplex(attrs.Name)
		kind := detectLinkKind(link)
		carrierUp := attrs.OperState == netlink.OperUp

		stats := attrs.Statistics
		var rxBps, txBps float64
		if old, exists := p.lastLinkStats[attrs.Name]; exists && elapsedSec > 0 {
			if stats.RxBytes >= old.rxBytes {
				rxBps = float64(stats.RxBytes-old.rxBytes) / elapsedSec
			}
			if stats.TxBytes >= old.txBytes {
				txBps = float64(stats.TxBytes-old.txBytes) / elapsedSec
			}
		}

		newStats[attrs.Name] = linkStatsSnapshot{
			rxBytes:   stats.RxBytes,
			txBytes:   stats.TxBytes,
			rxPackets: stats.RxPackets,
			txPackets: stats.TxPackets,
			rxDrops:   stats.RxDropped,
			txDrops:   stats.TxDropped,
			rxErrors:  stats.RxErrors,
			txErrors:  stats.TxErrors,
		}

		parentName := ""
		if attrs.ParentIndex > 0 {
			if parentLink, err := netlink.LinkByIndex(attrs.ParentIndex); err == nil {
				parentName = parentLink.Attrs().Name
			}
		}

		node := &madtomv1.InterfaceNode{
			Ifindex:         int32(attrs.Index),
			Name:            attrs.Name,
			MacAddress:      attrs.HardwareAddr.String(),
			Kind:            kind,
			CarrierUp:       carrierUp,
			IsUp:            attrs.Flags&net.FlagUp != 0,
			Mtu:             uint32(attrs.MTU),
			LinkSpeedMbps:   uint32(speed),
			Duplex:          duplex,
			MasterIfindex:   int32(attrs.MasterIndex),
			ParentInterface: parentName,
			Namespace:       "root",
			RxBytesPerSec:   rxBps,
			TxBytesPerSec:   txBps,
			RxPackets:       stats.RxPackets,
			TxPackets:       stats.TxPackets,
			RxDropped:       stats.RxDropped,
			TxDropped:       stats.TxDropped,
			RxErrors:        stats.RxErrors,
			TxErrors:        stats.TxErrors,
		}

		// Also attach known IPs to the interface card
		addrs, err := netlink.AddrList(link, netlink.FAMILY_ALL)
		if err == nil {
			for _, addr := range addrs {
				if addr.IPNet != nil {
					node.IpAddresses = append(node.IpAddresses, addr.IPNet.String())
				}
			}
		}

		payload.Interfaces = append(payload.Interfaces, node)
	}

	p.lastLinkStats = newStats
	p.lastStatsTime = now
	p.mu.Unlock()

	return payload, nil
}

func detectLinkKind(link netlink.Link) string {
	lType := link.Type()
	switch lType {
	case "device":
		if strings.HasPrefix(link.Attrs().Name, "wl") {
			return "wifi"
		}
		return "ethernet"
	case "bridge":
		return "bridge"
	case "veth":
		return "veth"
	case "wireguard":
		return "wireguard"
	case "bond":
		return "bond"
	case "vlan":
		return "vlan"
	case "loopback":
		return "loopback"
	case "tun", "tap":
		return "tunnel"
	default:
		return lType
	}
}

func readLinkSpeed(name string) int64 {
	b, err := os.ReadFile(fmt.Sprintf("/sys/class/net/%s/speed", name))
	if err == nil {
		s, err := strconv.ParseInt(strings.TrimSpace(string(b)), 10, 64)
		if err == nil && s >= 0 {
			return s
		}
	}
	return 0
}

func readLinkDuplex(name string) string {
	b, err := os.ReadFile(fmt.Sprintf("/sys/class/net/%s/duplex", name))
	if err == nil {
		return strings.TrimSpace(string(b))
	}
	return "unknown"
}

// ---------------------------------------------------------------------
// STAGE: Addresses & Neighbors
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeAddressesAndNeighbors(ctx context.Context) (*madtomv1.AddressesNeighborsStagePayload, error) {
	links, err := netlink.LinkList()
	if err != nil {
		return nil, fmt.Errorf("netlink link list: %w", err)
	}

	payload := &madtomv1.AddressesNeighborsStagePayload{
		Addresses: make([]*madtomv1.InterfaceAddress, 0),
		Neighbors: make([]*madtomv1.NeighborEntry, 0),
	}

	for _, link := range links {
		attrs := link.Attrs()
		if attrs == nil {
			continue
		}

		addrs, err := netlink.AddrList(link, netlink.FAMILY_ALL)
		if err == nil {
			for i, addr := range addrs {
				if addr.IPNet == nil {
					continue
				}
				isV6 := addr.IP.To4() == nil
				payload.Addresses = append(payload.Addresses, &madtomv1.InterfaceAddress{
					Ifindex:       int32(attrs.Index),
					InterfaceName: attrs.Name,
					Cidr:          addr.IPNet.String(),
					Scope:         addrScopeToString(addr.Scope),
					IsIpv6:        isV6,
					IsPrimary:     i == 0 && (addr.Flags&unix.IFA_F_SECONDARY == 0),
				})
			}
		}

		neighs, err := netlink.NeighList(attrs.Index, netlink.FAMILY_ALL)
		if err == nil {
			for _, neigh := range neighs {
				payload.Neighbors = append(payload.Neighbors, &madtomv1.NeighborEntry{
					Ifindex:       int32(attrs.Index),
					InterfaceName: attrs.Name,
					IpAddress:     neigh.IP.String(),
					MacAddress:    neigh.HardwareAddr.String(),
					State:         neighStateToString(neigh.State),
					IsRouter:      neigh.Flags&netlink.NTF_ROUTER != 0,
				})
			}
		}
	}

	return payload, nil
}

func addrScopeToString(scope int) string {
	switch scope {
	case unix.RT_SCOPE_UNIVERSE:
		return "global"
	case unix.RT_SCOPE_SITE:
		return "site"
	case unix.RT_SCOPE_LINK:
		return "link"
	case unix.RT_SCOPE_HOST:
		return "host"
	case unix.RT_SCOPE_NOWHERE:
		return "nowhere"
	default:
		return fmt.Sprintf("scope-%d", scope)
	}
}

func neighStateToString(state int) string {
	switch state {
	case netlink.NUD_INCOMPLETE:
		return "INCOMPLETE"
	case netlink.NUD_REACHABLE:
		return "REACHABLE"
	case netlink.NUD_STALE:
		return "STALE"
	case netlink.NUD_DELAY:
		return "DELAY"
	case netlink.NUD_PROBE:
		return "PROBE"
	case netlink.NUD_FAILED:
		return "FAILED"
	case netlink.NUD_NOARP:
		return "NOARP"
	case netlink.NUD_PERMANENT:
		return "PERMANENT"
	default:
		return "NONE"
	}
}

// ---------------------------------------------------------------------
// STAGE: Routing Table
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeRoutingTable(ctx context.Context) (*madtomv1.RoutingStagePayload, error) {
	routes4, err4 := netlink.RouteListFiltered(netlink.FAMILY_V4, &netlink.Route{}, 0)
	routes6, err6 := netlink.RouteListFiltered(netlink.FAMILY_V6, &netlink.Route{}, 0)

	var routes []netlink.Route
	if err4 == nil {
		routes = append(routes, routes4...)
	}
	if err6 == nil {
		routes = append(routes, routes6...)
	}
	if err4 != nil && err6 != nil {
		return nil, fmt.Errorf("route list failed: v4 err: %v, v6 err: %v", err4, err6)
	}

	linkNameMap := make(map[int]string)
	if links, err := netlink.LinkList(); err == nil {
		for _, l := range links {
			linkNameMap[l.Attrs().Index] = l.Attrs().Name
		}
	}

	payload := &madtomv1.RoutingStagePayload{
		Routes: make([]*madtomv1.RouteEntry, 0, len(routes)),
	}

	for _, r := range routes {
		dst := "default"
		isDefault := false
		isV6 := false

		if r.Dst != nil {
			dst = r.Dst.String()
			if r.Dst.IP.To4() == nil {
				isV6 = true
			}
		} else {
			isDefault = true
		}

		gw := ""
		if r.Gw != nil {
			gw = r.Gw.String()
		}

		src := ""
		if r.Src != nil {
			src = r.Src.String()
		}

		entry := &madtomv1.RouteEntry{
			Destination:     dst,
			Gateway:         gw,
			Ifindex:         int32(r.LinkIndex),
			InterfaceName:   linkNameMap[r.LinkIndex],
			Protocol:        routeProtocolToString(int(r.Protocol)),
			Scope:           addrScopeToString(int(r.Scope)),
			Metric:          uint32(r.Priority),
			Table:           uint32(r.Table),
			IsIpv6:          isV6,
			IsDefault:       isDefault,
			Type:            routeTypeToString(r.Type),
			PreferredSource: src,
			Flags:           parseRouteFlags(r.Flags),
		}

		payload.Routes = append(payload.Routes, entry)
	}

	// Read routing policy rules
	rules, err := netlink.RuleList(netlink.FAMILY_ALL)
	if err == nil {
		for _, rl := range rules {
			src := ""
			if rl.Src != nil {
				src = rl.Src.String()
			}
			dst := ""
			if rl.Dst != nil {
				dst = rl.Dst.String()
			}
			payload.PolicyRules = append(payload.PolicyRules, &madtomv1.RoutingPolicyRule{
				Priority:      uint32(rl.Priority),
				Source:        src,
				Destination:   dst,
				Table:         uint32(rl.Table),
				InterfaceName: rl.IifName,
				Action:        "lookup",
			})
		}
	}

	return payload, nil
}

func parseRouteFlags(flags int) []string {
	var res []string
	if flags&unix.RTNH_F_ONLINK != 0 {
		res = append(res, "ONLINK")
	}
	if flags&unix.RTNH_F_PERVASIVE != 0 {
		res = append(res, "PERVASIVE")
	}
	return res
}

func ruleActionToString(action int) string {
	switch action {
	case unix.FR_ACT_TO_TBL:
		return "lookup"
	case unix.FR_ACT_GOTO:
		return "goto"
	case unix.FR_ACT_NOP:
		return "nop"
	case unix.FR_ACT_BLACKHOLE:
		return "blackhole"
	case unix.FR_ACT_UNREACHABLE:
		return "unreachable"
	case unix.FR_ACT_PROHIBIT:
		return "prohibit"
	default:
		return fmt.Sprintf("action-%d", action)
	}
}

func routeProtocolToString(proto int) string {
	switch proto {
	case unix.RTPROT_UNSPEC:
		return "unspec"
	case unix.RTPROT_REDIRECT:
		return "redirect"
	case unix.RTPROT_KERNEL:
		return "kernel"
	case unix.RTPROT_BOOT:
		return "boot"
	case unix.RTPROT_STATIC:
		return "static"
	case unix.RTPROT_DHCP:
		return "dhcp"
	case 186:
		return "bgp"
	case 187:
		return "isis"
	case 188:
		return "ospf"
	case 189:
		return "rip"
	default:
		return fmt.Sprintf("proto-%d", proto)
	}
}

func routeTypeToString(rtType int) string {
	switch rtType {
	case unix.RTN_UNSPEC:
		return "unspec"
	case unix.RTN_UNICAST:
		return "unicast"
	case unix.RTN_LOCAL:
		return "local"
	case unix.RTN_BROADCAST:
		return "broadcast"
	case unix.RTN_ANYCAST:
		return "anycast"
	case unix.RTN_MULTICAST:
		return "multicast"
	case unix.RTN_BLACKHOLE:
		return "blackhole"
	case unix.RTN_UNREACHABLE:
		return "unreachable"
	case unix.RTN_PROHIBIT:
		return "prohibit"
	case unix.RTN_THROW:
		return "throw"
	case unix.RTN_NAT:
		return "nat"
	default:
		return fmt.Sprintf("type-%d", rtType)
	}
}

// ---------------------------------------------------------------------
// STAGE: Firewall (nftables JSON)
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeFirewall(ctx context.Context) (*madtomv1.FirewallStagePayload, error) {
	cmd := exec.CommandContext(ctx, "nft", "-j", "-a", "list", "ruleset")
	out, err := cmd.Output()
	if err != nil {
		return &madtomv1.FirewallStagePayload{
			Backend:         "nftables",
			RulesetVersion:  "empty",
			Tables:          nil,
			TotalRuleCount:  0,
		}, nil
	}

	payload, err := ParseNftablesRulesetJson(out)
	if err != nil {
		return &madtomv1.FirewallStagePayload{
			Backend:        "nftables",
			RulesetVersion: "unparsed",
		}, nil
	}

	return payload, nil
}

// ParseNftablesRulesetJson parses output of `nft -j -a list ruleset`
func ParseNftablesRulesetJson(raw []byte) (*madtomv1.FirewallStagePayload, error) {
	var doc struct {
		Nftables []map[string]json.RawMessage `json:"nftables"`
	}

	if err := json.Unmarshal(raw, &doc); err != nil {
		return nil, fmt.Errorf("unmarshal nftables JSON: %w", err)
	}

	var version string
	tablesMap := make(map[string]*madtomv1.NftTable)
	chainsMap := make(map[string]*madtomv1.NftChain)
	var totalRules uint32

	for _, item := range doc.Nftables {
		for key, val := range item {
			switch key {
			case "metainfo":
				var meta struct {
					Version string `json:"version"`
				}
				if err := json.Unmarshal(val, &meta); err == nil {
					version = meta.Version
				}

			case "table":
				var t struct {
					Family string `json:"family"`
					Name   string `json:"name"`
				}
				if err := json.Unmarshal(val, &t); err == nil {
					tbl := &madtomv1.NftTable{
						Name:   t.Name,
						Family: t.Family,
					}
					tKey := fmt.Sprintf("%s:%s", t.Family, t.Name)
					tablesMap[tKey] = tbl
				}

			case "chain":
				var c struct {
					Family string `json:"family"`
					Table  string `json:"table"`
					Name   string `json:"name"`
					Type   string `json:"type"`
					Hook   string `json:"hook"`
					Prio   int32  `json:"prio"`
					Policy string `json:"policy"`
				}
				if err := json.Unmarshal(val, &c); err == nil {
					chn := &madtomv1.NftChain{
						Name:     c.Name,
						Type:     c.Type,
						Hook:     c.Hook,
						Priority: c.Prio,
						Policy:   c.Policy,
					}
					cKey := fmt.Sprintf("%s:%s:%s", c.Family, c.Table, c.Name)
					chainsMap[cKey] = chn
				}

			case "rule":
				var r struct {
					Family  string          `json:"family"`
					Table   string          `json:"table"`
					Chain   string          `json:"chain"`
					Handle  uint64          `json:"handle"`
					Comment string          `json:"comment"`
					Expr    json.RawMessage `json:"expr"`
				}
				if err := json.Unmarshal(val, &r); err == nil {
					totalRules++
					rule := &madtomv1.NftRule{
						Handle:    r.Handle,
						Statement: summarizeExpr(r.Expr),
						Verdict:   extractAction(r.Expr),
						Comment:   r.Comment,
						RawJson:   string(r.Expr),
					}
					cKey := fmt.Sprintf("%s:%s:%s", r.Family, r.Table, r.Chain)
					if chn, exists := chainsMap[cKey]; exists {
						chn.Rules = append(chn.Rules, rule)
					}
				}
			}
		}
	}

	for _, chn := range chainsMap {
		// Recover table key from chain's rules or by indexing
		for tKey, tbl := range tablesMap {
			parts := strings.Split(tKey, ":")
			if len(parts) == 2 {
				cKeyPrefix := fmt.Sprintf("%s:%s:", parts[0], parts[1])
				for k, c := range chainsMap {
					if c == chn && strings.HasPrefix(k, cKeyPrefix) {
						tbl.Chains = append(tbl.Chains, chn)
						break
					}
				}
			}
		}
	}

	tables := make([]*madtomv1.NftTable, 0, len(tablesMap))
	for _, tbl := range tablesMap {
		tables = append(tables, tbl)
	}

	return &madtomv1.FirewallStagePayload{
		Backend:        "nftables",
		RulesetVersion: version,
		Tables:         tables,
		TotalRuleCount: totalRules,
	}, nil
}

func summarizeExpr(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	return string(raw)
}

func extractAction(raw json.RawMessage) string {
	s := string(raw)
	if strings.Contains(s, `"accept"`) {
		return "accept"
	}
	if strings.Contains(s, `"drop"`) {
		return "drop"
	}
	if strings.Contains(s, `"reject"`) {
		return "reject"
	}
	if strings.Contains(s, `"masquerade"`) {
		return "masquerade"
	}
	if strings.Contains(s, `"return"`) {
		return "return"
	}
	return "eval"
}

// ---------------------------------------------------------------------
// STAGE: Sockets & TCP Metrics
// ---------------------------------------------------------------------

func (p *NetworkProber) ProbeSockets(ctx context.Context, maxSockets uint32) (*madtomv1.SocketsStagePayload, error) {
	if maxSockets == 0 {
		maxSockets = 500
	}

	payload := &madtomv1.SocketsStagePayload{
		Sockets: make([]*madtomv1.SocketEntry, 0),
		CongestionSummary: &madtomv1.TcpCongestionSummary{
			DefaultAlgorithm:     readDefaultTcpCc(),
			AvailableAlgorithms:  readAvailableTcpCc(),
			SocketsPerAlgorithm: make(map[string]uint32),
		},
	}

	socketsV4, err4 := parseProcNetTCP("/proc/net/tcp", "IPv4", maxSockets)
	if err4 == nil {
		payload.Sockets = append(payload.Sockets, socketsV4...)
	}

	remaining := maxSockets - uint32(len(payload.Sockets))
	if remaining > 0 {
		socketsV6, err6 := parseProcNetTCP("/proc/net/tcp6", "IPv6", remaining)
		if err6 == nil {
			payload.Sockets = append(payload.Sockets, socketsV6...)
		}
	}

	// Update summaries
	for _, s := range payload.Sockets {
		switch s.State {
		case "ESTABLISHED":
			payload.TotalEstablished++
		case "LISTEN":
			payload.TotalListening++
		case "TIME_WAIT":
			payload.TotalTimeWait++
		}
		if s.CongestionAlgorithm != "" {
			payload.CongestionSummary.SocketsPerAlgorithm[s.CongestionAlgorithm]++
		}
	}

	enrichSocketProcesses(payload.Sockets)

	return payload, nil
}

func parseProcNetTCP(path string, family string, limit uint32) ([]*madtomv1.SocketEntry, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	var entries []*madtomv1.SocketEntry
	dec := bufioReader(f)

	// Skip header
	_, _ = dec.ReadString('\n')

	for uint32(len(entries)) < limit {
		line, err := dec.ReadString('\n')
		if len(line) == 0 && err != nil {
			break
		}

		fields := strings.Fields(line)
		if len(fields) < 10 {
			continue
		}

		localIP, localPort := parseHexAddr(fields[1], family)
		remoteIP, remotePort := parseHexAddr(fields[2], family)
		stateCode, _ := strconv.ParseUint(fields[3], 16, 8)
		inode, _ := strconv.ParseUint(fields[9], 10, 64)

		stateStr := tcpStateToString(uint8(stateCode))

		entry := &madtomv1.SocketEntry{
			SocketCookie:        fmt.Sprintf("%s:%d-%s:%d-%d", localIP, localPort, remoteIP, remotePort, inode),
			LocalAddress:        localIP,
			LocalPort:           uint32(localPort),
			RemoteAddress:       remoteIP,
			RemotePort:          uint32(remotePort),
			State:               stateStr,
			CongestionAlgorithm: "cubic",
		}

		entries = append(entries, entry)
	}

	return entries, nil
}

func bufioReader(r *os.File) *bufioReaderHelper {
	return &bufioReaderHelper{r: r}
}

type bufioReaderHelper struct {
	r *os.File
	b [4096]byte
	i int
	n int
}

func (h *bufioReaderHelper) ReadString(delim byte) (string, error) {
	var res []byte
	for {
		if h.i >= h.n {
			n, err := h.r.Read(h.b[:])
			if n == 0 {
				return string(res), err
			}
			h.i = 0
			h.n = n
		}
		ch := h.b[h.i]
		h.i++
		res = append(res, ch)
		if ch == delim {
			return string(res), nil
		}
	}
}

func parseHexAddr(s string, family string) (string, uint16) {
	parts := strings.Split(s, ":")
	if len(parts) != 2 {
		return "", 0
	}

	portNum, _ := strconv.ParseUint(parts[1], 16, 16)

	if family == "IPv4" {
		ipBytes, err := strconv.ParseUint(parts[0], 16, 32)
		if err != nil {
			return "", uint16(portNum)
		}
		ip := net.IPv4(
			byte(ipBytes),
			byte(ipBytes>>8),
			byte(ipBytes>>16),
			byte(ipBytes>>24),
		)
		return ip.String(), uint16(portNum)
	}

	if len(parts[0]) == 32 {
		var ip net.IP = make([]byte, 16)
		for i := 0; i < 4; i++ {
			dw, _ := strconv.ParseUint(parts[0][i*8:(i+1)*8], 16, 32)
			ip[i*4] = byte(dw)
			ip[i*4+1] = byte(dw >> 8)
			ip[i*4+2] = byte(dw >> 16)
			ip[i*4+3] = byte(dw >> 24)
		}
		return ip.String(), uint16(portNum)
	}

	return parts[0], uint16(portNum)
}

func tcpStateToString(st uint8) string {
	switch st {
	case 1:
		return "ESTABLISHED"
	case 2:
		return "SYN_SENT"
	case 3:
		return "SYN_RECV"
	case 4:
		return "FIN_WAIT1"
	case 5:
		return "FIN_WAIT2"
	case 6:
		return "TIME_WAIT"
	case 7:
		return "CLOSE"
	case 8:
		return "CLOSE_WAIT"
	case 9:
		return "LAST_ACK"
	case 10:
		return "LISTEN"
	case 11:
		return "CLOSING"
	default:
		return fmt.Sprintf("STATE_%d", st)
	}
}

func enrichSocketProcesses(sockets []*madtomv1.SocketEntry) {
	if len(sockets) == 0 {
		return
	}

	cookieMap := make(map[uint64]*madtomv1.SocketEntry)
	for _, s := range sockets {
		// Cookie is local:port-remote:port-inode
		parts := strings.Split(s.SocketCookie, "-")
		if len(parts) == 3 {
			if inode, err := strconv.ParseUint(parts[2], 10, 64); err == nil && inode > 0 {
				cookieMap[inode] = s
			}
		}
	}

	entries, err := os.ReadDir("/proc")
	if err != nil {
		return
	}

	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		pid, err := strconv.ParseUint(entry.Name(), 10, 32)
		if err != nil {
			continue
		}

		fdDir := fmt.Sprintf("/proc/%d/fd", pid)
		fds, err := os.ReadDir(fdDir)
		if err != nil {
			continue
		}

		var procName string
		for _, fd := range fds {
			link, err := os.Readlink(fmt.Sprintf("%s/%s", fdDir, fd.Name()))
			if err != nil {
				continue
			}

			if strings.HasPrefix(link, "socket:[") && strings.HasSuffix(link, "]") {
				inodeStr := link[8 : len(link)-1]
				inode, err := strconv.ParseUint(inodeStr, 10, 64)
				if err == nil {
					if sock, found := cookieMap[inode]; found {
						if procName == "" {
							cmdline, _ := os.ReadFile(fmt.Sprintf("/proc/%d/comm", pid))
							procName = strings.TrimSpace(string(cmdline))
							if procName == "" {
								procName = fmt.Sprintf("pid-%d", pid)
							}
						}
						sock.Pid = int32(pid)
						sock.ProcessName = procName
					}
				}
			}
		}
	}
}

// ---------------------------------------------------------------------
// STAGE: Read-Only Diagnostics (Ping, Traceroute, RouteGet)
// ---------------------------------------------------------------------

var validTargetRegex = regexp.MustCompile(`^[a-zA-Z0-9\.\-\:]+$`)

func ValidateProbeTarget(target string) error {
	trimmed := strings.TrimSpace(target)
	if trimmed == "" {
		return fmt.Errorf("target cannot be empty")
	}
	if len(trimmed) > 255 {
		return fmt.Errorf("target exceeds 255 characters")
	}
	if !validTargetRegex.MatchString(trimmed) {
		return fmt.Errorf("target contains invalid characters: %q", target)
	}
	return nil
}

func (p *NetworkProber) RunDiagnosticProbe(ctx context.Context, req *madtomv1.DiagnosticProbeRequest, send func(*madtomv1.DiagnosticProbeOutput) error) error {
	if err := ValidateProbeTarget(req.Target); err != nil {
		return err
	}

	switch req.Type {
	case madtomv1.DiagnosticProbeType_PROBE_PING:
		return p.runPing(ctx, req, send)
	case madtomv1.DiagnosticProbeType_PROBE_TRACEROUTE:
		return p.runTraceroute(ctx, req, send)
	case madtomv1.DiagnosticProbeType_PROBE_ROUTE_GET:
		return p.runRouteGet(ctx, req, send)
	default:
		return fmt.Errorf("unsupported probe type: %v", req.Type)
	}
}

func (p *NetworkProber) runPing(ctx context.Context, req *madtomv1.DiagnosticProbeRequest, send func(*madtomv1.DiagnosticProbeOutput) error) error {
	count := req.PacketCount
	if count <= 0 || count > 10 {
		count = 4
	}

	timeoutSec := req.TimeoutSeconds
	if timeoutSec <= 0 || timeoutSec > 15 {
		timeoutSec = 5
	}

	args := []string{
		"-c", strconv.Itoa(int(count)),
		"-W", strconv.Itoa(int(timeoutSec)),
	}

	if req.SourceInterface != "" {
		if !validTargetRegex.MatchString(req.SourceInterface) {
			return fmt.Errorf("invalid interface name: %q", req.SourceInterface)
		}
		args = append(args, "-I", req.SourceInterface)
	}

	args = append(args, req.Target)

	cmd := exec.CommandContext(ctx, "ping", args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		return err
	}

	scanner := bufioReader(stdout.(*os.File))

	for {
		line, err := scanner.ReadString('\n')
		if len(line) > 0 {
			_ = send(&madtomv1.DiagnosticProbeOutput{
				NodeId:             req.NodeId,
				Type:               madtomv1.DiagnosticProbeType_PROBE_PING,
				RawLine:            line,
				TimestampUnixNano: time.Now().UnixNano(),
			})
		}
		if err != nil {
			break
		}
	}

	_ = cmd.Wait()
	_ = send(&madtomv1.DiagnosticProbeOutput{
		NodeId:             req.NodeId,
		Type:               madtomv1.DiagnosticProbeType_PROBE_PING,
		IsComplete:         true,
		TimestampUnixNano: time.Now().UnixNano(),
	})
	return nil
}

func (p *NetworkProber) runTraceroute(ctx context.Context, req *madtomv1.DiagnosticProbeRequest, send func(*madtomv1.DiagnosticProbeOutput) error) error {
	timeoutSec := req.TimeoutSeconds
	if timeoutSec <= 0 || timeoutSec > 10 {
		timeoutSec = 3
	}

	args := []string{
		"-m", "20",
		"-w", strconv.Itoa(int(timeoutSec)),
		"-n",
	}

	if req.SourceInterface != "" {
		if !validTargetRegex.MatchString(req.SourceInterface) {
			return fmt.Errorf("invalid interface name: %q", req.SourceInterface)
		}
		args = append(args, "-i", req.SourceInterface)
	}

	args = append(args, req.Target)

	cmd := exec.CommandContext(ctx, "traceroute", args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		return err
	}

	scanner := bufioReader(stdout.(*os.File))

	for {
		line, err := scanner.ReadString('\n')
		if len(line) > 0 {
			_ = send(&madtomv1.DiagnosticProbeOutput{
				NodeId:             req.NodeId,
				Type:               madtomv1.DiagnosticProbeType_PROBE_TRACEROUTE,
				RawLine:            line,
				TimestampUnixNano: time.Now().UnixNano(),
			})
		}
		if err != nil {
			break
		}
	}

	_ = cmd.Wait()
	_ = send(&madtomv1.DiagnosticProbeOutput{
		NodeId:             req.NodeId,
		Type:               madtomv1.DiagnosticProbeType_PROBE_TRACEROUTE,
		IsComplete:         true,
		TimestampUnixNano: time.Now().UnixNano(),
	})
	return nil
}

func (p *NetworkProber) runRouteGet(ctx context.Context, req *madtomv1.DiagnosticProbeRequest, send func(*madtomv1.DiagnosticProbeOutput) error) error {
	args := []string{"route", "get", req.Target}
	cmd := exec.CommandContext(ctx, "ip", args...)
	out, err := cmd.CombinedOutput()
	outputStr := string(out)
	if err != nil && len(outputStr) == 0 {
		outputStr = err.Error()
	}

	_ = send(&madtomv1.DiagnosticProbeOutput{
		NodeId:             req.NodeId,
		Type:               madtomv1.DiagnosticProbeType_PROBE_ROUTE_GET,
		RawLine:            outputStr,
		IsComplete:         true,
		TimestampUnixNano: time.Now().UnixNano(),
	})
	return nil
}
