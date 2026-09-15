using System;
using MADTOM.Plugins.Telemetry.Proto.V1;

namespace MadTOM.Common;

public static class TelemetryOptInResolver
{
    public static TelemetryOptInMode ResolveMode(TelemetryOptInMode mode, bool legacyBool)
    {
        if (mode != TelemetryOptInMode.OptInOff)
            return mode;
        return legacyBool ? TelemetryOptInMode.OptInMonitorAndStore : TelemetryOptInMode.OptInOff;
    }

    public static TelemetryOptInMode GetMetricOptInMode(NodeConfig? cfg, string metric)
    {
        if (cfg == null)
            return TelemetryOptInMode.OptInMonitorAndStore;

        string m = metric.Trim().ToLowerInvariant();

        // 1. Overall CPU
        if (m is "cpu.total" or "cpu.user" or "cpu.system" or "cpu.iowait" or "cpu.idle")
        {
            return ResolveMode(cfg.CpuOverallMode, cfg.CollectCpuOverall);
        }

        // 2. Per-Core CPU (cpu.core.<N>)
        if (m.StartsWith("cpu.core."))
        {
            string coreIdx = m.Substring("cpu.core.".Length);
            if (cfg.CoreModes.TryGetValue(coreIdx, out var coreMode))
                return coreMode;
            return ResolveMode(cfg.CpuPerCoreMode, cfg.CollectCpuPerCore);
        }

        // 3. Basic Memory
        if (m is "memory.total" or "memory.available" or "memory.used" or "memory.free" or "memory.buffers" or "memory.cached")
        {
            return ResolveMode(cfg.MemoryBasicMode, cfg.CollectMemoryBasic);
        }

        // 4. Overall Swap
        if (m is "memory.swap_total" or "memory.swap_used" or "memory.swap_free")
        {
            return ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram);
        }

        // 5. Partition Swap (swap.<dev>.*)
        if (m.StartsWith("swap."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string devName = parts[1];
                if (cfg.SwapDeviceModes.TryGetValue(devName, out var swapMode))
                    return swapMode;
            }
            return ResolveMode(cfg.MemorySwapMode, cfg.CollectMemorySwapZram);
        }

