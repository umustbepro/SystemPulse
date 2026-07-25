using System.Management;
using LibreHardwareMonitor.Hardware;

namespace SystemPulse.Services;

/// <summary>
/// Reads motherboard/controller temperatures and provides a GPU-voltage fallback through
/// LibreHardwareMonitor. PawnIO and the vendor GPU APIs remain the primary telemetry paths.
/// </summary>
internal sealed class MotherboardTemperatureReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsGpuEnabled = true
    };
    private bool _isOpen;
    private bool _disposed;

    public MotherboardTemperatureSample Read()
    {
        if (_disposed)
            return Unavailable("LibreHardwareMonitor reader is closed");

        try
        {
            EnsureOpen();
            var candidates = new List<BoardTemperatureCandidate>();

            foreach (var hardware in _computer.Hardware.Where(item => item.HardwareType == HardwareType.Motherboard))
                CollectBoardTemperatures(hardware, hardware.Name, candidates);

            var preferred = candidates
                .Where(candidate => candidate.Score >= 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Temperature)
                .FirstOrDefault();

            if (preferred is not null)
            {
                return new MotherboardTemperatureSample(
                    preferred.Temperature,
                    $"LibreHardwareMonitor · {preferred.HardwareName} · {preferred.SensorName}");
            }
        }
        catch
        {
            CloseComputer();
        }

        // ACPI remains a useful firmware fallback on machines whose Super I/O or EC is
        // not exposed by the board. It is never preferred over a valid Libre reading.
        return ReadAcpiFallback();
    }

    public GpuVoltageSample ReadGpuVoltage(string gpuName, int physicalGpuIndex)
    {
        if (_disposed)
            return GpuVoltageSample.Unavailable;

        try
        {
            EnsureOpen();
            var hardware = _computer.Hardware
                .Where(item => item.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                .Where(item => MatchesGpuVendor(item.HardwareType, gpuName))
                .ToArray();

            if (hardware.Length == 0)
                return GpuVoltageSample.Unavailable;

            var candidates = new List<GpuVoltageCandidate>();
            for (var index = 0; index < hardware.Length; index++)
            {
                var device = hardware[index];
                CollectGpuVoltages(
                    device,
                    device.Name,
                    ScoreGpuMatch(device.Name, gpuName) + (index == physicalGpuIndex ? 25 : 0),
                    candidates);
            }

            var preferred = candidates
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            return preferred is null
                ? GpuVoltageSample.Unavailable
                : new GpuVoltageSample(
                    preferred.Voltage,
                    $"LibreHardwareMonitor · {preferred.HardwareName} · {preferred.SensorName}");
        }
        catch
        {
            CloseComputer();
            return GpuVoltageSample.Unavailable;
        }
    }

    private void EnsureOpen()
    {
        if (_isOpen)
            return;
        _computer.Open();
        _isOpen = true;
    }

    private static void CollectBoardTemperatures(
        IHardware hardware,
        string boardName,
        ICollection<BoardTemperatureCandidate> candidates)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value ||
                !float.IsFinite(value) || value is < 1 or > 125)
                continue;

            candidates.Add(new BoardTemperatureCandidate(
                value,
                sensor.Name,
                hardware.HardwareType == HardwareType.Motherboard ? boardName : hardware.Name,
                ScoreSensor(sensor.Name)));
        }

        foreach (var child in hardware.SubHardware)
            CollectBoardTemperatures(child, boardName, candidates);
    }

    private static void CollectGpuVoltages(
        IHardware hardware,
        string gpuName,
        int hardwareScore,
        ICollection<GpuVoltageCandidate> candidates)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Voltage || sensor.Value is not float voltage ||
                !float.IsFinite(voltage) || voltage is < 0.05f or > 3f)
                continue;

            var sensorName = sensor.Name.ToUpperInvariant();
            var sensorScore = ContainsAny(sensorName, "GPU CORE", "CORE VOLTAGE") ? 100
                : sensorName.Contains("VDDC") ? 95
                : sensorName.Contains("CORE") ? 90
                : sensorName.Contains("GPU") ? 80
                : ContainsAny(sensorName, "SOC", "MEMORY", "VRAM") ? 10
                : 40;
            candidates.Add(new GpuVoltageCandidate(
                voltage,
                sensor.Name,
                gpuName,
                hardwareScore + sensorScore));
        }

        foreach (var child in hardware.SubHardware)
            CollectGpuVoltages(child, gpuName, hardwareScore, candidates);
    }

    private static bool MatchesGpuVendor(HardwareType hardwareType, string gpuName)
    {
        var normalized = gpuName.ToUpperInvariant();
        if (normalized.Contains("NVIDIA") || normalized.Contains("GEFORCE") || normalized.Contains("QUADRO"))
            return hardwareType == HardwareType.GpuNvidia;
        if (normalized.Contains("AMD") || normalized.Contains("RADEON"))
            return hardwareType == HardwareType.GpuAmd;
        return true;
    }

    private static int ScoreGpuMatch(string hardwareName, string requestedName)
    {
        var hardware = NormalizeGpuName(hardwareName);
        var requested = NormalizeGpuName(requestedName);
        if (hardware.Length == 0 || requested.Length == 0)
            return 0;
        if (hardware == requested)
            return 100;
        if (hardware.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
            requested.Contains(hardware, StringComparison.OrdinalIgnoreCase))
            return 75;
        return hardware.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Intersect(requested.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            .Count() * 10;
    }

    private static string NormalizeGpuName(string name) =>
        string.Join(' ', name
            .Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("AMD", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("RADEON", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("GEFORCE", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static int ScoreSensor(string name)
    {
        var normalized = name.ToUpperInvariant();
        if (ContainsAny(normalized, "CPU", "PACKAGE", "CORE", "PECI", "GPU", "DIMM", "MEMORY", "NVME", "SSD"))
            return -1;
        if (ContainsAny(normalized, "MOTHERBOARD", "MAINBOARD"))
            return 100;
        if (ContainsAny(normalized, "SYSTEM", "SYSTIN"))
            return 95;
        if (ContainsAny(normalized, "CHIPSET", "PCH"))
            return 90;
        if (ContainsAny(normalized, "BOARD", "AMBIENT"))
            return 80;
        if (ContainsAny(normalized, "VRM", "MOS"))
            return 65;
        if (ContainsAny(normalized, "TEMP", "EC"))
            return normalized.Contains("AUX") ? 20 : 45;
        return 30;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(value.Contains);

    private static MotherboardTemperatureSample ReadAcpiFallback()
    {
        try
        {
            var zones = new List<(string Name, float Temperature)>();
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["CurrentTemperature"] is null)
                        continue;
                    var temperature = Convert.ToSingle(item["CurrentTemperature"]) / 10f - 273.15f;
                    if (temperature is <= 0 or >= 130)
                        continue;
                    zones.Add((Convert.ToString(item["InstanceName"]) ?? "ACPI thermal zone", temperature));
                }
            }

            var preferred = zones
                .OrderByDescending(zone => IsLikelyBoardZone(zone.Name))
                .ThenByDescending(zone => zone.Temperature)
                .FirstOrDefault();
            return zones.Count == 0
                ? Unavailable()
                : new MotherboardTemperatureSample(preferred.Temperature, "Windows ACPI fallback");
        }
        catch
        {
            return Unavailable();
        }
    }

    private static bool IsLikelyBoardZone(string name)
    {
        var upper = name.ToUpperInvariant();
        return ContainsAny(upper, "MOTHERBOARD", "MAINBOARD", "SYSTEM", "THM", "TZ");
    }

    private static MotherboardTemperatureSample Unavailable(string? detail = null) =>
        new(null, detail ?? "Not exposed by LibreHardwareMonitor or motherboard firmware");

    private void CloseComputer()
    {
        if (!_isOpen)
            return;
        try { _computer.Close(); } catch { }
        _isOpen = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CloseComputer();
        _disposed = true;
    }

    private sealed record BoardTemperatureCandidate(
        float Temperature,
        string SensorName,
        string HardwareName,
        int Score);

    private sealed record GpuVoltageCandidate(
        float Voltage,
        string SensorName,
        string HardwareName,
        int Score);
}

internal sealed record MotherboardTemperatureSample(float? Temperature, string Source);
internal sealed record GpuVoltageSample(float? Voltage, string Source)
{
    public static GpuVoltageSample Unavailable { get; } =
        new(null, "LibreHardwareMonitor did not expose GPU core voltage");
}
