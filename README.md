# PreySense

PreySense is a high-performance, lightweight hardware management and telemetry suite designed specifically for Acer Predator and Nitro series gaming laptops. It completely replaces heavy OEM background software with a modern, responsive user interface, deeper low-level hardware control, calibrated custom fan curves, zero-dependency deployment, and universal compatibility across both Intel Core and AMD Ryzen architectures.

Verified across Acer Predator Helios (PH16, PH18, PHN16, PHN16S) and Acer Nitro (Nitro 5 AN515, Nitro 16 AN16, Nitro 17 AN17, Nitro V 15 ANV15, Nitro V 16 ANV16-41).

<p align="center">
  <img src="pics/Dashboard-v2.png" alt="PreySense Dashboard" width="450">
</p>

---

## Key Features

### Universal Hardware Architecture (AMD Ryzen & Intel Core)
- Full support for AMD Ryzen processors (Zen 3, Zen 4, Zen 5 including Ryzen 7 8845HS, 7840HS, 7735HS, 5600H) and Intel Core (10th to 14th Gen, Core Ultra).
- Native user-mode CPU telemetry engine powered by Windows Kernel Power Information (`CallNtPowerInformation`), delivering real-time per-core clock tracking without kernel driver dependency.
- Direct Embedded Controller (EC) sensor readings for CPU and GPU package temperatures, chassis intake, and exhaust fan RPMs.
- **PROCHOT Offset (Intel Core Thermal Floor)**: Fine-grained temperature target adjustment via MSR 0x1FC (`IA32_TEMPERATURE_TARGET`). Automatically detected on unlocked Intel processors and gracefully gated on AMD systems.

### Intelligent Fan Curve Control, Calibration & Silent Idle
- **Sub-2000 RPM & Silent Idle Support**: Lowers fan operating boundaries down to 1800 RPM and supports 0 RPM Silent Idle on compatible Acer EC firmware, breaking free from fixed 2400+ RPM manufacturer minimums.
- **Bi-Directional Fan Calibration Engine**: Automated hardware probing (`FanCalibrationService`) that maps physical RPM response curves across 0% to 100% duty cycles for exact acoustic prediction.
- **Monotone Cubic Spline Interpolation**: Advanced mathematical spline model (`MonotoneCubicInterpolator`) ensuring smooth, oscillation-free transitions between curve control points from 30 deg C to 100 deg C.
- **Dynamic Slew-Rate Limiting & PI Anti-Hunt Tracking**: Eliminates rapid fan revving and acoustic pulsing during transient thermal spikes.
- **Automatic Acer EC Latch Release (`SetFanBehavior(0)`)**: Cleanly resets hardware override states when transitioning between Manual Max, Custom curves, and Auto modes.
- **Background Curve Follower**: Maintains custom thermal and acoustic profiles even when minimized to the Windows system tray.

### Advanced GPU Architecture & Dynamic Display Switching
<p align="center">
  <img src="pics/Power-Limits.png" alt="PreySense GPU and Power Limit Tuning" width="450">
</p>
- **Full Display MUX Mode Suite**:
  - **Auto (Dynamic Display Switch / DDS)**: Intelligently switches the display between integrated and discrete GPUs based on active 3D workloads without rebooting.
  - **Hybrid (Optimus)**: Desktop and everyday applications run on the power-efficient integrated GPU, waking the discrete NVIDIA GPU only for heavy 3D rendering.
  - **Advanced Optimus**: Direct display connection to the discrete NVIDIA GPU on the fly without system restart for ultra-low latency and peak frame rates.
  - **dGPU Only**: Hardware BIOS MUX switch exclusively directing the internal display to the NVIDIA discrete GPU (requires system restart).
  - **iGPU Only**: Completely disables the discrete GPU in Windows to maximize battery life on the go.
- **Universal GPU Frequency Ceiling & Wattage Lock**: Locks maximum GPU boost clocks via NVIDIA NVML APIs to cap peak power consumption on laptops lacking OEM platform power registers.
- **Platform Power Limit Tuning (ERM4)**: Direct PL1/PL2 and cross-load power adjustments on supported Predator laptops.

### Smart Boost Dynamic Throttle Prevention
- Real-time thermal surveillance engine monitoring CPU core temperatures at 1 Hz.
- Dynamically steps down aggressive turbo boost modes (`PERFBOOSTMODE`) when core temperatures approach critical thresholds, averting severe thermal throttling spikes.
- Restores maximum boost performance smoothly once operating temperatures stabilize within a safe hysteresis window.
- Consolidated directly into the Tuning view for streamlined power and cooling management.

### Platform Power, Battery & System Maintenance
<p align="center">
  <img src="pics/Platform-Settings.png" alt="PreySense Platform Settings" width="450">
