using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace SystemPulse.Services.PawnIo;

internal static class PawnIoInstaller
{
    private const string ExpectedInstallerSha256 =
        "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

    public static bool IsInstalled()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
                writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static Task<PawnIoInstallResult> EnsureInstalledAsync() =>
        IsInstalled()
            ? Task.FromResult(new PawnIoInstallResult(true, false, "PawnIO is already installed."))
            : InstallAsync();

    public static async Task<PawnIoInstallResult> InstallAsync()
    {
        try
        {
            var installer = BundledToolExtractor.Resolve(
                Path.Combine("PawnIO", "Installer", "PawnIO_setup.exe"),
                "SystemPulse.Bundled.PawnIO_setup.exe");

            if (!File.Exists(installer))
                return new PawnIoInstallResult(false, false, "The bundled PawnIO installer is missing.");

            await using (var stream = File.OpenRead(installer))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actualHash.Equals(ExpectedInstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new PawnIoInstallResult(
                        false,
                        false,
                        "PawnIO setup failed its integrity check and was not executed.");
                }
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-install -silent",
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return new PawnIoInstallResult(false, false, "Windows could not start PawnIO setup.");

            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                0 => new PawnIoInstallResult(true, false, "PawnIO was installed automatically."),
                3010 => new PawnIoInstallResult(true, true, "PawnIO was installed; Windows must be restarted."),
                _ => new PawnIoInstallResult(
                    false,
                    false,
                    $"PawnIO setup returned Windows error {process.ExitCode}.")
            };
        }
        catch (Exception exception)
        {
            return new PawnIoInstallResult(false, false, $"PawnIO installation failed: {exception.Message}");
        }
    }
}

internal sealed record PawnIoInstallResult(bool Success, bool RebootRequired, string Message);
