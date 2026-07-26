using System.IO;
using System.Text.Json;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class DriveTemperatureHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _path;
    private readonly Dictionary<string, DriveTemperatureRecord> _records;

    public DriveTemperatureHistoryService() : this(GetDefaultPath())
    {
    }

    internal DriveTemperatureHistoryService(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("A storage folder is required.", nameof(path)));
        _records = Load();
    }

    public float? Observe(StorageDeviceSnapshot drive)
    {
        lock (_syncRoot)
        {
            var key = CreateKey(drive);
            _records.TryGetValue(key, out var existing);
            var maximum = ValidTemperature(existing?.MaximumCelsius);
            maximum = Higher(maximum, ValidTemperature(drive.TemperatureMaximum));
            maximum = Higher(maximum, ValidTemperature(drive.Temperature));

            if (!maximum.HasValue)
                return null;

            if (existing is null || maximum.Value > existing.MaximumCelsius)
            {
                _records[key] = new DriveTemperatureRecord(
                    drive.DisplayName,
                    NormalizedSerial(drive.SerialNumber),
                    maximum.Value,
                    DateTime.UtcNow);
                Save();
            }

            return maximum;
        }
    }

    private Dictionary<string, DriveTemperatureRecord> Load()
    {
        try
        {
            var records = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, DriveTemperatureRecord>>(
                    File.ReadAllText(_path), JsonOptions)
                : null;
            return records is null
                ? new Dictionary<string, DriveTemperatureRecord>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DriveTemperatureRecord>(records, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, DriveTemperatureRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var temporary = _path + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records, JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string CreateKey(StorageDeviceSnapshot drive)
    {
        var serial = NormalizedSerial(drive.SerialNumber);
        return serial is not null
            ? $"serial:{serial}"
            : $"device:{drive.DeviceId.Trim()}|{drive.DisplayName.Trim()}|{drive.SizeBytes?.ToString() ?? "unknown"}";
    }

    private static string? NormalizedSerial(string? value)
    {
        var serial = value?.Trim();
        return string.IsNullOrWhiteSpace(serial) ||
               serial.Equals("Not reported", StringComparison.OrdinalIgnoreCase) ||
               serial.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : serial.ToUpperInvariant();
    }

    private static float? ValidTemperature(float? value) =>
        value is > 0 and <= 150 ? value : null;

    private static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemPulse",
        "drive-temperature-records.json");

    private static float? Higher(float? left, float? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return Math.Max(left.Value, right.Value);
    }

    private sealed record DriveTemperatureRecord(
        string DisplayName,
        string? SerialNumber,
        float MaximumCelsius,
        DateTime UpdatedUtc);
}
