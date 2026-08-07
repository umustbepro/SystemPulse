using System.Globalization;
using System.IO;
using System.Text;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class TelemetryHistoryService
{
    private const string Header = "timestamp,cpu_temperature_c,cpu_load_percent,gpu_temperature_c,gpu_hotspot_temperature_c,gpu_load_percent,memory_load_percent,storage_temperature_c,storage_load_percent";
    private readonly string _folder;
    private DateTime _lastWrite = DateTime.MinValue;

    public TelemetryHistoryService()
    {
        _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemPulse", "History");
        Directory.CreateDirectory(_folder);
    }

    public string Folder => _folder;

    public bool TryAppend(HistorySample sample, int retentionDays)
    {
        if (sample.Timestamp - _lastWrite < TimeSpan.FromSeconds(10))
            return false;

        _lastWrite = sample.Timestamp;
        var path = Path.Combine(_folder, $"telemetry-{sample.Timestamp:yyyy-MM-dd}.csv");
        var includesHotspot = !File.Exists(path);
        if (includesHotspot)
            File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(true));
        else
            includesHotspot = File.ReadLines(path).FirstOrDefault()?.Contains("gpu_hotspot_temperature_c", StringComparison.OrdinalIgnoreCase) == true;

        File.AppendAllText(path, (includesHotspot ? ToCsv(sample) : ToLegacyCsv(sample)) + Environment.NewLine, Encoding.UTF8);
        DeleteExpired(Math.Clamp(retentionDays, 1, 90));
        return true;
    }

    public void Export(string destination)
    {
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
        writer.WriteLine(Header);
        foreach (var file in Directory.EnumerateFiles(_folder, "telemetry-*.csv").OrderBy(path => path))
        {
            var first = true;
            foreach (var line in File.ReadLines(file))
            {
                if (first) { first = false; continue; }
                if (!string.IsNullOrWhiteSpace(line) && Parse(line) is { } sample)
                    writer.WriteLine(ToCsv(sample));
            }
        }
    }

    public IReadOnlyList<HistorySample> ReadRecent(int maximum = 120)
    {
        var lines = Directory.EnumerateFiles(_folder, "telemetry-*.csv")
            .OrderByDescending(path => path)
            .SelectMany(path => File.ReadLines(path).Skip(1).Reverse())
            .Take(maximum)
            .Reverse();
        return lines.Select(Parse).Where(sample => sample is not null).Cast<HistorySample>().ToList();
    }

    private void DeleteExpired(int retentionDays)
    {
        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(_folder, "telemetry-*.csv"))
            if (File.GetLastWriteTime(file) < cutoff)
                File.Delete(file);
    }

    private static string ToCsv(HistorySample value) => string.Join(',',
        value.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        Number(value.CpuTemperature), Number(value.CpuLoad), Number(value.GpuTemperature),
        Number(value.GpuHotspotTemperature), Number(value.GpuLoad), Number(value.MemoryLoad),
        Number(value.StorageTemperature), Number(value.StorageLoad));

    private static string ToLegacyCsv(HistorySample value) => string.Join(',',
        value.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        Number(value.CpuTemperature), Number(value.CpuLoad), Number(value.GpuTemperature),
        Number(value.GpuLoad), Number(value.MemoryLoad), Number(value.StorageTemperature), Number(value.StorageLoad));

    private static string Number(float? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static HistorySample? Parse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length is not (8 or 9) || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return null;
        return parts.Length == 9
            ? new HistorySample(timestamp, Float(parts[1]), Float(parts[2]), Float(parts[3]), Float(parts[4]), Float(parts[5]), Float(parts[6]), Float(parts[7]), Float(parts[8]))
            : new HistorySample(timestamp, Float(parts[1]), Float(parts[2]), Float(parts[3]), null, Float(parts[4]), Float(parts[5]), Float(parts[6]), Float(parts[7]));
    }

    private static float? Float(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
}
