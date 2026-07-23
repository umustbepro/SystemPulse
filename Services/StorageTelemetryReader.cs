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
            var smartTemperatures = ReadSmartTemperatures();
            var devices = ReadPhysicalDisks(counters, smartTemperatures);
            return devices.Count > 0 ? devices : ReadDiskDriveFallback(smartTemperatures);
        }
        catch
        {
            return ReadDiskDriveFallback(ReadSmartTemperatures());
        }
    }

    private static Dictionary<string, ReliabilityCounter> ReadReliabilityCounters()
    {
        try
        {
            var counters = new Dictionary<string, ReliabilityCounter>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Temperature, TemperatureMax, Wear FROM MSFT_StorageReliabilityCounter");
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
        IReadOnlyDictionary<string, float> smartTemperatures)
    {
        var devices = new List<StorageDeviceSnapshot>();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT DeviceId, FriendlyName, Size, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk");
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
                var temperature = relatedCounter?.Temperature ?? directCounter?.Temperature;
                if (!temperature.HasValue && smartTemperatures.TryGetValue(id, out var smartTemperature))
                    temperature = smartTemperature;
                var name = ReadString(item, "FriendlyName");
                devices.Add(new StorageDeviceSnapshot(
                    id,
                    string.IsNullOrWhiteSpace(name) ? $"Physical disk {id}" : name,
                    ReadUlong(item, "Size"),
                    FormatMediaType(ReadUshort(item, "MediaType")),
                    FormatBusType(ReadUshort(item, "BusType")),
                    temperature,
                    FormatHealth(ReadUshort(item, "HealthStatus")),
                    relatedCounter?.Wear ?? directCounter?.Wear));
            }
        }

        return devices.OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<StorageDeviceSnapshot> ReadDiskDriveFallback(
        IReadOnlyDictionary<string, float> smartTemperatures)
    {
        try
        {
            var devices = new List<StorageDeviceSnapshot>();
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Index, Model, Size, InterfaceType, Status FROM Win32_DiskDrive");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var id = ReadString(item, "Index");
                    var name = ReadString(item, "Model");
                    smartTemperatures.TryGetValue(id, out var smartTemperature);
                    devices.Add(new StorageDeviceSnapshot(
                        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                        string.IsNullOrWhiteSpace(name) ? "Physical storage device" : name,
                        ReadUlong(item, "Size"),
                        "Storage",
                        ReadString(item, "InterfaceType"),
                        smartTemperature is > 0 ? smartTemperature : null,
                        ReadString(item, "Status") is { Length: > 0 } status ? status : "Unknown",
                        null));
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
            ReadByte(item, "Wear"));

    private static IReadOnlyDictionary<string, float> ReadSmartTemperatures()
    {
        try
        {
            var identities = ReadDiskIdentities();
            var samples = new List<(string InstanceKey, float Temperature)>();
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

                    var temperature = ParseSmartTemperature(data);
                    if (temperature.HasValue)
                        samples.Add((NormalizeDeviceKey(ReadString(item, "InstanceName")), temperature.Value));
                }
            }

            var temperatures = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in identities)
            {
                var pnpKey = NormalizeDeviceKey(identity.PnpDeviceId);
                var sample = samples.FirstOrDefault(candidate =>
                    candidate.InstanceKey.Contains(pnpKey, StringComparison.OrdinalIgnoreCase) ||
                    pnpKey.Contains(candidate.InstanceKey, StringComparison.OrdinalIgnoreCase));
                if (sample.Temperature is > 0)
                    temperatures[identity.DeviceId] = sample.Temperature;
            }

            return temperatures;
        }
        catch
        {
            return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
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

    private static float? ParseSmartTemperature(byte[] data)
    {
        float? fallback = null;
        for (var offset = 2; offset + 11 < data.Length; offset += 12)
        {
            var attributeId = data[offset];
            if (attributeId is not (190 or 194))
                continue;

            var rawTemperature = data[offset + 5];
            if (rawTemperature is <= 0 or >= 130)
                continue;

            if (attributeId == 194)
                return rawTemperature;
            fallback = rawTemperature;
        }

        return fallback;
    }

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

    private static string ReadString(ManagementBaseObject item, string name) =>
        Convert.ToString(item[name])?.Trim() ?? string.Empty;

    private static byte? ReadByte(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToByte(item[name]);

    private static ushort? ReadUshort(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt16(item[name]);

    private static ulong? ReadUlong(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt64(item[name]);

    private sealed record ReliabilityCounter(float? Temperature, float? TemperatureMax, byte? Wear);
    private sealed record DiskIdentity(string DeviceId, string PnpDeviceId);
}
