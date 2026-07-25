# Third-party notices

## LibreHardwareMonitorLib

- Package: `LibreHardwareMonitorLib` 0.9.6
- Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- License: Mozilla Public License 2.0 (with separately licensed components documented by that project)

SystemPulse enables the library's motherboard and controller readers for board-level temperature discovery and its NVIDIA/AMD GPU readers as a final core-voltage fallback. PawnIO remains SystemPulse's CPU telemetry backend. The library and its required runtime dependencies are bundled by the single-file publish.

## Intel PresentMon

- Project: PresentMon
- Release: 2.4.1, 64-bit console application
- Source and releases: https://github.com/GameTechDev/PresentMon
- License: MIT
- Bundled file: `Vendor/PresentMon/PresentMon.exe`
- SHA-256: `D74183E7AE630F72CD3690BE0373ECBFDC6CBB86578148AAB8FA2A7166068F34`

SystemPulse launches the unmodified console capture binary invisibly to consume Windows ETW presentation events. It requires no separate installation and is included by the single-file publish profile.

## Microsoft System.Management

- Package: `System.Management` 10.0.10
- Publisher: Microsoft
- Source: https://github.com/dotnet/runtime
- License: MIT

SystemPulse uses this Windows-only package to query the operating system's storage reliability counters and firmware-provided ACPI thermal zones. Visual Studio restores it from NuGet, and the included single-file publish profile bundles it into the finished EXE.

## NVIDIA display-driver telemetry

- Runtime components: `nvidia-smi.exe` and `nvapi64.dll`
- Publisher: NVIDIA Corporation
- Documentation: https://developer.nvidia.com/nvapi

SystemPulse uses the copies installed by the NVIDIA display driver to read temperature, utilization, whole-board power, and supported read-only voltage domains. These NVIDIA files are not redistributed by SystemPulse.

## AMD display-driver telemetry

- Runtime component: `atiadlxx.dll` (AMD Display Library)
- Publisher: Advanced Micro Devices, Inc.
- Documentation and SDK source: https://github.com/GPUOpen-LibrariesAndSDKs/display-library

SystemPulse uses the ADL copy installed by the AMD Radeon display driver to read supported temperature, utilization, voltage, and ASIC-power metrics. AMD driver files are not redistributed by SystemPulse.

## PawnIO official installer

- Project: PawnIO
- Website: https://pawnio.eu/
- Installer release: 2.2.0
- Release page: https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- File: `Vendor/PawnIO/Installer/PawnIO_setup.exe`
- SHA-256: `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`

The official PawnIO.Modules integration guidance describes the official binary edition as proprietary and explicitly permits redistribution of its installer. SystemPulse launches this unmodified installer; it does not redistribute `PawnIO.sys` separately.

## PawnIO.Modules

- Project: PawnIO.Modules
- Copyright: namazso and contributors
- Release: 0.2.9
- Source: https://github.com/namazso/PawnIO.Modules/tree/0.2.9
- Signed binaries: https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.9
- License: GNU Lesser General Public License 2.1 or later

SystemPulse redistributes unmodified signed copies of `IntelMSR.bin` and `AMDFamily17.bin`. Their corresponding module sources and shared include sources are provided in `Vendor/PawnIO/ModuleSource`. The full license is in `Vendor/PawnIO/Modules/COPYING.LGPL-2.1`.

SystemPulse communicates with PawnIO only through the Windows device-control interface and does not link to the PawnIO driver or PawnIOLib.
