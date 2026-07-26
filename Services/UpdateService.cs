using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemPulse.Services;

public sealed class UpdateService : IDisposable
{
    private const string AssetName = "SystemPulse.exe";
    private readonly HttpClient _client;

    public UpdateService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SystemPulse", CurrentVersion.ToString(3)));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string Repository => Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GitHubRepository")?.Value
        ?? "umustbepro/SystemPulse";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ValidateRepository();
        using var response = await _client.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if ((int)response.StatusCode == 404)
            return UpdateCheckResult.Failed($"No published GitHub release was found for {Repository}.");

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var remoteVersion))
            return UpdateCheckResult.Failed($"The latest release tag '{tag}' is not a version such as v07.2, v.07.2, or v0.7.2.");

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => string.Equals(
                item.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            return UpdateCheckResult.Failed($"Release {tag} does not contain an asset named {AssetName}.");

        var release = new UpdateRelease(
            remoteVersion,
            tag,
            root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? string.Empty : string.Empty,
            asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
            asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null);

        return new UpdateCheckResult(true, IsNewerVersion(remoteVersion, CurrentVersion), release, string.Empty);
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(
            release.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength ?? release.SizeBytes;
        var destination = Path.Combine(Path.GetTempPath(), $"SystemPulse-update-{Guid.NewGuid():N}.exe");
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            var buffer = new byte[64 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (expectedLength > 0)
                    progress?.Report(received * 100d / expectedLength);
            }
        }

        try
        {
            ValidateDownloadedExecutable(destination, expectedLength, release.Digest);
            return destination;
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static void LaunchInstaller(string downloadedExecutable)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The running SystemPulse installation path is unavailable.");
        if (!File.Exists(downloadedExecutable))
            throw new FileNotFoundException("The downloaded SystemPulse update is unavailable.", downloadedExecutable);

        var startInfo = new ProcessStartInfo(downloadedExecutable)
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows could not start the update installer.");
    }

    public static async Task<UpdateApplyResult> ApplyDownloadedUpdateAsync(string target, int processId)
    {
        try
        {
            target = Path.GetFullPath(target);
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target) || !IsSystemPulseExecutable(target))
                return new UpdateApplyResult(false, "The SystemPulse installation target is invalid.");

            var source = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("The downloaded update path is unavailable."));
            if (!File.Exists(source) ||
                !Path.GetFileName(source).StartsWith("SystemPulse-update-", StringComparison.OrdinalIgnoreCase))
                return new UpdateApplyResult(false, "The temporary SystemPulse updater is invalid.");

            await EnsureProcessExitedAsync(processId, TimeSpan.FromSeconds(20));
            await StopOtherTargetProcessesAsync(target);

            var staged = target + ".update";
            var backup = target + ".previous";
            TryDelete(staged);
            TryDelete(backup);
            File.Copy(source, staged, true);
            await ReplaceTargetWithRetryAsync(staged, target, backup);

            var restart = new ProcessStartInfo(target) { UseShellExecute = true };
            restart.ArgumentList.Add("--cleanup-update");
            restart.ArgumentList.Add(source);
            restart.ArgumentList.Add(Environment.ProcessId.ToString());
            _ = Process.Start(restart) ?? throw new InvalidOperationException("The updated application could not be restarted.");
            return new UpdateApplyResult(true, "Update installed.");
        }
        catch (Exception exception)
        {
            return new UpdateApplyResult(false, $"The update could not be installed: {exception.Message}");
        }
    }

    public static async Task CleanupDownloadedUpdateAsync(string path, int updaterProcessId)
    {
        try
        {
            await WaitForProcessExitAsync(updaterProcessId, TimeSpan.FromSeconds(15));
            var fullPath = Path.GetFullPath(path);
            if (Path.GetFileName(fullPath).StartsWith("SystemPulse-update-", StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await DeleteWithRetryAsync(fullPath, TimeSpan.FromSeconds(12));
            var current = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(current))
            {
                await DeleteWithRetryAsync(current + ".previous", TimeSpan.FromSeconds(12));
                await DeleteWithRetryAsync(current + ".update", TimeSpan.FromSeconds(12));
            }
        }
        catch
        {
            // A stale temporary update can be removed by Windows' normal temp cleanup.
        }
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        if (processId <= 0)
            return;
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (ArgumentException) { }
    }

    private static async Task EnsureProcessExitedAsync(int processId, TimeSpan gracefulTimeout)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(gracefulTimeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
            // The initiating instance has already exited.
        }
    }

    private static async Task StopOtherTargetProcessesAsync(string target)
    {
        var normalizedTarget = Path.GetFullPath(target);
        foreach (var candidate in Process.GetProcesses())
        {
            using (candidate)
            {
                if (candidate.Id == Environment.ProcessId)
                    continue;

                string? candidatePath;
                try
                {
                    candidatePath = candidate.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(candidatePath) ||
                    !Path.GetFullPath(candidatePath).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var closeRequested = candidate.CloseMainWindow();
                    if (closeRequested)
                    {
                        using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        try
                        {
                            await candidate.WaitForExitAsync(graceful.Token);
                            continue;
                        }
                        catch (OperationCanceledException)
                        {
                            // A hidden or unresponsive duplicate instance must release the EXE.
                        }
                    }

                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                    // It exited between discovery and shutdown.
                }
            }
        }
    }

    private static async Task ReplaceTargetWithRetryAsync(string staged, string target, string backup)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                TryDelete(backup);
                try
                {
                    File.Replace(staged, target, backup, true);
                }
                catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
                {
                    MoveSwap(staged, target, backup);
                }
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                await Task.Delay(250);
            }
        }

        throw new IOException("The existing SystemPulse EXE remained locked and could not be replaced.", lastError);
    }

    private static void MoveSwap(string staged, string target, string backup)
    {
        File.Move(target, backup, true);
        try
        {
            File.Move(staged, target, true);
        }
        catch
        {
            if (File.Exists(backup))
                File.Move(backup, target, true);
            throw;
        }
    }

    private static void ValidateDownloadedExecutable(string path, long expectedLength, string? digest)
    {
        var info = new FileInfo(path);
        if (info.Length < 1024 * 1024 || (expectedLength > 0 && info.Length != expectedLength))
            throw new InvalidDataException("The downloaded executable is incomplete.");

        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                throw new InvalidDataException("GitHub did not return a valid Windows executable.");
        }

        if (!IsSystemPulseExecutable(path))
            throw new InvalidDataException("The GitHub asset is not a SystemPulse executable.");

        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return;
        using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(file));
        var expected = digest[7..].Trim();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The GitHub SHA-256 digest did not match the downloaded executable.");
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version();
        var normalized = value.Trim().TrimStart('v', 'V');
        var prerelease = normalized.IndexOfAny(['-', '+']);
        if (prerelease >= 0)
            normalized = normalized[..prerelease];

        var hadLeadingDot = normalized.StartsWith('.');
        normalized = normalized.TrimStart('.');
        var components = normalized.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length == 0 ||
            components.Any(component => !int.TryParse(component, out _)))
            return false;

        // Older SystemPulse releases used v.07.2 and v07.2 to mean v0.7.2.
        // Preserve normal semantic tags such as v0.7.2 and v10.1 unchanged.
        if (hadLeadingDot || (components[0].Length > 1 && components[0][0] == '0'))
        {
            components[0] = int.Parse(components[0]).ToString();
            normalized = "0." + string.Join('.', components);
        }
        else
        {
            normalized = string.Join('.', components);
        }

        return Version.TryParse(normalized, out version!);
    }

    private static bool IsNewerVersion(Version remote, Version current) =>
        NormalizeVersion(remote).CompareTo(NormalizeVersion(current)) > 0;

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static void ValidateRepository()
    {
        var parts = Repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("GitHubRepository must use the owner/repository format.");
    }

    private static bool IsSystemPulseExecutable(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.ProductName?.Equals("SystemPulse", StringComparison.OrdinalIgnoreCase) == true &&
                   version.FileDescription?.Equals("SystemPulse", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task DeleteWithRetryAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (File.Exists(path) && DateTime.UtcNow < deadline)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(250);
        }
        TryDelete(path);
    }

    public void Dispose() => _client.Dispose();
}

public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    string ReleasePage,
    string DownloadUrl,
    long SizeBytes,
    string? Digest);

public sealed record UpdateCheckResult(bool Success, bool IsUpdateAvailable, UpdateRelease? Release, string Error)
{
    public static UpdateCheckResult Failed(string error) => new(false, false, null, error);
}

public sealed record UpdateApplyResult(bool Success, string Message);
