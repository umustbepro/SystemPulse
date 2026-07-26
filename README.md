# SystemPulse

SystemPulse is a Windows 11 WPF hardware dashboard targeting .NET 10. It uses PawnIO for privileged CPU telemetry and LibreHardwareMonitor for broad motherboard, chipset, Super I/O, and embedded-controller temperature support.

Current release: **v0.7.2**

## What is included

- Responsive layouts across every page, with wrapping hardware names and values that use the available window width instead of requiring hover tooltips
- A themed storage selector covering every physical disk detected by Windows
- Storage temperature, health, media type, bus type, and capacity where the device exposes them
- Detected motherboard manufacturer/model plus temperature from LibreHardwareMonitor 0.9.6, with a firmware ACPI fallback
- A separate Sensor details page
- A separate Performance page with CPU/GPU load graphs, real ETW frame pacing, and per-drive activity
- A selectable frame-application dropdown with separate FPS/frame-time history for each presenting process
- Direct PawnIO device communication; `PawnIOLib.dll` is not required
- Intel package and per-logical-processor temperature reads using `IA32_PACKAGE_THERM_STATUS`, `IA32_THERM_STATUS`, and per-processor `IA32_TEMPERATURE_TARGET`
- AMD Family 17h through 1Ah package temperature reads using the SMN thermal register
- Intel/AMD CPU voltage where the processor exposes a valid voltage/VID field, plus package power calculated from hardware energy-counter deltas
- Processor-group affinity support for systems with more than 64 logical processors
- NVIDIA temperature, utilization, and board power from the installed NVIDIA display driver, with NVAPI, `nvidia-smi`, and LibreHardwareMonitor voltage fallbacks
- AMD Radeon temperature, utilization, voltage, and supported ASIC power from the installed AMD display driver, with a LibreHardwareMonitor voltage fallback
- Native Windows CPU-load and physical-memory utilization metrics
- Per-physical-disk active time plus current read/write throughput
- Bundled Intel PresentMon 2.4.1 console capture for accurate active-application frame time and FPS; no separate installation or visible console window
- A bundled, official PawnIO installer that can be launched from Sensor details
- Dark/light styling, custom vector window buttons, a matching embedded EXE/window icon, and a 1380 × 900 default window
- A live system-health card that reports heavy CPU/GPU/drive activity and hot CPU/GPU temperatures
- A themed, in-app changelog with plain-language release notes
- A fixed-height Storage Cleanup workspace with hidden auto-follow logs and one Keep/Delete decision per detected parent folder
- Configurable CPU, GPU, storage-health, and storage-temperature alerts with notification-area warnings and cooldown protection
- Persistent 10-second telemetry history with configurable retention, recent-sample review, and CSV export
- A live Processes page showing CPU, working memory, and per-process disk throughput, with filtering and click-again ascending/descending column sorting
- A live Network page showing every adapter, address, link speed, throughput, and byte totals
- Expanded physical-drive health with estimated remaining life, wear, power-on hours, maximum temperature, and error counters where Windows exposes them
- A dedicated Storage page beneath Network with one clean health card per drive, including serial, firmware, operational state, capacity, interface, and live throughput
- Saved refresh, alert, history, and notification-area preferences

- Automatic GitHub Release checks plus an animated top-right button that downloads and installs only `SystemPulse.exe`
- A source-folder button during large-file review; approved large EXEs delete their dedicated containing folder when it is safe to do so

No separate HWiNFO, LibreHardwareMonitor application, or other monitoring program needs to run beside SystemPulse; the Libre library is built into the EXE.

## Open and run in Visual Studio 2026

1. Open `SystemPulse.sln`.
2. Confirm that `SystemPulse` is the startup project.
3. Select **Debug** and **x64**.
4. Press **F5**.
5. Windows requests administrator approval whenever SystemPulse starts.
6. On its first elevated launch, SystemPulse verifies and silently installs the bundled official PawnIO driver. If setup reports that a restart is required, restart Windows once.

