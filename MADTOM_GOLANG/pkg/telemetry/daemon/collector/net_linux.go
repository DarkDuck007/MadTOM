package collector

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

type NetworkCollector struct{}

func NewNetworkCollector() *NetworkCollector {
	return &NetworkCollector{}
}

func (n *NetworkCollector) Collect() *madtomv1.NetworkMetrics {
	file, err := os.Open("/proc/net/dev")
	if err != nil {
		return &madtomv1.NetworkMetrics{}
	}
	defer file.Close()

	metrics := &madtomv1.NetworkMetrics{}
	scanner := bufio.NewScanner(file)

	// Skip two header lines
	if scanner.Scan() {
		_ = scanner.Text()
	}
	if scanner.Scan() {
		_ = scanner.Text()
	}

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		parts := strings.SplitN(line, ":", 2)
		if len(parts) != 2 {
			continue
		}
		ifaceName := strings.TrimSpace(parts[0])
		fields := strings.Fields(parts[1])
		if len(fields) < 16 {
			continue
		}

		// Fields format:
		// RX: bytes packets errs drop fifo frame compressed multicast
		// TX: bytes packets errs drop fifo colls carrier compressed
		rxBytes, _ := strconv.ParseUint(fields[0], 10, 64)
		rxPackets, _ := strconv.ParseUint(fields[1], 10, 64)
		rxErrors, _ := strconv.ParseUint(fields[2], 10, 64)
		rxDropped, _ := strconv.ParseUint(fields[3], 10, 64)

		txBytes, _ := strconv.ParseUint(fields[8], 10, 64)
		txPackets, _ := strconv.ParseUint(fields[9], 10, 64)
		txErrors, _ := strconv.ParseUint(fields[10], 10, 64)
		txDropped, _ := strconv.ParseUint(fields[11], 10, 64)

		nic := &madtomv1.NicMetric{
			Name:      ifaceName,
			RxBytes:   rxBytes,
			TxBytes:   txBytes,
			RxPackets: rxPackets,
			TxPackets: txPackets,
			RxDropped: rxDropped,
			TxDropped: txDropped,
			RxErrors:  rxErrors,
			TxErrors:  txErrors,
		}

		// Link state & speed via sysfs
		n.readSysfsNet(ifaceName, nic)
		metrics.Interfaces = append(metrics.Interfaces, nic)
	}

	return metrics
}

func (n *NetworkCollector) readSysfsNet(ifaceName string, nic *madtomv1.NicMetric) {
	// Operstate / Carrier
	stateData, err := os.ReadFile(fmt.Sprintf("/sys/class/net/%s/operstate", ifaceName))
	if err == nil {
		nic.CarrierUp = strings.TrimSpace(string(stateData)) == "up"
	}

	// MTU
	mtuData, err := os.ReadFile(fmt.Sprintf("/sys/class/net/%s/mtu", ifaceName))
	if err == nil {
		mtuVal, err := strconv.ParseUint(strings.TrimSpace(string(mtuData)), 10, 32)
		if err == nil {
			nic.Mtu = uint32(mtuVal)
		}
	}

	// Speed (Mbps)
	speedData, err := os.ReadFile(fmt.Sprintf("/sys/class/net/%s/speed", ifaceName))
	if err == nil {
		speedVal, err := strconv.ParseInt(strings.TrimSpace(string(speedData)), 10, 32)
		if err == nil && speedVal > 0 {
			nic.LinkSpeedMbps = uint32(speedVal)
		}
	}
}
