using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SystemPulse.Services;

internal sealed class GpuTelemetryReader
{
    private readonly string? _nvidiaSmi = FindNvidiaSmi();
    private readonly string _fallbackName = ReadDisplayAdapterName();
    private readonly NvidiaVoltageReader _voltage = new();
    private readonly AmdGpuTelemetryReader _amd = new();

    public GpuTelemetry Read()
    {
        if (_nvidiaSmi is null)
            return _amd.Read() ?? GpuTelemetry.Unavailable(_fallbackName, "Windows display adapter");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _nvidiaSmi,
                Arguments = "--query-gpu=index,name,temperature.gpu,utilization.gpu,power.draw --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
                return _amd.Read() ?? GpuTelemetry.Unavailable(_fallbackName, "Windows display adapter");

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1800))
            {
                process.Kill(entireProcessTree: true);
                return _amd.Read() ?? GpuTelemetry.Unavailable(_fallbackName, "NVIDIA driver query timed out");
            }

            var readings = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseLine)
                .Where(item => item is not null)
                .Cast<GpuTelemetry>()
                .ToArray();

            if (readings.Length == 0)
                return _amd.Read() ?? GpuTelemetry.Unavailable(_fallbackName, "NVIDIA driver telemetry unavailable");

            var hottest = readings.OrderByDescending(item => item.Temperature ?? float.MinValue).First();
            var voltage = _voltage.Read(hottest.PhysicalIndex);
            return hottest with
            {
                Voltage = voltage.Voltage,
                Source = $"NVIDIA driver telemetry · {readings.Length} GPU(s)",
                ElectricalSource = hottest.PowerWatts.HasValue
                    ? $"NVIDIA board power · {voltage.Source}"
                    : voltage.Source
            };
        }
        catch
        {
            return _amd.Read() ?? GpuTelemetry.Unavailable(_fallbackName, "Windows display adapter");
        }
    }

    private static GpuTelemetry? ParseLine(string line)
    {
        var columns = line.Split(',');
        if (columns.Length < 5 || !int.TryParse(columns[0].Trim(), out var physicalIndex))
            return null;

        var name = string.Join(",", columns[1..^3]).Trim();
        var temperature = ParseReading(columns[^3], 1, 130);
        var load = ParseReading(columns[^2], 0, 100);
        var power = ParseReading(columns[^1], 0, 2000);

        return new GpuTelemetry(
            temperature,
            load,
            null,
            power,
            string.IsNullOrWhiteSpace(name) ? "NVIDIA GPU" : name,
            "NVIDIA driver telemetry",
            power.HasValue ? "NVIDIA board power" : "NVIDIA electrical telemetry unavailable",
            physicalIndex);
    }

    private static float? ParseReading(string text, float minimum, float maximum) =>
        float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        float.IsFinite(value) && value >= minimum && value <= maximum
            ? value
            : null;

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ReadDisplayAdapterName()
    {
        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; NativeMethods.EnumDisplayDevices(null, index, ref display, 0); index++)
        {
            const int attachedToDesktop = 0x00000001;
            if ((display.StateFlags & attachedToDesktop) != 0 && !string.IsNullOrWhiteSpace(display.DeviceString))
                return display.DeviceString;
            display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }
        return "GPU not detected";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
    }
}

internal sealed record GpuTelemetry(
    float? Temperature,
    float? Load,
    float? Voltage,
    float? PowerWatts,
    string Name,
    string Source,
    string ElectricalSource,
    int PhysicalIndex)
{
    public static GpuTelemetry Unavailable(string name, string source) =>
        new(null, null, null, null, name, source, "GPU electrical telemetry unavailable", -1);
}
