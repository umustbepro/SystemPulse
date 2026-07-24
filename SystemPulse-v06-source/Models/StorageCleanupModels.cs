namespace SystemPulse.Models;

public sealed record CleanupDriveInfo(
    string RootPath,
    string DisplayName,
    long TotalBytes,
    long FreeBytes);

public sealed record CleanupCandidate(
    string FullPath,
    long SizeBytes,
    DateTime LastActivity,
    bool IsTemporary);

public sealed record CleanupScanResult(
    IReadOnlyList<CleanupCandidate> TemporaryFiles,
    IReadOnlyList<CleanupCandidate> ReviewFiles,
    int SkippedLocations);

public sealed record CleanupDeleteResult(bool Success, string Message);
