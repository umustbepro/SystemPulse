using LibreHardwareMonitor.Hardware;

namespace SystemPulse.Services;

internal static class SharedLibreHardware
{
    internal static readonly object SyncRoot = new();
    internal static readonly Computer Computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true
    };
    private static int _leases;
    private static bool _open;

    internal static void Acquire()
    {
        lock (SyncRoot)
        {
            if (_leases++ > 0) return;
            try { Computer.Open(); _open = true; }
            catch { _leases = 0; _open = false; throw; }
        }
    }

    internal static void Release()
    {
        lock (SyncRoot)
        {
            if (_leases <= 0 || --_leases > 0) return;
            if (_open) try { Computer.Close(); } catch { }
            _open = false;
        }
    }
}
