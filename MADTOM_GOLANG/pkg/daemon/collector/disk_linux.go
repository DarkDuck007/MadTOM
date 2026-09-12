package collector

import (
	"bufio"
	"os"
	"regexp"
	"strconv"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

var wholeDiskPattern = regexp.MustCompile(`^(sd[a-z]+|nvme[0-9]+n[0-9]+|vd[a-z]+|xvd[a-z]+|mmcblk[0-9]+|hd[a-z]+)$`)

type DiskCollector struct{}

func NewDiskCollector() *DiskCollector {
	return &DiskCollector{}
}

func (d *DiskCollector) Collect() *madtomv1.DiskIoMetrics {
	file, err := os.Open("/proc/diskstats")
	if err != nil {
		return &madtomv1.DiskIoMetrics{}
	}
	defer file.Close()

	res := &madtomv1.DiskIoMetrics{}
	scanner := bufio.NewScanner(file)

	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) < 14 {
			continue
		}

		devName := fields[2]
		// Skip virtual loop and ram block devices
		if strings.HasPrefix(devName, "loop") || strings.HasPrefix(devName, "ram") {
			continue
		}

		readOps, _ := strconv.ParseUint(fields[3], 10, 64)
		readSectors, _ := strconv.ParseUint(fields[5], 10, 64)
		readBytes := readSectors * 512

		writeOps, _ := strconv.ParseUint(fields[7], 10, 64)
		writeSectors, _ := strconv.ParseUint(fields[9], 10, 64)
		writeBytes := writeSectors * 512

		deviceMetric := &madtomv1.DiskIoDevice{
			Name:       devName,
			ReadBytes:  readBytes,
			WriteBytes: writeBytes,
			ReadOps:    readOps,
			WriteOps:   writeOps,
		}
		res.Devices = append(res.Devices, deviceMetric)

		// Accumulate totals for whole disks (avoid double counting partitions)
		if wholeDiskPattern.MatchString(devName) {
			res.ReadBytes += readBytes
			res.WriteBytes += writeBytes
			res.ReadOps += readOps
			res.WriteOps += writeOps
		}
	}

	// Fallback: If no whole-disk matched, sum all collected devices
	if res.ReadBytes == 0 && res.WriteBytes == 0 && len(res.Devices) > 0 {
		for _, dev := range res.Devices {
			res.ReadBytes += dev.ReadBytes
			res.WriteBytes += dev.WriteBytes
			res.ReadOps += dev.ReadOps
			res.WriteOps += dev.WriteOps
		}
	}

	return res
}
