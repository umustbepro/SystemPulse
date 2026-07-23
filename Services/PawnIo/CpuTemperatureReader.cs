using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace SystemPulse.Services.PawnIo;

internal sealed class CpuTemperatureReader : IDisposable
{
    private const uint IntelThermalStatus = 0x019C;
    private const uint IntelTemperatureTarget = 0x01A2;
    private const uint IntelPackageThermalStatus = 0x01B1;
    private const ulong ThermalValid = 1UL << 31;
    private const uint AmdThermalRegister = 0x00059800;
    private const uint AmdTemperatureOffsetFlag = 1U << 19;

    private readonly CpuVendor _vendor;
    private readonly List<ProcessorLocation> _processors = [];
    private PawnIoClient? _client;
    private int[] _tjMax = [];
    private DateTime _lastConnectAttempt = DateTime.MinValue;
    private string _lastError = "PawnIO sensor driver has not been opened yet.";

    public CpuTemperatureReader()
    {
        _vendor = DetectVendor();
        for (ushort group = 0; group < NativeMethods.GetActiveProcessorGroupCount(); group++)
        {
            var count = NativeMethods.GetActiveProcessorCount(group);
            for (byte processor = 0; processor < count && processor < 64; processor++)
                _processors.Add(new ProcessorLocation(group, processor));
        }

        if (_processors.Count == 0)
            _processors.Add(new ProcessorLocation(0, 0));
    }

    public CpuTemperatureSample Read()
    {
        EnsureConnected();
        if (_client is null)
            return CpuTemperatureSample.Unavailable(_lastError);

        try
        {
            return _vendor switch
            {
                CpuVendor.Intel => ReadIntel(),
                CpuVendor.Amd => ReadAmd(),
                _ => CpuTemperatureSample.Unavailable("This CPU vendor is not supported by the bundled PawnIO modules.")
            };
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _client.Dispose();
            _client = null;
            return CpuTemperatureSample.Unavailable(_lastError);
        }
    }

    private void EnsureConnected()
    {
        if (_client is not null || DateTime.UtcNow - _lastConnectAttempt < TimeSpan.FromSeconds(5))
            return;

        _lastConnectAttempt = DateTime.UtcNow;
        try
        {
            var moduleName = _vendor switch
            {
                CpuVendor.Intel => "IntelMSR.bin",
                CpuVendor.Amd => "AMDFamily17.bin",
                _ => throw new PawnIoException("Only Intel x64 and AMD Family 17h-1Ah CPUs are currently supported.")
            };

            var modulePath = Path.Combine(AppContext.BaseDirectory, "PawnIO", "Modules", moduleName);
            _client = PawnIoClient.Load(modulePath);
            _lastError = string.Empty;

            if (_vendor == CpuVendor.Intel)
                ReadIntelTemperatureTargets();
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _client?.Dispose();
            _client = null;
        }
    }

    private void ReadIntelTemperatureTargets()
    {
        _tjMax = new int[_processors.Count];
        for (var index = 0; index < _processors.Count; index++)
        {
            try
            {
                using var affinity = ProcessorAffinity.Pin(_processors[index]);
                var raw = _client!.ReadMsr(IntelTemperatureTarget);
                var target = (int)((raw >> 16) & 0xFF);
                _tjMax[index] = target is >= 70 and <= 125 ? target : 100;
            }
            catch
            {
                _tjMax[index] = 100;
            }
        }
    }

