using System.Runtime.InteropServices;

namespace SystemPulse.Services;

/// <summary>
/// Reads NVIDIA's optional, read-only voltage telemetry directly from the installed display driver.
/// No NVAPI SDK DLL is bundled; nvapi64.dll is supplied by the NVIDIA driver.
/// </summary>
internal sealed class NvidiaVoltageReader
{
    private const uint InitializeId = 0x0150E828;
    private const uint EnumPhysicalGpusId = 0xE5AC921F;
    private const uint GetVoltageDomainsStatusId = 0xC16C7E2C;
    private const uint GetCurrentPstateId = 0x927DA4F6;
    private const uint GetPstates20Id = 0x6FF81213;
    private const int MaximumPhysicalGpus = 64;
    private const int VoltageDomainsStatusSize = 140;
    private const int Pstates20Size = 7416;
    private const int PstateEntrySize = 456;
    private const int PstateBaseVoltageOffset = 360;
    private const int BaseVoltageEntrySize = 24;

    private bool _initializationAttempted;
    private IntPtr[] _physicalGpus = [];
    private GetVoltageDomainsStatusDelegate? _getVoltageDomainsStatus;
    private GetCurrentPstateDelegate? _getCurrentPstate;
    private GetPstates20Delegate? _getPstates20;

    public NvidiaVoltageSample Read(int physicalGpuIndex)
    {
        EnsureInitialized();
        if (physicalGpuIndex < 0 || physicalGpuIndex >= _physicalGpus.Length)
            return NvidiaVoltageSample.Unavailable;

        var gpu = _physicalGpus[physicalGpuIndex];
        var liveVoltage = ReadLiveVoltageDomain(gpu);
        if (liveVoltage.HasValue)
            return new NvidiaVoltageSample(liveVoltage, "NVIDIA driver · live core voltage");

        var pstateVoltage = ReadCurrentPstateVoltage(gpu);
        return pstateVoltage.HasValue
            ? new NvidiaVoltageSample(pstateVoltage, "NVIDIA driver · current P-state voltage")
            : NvidiaVoltageSample.Unavailable;
    }

    private void EnsureInitialized()
    {
        if (_initializationAttempted)
            return;

        _initializationAttempted = true;
        try
        {
            var initialize = GetDelegate<InitializeDelegate>(InitializeId);
            var enumerate = GetDelegate<EnumPhysicalGpusDelegate>(EnumPhysicalGpusId);
            _getVoltageDomainsStatus = TryGetDelegate<GetVoltageDomainsStatusDelegate>(GetVoltageDomainsStatusId);
            _getCurrentPstate = TryGetDelegate<GetCurrentPstateDelegate>(GetCurrentPstateId);
            _getPstates20 = TryGetDelegate<GetPstates20Delegate>(GetPstates20Id);
            if (initialize is null || enumerate is null || initialize() != 0)
                return;

            var handles = new IntPtr[MaximumPhysicalGpus];
            if (enumerate(handles, out var count) != 0 || count <= 0)
                return;

            _physicalGpus = handles.Take(Math.Min(count, handles.Length)).ToArray();
        }
        catch
        {
            _physicalGpus = [];
        }
    }

    private float? ReadLiveVoltageDomain(IntPtr gpu)
    {
        if (_getVoltageDomainsStatus is null)
            return null;

        var buffer = Marshal.AllocHGlobal(VoltageDomainsStatusSize);
        try
        {
            Zero(buffer, VoltageDomainsStatusSize);
            Marshal.WriteInt32(buffer, VoltageDomainsStatusSize | 1 << 16);
            if (_getVoltageDomainsStatus(gpu, buffer) != 0)
                return null;

            var reportedDomains = Math.Clamp(Marshal.ReadInt32(buffer, 8), 0, 16);
            var domainCount = reportedDomains > 0 ? reportedDomains : 16;
            for (var index = 0; index < domainCount; index++)
            {
                var offset = 12 + index * 8;
                var domain = Marshal.ReadInt32(buffer, offset);
                var microvolts = (uint)Marshal.ReadInt32(buffer, offset + 4);
                if (domain == 0 && microvolts is >= 350_000 and <= 2_000_000)
                    return microvolts / 1_000_000f;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private float? ReadCurrentPstateVoltage(IntPtr gpu)
    {
        if (_getCurrentPstate is null || _getPstates20 is null ||
            _getCurrentPstate(gpu, out var currentPstate) != 0)
            return null;

        var buffer = Marshal.AllocHGlobal(Pstates20Size);
        try
        {
            Zero(buffer, Pstates20Size);
            Marshal.WriteInt32(buffer, Pstates20Size | 3 << 16);
            if (_getPstates20(gpu, buffer) != 0)
                return null;

            var pstateCount = Math.Clamp(Marshal.ReadInt32(buffer, 8), 0, 16);
            var voltageCount = Math.Clamp(Marshal.ReadInt32(buffer, 16), 0, 4);
            for (var pstateIndex = 0; pstateIndex < pstateCount; pstateIndex++)
            {
                var pstateOffset = 20 + pstateIndex * PstateEntrySize;
                if (Marshal.ReadInt32(buffer, pstateOffset) != currentPstate)
                    continue;

                for (var voltageIndex = 0; voltageIndex < voltageCount; voltageIndex++)
                {
                    var voltageOffset = pstateOffset + PstateBaseVoltageOffset + voltageIndex * BaseVoltageEntrySize;
                    var domain = Marshal.ReadInt32(buffer, voltageOffset);
                    var microvolts = (uint)Marshal.ReadInt32(buffer, voltageOffset + 8);
                    var deltaMicrovolts = Marshal.ReadInt32(buffer, voltageOffset + 12);
                    var adjustedMicrovolts = (long)microvolts + deltaMicrovolts;
                    if (domain == 0 && adjustedMicrovolts is >= 350_000 and <= 2_000_000)
                        return adjustedMicrovolts / 1_000_000f;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static T? GetDelegate<T>(uint interfaceId) where T : Delegate
    {
        var address = NativeMethods.QueryInterface(interfaceId);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T? TryGetDelegate<T>(uint interfaceId) where T : Delegate
    {
        try
        {
            return GetDelegate<T>(interfaceId);
        }
        catch
        {
            return null;
        }
    }

    private static void Zero(IntPtr buffer, int size)
    {
        var zeros = new byte[size];
        Marshal.Copy(zeros, 0, buffer, size);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVoltageDomainsStatusDelegate(IntPtr gpu, IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCurrentPstateDelegate(IntPtr gpu, out int pstate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPstates20Delegate(IntPtr gpu, IntPtr pstates);

    private static class NativeMethods
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr QueryInterface(uint interfaceId);
    }
}

internal sealed record NvidiaVoltageSample(float? Voltage, string Source)
{
    public static NvidiaVoltageSample Unavailable { get; } =
        new(null, "NVIDIA driver did not expose core voltage");
}
