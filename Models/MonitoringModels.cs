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
    public string? IgnoredUpdateVersion { get; set; }
}

public sealed record ProcessTelemetrySnapshot(
    int ProcessId,
    string Name,
    long StartTimeUtcTicks,
    double CpuPercent,
    double GpuPercent,
    ulong WorkingSetBytes,
    ulong ReadBytesPerSecond,
    ulong WriteBytesPerSecond,
    ProcessCategory Category);

public enum ProcessCategory
{
    System,
    App,
    Game
}

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
    float? GpuHotspotTemperature,
    float? GpuLoad,
    float? MemoryLoad,
    float? StorageTemperature,
    float? StorageLoad);

public sealed record MonitorAlert(string Key, string Title, string Message, DateTime Timestamp);

public sealed record PerformanceSuggestion(
    string Title,
    string Explanation,
    IReadOnlyList<string> Steps);

public sealed record ResourceProcessCandidate(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name,
    string ResourceSummary);
