using System.Diagnostics;
using System.Runtime.InteropServices;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class ProcessTelemetryService
{
    private readonly Dictionary<int, PreviousSample> _previous = new();
    private DateTime _lastRead = DateTime.UtcNow;

    public IReadOnlyList<ProcessTelemetrySnapshot> Read()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max((now - _lastRead).TotalSeconds, 0.1);
        _lastRead = now;
        var current = new Dictionary<int, PreviousSample>();
        var items = new List<ProcessTelemetrySnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var cpu = process.TotalProcessorTime;
                    var io = TryGetIo(process.Handle);
                    var sample = new PreviousSample(cpu, io.ReadTransferCount, io.WriteTransferCount);
                    current[process.Id] = sample;
                    _previous.TryGetValue(process.Id, out var previous);
                    var cpuPercent = previous is null ? 0 : Math.Max(0, (cpu - previous.CpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
                    var readRate = previous is null ? 0 : Rate(io.ReadTransferCount, previous.ReadBytes, elapsed);
                    var writeRate = previous is null ? 0 : Rate(io.WriteTransferCount, previous.WriteBytes, elapsed);
                    items.Add(new ProcessTelemetrySnapshot(process.Id, process.ProcessName, cpuPercent, (ulong)Math.Max(process.WorkingSet64, 0), readRate, writeRate));
                }
                catch
                {
                    // Protected and terminating processes are expected to be unreadable.
                }
            }
        }

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = pair.Value;
        return items.OrderByDescending(item => item.CpuPercent).ThenByDescending(item => item.WorkingSetBytes).Take(30).ToList();
    }

    private static ulong Rate(ulong current, ulong previous, double elapsed) => current >= previous ? (ulong)((current - previous) / elapsed) : 0;

    private static IO_COUNTERS TryGetIo(IntPtr handle) => GetProcessIoCounters(handle, out var counters) ? counters : default;

    private sealed record PreviousSample(TimeSpan CpuTime, ulong ReadBytes, ulong WriteBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);
}
