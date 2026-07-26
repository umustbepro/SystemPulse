using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using Microsoft.Win32;
using SystemPulse.Services.PawnIo;

namespace SystemPulse.Services;

internal sealed class OverclockService
{
    private readonly IntelCpuTuningService _intelCpuTuning = new();
    private const string IntelXtuUrl = "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html";
    private const string AmdRyzenMasterUrl = "https://www.amd.com/en/products/software/ryzen-master.html";
    private const string AmdSoftwareUrl = "https://www.amd.com/en/products/software/adrenalin.html";
    private const string IntelGraphicsUrl = "https://www.intel.com/content/www/us/en/products/docs/discrete-gpus/arc/software/overview.html";

    public async Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken = default)
    {
        var cpuName = ReadCpuName();
        var cpuVendor = VendorFromName(cpuName);
        var gpuName = ReadGpuName();
        var gpuVendor = VendorFromName(gpuName);
        var cpuTool = FindCpuTool(cpuVendor);
        var gpuTool = FindGpuTool(gpuVendor);
        var intelCpuState = cpuVendor.Equals("Intel", StringComparison.OrdinalIgnoreCase)
            ? await Task.Run(() => _intelCpuTuning.Detect(cpuName), cancellationToken)
            : null;

        var capabilities = new OverclockCapabilities(
            cpuName,
            cpuVendor,
            cpuTool.Path,
            cpuTool.Url,
            cpuTool.Label,
            intelCpuState?.Status ?? CpuBackendMessage(cpuVendor, cpuTool.Path),
            gpuName,
            gpuVendor,
            gpuTool.Path,
            gpuTool.Url,
            gpuTool.Label,
            GpuBackendMessage(gpuVendor, gpuTool.Path),
            null,
            0,
            false, 0, 0, 0,
            false, 0, 0, 0,
            false, 0, 0, 0,
            false, 0, 0, 0)
        {
            IntelCpuState = intelCpuState,
            CanSetCpuCoreClock = intelCpuState?.CanSetRatio == true,
            CpuCoreMinimum = (intelCpuState?.MinimumRatio ?? 0) * 100d,
            CpuCoreMaximum = (intelCpuState?.MaximumRatio ?? 0) * 100d,
            CpuCoreCurrent = (intelCpuState?.CurrentRatio ?? 0) * 100d,
            CanSetCpuPower = intelCpuState?.CanSetPower == true,
            CpuPowerMinimum = intelCpuState?.MinimumPowerWatts ?? 0,
            CpuPowerMaximum = intelCpuState?.MaximumPowerWatts ?? 0,
            CpuPowerCurrent = intelCpuState?.CurrentPowerWatts ?? 0
        };

        if (!gpuVendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return capabilities;

        var nvidiaSmi = FindNvidiaSmi();
        if (nvidiaSmi is null)
            return capabilities with { GpuBackend = "NVIDIA driver found, but nvidia-smi is unavailable. Controls remain locked." };

        var query = await RunAsync(
            nvidiaSmi,
            [
                "--query-gpu=index,name,power.limit,power.default_limit,power.min_limit,power.max_limit,clocks.current.graphics,clocks.max.graphics,clocks.current.memory,clocks.max.memory",
                "--format=csv,noheader,nounits"
            ],
            cancellationToken);
        if (!query.Success)
            return capabilities with { GpuBackend = $"NVIDIA tuning query failed: {query.Message}" };

        var firstLine = query.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var values = firstLine?.Split(',', StringSplitOptions.TrimEntries);
        if (values is null || values.Length < 10 || !int.TryParse(values[0], out var gpuIndex))
            return capabilities with { GpuBackend = "NVIDIA returned an unsupported tuning-data format." };

        var currentPower = Number(values[2]);
        var defaultPower = Number(values[3]);
        var minimumPower = Number(values[4]);
        var maximumPower = Number(values[5]);
        var currentCore = Number(values[6]);
        var maximumCore = Number(values[7]);
        var currentMemory = Number(values[8]);
        var maximumMemory = Number(values[9]);

        var canPower = Positive(currentPower, minimumPower, maximumPower) && maximumPower > minimumPower;
        var canCore = Positive(currentCore, maximumCore);
        var canMemory = Positive(currentMemory, maximumMemory);

        return capabilities with
        {
            GpuName = string.IsNullOrWhiteSpace(values[1]) ? gpuName : values[1],
            GpuBackend = "NVIDIA driver control · supported values are applied through nvidia-smi",
            NvidiaSmiPath = nvidiaSmi,
            NvidiaGpuIndex = gpuIndex,
            CanSetGpuPower = canPower,
            GpuPowerMinimum = canPower ? minimumPower : 0,
            GpuPowerMaximum = canPower ? maximumPower : 0,
            GpuPowerCurrent = canPower ? currentPower : 0,
            CanSetGpuCoreClock = canCore,
            GpuCoreMinimum = canCore ? Math.Max(210, Math.Min(currentCore, maximumCore) * 0.5) : 0,
            GpuCoreMaximum = canCore ? Math.Max(currentCore, maximumCore) : 0,
            GpuCoreCurrent = canCore ? currentCore : 0,
            CanSetGpuMemoryClock = canMemory,
            GpuMemoryMinimum = canMemory ? Math.Max(405, Math.Min(currentMemory, maximumMemory) * 0.5) : 0,
            GpuMemoryMaximum = canMemory ? Math.Max(currentMemory, maximumMemory) : 0,
            GpuMemoryCurrent = canMemory ? currentMemory : 0,
            CanSetGpuVoltage = false,
            GpuVoltageMinimum = 0,
            GpuVoltageMaximum = 0,
            GpuVoltageCurrent = 0,
            GpuPowerDefault = canPower && defaultPower > 0 ? defaultPower : currentPower
        };
    }

    public Task<OverclockActionResult> ApplyCpuAsync(
        OverclockCapabilities capabilities,
        OverclockProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (capabilities.IntelCpuState is null)
            return Task.FromResult(new OverclockActionResult(false, "This CPU does not expose a direct SystemPulse tuning backend."));

        return Task.Run(() =>
        {
            var result = _intelCpuTuning.Apply(capabilities.IntelCpuState, profile.CoreClockMhz, profile.PowerLimitWatts);
            return new OverclockActionResult(result.Success, result.Message);
        }, cancellationToken);
    }

    public Task<OverclockActionResult> ResetCpuAsync(
        OverclockCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        if (capabilities.IntelCpuState is null)
            return Task.FromResult(new OverclockActionResult(false, "This CPU does not expose a direct SystemPulse tuning backend."));

        return Task.Run(() =>
        {
            var result = _intelCpuTuning.Reset(capabilities.IntelCpuState);
            return new OverclockActionResult(result.Success, result.Message);
        }, cancellationToken);
    }

    public async Task<OverclockActionResult> ApplyGpuAsync(
        OverclockCapabilities capabilities,
        OverclockProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (capabilities.NvidiaSmiPath is null)
            return new(false, "This GPU does not expose a writable SystemPulse tuning backend. Open its official vendor tuner instead.");

        if ((capabilities.CanSetGpuPower && !InRange(profile.PowerLimitWatts, capabilities.GpuPowerMinimum, capabilities.GpuPowerMaximum)) ||
            (capabilities.CanSetGpuCoreClock && !InRange(profile.CoreClockMhz, capabilities.GpuCoreMinimum, capabilities.GpuCoreMaximum)) ||
            (capabilities.CanSetGpuMemoryClock && !InRange(profile.MemoryClockMhz, capabilities.GpuMemoryMinimum, capabilities.GpuMemoryMaximum)) ||
            (capabilities.CanSetGpuVoltage && !InRange(profile.VoltageMillivolts, capabilities.GpuVoltageMinimum, capabilities.GpuVoltageMaximum)))
            return new(false, "A tuning value is outside the range reported by the vendor driver.");

        var operations = new List<string[]>();
        if (capabilities.CanSetGpuPower)
            operations.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-pl", profile.PowerLimitWatts.ToString("0.##", CultureInfo.InvariantCulture)]);
        if (capabilities.CanSetGpuCoreClock)
        {
            var core = ((int)Math.Round(profile.CoreClockMhz)).ToString(CultureInfo.InvariantCulture);
            operations.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-lgc", $"{core},{core}"]);
        }
        if (capabilities.CanSetGpuMemoryClock)
        {
            var memory = ((int)Math.Round(profile.MemoryClockMhz)).ToString(CultureInfo.InvariantCulture);
            operations.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-lmc", $"{memory},{memory}"]);
        }

        if (operations.Count == 0)
            return new(false, "The NVIDIA driver did not report any writable tuning controls for this GPU.");

        foreach (var arguments in operations)
        {
            var result = await RunAsync(capabilities.NvidiaSmiPath, arguments, cancellationToken);
            if (result.Success)
                continue;

            _ = await ResetGpuAsync(capabilities, CancellationToken.None);
            return new(false, $"The driver rejected a tuning value and SystemPulse restored defaults. {result.Message}");
        }

        var voltageNote = capabilities.CanSetGpuVoltage
            ? string.Empty
            : " Voltage was left driver-controlled because NVIDIA does not expose a supported writable voltage command.";
        return new(true, $"The supported GPU power and clock targets were applied.{voltageNote}");
    }

    public async Task<OverclockActionResult> ResetGpuAsync(
        OverclockCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        if (capabilities.NvidiaSmiPath is null)
            return new(false, "Open the official vendor tuner to restore this GPU's factory profile.");

        var resets = new List<string[]>();
        if (capabilities.CanSetGpuCoreClock)
            resets.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-rgc"]);
        if (capabilities.CanSetGpuMemoryClock)
            resets.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-rmc"]);
        if (capabilities.CanSetGpuPower && capabilities.GpuPowerDefault > 0)
            resets.Add(["-i", capabilities.NvidiaGpuIndex.ToString(), "-pl", capabilities.GpuPowerDefault.ToString("0.##", CultureInfo.InvariantCulture)]);

        var failures = new List<string>();
        foreach (var arguments in resets)
        {
            var result = await RunAsync(capabilities.NvidiaSmiPath, arguments, cancellationToken);
            if (!result.Success)
                failures.Add(result.Message);
        }

        return failures.Count == 0
            ? new(true, "GPU clocks and power limit were restored to driver defaults.")
            : new(false, $"The driver only completed part of the reset: {string.Join(" · ", failures.Distinct())}");
    }

    public static OverclockActionResult OpenVendorTuner(string? path, string url, string label)
    {
        try
        {
            var target = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : url;
            _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return new(true, !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? $"Opened {label}."
                : $"Opened the official {label} download page.");
        }
        catch (Exception exception)
        {
            return new(false, $"{label} could not be opened: {exception.Message}");
        }
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, string.Empty, "Windows could not start the vendor control interface.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(false, string.Empty, "The vendor driver command timed out.");
            }

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return process.ExitCode == 0
                ? new(true, output, output)
                : new(false, output, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception exception)
        {
            return new(false, string.Empty, exception.Message);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static string ReadGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(item => Convert.ToString(item["Name"])?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .OrderByDescending(item => item!.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                           item.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                           item.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                           item.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault() ?? "GPU";
        }
        catch { return "GPU"; }
    }

    private static string VendorFromName(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
        name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
        name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";

    private static VendorTool FindCpuTool(string vendor) => vendor switch
    {
        "AMD" => Tool("AMD Ryzen Master", AmdRyzenMasterUrl,
            @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe",
            @"C:\Program Files\AMD\RyzenMaster\AMD Ryzen Master.exe"),
        "Intel" => Tool("Intel XTU", IntelXtuUrl,
            @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Client\XtuUiLauncher.exe",
            @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Client\XtuUi.exe"),
        _ => new("Vendor CPU tuner", IntelXtuUrl, null)
    };

    private static VendorTool FindGpuTool(string vendor) => vendor switch
    {
        "AMD" => Tool("AMD Software: Adrenalin Edition", AmdSoftwareUrl,
            @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe"),
        "Intel" => Tool("Intel graphics tuning software", IntelGraphicsUrl,
            @"C:\Program Files\Intel\Intel Arc Control\ArcControl.exe",
            @"C:\Program Files\Intel\Intel Graphics Software\IntelGraphicsSoftware.exe"),
        "NVIDIA" => new("NVIDIA driver controls", "https://www.nvidia.com/software/nvidia-app/", null),
        _ => new("Vendor GPU tuner", IntelGraphicsUrl, null)
    };

    private static VendorTool Tool(string label, string url, params string[] paths) =>
        new(label, url, paths.FirstOrDefault(File.Exists));

    private static string CpuBackendMessage(string vendor, string? path) => vendor switch
    {
        "AMD" => path is null ? "Install AMD Ryzen Master for capability-checked CPU tuning." : "AMD Ryzen Master detected · CPU values must be applied through AMD's supported interface.",
        "Intel" => path is null ? "Install Intel XTU; CPU tuning requires a supported unlocked processor and chipset." : "Intel XTU detected · CPU values must be applied through Intel's supported interface.",
        _ => "No supported CPU tuning interface was detected."
    };

    private static string GpuBackendMessage(string vendor, string? path) => vendor switch
    {
        "AMD" => path is null ? "Install AMD Software for ADLX-backed Radeon tuning." : "AMD Software detected · use its ADLX-backed tuning controls.",
        "Intel" => path is null ? "Install Intel graphics software for supported Arc tuning." : "Intel graphics tuning software detected.",
        "NVIDIA" => "Detecting NVIDIA driver tuning capabilities…",
        _ => "No supported GPU tuning interface was detected."
    };

    private static string? FindNvidiaSmi() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
    }.FirstOrDefault(File.Exists);

    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : 0;

    private static bool Positive(params double[] values) => values.All(value => value > 0 && double.IsFinite(value));

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private sealed record VendorTool(string Label, string Url, string? Path);
    private sealed record CommandResult(bool Success, string Output, string Message);
}

internal sealed record OverclockCapabilities(
    string CpuName,
    string CpuVendor,
    string? CpuToolPath,
    string CpuToolUrl,
    string CpuToolLabel,
    string CpuBackend,
    string GpuName,
    string GpuVendor,
    string? GpuToolPath,
    string GpuToolUrl,
    string GpuToolLabel,
    string GpuBackend,
    string? NvidiaSmiPath,
    int NvidiaGpuIndex,
    bool CanSetGpuCoreClock,
    double GpuCoreMinimum,
    double GpuCoreMaximum,
    double GpuCoreCurrent,
    bool CanSetGpuMemoryClock,
    double GpuMemoryMinimum,
    double GpuMemoryMaximum,
    double GpuMemoryCurrent,
    bool CanSetGpuVoltage,
    double GpuVoltageMinimum,
    double GpuVoltageMaximum,
    double GpuVoltageCurrent,
    bool CanSetGpuPower,
    double GpuPowerMinimum,
    double GpuPowerMaximum,
    double GpuPowerCurrent)
{
    public double GpuPowerDefault { get; init; } = GpuPowerCurrent;
    public IntelCpuTuningState? IntelCpuState { get; init; }
    public bool CanSetCpuCoreClock { get; init; }
    public double CpuCoreMinimum { get; init; }
    public double CpuCoreMaximum { get; init; }
    public double CpuCoreCurrent { get; init; }
    public bool CanSetCpuMemoryClock { get; init; }
    public bool CanSetCpuVoltage { get; init; }
    public bool CanSetCpuPower { get; init; }
    public double CpuPowerMinimum { get; init; }
    public double CpuPowerMaximum { get; init; }
    public double CpuPowerCurrent { get; init; }
}

internal sealed record OverclockProfile(
    double CoreClockMhz,
    double MemoryClockMhz,
    double VoltageMillivolts,
    double PowerLimitWatts);

internal sealed record OverclockActionResult(bool Success, string Message);
