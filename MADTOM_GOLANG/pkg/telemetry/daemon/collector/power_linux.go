package collector

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

type PowerCollector struct{}

func NewPowerCollector() *PowerCollector {
	return &PowerCollector{}
}

func (p *PowerCollector) Collect() *madtomv1.PowerMetrics {
	metrics := &madtomv1.PowerMetrics{
		AcPlugged:      true, // Default if no power supply subsystem
		BatteryPresent: false,
		BatteryState:   "Unknown",
	}

	supplies, err := filepath.Glob("/sys/class/power_supply/*")
	if err != nil || len(supplies) == 0 {
		return metrics
	}

	for _, supply := range supplies {
		typeBytes, err := os.ReadFile(filepath.Join(supply, "type"))
		if err != nil {
			continue
		}
		supplyType := strings.TrimSpace(string(typeBytes))

		if supplyType == "Mains" {
			onlineBytes, err := os.ReadFile(filepath.Join(supply, "online"))
			if err == nil {
				metrics.AcPlugged = strings.TrimSpace(string(onlineBytes)) == "1"
			}
		} else if supplyType == "Battery" {
			metrics.BatteryPresent = true

			// Status
			statusBytes, err := os.ReadFile(filepath.Join(supply, "status"))
			if err == nil {
				metrics.BatteryState = strings.TrimSpace(string(statusBytes))
			}

			// Percentage capacity
			capBytes, err := os.ReadFile(filepath.Join(supply, "capacity"))
			if err == nil {
				capVal, err := strconv.ParseFloat(strings.TrimSpace(string(capBytes)), 64)
				if err == nil {
					metrics.BatteryPct = capVal
				}
			}

			// Energy Wh and Design Wh (micro-watt-hours -> divide by 1,000,000)
			var energyNowUwh, energyFullUwh, energyDesignUwh float64
			if data, err := os.ReadFile(filepath.Join(supply, "energy_now")); err == nil {
				energyNowUwh, _ = strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
			}
			if data, err := os.ReadFile(filepath.Join(supply, "energy_full")); err == nil {
				energyFullUwh, _ = strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
			}
			if data, err := os.ReadFile(filepath.Join(supply, "energy_full_design")); err == nil {
				energyDesignUwh, _ = strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
			}

			// If system uses charge instead of energy (uAh * uV)
			if energyNowUwh == 0 {
				var voltageUv float64
				if data, err := os.ReadFile(filepath.Join(supply, "voltage_now")); err == nil {
					voltageUv, _ = strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
				}
				if voltageUv > 0 {
					if data, err := os.ReadFile(filepath.Join(supply, "charge_now")); err == nil {
						chargeUah, _ := strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
						energyNowUwh = (chargeUah * voltageUv) / 1000000.0
					}
					if data, err := os.ReadFile(filepath.Join(supply, "charge_full")); err == nil {
						chargeUah, _ := strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
						energyFullUwh = (chargeUah * voltageUv) / 1000000.0
					}
					if data, err := os.ReadFile(filepath.Join(supply, "charge_full_design")); err == nil {
						chargeUah, _ := strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
						energyDesignUwh = (chargeUah * voltageUv) / 1000000.0
					}
				}
			}

			metrics.CurrentEnergyWh = energyNowUwh / 1000000.0
			metrics.DesignCapacityWh = energyDesignUwh / 1000000.0

			if energyDesignUwh > 0 && energyFullUwh > 0 {
				metrics.HealthPct = clampPct((energyFullUwh / energyDesignUwh) * 100.0)
			}

			// Current power rate (Watts)
			if data, err := os.ReadFile(filepath.Join(supply, "power_now")); err == nil {
				powerUwatts, _ := strconv.ParseFloat(strings.TrimSpace(string(data)), 64)
				metrics.RateWatts = powerUwatts / 1000000.0
			}

			// Cycle count
			if data, err := os.ReadFile(filepath.Join(supply, "cycle_count")); err == nil {
				cycles, _ := strconv.ParseUint(strings.TrimSpace(string(data)), 10, 32)
				metrics.Cycles = uint32(cycles)
			}
		}
	}

	return metrics
}