</p>
- **Acer OEM Services Debloater**: Stops and disables unnecessary Acer background services (`AcerCareCenter`, `PredatorSenseService`, `AcerAgent`) to reclaim system memory and CPU cycles.
- **Battery Health Limiter**: Hardware-level 80% charge limit enforcement on AC power to preserve lithium-ion battery longevity.
- **Auto Display Refresh Rate**: Dynamically drops internal panel refresh rate to 60 Hz on battery power and restores high refresh rates (144 Hz / 165 Hz / 240 Hz) on AC.
- **USB Power-Off Charging**: Configure power delivery to external devices via USB ports while the laptop is powered down.

### Liquid Glass Aesthetic & Native Windows 11 Integration
- **Live Windows 11 Acrylic Blur**: Real-time DWM Acrylic blur backdrop powered by `SetWindowCompositionAttribute` (`WCA_ACCENT_POLICY`), matching the soft, luminous translucency of the Windows 11 Start Menu.
- **Frosted Translucent Glass UI**: Delicate frosted glass buttons and panels with subtle specular highlights, allowing radiant background diffusion without dark muddying.
- **Native DWM Drop Shadow & Rounded Corners**: Borderless window styling with native Windows 11 corner rounding and drop shadows without native title bar button artifacts.
- **Native Window Transitions**: Preserves smooth Windows open, minimize, and restore animations.
- **Dark & Light Obsidian Themes**: High-contrast typography and color schemes tailored for both dark and light desktop environments.
- **JSON Configuration Backup**: One-click import, export, and verification of user profiles and fan curves.

### Multi-Language Localization & Interactive Tooltips
- Comprehensive multi-language support:
  - English (`en-US`)
  - Turkish (`tr-TR`)
  - Simplified Chinese (`zh-CN`)
  - Hindi (`hi-IN`)
  - Spanish (`es-ES`)
  - French (`fr-FR`)
- **Interactive Hover Tooltips**: Informative tooltips across all buttons, dropdowns, sliders, and toggles explaining the technical function and impact of every setting.

### In-Game Overlay & Diagnostics
<p align="center">
  <img src="pics/Overlay.png" alt="PreySense In-Game Overlay" width="450">
</p>
<p align="center">
  <img src="pics/Metrics.png" alt="PreySense Telemetry Metrics" width="450">
</p>
- Minimal-overhead DirectX and DWM HUD overlay.
- Real-time monitoring of CPU/GPU temperatures, clocks, fan speeds, power draw, and VRAM utilization.
- Expanded 576px wide configuration view with responsive 4-column layout for clean slot selection.

### RGB Keyboard & Lighting Control
<p align="center">
  <img src="pics/Lighting.png" alt="PreySense RGB Keyboard Lighting" width="450">
</p>
- Multi-zone RGB keyboard customization with static colors, breathing, wave, and neon effects.
- Lid logo and chassis light bar controls on supported Predator models.

---

## Architecture

PreySense is built on .NET 10 and C# 13, structured into clean, decoupled modules:

- **PreySense.Core**: Hardware abstraction layer, Acer WMI EC communications, NVML/NVAPI GPU controls, native Windows power telemetry, Smart Boost engine, bi-directional fan calibration, and monotone cubic mathematical solvers.
- **PreySense.Frontend**: Modern WPF interface featuring CommunityToolkit MVVM, real-time Acrylic/Mica backdrops, reactive data bindings, full multi-language localization, and interactive tooltips.
- **PreySense.Overlay**: Direct3D/DWM hardware overlay renderer for full-screen and borderless games.
- **PreySense.Tests**: Comprehensive unit test suite with 542 automated tests verifying mathematical solvers, config serialization, WMI schemas, localization dictionaries, and hardware safety bounds.

---

## Requirements

- Windows 10 (21H2+) or Windows 11 (22H2+ recommended for Acrylic effects) 64-bit.
- Acer Predator or Acer Nitro laptop.
- NVIDIA GeForce GTX / RTX laptop GPU (for GPU features).
- Administrator privileges (required for WMI and EC hardware access).
- No external runtime installations required (packaged as a self-contained single-file executable).

---

## Building from Source

To compile PreySense locally, install the .NET 10 SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .github\publish.ps1
```

The self-contained, single-file binary will be generated at:
```
publish\PreySense.exe
```

---

## Running Automated Tests

Execute the full automated test suite:

```powershell
dotnet test PreySense.Tests\PreySense.Tests.csproj
```

---

## Upstream Attribution and Credits

PreySense originated as a community fork of reverse-engineered PredatorSense tooling. Over 95% of the codebase has since been redesigned and rewritten from the ground up to establish an enterprise-grade .NET 10 modular architecture.

We gratefully acknowledge:
- The Acer Community and hardware reverse-engineering contributors for documenting Acer WMI and EC registers.
- The open-source hardware monitoring ecosystem (LibreHardwareMonitor, NVAPIWrapper) for hardware documentation and low-level protocol research.

---

## License

PreySense is licensed under the MIT License. See [LICENSE](LICENSE) for details.
