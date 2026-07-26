namespace SystemPulse.Models;

public sealed class AppSettings
{
    public bool AlertsEnabled { get; set; } = true;
    public bool CpuAlertsEnabled { get; set; } = true;
    public bool GpuAlertsEnabled { get; set; } = true;
    public bool StorageTemperatureAlertsEnabled { get; set; } = true;
    public bool StorageHealthAlertsEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public int RefreshSeconds { get; set; } = 2;
    public int CpuTemperatureAlert { get; set; } = 90;
    public int GpuTemperatureAlert { get; set; } = 88;
    public int StorageTemperatureAlert { get; set; } = 70;
    public int HistoryRetentionDays { get; set; } = 7;
}

public sealed record ProcessTelemetrySnapshot(
    int ProcessId,
    string Name,
    double CpuPercent,
    ulong WorkingSetBytes,
    ulong ReadBytesPerSecond,
    ulong WriteBytesPerSecond);

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    string Status,
    string Addresses,
    long LinkSpeedBitsPerSecond,
    ulong ReceivedBytesPerSecond,
    ulong SentBytesPerSecond,
    ulong TotalReceivedBytes,
    ulong TotalSentBytes);

public sealed record HistorySample(
    DateTime Timestamp,
    float? CpuTemperature,
    float? CpuLoad,
    float? GpuTemperature,
    float? GpuLoad,
    float? MemoryLoad,
    float? StorageTemperature,
    float? StorageLoad);

public sealed record MonitorAlert(string Key, string Title, string Message, DateTime Timestamp);
