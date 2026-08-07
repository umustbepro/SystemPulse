using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class PresentMonFrameReader : IDisposable
{
    private const int SampleLimit = 45;
    private readonly object _syncRoot = new();
    private readonly Dictionary<int, FrameProcessSample> _samples = new();
    private Process? _process;
    private Dictionary<string, int>? _columns;
    private bool _disposed;

    public PresentMonFrameReader() => Start();

    public FrameTimeSample Read()
    {
        lock (_syncRoot)
        {
            EnsureRunning();
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(3);
            var foregroundProcessId = GetForegroundProcessId();
            var regularApplications = GetRegularApplications();
            var activeSamples = _samples
                .Where(item => item.Value.LastFrame >= cutoff && item.Value.FrameTimes.Count > 0)
                .ToDictionary(item => item.Key, item => item.Value);

            var applicationIds = regularApplications.Keys.Union(activeSamples.Keys).Distinct();
            var applications = applicationIds
                .Select(processId =>
                {
                    activeSamples.TryGetValue(processId, out var sample);
                    var displayName = regularApplications.TryGetValue(processId, out var regularName)
                        ? regularName
                        : $"{sample?.ProcessName ?? "Application"} ({processId})";
                    var statistics = sample is null
                        ? FrameStatistics.Unavailable
                        : CalculateStatistics(sample.FrameTimes);
                    return new FrameApplicationSnapshot(
                        processId,
                        displayName,
                        statistics.Average,
                        statistics.P95,
                        statistics.Maximum,
                        statistics.Deviation,
                        statistics.StutterPercent);
                })
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selectedApplication = applications.FirstOrDefault(item =>
                    item.ProcessId == foregroundProcessId && item.FrameTimeMilliseconds.HasValue)
                ?? applications.FirstOrDefault(item => item.FrameTimeMilliseconds.HasValue)
                ?? applications.FirstOrDefault(item => item.ProcessId == foregroundProcessId);
            return new FrameTimeSample(
                selectedApplication?.FrameTimeMilliseconds,
                selectedApplication?.DisplayName ?? "No active 3D presentation",
                applications);
        }
    }

    private static Dictionary<int, string> GetRegularApplications()
    {
        var applications = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero ||
                        string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        continue;

                    applications[process.Id] = $"{GetFriendlyApplicationName(process)} ({process.Id})";
                }
                catch
                {
                    // Processes can exit or become inaccessible while being enumerated.
                }
            }
        }

        return applications;
    }

    private static string GetFriendlyApplicationName(Process process)
    {
        var executableName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? process.ProcessName
            : $"{process.ProcessName}.exe";
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return executableName;

            var version = FileVersionInfo.GetVersionInfo(path);
            var productName = version.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(productName) &&
                !productName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase) &&
                !productName.Equals(executableName, StringComparison.OrdinalIgnoreCase))
                return $"{productName} · {executableName}";
        }
        catch
        {
            // Protected applications can still be identified by executable name.
        }

        return executableName;
    }

    private static FrameStatistics CalculateStatistics(IEnumerable<double> values)
    {
        var ordered = values.Where(value => double.IsFinite(value) && value > 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return FrameStatistics.Unavailable;

        var average = ordered.Average();
        var median = ordered[ordered.Length / 2];
        var p95 = ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1)];
        var maximum = ordered[^1];
        var deviation = Math.Sqrt(ordered.Average(value => Math.Pow(value - average, 2)));
        var stutterThreshold = Math.Max(median * 1.5, median + 6);
        var stutterPercent = ordered.Count(value => value >= stutterThreshold) * 100d / ordered.Length;

        return new FrameStatistics(
            (float)average,
            (float)p95,
            (float)maximum,
            (float)deviation,
            (float)stutterPercent);
    }

    private void EnsureRunning()
    {
        if (_disposed || _process is { HasExited: false })
            return;
        Start();
    }

    private void Start()
    {
        if (_disposed)
            return;

        var executable = Path.Combine(AppContext.BaseDirectory, "PresentMon", "PresentMon.exe");
        if (!File.Exists(executable))
            return;

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                Arguments = "--output_stdout --no_console_stats --v1_metrics --session_name SystemPulsePresentMon --stop_existing_session --exclude SystemPulse.exe --exclude dwm.exe --exclude explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
            return;

        var fields = ParseCsvLine(eventArgs.Data);
        lock (_syncRoot)
        {
            if (_columns is null)
            {
                _columns = fields
                    .Select((name, index) => (name, index))
                    .ToDictionary(item => item.name.Trim(), item => item.index, StringComparer.OrdinalIgnoreCase);
                return;
            }

            if (!TryRead(fields, "ProcessID", out var processText) ||
                !int.TryParse(processText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                !TryRead(fields, "msBetweenPresents", out var frameText) ||
                !double.TryParse(frameText, NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime) ||
                frameTime is <= 0 or > 250)
                return;

            var processName = TryRead(fields, "Application", out var application) && !string.IsNullOrWhiteSpace(application)
                ? application
                : $"Process {processId}";
            if (!_samples.TryGetValue(processId, out var sample))
            {
                sample = new FrameProcessSample(processName);
                _samples[processId] = sample;
            }

            sample.ProcessName = processName;
            sample.LastFrame = DateTime.UtcNow;
            sample.FrameTimes.Enqueue(frameTime);
            while (sample.FrameTimes.Count > SampleLimit)
                sample.FrameTimes.Dequeue();
        }
    }

    private bool TryRead(IReadOnlyList<string> fields, string column, out string value)
    {
        value = string.Empty;
        return _columns is not null && _columns.TryGetValue(column, out var index) && index < fields.Count &&
               (value = fields[index]) is not null;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static int GetForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return 0;
        _ = GetWindowThreadProcessId(window, out var processId);
        return unchecked((int)processId);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The capture process may already be shutting down.
            }
            _process?.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private sealed class FrameProcessSample(string processName)
    {
        public string ProcessName { get; set; } = processName;
        public DateTime LastFrame { get; set; }
        public Queue<double> FrameTimes { get; } = new();
    }

    private readonly record struct FrameStatistics(
        float? Average,
        float? P95,
        float? Maximum,
        float? Deviation,
        float? StutterPercent)
    {
        public static FrameStatistics Unavailable { get; } = new(null, null, null, null, null);
    }
}

internal sealed record FrameTimeSample(
    float? Milliseconds,
    string ProcessName,
    IReadOnlyList<FrameApplicationSnapshot> Applications);
