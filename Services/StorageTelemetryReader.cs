using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class StorageTelemetryReader
{
    public IReadOnlyList<StorageDeviceSnapshot> Read()
    {
        var capacityUsage = ReadCapacityUsage();
        try
        {
            var counters = ReadReliabilityCounters();
            var smartData = ReadSmartData();
            var devices = ReadPhysicalDisks(counters, smartData, capacityUsage);
            return devices.Count > 0 ? devices : ReadDiskDriveFallback(smartData, capacityUsage);
        }
        catch
        {
            return ReadDiskDriveFallback(ReadSmartData(), capacityUsage);
        }
    }

    private static IReadOnlyDictionary<string, CapacityUsage> ReadCapacityUsage()
    {
        var usageByDisk = new Dictionary<string, CapacityUsage>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.TotalSize <= 0 || drive.DriveType is DriveType.Network or DriveType.CDRom)
                    continue;

                var extents = ReadVolumeDiskExtents(drive.Name);
                if (extents.Count == 0)
                    continue;

                var totalExtentLength = extents.Aggregate<VolumeDiskExtent, ulong>(0, (sum, extent) => sum + extent.Length);
                if (totalExtentLength == 0)
                    continue;

                var volumeTotal = (ulong)drive.TotalSize;
                var volumeUsed = volumeTotal - Math.Min(volumeTotal, (ulong)Math.Max(0, drive.AvailableFreeSpace));
                foreach (var diskGroup in extents.GroupBy(extent => extent.DiskNumber))
                {
                    var diskExtentLength = diskGroup.Aggregate<VolumeDiskExtent, ulong>(0, (sum, extent) => sum + extent.Length);
                    var share = diskExtentLength / (double)totalExtentLength;
                    AddCapacity(usageByDisk, diskGroup.Key.ToString(),
                        (ulong)Math.Round(volumeTotal * share),
                        (ulong)Math.Round(volumeUsed * share));
                }
            }
        }
        catch
        {
            // WMI association fallbacks below still cover systems that reject volume handles.
        }

        AddWmiAssociationCapacity(usageByDisk);
        AddStoragePartitionCapacity(usageByDisk);
        return usageByDisk;
    }

    private static void AddWmiAssociationCapacity(IDictionary<string, CapacityUsage> usageByDisk)
    {
        try
        {
            var partitionToDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                using (item)
                {
                    var physicalId = ReadWmiReferenceKey(ReadString(item, "Antecedent"), "DeviceID");
                    var partitionId = ReadWmiReferenceKey(ReadString(item, "Dependent"), "DeviceID");
                    var diskNumber = ParsePhysicalDriveNumber(physicalId);
                    if (diskNumber is not null && !string.IsNullOrWhiteSpace(partitionId))
                        partitionToDisk[partitionId] = diskNumber.Value.ToString();
                }
            }

            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var logicalSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
            using var logicalResults = logicalSearcher.Get();
            foreach (ManagementObject item in logicalResults)
            using (item)
            {
                var partitionId = ReadWmiReferenceKey(ReadString(item, "Antecedent"), "DeviceID");
                var volumeId = ReadWmiReferenceKey(ReadString(item, "Dependent"), "DeviceID");
                if (!partitionToDisk.TryGetValue(partitionId, out var diskId) || !counted.Add($"{diskId}|{volumeId}"))
                    continue;

                using var volume = new ManagementObject(@"root\cimv2", $"Win32_LogicalDisk.DeviceID='{volumeId.Replace("'", "''")}'", null);
                volume.Get();
                var total = ReadUlong(volume, "Size");
                if (total is not > 0)
                    continue;
                var free = Math.Min(total.Value, ReadUlong(volume, "FreeSpace") ?? 0);
                AddCapacityIfMissing(usageByDisk, diskId, total.Value, total.Value - free);
            }
        }
        catch
        {
            // Some storage controller providers omit the legacy association classes.
        }
    }

    private static void AddStoragePartitionCapacity(IDictionary<string, CapacityUsage> usageByDisk)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter IS NOT NULL");
            using var results = searcher.Get();
            foreach (ManagementObject partition in results)
            using (partition)
            {
                var diskId = ReadString(partition, "DiskNumber");
                var letter = ReadString(partition, "DriveLetter").TrimEnd(':');
                if (string.IsNullOrWhiteSpace(diskId) || string.IsNullOrWhiteSpace(letter))
                    continue;

                var drive = new DriveInfo($"{letter}:\\");
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;
                var total = (ulong)drive.TotalSize;
                var free = Math.Min(total, (ulong)Math.Max(0, drive.AvailableFreeSpace));
                AddCapacityIfMissing(usageByDisk, diskId, total, total - free);
            }
        }
        catch
        {
            // Older Windows/storage providers can reject MSFT_Partition queries.
        }
    }

    private static string ReadWmiReferenceKey(string objectPath, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
            return string.Empty;
        try
        {
            using var referenced = new ManagementObject(objectPath);
            return Convert.ToString(referenced[propertyName])?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AddCapacity(IDictionary<string, CapacityUsage> values, string diskId, ulong total, ulong used)
    {
        if (total == 0)
            return;
        if (values.TryGetValue(diskId, out var existing))
            values[diskId] = new CapacityUsage(existing.TotalBytes + total, existing.UsedBytes + Math.Min(total, used));
        else
            values[diskId] = new CapacityUsage(total, Math.Min(total, used));
    }

    private static void AddCapacityIfMissing(IDictionary<string, CapacityUsage> values, string diskId, ulong total, ulong used)
    {
        if (!values.ContainsKey(diskId))
            AddCapacity(values, diskId, total, used);
    }

    private static IReadOnlyList<VolumeDiskExtent> ReadVolumeDiskExtents(string driveName)
    {
        var volumePath = @"\\.\" + driveName.TrimEnd('\\');
        using var handle = CreateFile(volumePath, 0, FileShare.ReadWrite, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return Array.Empty<VolumeDiskExtent>();

        const int bufferSize = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!DeviceIoControl(handle, 0x00560000, IntPtr.Zero, 0, buffer, bufferSize, out _, IntPtr.Zero))
                return Array.Empty<VolumeDiskExtent>();

            var count = Math.Clamp(Marshal.ReadInt32(buffer), 0, (bufferSize - 8) / 24);
            var values = new List<VolumeDiskExtent>(count);
            for (var index = 0; index < count; index++)
            {
                var offset = 8 + index * 24;
                var diskNumber = Marshal.ReadInt32(buffer, offset);
                var length = Marshal.ReadInt64(buffer, offset + 16);
                if (diskNumber >= 0 && length > 0)
                    values.Add(new VolumeDiskExtent(diskNumber, (ulong)length));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ParsePhysicalDriveNumber(string value)
    {
        const string marker = "PHYSICALDRIVE";
        var index = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && int.TryParse(value[(index + marker.Length)..], out var number) ? number : null;
    }

    private static CapacityUsage? ResolveCapacityUsage(
        ManagementBaseObject physicalDisk,
        string deviceId,
        IReadOnlyDictionary<string, CapacityUsage> capacityUsage)
    {
        if (capacityUsage.TryGetValue(deviceId, out var direct))
            return direct;

        try
        {
            var serial = NormalizeDeviceKey(ReadString(physicalDisk, "SerialNumber"));
            var model = NormalizeDeviceKey(ReadString(physicalDisk, "FriendlyName"));
            var physicalSize = ReadUlong(physicalDisk, "Size");
            var candidates = new List<(string Index, string Serial, string Model, ulong? Size)>();
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2", "SELECT Index, SerialNumber, Model, Size FROM Win32_DiskDrive");
            using var results = searcher.Get();
            foreach (ManagementObject disk in results)
            using (disk)
            {
                var diskIndex = ReadString(disk, "Index");
                if (!string.IsNullOrWhiteSpace(diskIndex) && capacityUsage.ContainsKey(diskIndex))
                    candidates.Add((diskIndex, NormalizeDeviceKey(ReadString(disk, "SerialNumber")),
                        NormalizeDeviceKey(ReadString(disk, "Model")), ReadUlong(disk, "Size")));
            }

            var serialMatch = candidates.FirstOrDefault(candidate =>
                serial.Length >= 4 && candidate.Serial.Length >= 4 &&
                (candidate.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Serial.Contains(serial, StringComparison.OrdinalIgnoreCase) ||
                 serial.Contains(candidate.Serial, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(serialMatch.Index))
                return capacityUsage[serialMatch.Index];

            var modelMatches = candidates.Where(candidate =>
                candidate.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                SizesApproximatelyMatch(candidate.Size, physicalSize)).ToList();
            if (modelMatches.Count == 1)
                return capacityUsage[modelMatches[0].Index];

            var sizeMatches = candidates.Where(candidate => SizesApproximatelyMatch(candidate.Size, physicalSize)).ToList();
            if (sizeMatches.Count == 1)
                return capacityUsage[sizeMatches[0].Index];
        }
        catch
        {
            // A missing identity should not suppress the rest of the drive telemetry.
        }

        return null;
    }

    private static bool SizesApproximatelyMatch(ulong? left, ulong? right)
    {
        if (left is not > 0 || right is not > 0)
            return false;
        var difference = left.Value > right.Value ? left.Value - right.Value : right.Value - left.Value;
        return difference <= Math.Max(left.Value, right.Value) / 100;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, IntPtr inputBuffer, int inputBufferSize,
        IntPtr outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);

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
        IReadOnlyDictionary<string, SmartFallback> smartData,
        IReadOnlyDictionary<string, CapacityUsage> capacityUsage)
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
                    : ataSmart?.PredictFailure == true
                        ? "Warning"
                    : FormatHealth(ReadUshort(item, "HealthStatus"));
                var healthSource = nvme is not null
                    ? "Direct NVMe SMART / Health log"
                    : ataSmart is not null && (ataSmart.PowerOnHours.HasValue || ataSmart.Wear.HasValue)
                        ? "Legacy ATA SMART attributes"
                        : "Windows storage reliability provider";
                var name = ReadString(item, "FriendlyName");
                var usage = ResolveCapacityUsage(item, id, capacityUsage);
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
                    Maximum(counter?.ReadErrorsTotal, ataSmart?.ReadErrorsTotal),
                    Maximum(counter?.ReadErrorsUncorrected, nvme?.MediaErrors, ataSmart?.ReadErrorsUncorrected),
                    Maximum(counter?.WriteErrorsTotal, ataSmart?.WriteErrorsTotal),
                    Maximum(counter?.WriteErrorsUncorrected, ataSmart?.WriteErrorsUncorrected),
                    ValueOrUnavailable(ReadString(item, "SerialNumber")),
                    ValueOrUnavailable(ReadString(item, "FirmwareVersion")),
                    FormatOperationalStatus(item["OperationalStatus"]),
                    ValueOrUnavailable(ReadString(item, "PhysicalLocation")),
                    nvme?.UnsafeShutdowns,
                    healthSource,
                    usage?.TotalBytes,
                    usage?.UsedBytes));
            }
        }

        return devices.OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<StorageDeviceSnapshot> ReadDiskDriveFallback(
        IReadOnlyDictionary<string, SmartFallback> smartData,
        IReadOnlyDictionary<string, CapacityUsage> capacityUsage)
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
                    capacityUsage.TryGetValue(id, out var usage);
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
                        nvme?.CriticalWarning > 0 || ataSmart?.PredictFailure == true ? "Warning" : ReadString(item, "Status") is { Length: > 0 } status ? status : "Unknown",
                        nvme?.PercentageUsed ?? ataSmart?.Wear,
                        PowerOnHours: nvme?.PowerOnHours ?? ataSmart?.PowerOnHours,
                        ReadErrorsTotal: ataSmart?.ReadErrorsTotal,
                        ReadErrorsUncorrected: Maximum(nvme?.MediaErrors, ataSmart?.ReadErrorsUncorrected),
                        WriteErrorsTotal: ataSmart?.WriteErrorsTotal,
                        WriteErrorsUncorrected: ataSmart?.WriteErrorsUncorrected,
                        SerialNumber: ValueOrUnavailable(ReadString(item, "SerialNumber")),
                        FirmwareVersion: ValueOrUnavailable(ReadString(item, "FirmwareRevision")),
                        OperationalStatus: ValueOrUnavailable(ReadString(item, "Status")),
                        PhysicalLocation: ValueOrUnavailable(ReadString(item, "PNPDeviceID")),
                        UnsafeShutdowns: nvme?.UnsafeShutdowns,
                        HealthDataSource: healthSource,
                        VolumeCapacityBytes: usage?.TotalBytes,
                        UsedCapacityBytes: usage?.UsedBytes));
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
            var failurePrediction = ReadFailurePrediction();
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
                    {
                        var instanceKey = NormalizeDeviceKey(ReadString(item, "InstanceName"));
                        if (failurePrediction.TryGetValue(instanceKey, out var predicted))
                            parsed = parsed with { PredictFailure = predicted };
                        samples.Add((instanceKey, parsed));
                    }
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

    private static IReadOnlyDictionary<string, bool> ReadFailurePrediction()
    {
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            using (item)
            {
                var key = NormalizeDeviceKey(ReadString(item, "InstanceName"));
                if (!string.IsNullOrWhiteSpace(key))
                    values[key] = item["PredictFailure"] is bool predicted && predicted;
            }
        }
        catch
        {
            // USB bridges and some NVMe drivers do not expose legacy failure prediction.
        }
        return values;
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
        ulong? readErrorsTotal = null;
        ulong? readErrorsUncorrected = null;
        ulong? writeErrorsTotal = null;
        ulong? writeErrorsUncorrected = null;
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

            if (attributeId is 5 or 187 or 197 or 198)
                readErrorsTotal = (readErrorsTotal ?? 0) + rawValue;
            if (attributeId is 187 or 197 or 198)
                readErrorsUncorrected = (readErrorsUncorrected ?? 0) + rawValue;
            if (attributeId is 188 or 199)
                writeErrorsTotal = (writeErrorsTotal ?? 0) + rawValue;
            if (attributeId == 187)
                writeErrorsUncorrected = (writeErrorsUncorrected ?? 0) + rawValue;
        }
        temperature ??= fallbackTemperature;
        return temperature.HasValue || powerOnHours.HasValue || wear.HasValue ||
               readErrorsTotal.HasValue || writeErrorsTotal.HasValue
            ? new SmartFallback(temperature, wear, powerOnHours, readErrorsTotal,
                readErrorsUncorrected, writeErrorsTotal, writeErrorsUncorrected, false)
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

    private static ulong? Maximum(params ulong?[] values)
    {
        var reported = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return reported.Length == 0 ? null : reported.Max();
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
    private sealed record SmartFallback(
        float? Temperature,
        byte? Wear,
        ulong? PowerOnHours,
        ulong? ReadErrorsTotal,
        ulong? ReadErrorsUncorrected,
        ulong? WriteErrorsTotal,
        ulong? WriteErrorsUncorrected,
        bool PredictFailure);
    private sealed record CapacityUsage(ulong TotalBytes, ulong UsedBytes);
    private sealed record VolumeDiskExtent(int DiskNumber, ulong Length);
    private sealed record DiskIdentity(string DeviceId, string PnpDeviceId);
}
