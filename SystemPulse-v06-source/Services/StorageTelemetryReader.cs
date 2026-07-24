using System.Management;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class StorageTelemetryReader
{
    public IReadOnlyList<StorageDeviceSnapshot> Read()
    {
        try
        {
            var counters = ReadReliabilityCounters();
            var smartData = ReadSmartData();
            var devices = ReadPhysicalDisks(counters, smartData);
            return devices.Count > 0 ? devices : ReadDiskDriveFallback(smartData);
        }
        catch
        {
            return ReadDiskDriveFallback(ReadSmartData());
        }
    }

    private static Dictionary<string, ReliabilityCounter> ReadReliabilityCounters()
    {
        try
        {
            var counters = new Dictionary<string, ReliabilityCounter>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsTotal, ReadErrorsUncorrected, WriteErrorsTotal, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var id = ReadString(item, "DeviceId");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    counters[id] = ReadCounter(item);
                }
            }

            return counters;
        }
        catch
        {
            return new Dictionary<string, ReliabilityCounter>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<StorageDeviceSnapshot> ReadPhysicalDisks(
        IReadOnlyDictionary<string, ReliabilityCounter> counters,
        IReadOnlyDictionary<string, SmartFallback> smartData)
    {
        var devices = new List<StorageDeviceSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, Size, MediaType, BusType, HealthStatus, SerialNumber, FirmwareVersion, OperationalStatus, PhysicalLocation FROM MSFT_PhysicalDisk");
        using var results = searcher.Get();

        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var id = ReadString(item, "DeviceId");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                counters.TryGetValue(id, out var directCounter);
                var relatedCounter = ReadRelatedCounter(item);
                var counter = relatedCounter ?? directCounter;
                smartData.TryGetValue(id, out var ataSmart);
                var nvme = int.TryParse(id, out var physicalDriveNumber)
                    ? NvmeHealthReader.Read(physicalDriveNumber)
                    : null;
                var temperature = nvme?.Temperature ?? counter?.Temperature ?? ataSmart?.Temperature;
                var wear = nvme?.PercentageUsed ?? counter?.Wear ?? ataSmart?.Wear;
                var powerOnHours = nvme is not null
                    ? nvme.PowerOnHours
                    : PreferPositive(counter?.PowerOnHours, ataSmart?.PowerOnHours);
                var health = nvme?.CriticalWarning > 0
                    ? "Warning"
                    : FormatHealth(ReadUshort(item, "HealthStatus"));
                var healthSource = nvme is not null
                    ? "Direct NVMe SMART / Health log"
                    : ataSmart is not null && (ataSmart.PowerOnHours.HasValue || ataSmart.Wear.HasValue)
                        ? "Legacy ATA SMART attributes"
                        : "Windows storage reliability provider";
                var name = ReadString(item, "FriendlyName");
                devices.Add(new StorageDeviceSnapshot(
                    id,
                    string.IsNullOrWhiteSpace(name) ? $"Physical disk {id}" : name,
                    ReadUlong(item, "Size"),
                    FormatMediaType(ReadUshort(item, "MediaType")),
                    FormatBusType(ReadUshort(item, "BusType")),
                    temperature,
                    health,
                    wear,
                    counter?.TemperatureMaximum,
                    powerOnHours,
                    counter?.ReadErrorsTotal,
                    counter?.ReadErrorsUncorrected ?? nvme?.MediaErrors,
                    counter?.WriteErrorsTotal,
                    counter?.WriteErrorsUncorrected,
                    ValueOrUnavailable(ReadString(item, "SerialNumber")),
                    ValueOrUnavailable(ReadString(item, "FirmwareVersion")),
                    FormatOperationalStatus(item["OperationalStatus"]),
                    ValueOrUnavailable(ReadString(item, "PhysicalLocation")),
                    nvme?.UnsafeShutdowns,
                    healthSource));
            }
        }

        return devices.OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<StorageDeviceSnapshot> ReadDiskDriveFallback(
        IReadOnlyDictionary<string, SmartFallback> smartData)
    {
        try
        {
            var devices = new List<StorageDeviceSnapshot>();
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Index, Model, Size, InterfaceType, Status, SerialNumber, FirmwareRevision, PNPDeviceID FROM Win32_DiskDrive");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var id = ReadString(item, "Index");
                    var name = ReadString(item, "Model");
                    smartData.TryGetValue(id, out var ataSmart);
                    var nvme = int.TryParse(id, out var physicalDriveNumber)
                        ? NvmeHealthReader.Read(physicalDriveNumber)
                        : null;
                    var healthSource = nvme is not null
                        ? "Direct NVMe SMART / Health log"
                        : ataSmart is not null
                            ? "Legacy ATA SMART attributes"
                            : "Windows disk provider";
                    devices.Add(new StorageDeviceSnapshot(
                        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                        string.IsNullOrWhiteSpace(name) ? "Physical storage device" : name,
                        ReadUlong(item, "Size"),
                        "Storage",
                        ReadString(item, "InterfaceType"),
                        nvme?.Temperature ?? ataSmart?.Temperature,
                        nvme?.CriticalWarning > 0 ? "Warning" : ReadString(item, "Status") is { Length: > 0 } status ? status : "Unknown",
                        nvme?.PercentageUsed ?? ataSmart?.Wear,
                        PowerOnHours: nvme?.PowerOnHours ?? ataSmart?.PowerOnHours,
                        ReadErrorsUncorrected: nvme?.MediaErrors,
                        SerialNumber: ValueOrUnavailable(ReadString(item, "SerialNumber")),
                        FirmwareVersion: ValueOrUnavailable(ReadString(item, "FirmwareRevision")),
                        OperationalStatus: ValueOrUnavailable(ReadString(item, "Status")),
                        PhysicalLocation: ValueOrUnavailable(ReadString(item, "PNPDeviceID")),
                        UnsafeShutdowns: nvme?.UnsafeShutdowns,
                        HealthDataSource: healthSource));
                }
            }

            return devices;
        }
        catch
        {
            return Array.Empty<StorageDeviceSnapshot>();
        }
    }

    private static ReliabilityCounter? ReadRelatedCounter(ManagementObject physicalDisk)
    {
        try
        {
            using var related = physicalDisk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (ManagementObject item in related)
            {
                using (item)
                    return ReadCounter(item);
            }
        }
        catch
        {
            // Some storage providers do not implement the association query.
        }

        return null;
    }

    private static ReliabilityCounter ReadCounter(ManagementBaseObject item) =>
        new(
            NormalizeTemperature(ReadByte(item, "Temperature")),
            NormalizeTemperature(ReadByte(item, "TemperatureMax")),
            ReadByte(item, "Wear"),
            ReadUlong(item, "PowerOnHours"),
            ReadUlong(item, "ReadErrorsTotal"),
            ReadUlong(item, "ReadErrorsUncorrected"),
            ReadUlong(item, "WriteErrorsTotal"),
            ReadUlong(item, "WriteErrorsUncorrected"));

    private static IReadOnlyDictionary<string, SmartFallback> ReadSmartData()
    {
        try
        {
            var identities = ReadDiskIdentities();
            var samples = new List<(string InstanceKey, SmartFallback Data)>();
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["VendorSpecific"] is not byte[] data)
                        continue;

                    var parsed = ParseSmartData(data);
                    if (parsed is not null)
                        samples.Add((NormalizeDeviceKey(ReadString(item, "InstanceName")), parsed));
                }
            }

            var values = new Dictionary<string, SmartFallback>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in identities)
            {
                var pnpKey = NormalizeDeviceKey(identity.PnpDeviceId);
                var sample = samples.FirstOrDefault(candidate =>
                    candidate.InstanceKey.Contains(pnpKey, StringComparison.OrdinalIgnoreCase) ||
                    pnpKey.Contains(candidate.InstanceKey, StringComparison.OrdinalIgnoreCase));
                if (sample.Data is not null)
                    values[identity.DeviceId] = sample.Data;
            }

            return values;
        }
        catch
        {
            return new Dictionary<string, SmartFallback>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<DiskIdentity> ReadDiskIdentities()
    {
        var identities = new List<DiskIdentity>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Index, PNPDeviceID FROM Win32_DiskDrive");
        using var results = searcher.Get();

        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var id = ReadString(item, "Index");
                var pnpId = ReadString(item, "PNPDeviceID");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(pnpId))
                    identities.Add(new DiskIdentity(id, pnpId));
            }
        }

        return identities;
    }

    private static SmartFallback? ParseSmartData(byte[] data)
    {
        float? temperature = null;
        float? fallbackTemperature = null;
        ulong? powerOnHours = null;
        byte? wear = null;
        for (var offset = 2; offset + 11 < data.Length; offset += 12)
        {
            var attributeId = data[offset];
            var currentValue = data[offset + 3];
            var rawValue = ReadSixByteValue(data, offset + 5);
            if (attributeId == 9 && rawValue is > 0 and < 10_000_000)
                powerOnHours = rawValue;
            else if (attributeId is 177 or 231 or 233 && currentValue is > 0 and <= 100)
                wear = (byte)(100 - currentValue);
            else if (attributeId is 190 or 194)
            {
                var rawTemperature = data[offset + 5];
                if (rawTemperature is > 0 and < 130)
                {
                    if (attributeId == 194) temperature = rawTemperature;
                    else fallbackTemperature = rawTemperature;
                }
            }
        }
        temperature ??= fallbackTemperature;
        return temperature.HasValue || powerOnHours.HasValue || wear.HasValue
            ? new SmartFallback(temperature, wear, powerOnHours)
            : null;
    }

    private static ulong ReadSixByteValue(byte[] data, int offset)
    {
        ulong value = 0;
        for (var index = 0; index < 6; index++)
            value |= (ulong)data[offset + index] << (index * 8);
        return value;
    }

    private static ulong? PreferPositive(ulong? primary, ulong? fallback) =>
        primary is > 0 ? primary : fallback is > 0 ? fallback : null;

    private static string NormalizeDeviceKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static float? NormalizeTemperature(byte? value) =>
        value is > 0 and < 130 ? value.Value : null;

    private static string FormatMediaType(ushort? value) => value switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "Storage-class memory",
        _ => "Storage"
    };

    private static string FormatBusType(ushort? value) => value switch
    {
        7 => "USB",
        8 => "RAID",
        11 => "SATA",
        14 => "Virtual",
        17 => "NVMe",
        18 => "SCM",
        _ => "Unknown bus"
    };

    private static string FormatHealth(ushort? value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };

    private static string FormatOperationalStatus(object? value)
    {
        if (value is not ushort[] statuses || statuses.Length == 0)
            return "Unknown";
        return string.Join(", ", statuses.Select(status => status switch
        {
            2 => "OK",
            3 => "Degraded",
            5 => "Predictive failure",
            6 => "Error",
            8 => "Starting",
            9 => "Stopping",
            10 => "Stopped",
            11 => "In service",
            15 => "Dormant",
            17 => "Completed",
            18 => "Power mode",
            _ => $"Status {status}"
        }));
    }

    private static string ValueOrUnavailable(string value) => string.IsNullOrWhiteSpace(value) ? "Not reported" : value;

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name])?.Trim() ?? string.Empty;

    private static byte? ReadByte(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToByte(item[name]);

    private static ushort? ReadUshort(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt16(item[name]);

    private static ulong? ReadUlong(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt64(item[name]);

    private sealed record ReliabilityCounter(
        float? Temperature,
        float? TemperatureMaximum,
        byte? Wear,
        ulong? PowerOnHours,
        ulong? ReadErrorsTotal,
        ulong? ReadErrorsUncorrected,
        ulong? WriteErrorsTotal,
        ulong? WriteErrorsUncorrected);
    private sealed record SmartFallback(float? Temperature, byte? Wear, ulong? PowerOnHours);
    private sealed record DiskIdentity(string DeviceId, string PnpDeviceId);
}