        // 6. ZRAM (zram.<dev>.* or memory.zram_ratio)
        if (m.StartsWith("zram."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string devName = parts[1];
                if (cfg.ZramDeviceModes.TryGetValue(devName, out var zramMode))
                    return zramMode;
            }
            return ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram);
        }
        if (m == "memory.zram_ratio")
        {
            return ResolveMode(cfg.ZramMode, cfg.CollectMemorySwapZram);
        }

        // 7. Network NIC (nic.<iface>.*)
        if (m.StartsWith("nic."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string iface = parts[1];
                if (cfg.NicModes.TryGetValue(iface, out var nicMode))
                    return nicMode;
            }
            return ResolveMode(cfg.NetworkMode, cfg.CollectNetworkInterfaces);
        }

        // 8. Disk I/O (disk.io.<dev>.* or disk.io.*)
        if (m.StartsWith("disk.io."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 4)
            {
                string dev = parts[2];
                if (cfg.DiskDeviceModes.TryGetValue(dev, out var diskMode))
                    return diskMode;
            }
            return ResolveMode(cfg.DiskIoMode, true);
        }

        // 9. Power & Battery (power.*)
        if (m.StartsWith("power."))
        {
            if (cfg.PowerMetricModes.TryGetValue(m, out var pwrMode))
                return pwrMode;
            return ResolveMode(cfg.PowerMode, cfg.CollectPowerBattery);
        }

        // 10. TWAMP Latency (twamp.*)
        if (m.StartsWith("twamp."))
        {
            return ResolveMode(cfg.TwampMode, !string.IsNullOrEmpty(cfg.TwampTarget));
        }

        // 11. Processes (proc.cpu.*)
        if (m.StartsWith("proc.cpu."))
        {
            return cfg.ProcessMode switch
            {
                ProcessTelemetryMode.ProcessModeProbedAndStored => TelemetryOptInMode.OptInMonitorAndStore,
                ProcessTelemetryMode.ProcessModeLiveOnly => TelemetryOptInMode.OptInMonitorOnly,
                _ => TelemetryOptInMode.OptInOff,
            };
        }

        return TelemetryOptInMode.OptInMonitorAndStore;
    }

    public static void SetMetricOptInMode(NodeConfig cfg, string metric, TelemetryOptInMode mode)
    {
        if (cfg == null) return;
        string m = metric.Trim().ToLowerInvariant();

        if (m is "cpu.total" or "cpu.user" or "cpu.system" or "cpu.iowait" or "cpu.idle")
        {
            cfg.CpuOverallMode = mode;
            cfg.CollectCpuOverall = mode != TelemetryOptInMode.OptInOff;
            return;
        }

        if (m.StartsWith("cpu.core."))
        {
            string coreIdx = m.Substring("cpu.core.".Length);
            cfg.CoreModes[coreIdx] = mode;
            if (mode != TelemetryOptInMode.OptInOff)
            {
                cfg.CollectCpuPerCore = true;
                if (cfg.CpuPerCoreMode == TelemetryOptInMode.OptInOff)
                    cfg.CpuPerCoreMode = mode;
            }
            return;
        }

        if (m is "memory.total" or "memory.available" or "memory.used" or "memory.free" or "memory.buffers" or "memory.cached")
        {
            cfg.MemoryBasicMode = mode;
            cfg.CollectMemoryBasic = mode != TelemetryOptInMode.OptInOff;
            return;
        }

        if (m is "memory.swap_total" or "memory.swap_used" or "memory.swap_free")
        {
            cfg.MemorySwapMode = mode;
            cfg.CollectMemorySwapZram = mode != TelemetryOptInMode.OptInOff || cfg.ZramMode != TelemetryOptInMode.OptInOff;
            return;
        }

        if (m.StartsWith("swap."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string dev = parts[1];
                cfg.SwapDeviceModes[dev] = mode;
            }
            if (mode != TelemetryOptInMode.OptInOff)
            {
                cfg.CollectMemorySwapZram = true;
                if (cfg.MemorySwapMode == TelemetryOptInMode.OptInOff)
                    cfg.MemorySwapMode = mode;
            }
            return;
        }

        if (m.StartsWith("zram."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string dev = parts[1];
                cfg.ZramDeviceModes[dev] = mode;
            }
            if (mode != TelemetryOptInMode.OptInOff)
            {
                cfg.CollectMemorySwapZram = true;
                if (cfg.ZramMode == TelemetryOptInMode.OptInOff)
                    cfg.ZramMode = mode;
            }
            return;
        }

        if (m.StartsWith("nic."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 2)
            {
                string iface = parts[1];
                cfg.NicModes[iface] = mode;
            }
            if (mode != TelemetryOptInMode.OptInOff)
            {
                cfg.CollectNetworkInterfaces = true;
                if (cfg.NetworkMode == TelemetryOptInMode.OptInOff)
                    cfg.NetworkMode = mode;
            }
            return;
        }

        if (m.StartsWith("disk.io."))
        {
            var parts = m.Split('.');
            if (parts.Length >= 4)
            {
                string dev = parts[2];
                cfg.DiskDeviceModes[dev] = mode;
            }
            if (mode != TelemetryOptInMode.OptInOff)
            {
                if (cfg.DiskIoMode == TelemetryOptInMode.OptInOff)
                    cfg.DiskIoMode = mode;
            }
            return;
        }

        if (m.StartsWith("power."))
        {
            cfg.PowerMetricModes[m] = mode;
            if (mode != TelemetryOptInMode.OptInOff)
            {
                cfg.CollectPowerBattery = true;
                if (cfg.PowerMode == TelemetryOptInMode.OptInOff)
                    cfg.PowerMode = mode;
            }
            return;
        }

        if (m.StartsWith("twamp."))
        {
            cfg.TwampMode = mode;
            return;
        }

        if (m.StartsWith("proc.cpu."))
        {
            cfg.ProcessMode = mode switch
            {
                TelemetryOptInMode.OptInMonitorAndStore => ProcessTelemetryMode.ProcessModeProbedAndStored,
                TelemetryOptInMode.OptInMonitorOnly => ProcessTelemetryMode.ProcessModeLiveOnly,
                _ => ProcessTelemetryMode.ProcessModeDisabled,
            };
        }
    }
}

