using System.Runtime.InteropServices;

namespace SystemPulse.Services;

/// <summary>
/// Reads Radeon telemetry directly from the AMD Display Library installed with
/// the display driver. Every export is optional so mixed-vendor and older-driver
/// systems degrade to an unavailable metric instead of failing application start.
/// </summary>
internal sealed class AmdGpuTelemetryReader
{
    private const int AmdVendorId = 0x1002;
    private const int AdlOk = 0;
    private const int EdgeTemperature = 1;
    private const int AsicTotalPower = 0;

    public GpuTelemetry? Read()
    {
        if (!NativeLibrary.TryLoad("atiadlxx.dll", out var library))
            return null;

        IntPtr context = IntPtr.Zero;
        MemoryAllocate? allocate = null;
        try
        {
            var create = GetExport<MainControlCreate>(library, "ADL2_Main_Control_Create");
            var destroy = GetExport<MainControlDestroy>(library, "ADL2_Main_Control_Destroy");
            var adapterCount = GetExport<AdapterCountGet>(library, "ADL2_Adapter_NumberOfAdapters_Get");
            var adapterInfo = GetExport<AdapterInfoGet>(library, "ADL2_Adapter_AdapterInfo_Get");
            if (create is null || destroy is null || adapterCount is null || adapterInfo is null)
                return null;

            allocate = size => Marshal.AllocCoTaskMem(size);
            if (create(allocate, 1, out context) != AdlOk || context == IntPtr.Zero)
                return null;

            if (adapterCount(context, out var count) != AdlOk || count <= 0 || count > 250)
                return null;

            var adapters = ReadAdapters(context, count, adapterInfo);
            var physicalAdapters = adapters
                .Where(IsAmdAdapter)
                .GroupBy(item => (item.BusNumber, item.DeviceNumber, item.FunctionNumber))
                .Select(group => group.First())
                .ToArray();
            if (physicalAdapters.Length == 0)
                return null;

            var performanceN = GetExport<PerformanceStatusNGet>(library, "ADL2_OverdriveN_PerformanceStatus_Get");
            var temperatureN = GetExport<TemperatureNGet>(library, "ADL2_OverdriveN_Temperature_Get");
            var performance6 = GetExport<PerformanceStatus6Get>(library, "ADL2_Overdrive6_CurrentStatus_Get");
            var temperature6 = GetExport<Temperature6Get>(library, "ADL2_Overdrive6_Temperature_Get");
            var power6 = GetExport<CurrentPower6Get>(library, "ADL2_Overdrive6_CurrentPower_Get");

            var readings = physicalAdapters
                .Select((adapter, physicalIndex) => ReadAdapter(
                    context, adapter, physicalIndex,
                    performanceN, temperatureN, performance6, temperature6, power6))
                .ToArray();

            var best = readings
                .OrderByDescending(item => item.Temperature ?? float.MinValue)
                .ThenByDescending(item => item.Load ?? float.MinValue)
                .First();

            return best with
            {
                Source = $"AMD Radeon driver telemetry - {readings.Length} GPU(s)"
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                try
                {
                    GetExport<MainControlDestroy>(library, "ADL2_Main_Control_Destroy")?.Invoke(context);
                }
                catch
                {
                    // Telemetry is best-effort and must never block application shutdown.
                }
            }
            GC.KeepAlive(allocate);
            NativeLibrary.Free(library);
        }
    }

    private static AdapterInfo[] ReadAdapters(IntPtr context, int count, AdapterInfoGet getInfo)
    {
        var size = Marshal.SizeOf<AdapterInfo>();
        var bufferSize = checked(size * count);
        var buffer = Marshal.AllocCoTaskMem(bufferSize);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var empty = new AdapterInfo { Size = size };
                Marshal.StructureToPtr(empty, IntPtr.Add(buffer, index * size), false);
            }

            if (getInfo(context, buffer, bufferSize) != AdlOk)
                return [];

