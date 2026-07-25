using System.IO;
using SystemPulse.Models;

namespace SystemPulse.Services;

internal sealed class StorageCleanupService
{
    private const long ReviewMinimumBytes = 100L * 1024 * 1024;
    private const int MaximumTemporaryFiles = 50_000;
    private const int MaximumReviewFiles = 250;
    private static readonly TimeSpan TemporaryMinimumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReviewMinimumAge = TimeSpan.FromDays(183);
    private static readonly HashSet<string> ExcludedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery",
        "System Volume Information", "$Recycle.Bin", "WindowsApps", "WpSystem", "MSOCache"
    };

    public IReadOnlyList<CleanupDriveInfo> GetDrives()
    {
        var drives = new List<CleanupDriveInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
                drives.Add(new CleanupDriveInfo(
                    drive.RootDirectory.FullName,
                    $"{drive.Name.TrimEnd('\\')} · {label}",
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch
            {
                // A drive can disappear while Windows is enumerating it.
            }
        }

        return drives.OrderBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public CleanupScanResult Scan(string driveRoot)
    {
        var normalizedRoot = NormalizeDirectory(driveRoot);
        var temporaryFiles = new List<CleanupCandidate>();
        var reviewFiles = new List<CleanupCandidate>();
        var skippedLocations = 0;
        var now = DateTime.UtcNow;
        var temporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tempRoot in GetTemporaryRoots(normalizedRoot))
        {
            foreach (var path in EnumerateFilesSafe(tempRoot, skipProtectedRootFolders: false, ref skippedLocations))
            {
                if (temporaryFiles.Count >= MaximumTemporaryFiles)
                    break;
                if (!TryReadCandidate(path, isTemporary: true, out var candidate) ||
                    now - candidate.LastActivity.ToUniversalTime() < TemporaryMinimumAge)
                    continue;
                temporaryFiles.Add(candidate);
                temporaryPaths.Add(candidate.FullPath);
            }
        }

        foreach (var scanRoot in GetReviewRoots(normalizedRoot))
        {
            foreach (var path in EnumerateFilesSafe(scanRoot, skipProtectedRootFolders: true, ref skippedLocations))
            {
                if (reviewFiles.Count >= MaximumReviewFiles || temporaryPaths.Contains(path))
                    continue;
                if (!TryReadCandidate(path, isTemporary: false, out var candidate) ||
                    candidate.SizeBytes < ReviewMinimumBytes ||
                    now - candidate.LastActivity.ToUniversalTime() < ReviewMinimumAge)
                    continue;
                reviewFiles.Add(candidate);
            }
        }

        return new CleanupScanResult(
            temporaryFiles.OrderByDescending(file => file.SizeBytes).ToList(),
            reviewFiles.OrderByDescending(file => file.SizeBytes).Take(MaximumReviewFiles).ToList(),
            skippedLocations);
    }

    public CleanupDeleteResult Delete(CleanupCandidate candidate)
    {
        try
        {
            if (!File.Exists(candidate.FullPath))
                return new CleanupDeleteResult(false, "File no longer exists.");

            if (candidate.IsTemporary)
            {
                var attributes = File.GetAttributes(candidate.FullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return new CleanupDeleteResult(false, "Skipped a redirected temporary file.");
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(candidate.FullPath, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(candidate.FullPath);
            return new CleanupDeleteResult(true, "Deleted.");
        }
        catch (Exception exception)
        {
            return new CleanupDeleteResult(false, exception.Message);
        }
    }

    public CleanupDeleteResult DeleteApprovedReview(CleanupCandidate candidate)
    {
        if (candidate.IsTemporary || !WillDeleteContainingFolder(candidate))
            return Delete(candidate);

        var parent = Path.GetDirectoryName(candidate.FullPath)!;
        try
        {
            if (!File.Exists(candidate.FullPath))
                return new CleanupDeleteResult(false, "File no longer exists.");
            if (ContainsReparsePoint(parent))
            {
                var fileOnly = Delete(candidate);
                return fileOnly.Success
                    ? new CleanupDeleteResult(true, "Deleted the EXE only; its folder contains a redirected item and was kept.")
                    : fileOnly;
            }

            ClearReadOnlyAttributes(parent);
            Directory.Delete(parent, recursive: true);
            return new CleanupDeleteResult(true, $"Deleted the containing folder: {parent}", true);
        }
        catch (Exception exception)
        {
            return new CleanupDeleteResult(false, $"The containing folder could not be deleted: {exception.Message}");
        }
    }

    public bool WillDeleteContainingFolder(CleanupCandidate candidate)
    {
        if (candidate.IsTemporary || !Path.GetExtension(candidate.FullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var parent = Path.GetDirectoryName(candidate.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return false;
        try
        {
            var normalizedParent = NormalizeDirectory(parent);
            return !GetProtectedDeleteBoundaries()
                .Select(NormalizeDirectory)
                .Any(boundary => boundary.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetProtectedDeleteBoundaries()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(profile, "Downloads"),
            Path.GetPathRoot(profile) ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            foreach (var file in directoryInfo.EnumerateFiles())
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            foreach (var child in directoryInfo.EnumerateDirectories())
                pending.Push(child.FullName);
        }
        return false;
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static IReadOnlyList<string> GetTemporaryRoots(string driveRoot)
    {
        var candidates = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
        };

        return candidates
            .Where(Directory.Exists)
            .Select(NormalizeDirectory)
            .Where(path => IsOnDrive(path, driveRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> GetReviewRoots(string driveRoot)
    {
        var userProfile = NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (IsOnDrive(userProfile, driveRoot))
        {
            var personalRoots = new[]
            {
                Path.Combine(userProfile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            return personalRoots.Where(Directory.Exists).Select(NormalizeDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return Directory.Exists(driveRoot) ? new[] { driveRoot } : Array.Empty<string>();
    }

    private static IEnumerable<string> EnumerateFilesSafe(
        string root,
        bool skipProtectedRootFolders,
        ref int skippedLocations)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] directoryFiles;
            string[] childDirectories;
            try
            {
                directoryFiles = Directory.GetFiles(directory);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                skippedLocations++;
                continue;
            }

            files.AddRange(directoryFiles);
            foreach (var child in childDirectories)
            {
                try
                {
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        (skipProtectedRootFolders && IsProtectedRootDirectory(info, root)))
                        continue;
                    pending.Push(child);
                }
                catch
                {
                    skippedLocations++;
                }
            }
        }

        return files;
    }

    private static bool IsProtectedRootDirectory(DirectoryInfo directory, string scanRoot) =>
        NormalizeDirectory(directory.Parent?.FullName ?? string.Empty).Equals(
            NormalizeDirectory(scanRoot), StringComparison.OrdinalIgnoreCase) &&
        ExcludedRootDirectories.Contains(directory.Name);

    private static bool TryReadCandidate(string path, bool isTemporary, out CleanupCandidate candidate)
    {
        candidate = default!;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var lastActivity = info.LastWriteTime > info.LastAccessTime ? info.LastWriteTime : info.LastAccessTime;
            candidate = new CleanupCandidate(info.FullName, info.Length, lastActivity, isTemporary);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static bool IsOnDrive(string path, string driveRoot) =>
        string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(driveRoot), StringComparison.OrdinalIgnoreCase);
}
