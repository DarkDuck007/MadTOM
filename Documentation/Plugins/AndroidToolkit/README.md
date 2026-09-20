# MADTOM: Android Toolkit Plugin

The **Android Toolkit** (`MADTOM.Plugins.AndroidToolkit`) is a modular tool for managing connected Android devices via ADB, scheduling automated backups, exporting app storage, syncing SMS/MMS, and interacting with fastboot/recovery interfaces.

---

## Features

- **Device Discovery**: Real-time USB and Wi-Fi ADB device scanning.
- **Backup Suite**: Single-click full device backups and per-app `.tar` archiving.
- **Media Dump**: High-speed DCIM and storage synchronization.
- **System Tray**: Live device connection status in the MADTOM Console tray.

## Project Structure

- `MADTOM.Plugins.AndroidToolkit`: Primary class library implementing `IPluginModule`.
- `MADTOM.Plugins.AndroidToolkit.App`: Standalone runner application for independent development.
- `MADTOM.Plugins.AndroidToolkit.Tests`: Targeted unit and lifecycle test suite.