            var result = new AdapterInfo[count];
            for (var index = 0; index < count; index++)
                result[index] = Marshal.PtrToStructure<AdapterInfo>(IntPtr.Add(buffer, index * size));
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static bool IsAmdAdapter(AdapterInfo adapter) =>
        adapter.Present != 0 &&
        (adapter.VendorId == AmdVendorId ||
         adapter.AdapterName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
         adapter.AdapterName.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

    private static GpuTelemetry ReadAdapter(
        IntPtr context,
        AdapterInfo adapter,
        int physicalIndex,
        PerformanceStatusNGet? performanceN,
        TemperatureNGet? temperatureN,
        PerformanceStatus6Get? performance6,
        Temperature6Get? temperature6,
        CurrentPower6Get? power6)
    {
        float? load = null;
        float? voltage = null;

        if (performanceN is not null)
        {
            var status = new PerformanceStatusN();
            if (performanceN(context, adapter.AdapterIndex, ref status) == AdlOk)
            {
                load = Percentage(status.GpuActivityPercent);
                voltage = Millivolts(status.Vddc);
            }
        }

        if (!load.HasValue && performance6 is not null)
        {
            var status = new PerformanceStatus6();
            if (performance6(context, adapter.AdapterIndex, ref status) == AdlOk)
                load = Percentage(status.ActivityPercent);
        }

        float? temperature = null;
        if (temperatureN is not null &&
            temperatureN(context, adapter.AdapterIndex, EdgeTemperature, out var odnTemperature) == AdlOk)
        {
            temperature = Temperature(odnTemperature);
        }

        if (!temperature.HasValue && temperature6 is not null &&
            temperature6(context, adapter.AdapterIndex, out var od6Temperature) == AdlOk)
        {
            temperature = Temperature(od6Temperature);
        }

        float? power = null;
        if (power6 is not null &&
            power6(context, adapter.AdapterIndex, AsicTotalPower, out var fixedPointPower) == AdlOk)
        {
            var watts = fixedPointPower / 256f;
            if (float.IsFinite(watts) && watts >= 0 && watts <= 2000)
                power = watts;
        }

        var electricalSource = power.HasValue && voltage.HasValue
            ? "AMD ASIC power - AMD core voltage"
            : power.HasValue
                ? "AMD ASIC power - core voltage unavailable"
                : voltage.HasValue
                    ? "AMD core voltage - ASIC power unavailable"
                    : "AMD electrical telemetry unavailable";

        return new GpuTelemetry(
            temperature,
            load,
            voltage,
            power,
            string.IsNullOrWhiteSpace(adapter.AdapterName) ? "AMD Radeon GPU" : adapter.AdapterName.Trim(),
            "AMD Radeon driver telemetry",
            electricalSource,
            physicalIndex);
    }

    private static float? Percentage(int value) => value is >= 0 and <= 100 ? value : null;

    private static float? Millivolts(int value)
    {
        var volts = value / 1000f;
        return float.IsFinite(volts) && volts is >= 0.05f and <= 3f ? volts : null;
    }

    private static float? Temperature(int value)
    {
        var celsius = value > 250 ? value / 1000f : value;
        return float.IsFinite(celsius) && celsius is >= -30 and <= 150 ? celsius : null;
    }

    private static T? GetExport<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr MemoryAllocate(int size);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MainControlCreate(MemoryAllocate callback, int enumerateConnectedAdapters, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MainControlDestroy(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AdapterCountGet(IntPtr context, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AdapterInfoGet(IntPtr context, IntPtr info, int inputSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PerformanceStatusNGet(IntPtr context, int adapterIndex, ref PerformanceStatusN status);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TemperatureNGet(IntPtr context, int adapterIndex, int temperatureType, out int temperature);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int PerformanceStatus6Get(IntPtr context, int adapterIndex, ref PerformanceStatus6 status);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Temperature6Get(IntPtr context, int adapterIndex, out int temperature);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CurrentPower6Get(IntPtr context, int adapterIndex, int powerType, out int currentValue);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PnpString;
        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceStatusN
    {
        public int CoreClock;
        public int MemoryClock;
        public int DcefClock;
        public int GfxClock;
        public int UvdClock;
        public int VceClock;
        public int GpuActivityPercent;
        public int CurrentCorePerformanceLevel;
        public int CurrentMemoryPerformanceLevel;
        public int CurrentDcefPerformanceLevel;
        public int CurrentGfxPerformanceLevel;
        public int UvdPerformanceLevel;
        public int VcePerformanceLevel;
        public int CurrentBusSpeed;
        public int CurrentBusLanes;
        public int MaximumBusLanes;
        public int Vddc;
        public int Vddci;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceStatus6
    {
        public int EngineClock;
        public int MemoryClock;
        public int ActivityPercent;
        public int CurrentPerformanceLevel;
        public int CurrentBusSpeed;
        public int CurrentBusLanes;
        public int MaximumBusLanes;
        public int ExtensionValue;
        public int ExtensionMask;
    }
}