    private CpuTemperatureSample ReadIntel()
    {
        var coreTemperatures = new List<float>(_processors.Count);
        var packageTemperatures = new List<float>();

        for (var index = 0; index < _processors.Count; index++)
        {
            try
            {
                using var affinity = ProcessorAffinity.Pin(_processors[index]);
                var raw = _client!.ReadMsr(IntelThermalStatus);
                var target = _tjMax.ElementAtOrDefault(index);
                var decoded = DecodeIntel(raw, target > 0 ? target : 100);
                if (decoded.HasValue)
                    coreTemperatures.Add(decoded.Value);
            }
            catch
            {
                // A disabled/offline logical processor should not discard the other readings.
            }
        }

        foreach (var group in _processors.Select(item => item.Group).Distinct())
        {
            try
            {
                using var affinity = ProcessorAffinity.Pin(new ProcessorLocation(group, 0));
                var raw = _client!.ReadMsr(IntelPackageThermalStatus);
                var groupIndex = _processors.FindIndex(item => item.Group == group);
                var target = _tjMax.ElementAtOrDefault(groupIndex);
                var decoded = DecodeIntel(raw, target > 0 ? target : 100);
                if (decoded.HasValue)
                    packageTemperatures.Add(decoded.Value);
            }
            catch
            {
                // Fall back to the hottest valid per-core reading below.
            }
        }

        float? package = packageTemperatures.Count > 0
            ? packageTemperatures.Max()
            : coreTemperatures.Count > 0 ? coreTemperatures.Max() : null;

        return new CpuTemperatureSample(
            package,
            coreTemperatures,
            "PawnIO · Intel package/per-core MSR",
            true,
            $"PawnIO ready · {coreTemperatures.Count} logical CPU sensors read");
    }

    private CpuTemperatureSample ReadAmd()
    {
        var raw = _client!.ReadSmn(AmdThermalRegister);
        var temperature = (float)((raw >> 21) * 0.125);
        if ((raw & AmdTemperatureOffsetFlag) != 0)
            temperature -= 49f;

        return new CpuTemperatureSample(
            temperature,
            [],
            "PawnIO · AMD Zen SMN",
            true,
            "PawnIO ready · AMD package sensor read");
    }

    private static float? DecodeIntel(ulong raw, int tjMax)
    {
        if ((raw & ThermalValid) == 0)
            return null;

        var distance = (int)((raw >> 16) & 0x7F);
        var value = tjMax - distance;
        return value is > 0 and < 130 ? value : null;
    }

    private static CpuVendor DetectVendor()
    {
        if (!X86Base.IsSupported)
            return CpuVendor.Other;

        var leaf = X86Base.CpuId(0, 0);
        Span<byte> vendor = stackalloc byte[12];
        BitConverter.TryWriteBytes(vendor[..4], leaf.Ebx);
        BitConverter.TryWriteBytes(vendor.Slice(4, 4), leaf.Edx);
        BitConverter.TryWriteBytes(vendor.Slice(8, 4), leaf.Ecx);
        var name = System.Text.Encoding.ASCII.GetString(vendor);
        return name switch
        {
            "GenuineIntel" => CpuVendor.Intel,
            "AuthenticAMD" => CpuVendor.Amd,
            _ => CpuVendor.Other
        };
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    private enum CpuVendor { Other, Intel, Amd }
    internal readonly record struct ProcessorLocation(ushort Group, byte Processor);

    private sealed class ProcessorAffinity : IDisposable
    {
        private readonly GroupAffinity _previous;
        private bool _disposed;

        private ProcessorAffinity(GroupAffinity previous) => _previous = previous;

        public static ProcessorAffinity Pin(ProcessorLocation location)
        {
            var requested = new GroupAffinity
            {
                Mask = (UIntPtr)(1UL << location.Processor),
                Group = location.Group
            };

            if (!NativeMethods.SetThreadGroupAffinity(NativeMethods.GetCurrentThread(), in requested, out var previous))
                throw new PawnIoException("Windows could not select the requested logical processor.", Marshal.GetLastWin32Error());

            return new ProcessorAffinity(previous);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _ = NativeMethods.SetThreadGroupAffinity(NativeMethods.GetCurrentThread(), in _previous, out _);
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        private ushort Reserved0;
        private ushort Reserved1;
        private ushort Reserved2;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern ushort GetActiveProcessorGroupCount();

        [DllImport("kernel32.dll")]
        internal static extern uint GetActiveProcessorCount(ushort groupNumber);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadGroupAffinity(
            IntPtr thread,
            in GroupAffinity groupAffinity,
            out GroupAffinity previousGroupAffinity);
    }
}

internal sealed record CpuTemperatureSample(
    float? PackageTemperature,
    IReadOnlyList<float> CoreTemperatures,
    string Source,
    bool IsPawnIoReady,
    string DriverStatus)
{
    public static CpuTemperatureSample Unavailable(string status) =>
        new(null, [], "PawnIO unavailable", false, status);
}
