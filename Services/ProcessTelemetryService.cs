using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class ProcessTelemetryService
{
    private readonly Dictionary<int, PreviousSample> _previous = new();
    private readonly Dictionary<string, ProcessCategory> _categoryByExecutable = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRead = DateTime.UtcNow;

    public IReadOnlyList<ProcessTelemetrySnapshot> Read()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max((now - _lastRead).TotalSeconds, 0.1);
        _lastRead = now;
        var current = new Dictionary<int, PreviousSample>();
        var items = new List<ProcessTelemetrySnapshot>();
        var gpuUsage = ReadGpuUsage();

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
                    var name = process.ProcessName;
                    var startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    var executablePath = TryGetExecutablePath(process);
                    var category = Classify(name, executablePath, process.MainWindowHandle != IntPtr.Zero);
                    gpuUsage.TryGetValue(process.Id, out var gpuPercent);
                    items.Add(new ProcessTelemetrySnapshot(process.Id, name, startTimeUtcTicks, cpuPercent, gpuPercent, (ulong)Math.Max(process.WorkingSet64, 0), readRate, writeRate, category));
                }
                catch
                {
                    // Protected and terminating processes are expected to be unreadable.
                }
            }
        }

        _previous.Clear();
        foreach (var pair in current) _previous[pair.Key] = pair.Value;
        return items.OrderByDescending(item => item.CpuPercent).ThenByDescending(item => item.WorkingSetBytes).Take(120).ToList();
    }

    private ProcessCategory Classify(string name, string? executablePath, bool hasWindow)
    {
        var cacheKey = string.IsNullOrWhiteSpace(executablePath) ? $"name:{name}" : executablePath;
        if (_categoryByExecutable.TryGetValue(cacheKey, out var cached))
            return cached;

        if (IsGameExecutable(name, executablePath))
            return _categoryByExecutable[cacheKey] = ProcessCategory.Game;
        if (IsKnownApplication(name))
            return _categoryByExecutable[cacheKey] = ProcessCategory.App;

        // Window ownership can change while a process runs, so do not cache this fallback.
        return hasWindow ? ProcessCategory.App : ProcessCategory.System;
    }

    private static bool IsGameExecutable(string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsGameLauncher(name))
            return false;

        var normalized = path.Replace('/', '\\');
        return ContainsAny(normalized,
            "\\steamapps\\common\\",
            "\\Epic Games\\",
            "\\GOG Games\\",
            "\\GOG Galaxy\\Games\\",
            "\\XboxGames\\",
            "\\Riot Games\\",
            "\\EA Games\\",
            "\\Origin Games\\",
            "\\Ubisoft Game Launcher\\games\\");
    }

    private static bool IsGameLauncher(string name) => ContainsAny(name,
        "steam", "epicgameslauncher", "goggalaxy", "riotclient", "eadesktop",
        "origin", "upc", "ubisoftconnect", "battle.net", "gamingservices");

    private static bool IsKnownApplication(string name) => ContainsAny(name,
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "discord",
        "slack", "teams", "spotify", "telegram", "whatsapp", "signal", "zoom",
        "obs", "vlc", "notepad", "code", "devenv", "explorer", "photoshop",
        "lightroom", "blender", "steam", "epicgameslauncher", "goggalaxy",
        "riotclient", "eadesktop", "origin", "ubisoftconnect", "battle.net");

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static Dictionary<int, double> ReadGpuUsage()
    {
        var usage = new Dictionary<int, double>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var name = Convert.ToString(item["Name"]) ?? string.Empty;
                    if (!ContainsAny(name, "engtype_3D", "engtype_Compute", "engtype_VideoDecode", "engtype_VideoProcessing"))
                        continue;

                    var match = Regex.Match(name, @"(?:^|_)pid_(?<pid>\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var processId) || processId <= 0)
                        continue;

                    var value = Convert.ToDouble(item["UtilizationPercentage"] ?? 0d);
                    if (!double.IsFinite(value) || value <= 0)
                        continue;
                    usage[processId] = Math.Clamp(usage.GetValueOrDefault(processId) + value, 0, 100);
                }
            }
        }
        catch
        {
            // Per-process GPU counters are optional; CPU, memory, and disk telemetry remain available.
        }

        return usage;
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
