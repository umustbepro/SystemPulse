namespace SystemPulse.Models;

public sealed record SensorSnapshot(
    float? CpuTemperature,
    string CpuTemperatureSource,
    float? CpuLoad,
    float? GpuTemperature,
    float? GpuLoad,
    float? MemoryTemperature,
    float? MemoryLoad,
    float? CpuFanRpm,
    string CpuName,
    string GpuName,
    string MemoryName,
    IReadOnlyList<StorageDeviceSnapshot> StorageDevices,
    IReadOnlyList<StoragePerformanceSnapshot> StoragePerformance,
    float? FrameTimeMilliseconds,
    string FrameProcess,
    IReadOnlyList<FrameApplicationSnapshot> FrameApplications,
    float? MotherboardTemperature,
    string MotherboardTemperatureSource,
    DateTime Timestamp,
    bool HasElevatedAccess,
    bool IsPawnIoReady,
    int CpuSensorCount,
    string DriverStatus,
    string? Error = null);

public sealed record StorageDeviceSnapshot(
    string DeviceId,
    string DisplayName,
    ulong? SizeBytes,
    string MediaType,
    string BusType,
    float? Temperature,
    string Health,
    byte? Wear);

public sealed record StoragePerformanceSnapshot(
    string DeviceId,
    float? Load,
    ulong? ReadBytesPerSecond,
    ulong? WriteBytesPerSecond);

public sealed record FrameApplicationSnapshot(
    int ProcessId,
    string DisplayName,
    float? FrameTimeMilliseconds);
