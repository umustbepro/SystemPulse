using System.Management;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class StoragePerformanceReader
{
    public IReadOnlyList<StoragePerformanceSnapshot> Read(IReadOnlyList<StorageDeviceSnapshot> devices)
    {
        try
        {
            var counters = ReadCounters();
            return devices.Select(device =>
            {
                counters.TryGetValue(device.DeviceId, out var counter);
                return counter ?? new StoragePerformanceSnapshot(device.DeviceId, 0, 0, 0);
            }).ToList();
        }
        catch
        {
            return devices
                .Select(device => new StoragePerformanceSnapshot(device.DeviceId, null, null, null))
                .ToList();
        }
    }

    private static Dictionary<string, StoragePerformanceSnapshot> ReadCounters()
    {
        var counters = new Dictionary<string, StoragePerformanceSnapshot>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT Name, PercentDiskTime, DiskReadBytesPerSec, DiskWriteBytesPerSec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
        using var results = searcher.Get();

        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var name = Convert.ToString(item["Name"])?.Trim() ?? string.Empty;
                if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                    continue;

                var deviceId = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(deviceId))
                    continue;

                counters[deviceId] = new StoragePerformanceSnapshot(
                    deviceId,
                    ReadFloat(item, "PercentDiskTime"),
                    ReadUlong(item, "DiskReadBytesPerSec"),
                    ReadUlong(item, "DiskWriteBytesPerSec"));
            }
        }

        return counters;
    }

    private static float? ReadFloat(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Math.Clamp(Convert.ToSingle(item[name]), 0, 100);

    private static ulong? ReadUlong(ManagementBaseObject item, string name) =>
        item[name] is null ? null : Convert.ToUInt64(item[name]);
}
