# PreySense

PreySense is a lightweight Windows control suite for Acer laptops (Predator and Nitro series). It replaces heavy OEM software with a faster user interface, deeper hardware control, calibrated custom fan curves, and a bundled kernel driver for power tuning.

Verified on **Acer Nitro 5 (`AN515-58`)** and **Acer Predator Helios Neo 16S (`PHN16S-71`)**.

<p align="center">
  <img src="pics/Dashboard-v2.png" alt="PreySense dashboard" width="440">
</p>

## Features

### Performance Modes
Quiet, Eco, Balanced, Performance, and Turbo with one-click switching. Separate profiles can be configured for AC power and battery. Chassis mode buttons and keyboard shortcuts are supported.

### Liquid Glass Interface
Native Windows 11 DWM Acrylic backdrop integration with a dark obsidian glass aesthetic and specular highlights. Can be toggled on or off instantly in Settings.

### Hardware Tuning
Per-mode CPU and GPU power management through the bundled PreySenseDriver:
- CPU PL1, PL2, Tau time window, and CPU Boost mode control.
- GPU power offsets, Dynamic Boost, and platform power delta.
- Configurable global PROCHOT offset.
- Hardware calibration and reset to captured factory defaults.

### Advanced Fan Curves
- Multi-stage custom fan curves with physical RPM calibration.
- Smooth step duty transitions to prevent abrupt fan noise spikes.
- Independent GPU fan control with zero-RPM idle capability when cold.
- Background curve follower that operates while minimized to system tray.

### GPU and Display
- GPU modes: Auto, iGPU Only, Hybrid, Advanced Optimus, and dGPU Only.
- Display refresh rate control with automatic downscaling on battery power.

### Lighting
- Keyboard multi-zone RGB lighting with static colors and effects.
- Cover logo lighting control on supported models.

### Telemetry and Overlay
- Compact hardware telemetry overlay with selectable CPU and GPU metrics.
- Low-overhead polling with optimized memory footprint.

### System Settings
- Configurable battery charge limit (80% or 100%).
- Disable tray icon toggle.
- Windows logon startup task management.
- Multi-language support: English, Simplified Chinese, Hindi, Spanish, French.

## Requirements

- Windows 10 or 11 x64 (Windows 11 22H2 or newer recommended for Acrylic Glass).
- Compatible Acer laptop (Nitro or Predator series).
- .NET 10 Desktop Runtime x64.
- Administrator privileges.

## Building

Requires the .NET 10 SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .github\publish.ps1
```

Compiled single-file executable is generated at: `dist\PreySense.exe`.

## License

See [LICENSE](LICENSE).
