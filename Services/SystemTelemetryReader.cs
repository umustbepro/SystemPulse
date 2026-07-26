using System.Runtime.InteropServices;
using System.Management;
using Microsoft.Win32;

namespace SystemPulse.Services;

internal sealed class SystemTelemetryReader
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;

    public string CpuName { get; } = ReadCpuName();
    public string MotherboardName { get; } = ReadMotherboardName();

    public float? ReadCpuLoad()
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return null;

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();
        if (!_previousIdle.HasValue)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            return null;
        }

        var idleDelta = idle - _previousIdle.Value;
        var totalDelta = kernel - _previousKernel!.Value + user - _previousUser!.Value;
        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        return totalDelta == 0
            ? null
            : (float)Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0d, 100d);
    }

    public MemoryTelemetry ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            return new MemoryTelemetry(null, null, null, null, "System memory");

        var gibibytes = status.TotalPhysical / 1024d / 1024d / 1024d;
        var usedPhysical = status.TotalPhysical >= status.AvailablePhysical
            ? status.TotalPhysical - status.AvailablePhysical
            : 0;
        return new MemoryTelemetry(
            status.MemoryLoad,
            usedPhysical,
            status.AvailablePhysical,
            status.TotalPhysical,
            $"{gibibytes:0.#} GB system memory");
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch
        {
            return "CPU";
        }
    }

    private static string ReadMotherboardName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var manufacturer = CleanBoardValue(key?.GetValue("BaseBoardManufacturer") as string);
            var product = CleanBoardValue(key?.GetValue("BaseBoardProduct") as string);
            var registryName = CombineBoardName(manufacturer, product);
            if (registryName is not null)
                return registryName;
        }
        catch
        {
            // Some firmware does not publish baseboard fields in the hardware registry.
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = searcher.Get();
            foreach (ManagementObject board in results)
            {
                using (board)
                {
                    var manufacturer = CleanBoardValue(Convert.ToString(board["Manufacturer"]));
                    var product = CleanBoardValue(Convert.ToString(board["Product"]));
                    var wmiName = CombineBoardName(manufacturer, product);
                    if (wmiName is not null)
                        return wmiName;
                }
            }
        }
        catch
        {
            // The model label is optional; sensor monitoring must continue without WMI.
        }

        return "Motherboard model unavailable";
    }

    private static string? CombineBoardName(string? manufacturer, string? product)
    {
        if (manufacturer is null)
            return product;
        if (product is null)
            return manufacturer;
        return product.Contains(manufacturer, StringComparison.OrdinalIgnoreCase)
            ? product
            : $"{manufacturer} {product}";
    }

    private static string? CleanBoardValue(string? value)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned) ||
            cleaned.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        return cleaned;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    }
}

internal sealed record MemoryTelemetry(
    float? Load,
    ulong? UsedBytes,
    ulong? AvailableBytes,
    ulong? TotalBytes,
    string Name);
