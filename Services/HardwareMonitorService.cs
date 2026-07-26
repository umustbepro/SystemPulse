using System.Security.Principal;
using SystemPulse.Models;
using SystemPulse.Services.PawnIo;

namespace SystemPulse.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly CpuTemperatureReader _cpuTemperature = new();
    private readonly SystemTelemetryReader _system = new();
    private readonly GpuTelemetryReader _gpu = new();
    private readonly StorageTelemetryReader _storage = new();
    private readonly StoragePerformanceReader _storagePerformance = new();
    private readonly PresentMonFrameReader _frameTime = new();
    private readonly MotherboardTemperatureReader _motherboard = new();
    private readonly object _syncRoot = new();
    private bool _disposed;

    public string MotherboardName => _system.MotherboardName;

    public SensorSnapshot Read()
    {
        lock (_syncRoot)
        {
            try
            {
                var cpu = _cpuTemperature.Read();
                var memory = _system.ReadMemory();
                var gpu = _gpu.Read();
                if (!gpu.Voltage.HasValue)
                {
                    var libreVoltage = _motherboard.ReadGpuVoltage(gpu.Name, gpu.PhysicalIndex);
                    if (libreVoltage.Voltage.HasValue)
                    {
                        var powerSource = gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                            ? "NVIDIA board power"
                            : gpu.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                              gpu.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                                ? "AMD ASIC power"
                                : "GPU board power";
                        gpu = gpu with
                        {
                            Voltage = libreVoltage.Voltage,
                            ElectricalSource = gpu.PowerWatts.HasValue
                                ? $"{powerSource} · {libreVoltage.Source}"
                                : libreVoltage.Source
                        };
                    }
                }
                var storage = _storage.Read();
                var storagePerformance = _storagePerformance.Read(storage);
                var frameTime = _frameTime.Read();
                var motherboard = _motherboard.Read();

                return new SensorSnapshot(
                    cpu.PackageTemperature,
                    cpu.Source,
                    _system.ReadCpuLoad(),
                    cpu.PackageVoltage,
                    cpu.PackagePowerWatts,
                    cpu.ElectricalSource,
                    gpu.Temperature,
                    gpu.Load,
                    gpu.Voltage,
                    gpu.PowerWatts,
                    gpu.ElectricalSource,
                    null,
                    memory.Load,
                    memory.UsedBytes,
                    memory.AvailableBytes,
                    memory.TotalBytes,
                    null,
                    _system.CpuName,
                    gpu.Name,
                    memory.Name,
                    storage,
                    storagePerformance,
                    frameTime.Milliseconds,
                    frameTime.ProcessName,
                    frameTime.Applications,
                    motherboard.Temperature,
                    motherboard.Source,
                    DateTime.Now,
                    IsAdministrator(),
                    cpu.IsPawnIoReady,
                    cpu.CoreTemperatures.Count + (cpu.PackageTemperature.HasValue ? 1 : 0),
                    cpu.DriverStatus);
            }
            catch (Exception exception)
            {
                return new SensorSnapshot(
                    null, "PawnIO unavailable", null, null, null, "CPU electrical telemetry unavailable",
                    null, null, null, null, "GPU electrical telemetry unavailable", null, null, null, null, null, null,
                    _system.CpuName, "GPU", "System memory", Array.Empty<StorageDeviceSnapshot>(),
                    Array.Empty<StoragePerformanceSnapshot>(), null, "No active 3D presentation",
                    Array.Empty<FrameApplicationSnapshot>(),
                    null, "Not exposed by LibreHardwareMonitor or motherboard firmware", DateTime.Now, IsAdministrator(),
                    false, 0, exception.Message, exception.Message);
            }
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _cpuTemperature.Dispose();
        _motherboard.Dispose();
        _frameTime.Dispose();
        _disposed = true;
    }
}
