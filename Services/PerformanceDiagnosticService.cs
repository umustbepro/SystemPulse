using System.Text.RegularExpressions;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal static class PerformanceDiagnosticService
{
    public static PerformanceDiagnosticResult Evaluate(
        SensorSnapshot snapshot,
        FrameApplicationSnapshot? application,
        IReadOnlyList<ProcessTelemetrySnapshot> processes)
    {
        var issues = new List<string>();
        var suggestions = new Dictionary<string, PerformanceSuggestion>(StringComparer.OrdinalIgnoreCase);
        var gameName = application is { FrameTimeMilliseconds: > 0 } && GameProcessClassifier.IsGame(application)
            ? CleanApplicationName(application.DisplayName)
            : null;
        var resourceHogs = ResourceHogSummary.Create(processes, application?.ProcessId);
        var gamePerformanceIssue = false;

        var hottestDriveLoad = snapshot.StoragePerformance
            .Where(item => item.Load.HasValue)
            .OrderByDescending(item => item.Load)
            .FirstOrDefault();
        var storageBusy = hottestDriveLoad?.Load is >= 90;
        var memoryPressure = snapshot.MemoryLoad is >= 90;
        var cpuThermal = snapshot.CpuTemperature is >= 90;
        var gpuThermal = snapshot.GpuTemperature is >= 88 || snapshot.GpuHotspotTemperature is >= 105;
        var cpuBusy = snapshot.CpuLoad is >= 90;
        var gpuBusy = snapshot.GpuLoad is >= 95;

        if (application is { FrameTimeMilliseconds: > 0 } active && gameName is not null)
        {
            var fps = 1000f / active.FrameTimeMilliseconds.Value;
            var stuttering = active.StutterPercent is >= 4 &&
                             active.FrameTimeMaximumMilliseconds is > 0 &&
                             active.FrameTimeMaximumMilliseconds > active.FrameTimeMilliseconds * 1.45f;
            var unstablePacing = active.FrameTimeDeviationMilliseconds is >= 4 &&
                                 active.FrameTimeP95Milliseconds > active.FrameTimeMilliseconds * 1.25f;
            var lowFrameRate = fps < 45 || active.FrameTimeP95Milliseconds is > 33.3f;
            gamePerformanceIssue = stuttering || unstablePacing || lowFrameRate;

            var cause = DetermineLikelyCause(cpuThermal, gpuThermal, storageBusy, memoryPressure, gpuBusy, cpuBusy);
            if (stuttering)
            {
                issues.Add($"{gameName} is stuttering ({active.StutterPercent:0}% irregular frames) · {cause}");
                AddSuggestion(suggestions, GameFramePacingSuggestion(gameName, resourceHogs));
            }
            else if (unstablePacing)
            {
                issues.Add($"{gameName} has unstable frame pacing · {cause}");
                AddSuggestion(suggestions, GameFramePacingSuggestion(gameName, resourceHogs));
            }

            if (lowFrameRate)
            {
                issues.Add($"{gameName} is averaging about {fps:0} FPS · {cause}");
                AddSuggestion(suggestions, GamePerformanceSuggestion(gameName, gpuBusy, cpuBusy, resourceHogs));
            }

            if (stuttering || unstablePacing || lowFrameRate)
                AddCauseSuggestion(suggestions, gameName, cpuThermal, gpuThermal, storageBusy, memoryPressure, gpuBusy, cpuBusy, resourceHogs);
        }

        if (cpuBusy || cpuThermal)
        {
            issues.Add($"CPU pressure: {Percent(snapshot.CpuLoad)} · {Temperature(snapshot.CpuTemperature)}");
            AddSuggestion(suggestions, CpuSuggestion(gameName, cpuThermal, resourceHogs.CpuInstruction));
        }

        if (gpuBusy || gpuThermal)
        {
            issues.Add($"GPU pressure: {Percent(snapshot.GpuLoad)} · core {Temperature(snapshot.GpuTemperature)} · hotspot {Temperature(snapshot.GpuHotspotTemperature)}");
            AddSuggestion(suggestions, GpuSuggestion(gameName, gpuThermal, resourceHogs.GpuInstruction));
        }

        if (memoryPressure)
        {
            issues.Add($"Memory pressure is high ({Percent(snapshot.MemoryLoad)})");
            AddSuggestion(suggestions, MemorySuggestion(gameName, resourceHogs.MemoryInstruction));
        }

        var deviceNames = snapshot.StorageDevices
            .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
        foreach (var device in snapshot.StoragePerformance.Where(device => device.Load is >= 95))
        {
            var name = deviceNames.TryGetValue(device.DeviceId, out var displayName)
                ? displayName
                : $"Physical disk {device.DeviceId}";
            issues.Add($"{name} is under heavy load ({device.Load:0}%)");
            AddSuggestion(suggestions, StorageLoadSuggestion(gameName, name, resourceHogs.DiskInstruction));
        }

        foreach (var drive in snapshot.StorageDevices.Where(device =>
                     device.Temperature is >= 70 ||
                     device.Health.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
                     device.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(drive.Temperature is >= 70
                ? $"{drive.DisplayName} is hot ({drive.Temperature:0} °C)"
                : $"{drive.DisplayName} reports {drive.Health.ToLowerInvariant()} health");
            AddSuggestion(suggestions, StorageHealthSuggestion(drive.DisplayName, drive.Health, drive.Temperature));
        }

        if (issues.Count > 0)
        {
            var closeCandidates = resourceHogs.SelectRelevant(
                cpuBusy || cpuThermal || gamePerformanceIssue,
                gpuBusy || gpuThermal || gamePerformanceIssue,
                memoryPressure || gamePerformanceIssue,
                storageBusy || gamePerformanceIssue);
            return new PerformanceDiagnosticResult(
                gamePerformanceIssue ? "GAME PERFORMANCE ALERT" : "SYSTEM ALERT",
                string.Join(Environment.NewLine, issues.Distinct(StringComparer.OrdinalIgnoreCase).Take(3)),
                gameName is null ? "System suggestions" : $"Suggestions for {gameName}",
                suggestions.Values.ToArray(),
                closeCandidates,
                true);
        }

        if (gameName is not null && application?.FrameTimeMilliseconds is > 0)
        {
            var fps = 1000f / application.FrameTimeMilliseconds.Value;
            return new PerformanceDiagnosticResult(
                "GAME MONITORING",
                $"{gameName}: about {fps:0} FPS with stable frame pacing",
                $"Suggestions for {gameName}",
                Array.Empty<PerformanceSuggestion>(),
                Array.Empty<ResourceProcessCandidate>(),
                false);
        }

        return new PerformanceDiagnosticResult(
            "LIVE MONITORING",
            "System is nominal",
            "System suggestions",
            Array.Empty<PerformanceSuggestion>(),
            Array.Empty<ResourceProcessCandidate>(),
            false);
    }

    private static void AddCauseSuggestion(
        IDictionary<string, PerformanceSuggestion> suggestions,
        string gameName,
        bool cpuThermal,
        bool gpuThermal,
        bool storageBusy,
        bool memoryPressure,
        bool gpuBusy,
        bool cpuBusy,
        ResourceHogSummary resourceHogs)
    {
        if (cpuThermal || cpuBusy)
            AddSuggestion(suggestions, CpuSuggestion(gameName, cpuThermal, resourceHogs.CpuInstruction));
        if (gpuThermal || gpuBusy)
            AddSuggestion(suggestions, GpuSuggestion(gameName, gpuThermal, resourceHogs.GpuInstruction));
        if (memoryPressure)
            AddSuggestion(suggestions, MemorySuggestion(gameName, resourceHogs.MemoryInstruction));
        if (storageBusy)
            AddSuggestion(suggestions, StorageLoadSuggestion(gameName, "the drive containing the game", resourceHogs.DiskInstruction));
    }

    private static string DetermineLikelyCause(
        bool cpuThermal,
        bool gpuThermal,
        bool storageBusy,
        bool memoryPressure,
        bool gpuBusy,
        bool cpuBusy)
    {
        if (cpuThermal) return "likely CPU thermal throttling";
        if (gpuThermal) return "likely GPU thermal throttling";
        if (storageBusy) return "storage activity is interrupting asset delivery";
        if (memoryPressure) return "memory pressure is causing paging";
        if (gpuBusy) return "the GPU is the likely limit";
        if (cpuBusy) return "the CPU is the likely limit";
        return "frame pacing, shaders, overlays, or the game engine are the likely cause";
    }

    private static PerformanceSuggestion GameFramePacingSuggestion(string game, ResourceHogSummary resourceHogs) => new(
        $"Stabilize frame pacing in {game}",
        "Irregular frame delivery feels like stutter even when the average FPS looks acceptable.",
        [
            $"Set an FPS cap for {game} two or three frames below the monitor refresh rate.",
            resourceHogs.GpuInstruction ?? resourceHogs.CpuInstruction ?? "Try exclusive fullscreen and disable Discord, Steam, Xbox Game Bar, and GPU performance overlays one at a time.",
            "Let shader compilation finish after a game or driver update; verify the game files if stutter began unexpectedly.",
            "Use one sync method at a time: the game's V-Sync, G-Sync/FreeSync, or the driver limiter."
        ]);

    private static PerformanceSuggestion GamePerformanceSuggestion(string game, bool gpuBusy, bool cpuBusy, ResourceHogSummary resourceHogs) => new(
        $"Improve frame rate in {game}",
        gpuBusy
            ? "The graphics processor is close to full utilization, so graphics settings are the best first target."
            : cpuBusy
                ? "The processor is heavily loaded, so simulation-heavy settings are the best first target."
                : "The frame time is high without one fully saturated component, so start with the most expensive settings.",
        gpuBusy
            ? [
                $"In {game}, lower ray tracing, resolution scale, shadows, reflections, volumetrics, and anti-aliasing first.",
                resourceHogs.GpuInstruction ?? "Close unused GPU-accelerated background applications and overlays.",
                "Enable DLSS, FSR, or XeSS Quality mode when the game supports it.",
                "Cap FPS to a stable value the GPU can maintain instead of allowing frequent swings."
              ]
            : [
                $"In {game}, lower view distance, crowd density, simulation quality, physics, and world-detail settings first.",
                resourceHogs.CpuInstruction ?? "Close CPU-heavy browsers, recording tools, and background updates.",
                "Use a sensible FPS cap to leave processor headroom for difficult scenes."
              ]);

    private static PerformanceSuggestion CpuSuggestion(string? game, bool thermal, string? closeInstruction) => new(
        thermal ? "Reduce CPU temperature and throttling" : "Reduce CPU pressure",
        thermal
            ? "The CPU is hot enough that reduced boost clocks may be causing inconsistent frame times."
            : "High total CPU activity can delay game logic and frame submission.",
        [
            thermal ? "Check cooler mounting, pump/fan operation, dust buildup, and case airflow; restore unstable overclocks to baseline." : closeInstruction ?? "Close CPU-heavy background programs and pause scans, downloads, or video encoding.",
            game is null ? "Reduce CPU-intensive application activity." : $"In {game}, lower view distance, crowd density, simulation, physics, and object-detail settings.",
            "Use an FPS cap that leaves some CPU headroom instead of targeting the highest possible average."
        ]);

    private static PerformanceSuggestion GpuSuggestion(string? game, bool thermal, string? closeInstruction) => new(
        thermal ? "Reduce GPU temperature and throttling" : "Reduce GPU pressure",
        thermal
            ? "High core or hotspot temperature can reduce GPU boost clocks and create uneven performance."
            : "The GPU is close to full utilization and has little headroom for complex frames.",
        [
            thermal ? "Check GPU fans, dust, airflow, and fan curve; return unstable voltage or clock changes to baseline." : closeInstruction ?? "Close GPU-accelerated background applications and overlays.",
            game is null ? "Reduce graphics workload or resolution." : $"In {game}, lower ray tracing, resolution scale, shadows, reflections, volumetrics, and anti-aliasing.",
            "Enable DLSS, FSR, or XeSS when supported, then cap FPS to a stable target."
        ]);

    private static PerformanceSuggestion MemorySuggestion(string? game, string? closeInstruction) => new(
        "Reduce memory pressure",
        "When physical memory is nearly full, Windows may move data to disk and cause long frame-time spikes.",
        [
            closeInstruction ?? "Close unused browsers, virtual machines, and memory-heavy background applications.",
            game is null ? "Reduce memory-heavy application settings." : $"In {game}, lower texture quality or texture-pool size if VRAM and system RAM are both heavily used.",
            "Keep the Windows page file set to System managed and leave free space on its drive."
        ]);

    private static PerformanceSuggestion StorageLoadSuggestion(string? game, string drive, string? closeInstruction) => new(
        $"Reduce activity on {drive}",
        "Heavy drive activity can delay game assets and cause traversal or loading stutter.",
        [
            closeInstruction ?? "Pause downloads, cloud synchronization, antivirus scans, indexing, and file copies while playing.",
            game is null ? "Wait for the current drive activity to finish." : $"Verify {game}'s files and move it to an SSD or NVMe drive if it is currently on a hard drive.",
            "Keep adequate free space available and check the Storage page for health or temperature warnings."
        ]);

    private static PerformanceSuggestion StorageHealthSuggestion(string drive, string health, float? temperature) => new(
        $"Check {drive}",
        temperature is >= 70
            ? "The drive is unusually hot, which can reduce performance and reliability."
            : $"Windows reports {health.ToLowerInvariant()} health for this drive.",
        [
            "Back up important files before troubleshooting a drive health warning.",
            temperature is >= 70 ? "Improve airflow around the drive and check that heatsinks or thermal pads are installed correctly." : "Review SMART and error counters on the Storage page, then run the drive manufacturer's diagnostic tool.",
            "Avoid installing or moving games to a drive that reports reliability warnings."
        ]);

    private static void AddSuggestion(
        IDictionary<string, PerformanceSuggestion> suggestions,
        PerformanceSuggestion suggestion) => suggestions.TryAdd(suggestion.Title, suggestion);

    private static string CleanApplicationName(string displayName) =>
        Regex.Replace(displayName, @"\s*\(\d+\)\s*$", string.Empty).Trim();

    private sealed record ResourceHogSummary(
        IReadOnlyList<ResourceProcessCandidate> CpuCandidates,
        IReadOnlyList<ResourceProcessCandidate> GpuCandidates,
        IReadOnlyList<ResourceProcessCandidate> MemoryCandidates,
        IReadOnlyList<ResourceProcessCandidate> DiskCandidates)
    {
        private static readonly string[] ProtectedNames =
        [
            "SYSTEMPULSE", "EXPLORER", "DWM", "SEARCHHOST", "STARTMENUEXPERIENCEHOST",
            "SHELLEXPERIENCEHOST", "APPLICATIONFRAMEHOST", "TASKHOST", "RUNTIMEBROKER",
            "STEAM", "EPICGAMESLAUNCHER", "GOGGALAXY", "RIOTCLIENT", "EADESKTOP",
            "ORIGIN", "UBISOFTCONNECT", "UPLAY", "BATTLE.NET", "GAMINGSERVICES"
        ];

        public string? CpuInstruction => Instruction("CPU-heavy", CpuCandidates);
        public string? GpuInstruction => Instruction("GPU-accelerated", GpuCandidates);
        public string? MemoryInstruction => Instruction("memory-heavy", MemoryCandidates);
        public string? DiskInstruction => Instruction("drive-active", DiskCandidates);

        public static ResourceHogSummary Create(
            IReadOnlyList<ProcessTelemetrySnapshot> processes,
            int? activeProcessId)
        {
            var eligible = processes
                .Where(process => process.Category == ProcessCategory.App &&
                                  process.ProcessId != Environment.ProcessId &&
                                  process.ProcessId != activeProcessId &&
                                  process.StartTimeUtcTicks > 0 &&
                                  !ProtectedNames.Any(name => process.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            IReadOnlyList<ResourceProcessCandidate> Select(
                Func<ProcessTelemetrySnapshot, bool> predicate,
                Func<ProcessTelemetrySnapshot, double> order) => eligible
                .Where(predicate)
                .OrderByDescending(order)
                .Take(5)
                .Select(ToCandidate)
                .ToArray();

            return new ResourceHogSummary(
                Select(process => process.CpuPercent >= 1.5, process => process.CpuPercent),
                Select(process => process.GpuPercent >= 1, process => process.GpuPercent),
                Select(process => process.WorkingSetBytes >= 256UL * 1024 * 1024, process => process.WorkingSetBytes),
                Select(process => process.ReadBytesPerSecond + process.WriteBytesPerSecond >= 1024UL * 1024,
                    process => process.ReadBytesPerSecond + process.WriteBytesPerSecond));
        }

        public IReadOnlyList<ResourceProcessCandidate> SelectRelevant(
            bool includeCpu,
            bool includeGpu,
            bool includeMemory,
            bool includeDisk)
        {
            var selected = new List<ResourceProcessCandidate>();
            if (includeCpu) selected.AddRange(CpuCandidates);
            if (includeGpu) selected.AddRange(GpuCandidates);
            if (includeMemory) selected.AddRange(MemoryCandidates);
            if (includeDisk) selected.AddRange(DiskCandidates);
            return selected
                .GroupBy(candidate => candidate.ProcessId)
                .Select(group => group.First())
                .Take(6)
                .ToArray();
        }

        private static ResourceProcessCandidate ToCandidate(ProcessTelemetrySnapshot process)
        {
            var metrics = new List<string>();
            if (process.CpuPercent >= 0.1) metrics.Add($"CPU {process.CpuPercent:0.0}%");
            if (process.GpuPercent >= 0.1) metrics.Add($"GPU {process.GpuPercent:0.0}%");
            metrics.Add($"RAM {FormatBytes(process.WorkingSetBytes)}");
            var diskRate = process.ReadBytesPerSecond + process.WriteBytesPerSecond;
            if (diskRate >= 128UL * 1024) metrics.Add($"disk {FormatRate(diskRate)}");

            return new ResourceProcessCandidate(
                process.ProcessId,
                process.StartTimeUtcTicks,
                $"{FriendlyName(process.Name)} (PID {process.ProcessId})",
                string.Join(" · ", metrics));
        }

        private static string? Instruction(string resource, IReadOnlyList<ResourceProcessCandidate> candidates) =>
            candidates.Count == 0
                ? null
                : $"{resource} apps detected: {string.Join(", ", candidates.Take(4).Select(candidate => candidate.Name))}. Use End task only for apps you are not using, and save work first.";

        private static string FriendlyName(string name)
        {
            var normalized = name.ToLowerInvariant();
            if (normalized.Contains("chrome")) return "Google Chrome";
            if (normalized.Contains("msedge")) return "Microsoft Edge";
            if (normalized.Contains("firefox")) return "Mozilla Firefox";
            if (normalized.Contains("brave")) return "Brave";
            if (normalized.Contains("discord")) return "Discord";
            if (normalized.Contains("chatgpt")) return "ChatGPT";
            if (normalized.Contains("obs")) return "OBS Studio";
            if (normalized.Contains("teams")) return "Microsoft Teams";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        private static string FormatBytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / Math.Pow(1024, 3):0.0} GB"
            : $"{bytes / Math.Pow(1024, 2):0} MB";

        private static string FormatRate(ulong bytes) => bytes >= 1024UL * 1024
            ? $"{bytes / Math.Pow(1024, 2):0.0} MB/s"
            : $"{bytes / 1024d:0} KB/s";
    }

    private static string Percent(float? value) => value.HasValue ? $"{value:0}%" : "unavailable";
    private static string Temperature(float? value) => value.HasValue ? $"{value:0} °C" : "unavailable";
}

internal sealed record PerformanceDiagnosticResult(
    string Headline,
    string Message,
    string SuggestionsTitle,
    IReadOnlyList<PerformanceSuggestion> Suggestions,
    IReadOnlyList<ResourceProcessCandidate> CloseCandidates,
    bool IsAlert);
