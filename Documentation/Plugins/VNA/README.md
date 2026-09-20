# MADTOM: VNA (Visual Network Analyzer)

**MADTOM: VNA** is a visual network analysis module designed for real-time traffic inspection, latency topology mapping, and packet flow visualization across local and distributed network nodes.

---

## 1. Overview

- **Module ID**: `vna`
- **Display Name**: `VNA`
- **Icon**: `🌐`
- **Category**: `Network`
- **Order Weight**: `70`

VNA provides deep visual insights into live TCP/UDP connections, bandwidth saturation points, and distributed routing hops.

---

## 2. Planned Features

- **Interactive Flow Canvas**: Real-time packet flow visualization mapping ingress/egress endpoints and protocol distribution.
- **Capture Engine Integrations**: Pluggable backends supporting `libpcap`, Linux `eBPF` socket probes, and collector flow scrapers.
- **Latency Topology**: Correlates TWAMP Light network latency measurements with active network interfaces and routing paths.
- **Dynamic Tray Metrics**: Live interface throughput and active socket metrics exposed in the MADTOM Studio tray menu.

---

## 3. Project Structure

| Path | Description |
|---|---|
| `MADTOM_DOTNET/src/Plugins/VNA/MADTOM.Plugins.VNA/` | Core plugin library implementing `IPluginModule` |
| `MADTOM_DOTNET/src/Plugins/VNA/MADTOM.Plugins.VNA.App/` | Standalone runner application for dedicated network analysis |
| `MADTOM_DOTNET/MadTOM.Tests/Plugins/VNA/` | Automated unit and lifecycle verification tests |

