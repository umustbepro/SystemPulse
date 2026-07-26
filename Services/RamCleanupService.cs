using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SystemPulse.Services;

internal static class RamCleanupService
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryInformation = 0x0400;
    private const long MinimumWorkingSetBytes = 32L * 1024 * 1024;

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "dwm", "Idle", "lsass", "Memory Compression", "Registry",
        "Secure System", "services", "smss", "System", "wininit", "winlogon"
    };

    public static RamCleanupResult TrimUserWorkingSets()
    {
        var availableBefore = ReadAvailablePhysicalMemory();
        var currentProcessId = Environment.ProcessId;
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var trimmedProcesses = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == currentProcessId ||
                        process.SessionId != currentSessionId ||
                        ProtectedProcesses.Contains(process.ProcessName) ||
                        process.WorkingSet64 < MinimumWorkingSetBytes)
                        continue;

                    var handle = NativeMethods.OpenProcess(
                        ProcessSetQuota | ProcessQueryInformation,
                        inheritHandle: false,
                        process.Id);
                    if (handle == IntPtr.Zero)
                        continue;

                    try
                    {
                        if (NativeMethods.EmptyWorkingSet(handle))
                            trimmedProcesses++;
                    }
                    finally
                    {
                        _ = NativeMethods.CloseHandle(handle);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // Processes can exit or become protected while the list is being trimmed.
                }
            }
        }

        Thread.Sleep(250);
        var availableAfter = ReadAvailablePhysicalMemory();
        return new RamCleanupResult(
            trimmedProcesses,
            availableBefore,
            availableAfter,
            Math.Max(0, availableAfter - availableBefore));
    }

    private static long ReadAvailablePhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? checked((long)status.AvailablePhysical)
            : 0;
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
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr process);
    }
}

internal sealed record RamCleanupResult(
    int TrimmedProcesses,
    long AvailableBeforeBytes,
    long AvailableAfterBytes,
    long AvailableIncreaseBytes);
