# MADTOM: Connection Toolkit

**MADTOM: Connection Toolkit** is a centralized connection orchestrator for managing SSH jump tunnels, WireGuard/VPN links, serial device bridges, and port forward cascades.

---

## 1. Overview

- **Module ID**: `connection-toolkit`
- **Display Name**: `Connection Toolkit`
- **Icon**: `🔌`
- **Category**: `Connectivity`
- **Order Weight**: `80`

Connection Toolkit simplifies multi-host bastion hops, remote service forwarding, and secure gateway management directly from MADTOM Studio.

---

## 2. Planned Features

- **Multi-Protocol Tunneling**: SSH dynamic port forwarding (SOCKS5), local/remote port tunnels, and WireGuard mesh configurations.
- **Hardware Serial Bridges**: Direct UART/serial-to-network bridge mappings for embedded debugging alongside MADTOM ESC.
- **Credential Vault**: Encrypted session credentials and SSH key agent integration.
- **Dynamic Tray Status**: Active tunnel indicators, throughput health, and quick disconnect actions in the MADTOM Studio system tray.

---

## 3. Project Structure

| Path | Description |
|---|---|
| `MADTOM_DOTNET/src/Plugins/ConnectionToolkit/MADTOM.Plugins.ConnectionToolkit/` | Core plugin library implementing `IPluginModule` |
| `MADTOM_DOTNET/src/Plugins/ConnectionToolkit/MADTOM.Plugins.ConnectionToolkit.App/` | Standalone runner application for dedicated connection management |
| `MADTOM_DOTNET/MadTOM.Tests/Plugins/ConnectionToolkit/` | Automated unit and lifecycle verification tests |