The .NET 10 SDK and the Visual Studio **.NET desktop development** workload are sufficient. Visual Studio restores the official Microsoft `System.Management` package automatically.

## Build one optimized EXE with PowerShell

The included script restores dependencies, embeds the updater repository metadata, and publishes a compressed single-file build:

```powershell
.\Build-SystemPulse.ps1 -GitHubRepository 'umustbepro/SystemPulse'
```

If Windows PowerShell blocks local scripts, run it once with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-SystemPulse.ps1 -GitHubRepository 'umustbepro/SystemPulse'
```

The finished file is `publish/SystemPulse.exe`. The default is self-contained so users do not need .NET installed. If every target computer already has the .NET 10 Desktop Runtime, `-FrameworkDependent` creates a considerably smaller EXE.

Create releases at `https://github.com/umustbepro/SystemPulse/releases` with semantic version tags such as `v0.8.0` and attach a Windows asset named `SystemPulse.exe`. The application checks that repository's latest release at startup and shows a badge when a newer version is available. Clicking the update button downloads the asset, closes every instance using the same installed EXE, atomically replaces that exact path even when the local file was renamed, restarts it, and removes the temporary updater without another confirmation or manual cleanup. GitHub's SHA-256 asset digest is verified when the API supplies one.

## Publish one self-contained EXE in Visual Studio

The included `Win-x64-SingleFile` publish profile places the .NET 10 Windows Desktop runtime, PawnIO installer, and signed sensor modules inside one `SystemPulse.exe`.

1. Right-click the **SystemPulse** project in Solution Explorer and select **Publish**.
2. Select the **Win-x64-SingleFile** profile.
3. Select **Publish**.
4. Find the finished executable in `bin/Release/net10.0-windows/win-x64/publish`.

The profile deliberately keeps trimming disabled for WPF compatibility. On first launch, .NET extracts the bundled runtime/content beneath `%TEMP%/.net`; this is automatic and the publish folder itself contains only the distributable EXE.

SystemPulse uses `requireAdministrator`, so Windows displays a UAC consent prompt every time the EXE launches. PawnIO setup only runs automatically when its machine-wide installation registry entry is absent. The installer is checked against its official SHA-256 before execution.

The equivalent terminal command is:

```powershell
dotnet publish .\SystemPulse.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None -o .\publish
```

## Sensor behavior

- **Intel CPU:** reads every active logical processor, then reads a package sensor for each Windows processor group. Hybrid P/E-core TjMax values are read individually. Package watts appear after two energy samples; voltage remains unavailable on models that do not expose a valid `IA32_PERF_STATUS` voltage field.
- **AMD CPU:** supports the official `AMDFamily17.bin` module, whose current signed release accepts Family 17h through 1Ah processors. Package watts use the AMD energy counter; voltage is the current firmware-exposed P-state VID when available.
- **NVIDIA GPU:** queries temperature, utilization, and whole-board power from the telemetry utility installed with the NVIDIA display driver. Voltage uses a direct read-only NVAPI query first, the driver's `nvidia-smi -q -d VOLTAGE` report second, and a matching LibreHardwareMonitor GPU-core sensor last. Unsupported drivers and GPUs still show `Unavailable` rather than an estimated value.
- **AMD GPU:** queries Radeon temperature and utilization through AMD Overdrive N with Overdrive 6 fallbacks. Supported cards also expose core voltage and ASIC power. If ADL omits core voltage, SystemPulse tries a matching LibreHardwareMonitor GPU-core sensor. The installed Radeon driver supplies AMD's ADL runtime; SystemPulse does not launch or require Radeon Software as a separate monitoring application.
- **Frame time:** SystemPulse launches its bundled PresentMon capture engine invisibly and reports ETW-derived frame intervals for the active presenting application. It shows unavailable when no 3D application is presenting frames instead of estimating FPS from GPU utilization.
- **Storage:** enumerates Windows physical disks, reads the standard NVMe SMART/Health log directly through `IOCTL_STORAGE_QUERY_PROPERTY`, follows the Windows reliability-counter association, and falls back to legacy ATA SMART attributes for temperature, power-on hours, and supported SSD wear indicators. Some USB/RAID bridges still block pass-through data; those devices remain selectable and show `Not reported` honestly.
- **Motherboard:** enables LibreHardwareMonitor's motherboard and controller backends, walks motherboard child devices, and prioritizes board/system/chipset/PCH sensors while filtering CPU, GPU, memory, and storage readings. Its GPU backend is also enabled only as the final NVIDIA/AMD voltage fallback. ACPI remains a board-temperature fallback. Hardware support still depends on what a particular board, GPU driver, and embedded controller expose.
- **Fans:** fan RPM is shown as unavailable until a board-specific Super I/O backend is added.

## PawnIO files

The project copies these files into the application output:

- `Vendor/PawnIO/Installer/PawnIO_setup.exe` — official PawnIO 2.2.0 installer
- `Vendor/PawnIO/Modules/IntelMSR.bin` — signed PawnIO.Modules 0.2.9 Intel module
- `Vendor/PawnIO/Modules/AMDFamily17.bin` — signed PawnIO.Modules 0.2.9 AMD module

The installer SHA-256 is:

`1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`

See `THIRD-PARTY-NOTICES.md` and `Vendor/PawnIO/Modules/COPYING.LGPL-2.1` for licensing and source locations.

## Project structure

- `Services/PawnIo/PawnIoClient.cs` — direct PawnIO device-control client
- `Services/PawnIo/CpuTemperatureReader.cs` — Intel/AMD custom register decoding and CPU affinity
- `Services/GpuTelemetryReader.cs` — graphics-driver telemetry
- `Services/NvidiaVoltageReader.cs` — optional direct NVIDIA driver voltage telemetry
- `Services/AmdGpuTelemetryReader.cs` — direct AMD Radeon driver temperature, load, voltage, and power telemetry
- `Services/StorageTelemetryReader.cs` — physical-disk discovery and reliability temperatures
- `Services/StoragePerformanceReader.cs` — per-physical-disk load and read/write throughput
- `Services/PresentMonFrameReader.cs` — hidden ETW frame-time capture and active-application selection
- `Services/StorageCleanupService.cs` — conservative per-drive scanning plus explicitly approved file or dedicated EXE-folder deletion
- `Services/MotherboardTemperatureReader.cs` — LibreHardwareMonitor motherboard/controller reading, GPU-voltage fallback, and ACPI fallback
- `Services/UpdateService.cs` — GitHub Release check, EXE validation, replacement, restart, and temporary-file cleanup
- `Services/SystemTelemetryReader.cs` — native Windows load and memory readings
- `Services/HardwareMonitorService.cs` — combines readings into UI snapshots
- `ViewModels/MainViewModel.cs` — refresh loop, status, commands, and chart histories
- `ViewModels/StorageCleanupViewModel.cs` — cleanup scan, output log, and explicit file-review workflow
- `MainWindow.xaml` — Metro-style shell, overview, and Sensor details page

## Safety note

PawnIO provides privileged hardware access through signed, restricted modules. Use the official driver edition and official signed modules included here. Low-level monitoring software is provided without warranty; test on the intended hardware before redistribution.

Storage Cleanup never deletes during scanning. Temporary files require a separate cleanup action. Large non-temporary files are only suggestions based on last-access/last-modified timestamps and require an individual Delete choice. When the approved candidate is an EXE inside a dedicated subfolder, that containing folder is deleted recursively so associated files are removed too. User roots such as Downloads, Desktop, Documents, Pictures, Music, Videos, the profile root, drive roots, and Windows/program directories are protected and are never selected as the recursive target. Folders containing redirected/reparse-point items are kept and only the reviewed EXE is removed. Windows does not provide a reliable universal “last launched” timestamp.
