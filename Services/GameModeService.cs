using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SystemPulse.Services;

internal sealed class GameModeService
{
    private const string GameBarKeyPath = @"Software\Microsoft\GameBar";
    private const string AutoGameModeValue = "AutoGameModeEnabled";
    private const string AllowGameModeValue = "AllowAutoGameMode";
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private static readonly Regex PowerSchemePattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemPulse",
        "game-mode-state.json");

    public bool IsEnabled => LoadState() is not null;

    public async Task<GameModeResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        if (LoadState() is not null)
            return new(true, "Game Mode is already active.");

        var activeScheme = await RunPowerCfgAsync(["/getactivescheme"], cancellationToken);
        var schemeGuid = PowerSchemePattern.Match(activeScheme.Output).Value;
        if (!activeScheme.Success || string.IsNullOrWhiteSpace(schemeGuid))
            return new(false, "Windows did not report the current power plan, so no settings were changed.");

        var state = new PersistedGameModeState(
            schemeGuid,
            ReadRegistryValue(AutoGameModeValue),
            ReadRegistryValue(AllowGameModeValue),
            true);

        try
        {
            SaveState(state);
            try
            {
                using var gameBar = Registry.CurrentUser.CreateSubKey(GameBarKeyPath, writable: true);
                if (gameBar is null)
                    throw new InvalidOperationException();
                gameBar.SetValue(AutoGameModeValue, 1, RegistryValueKind.DWord);
                gameBar.SetValue(AllowGameModeValue, 1, RegistryValueKind.DWord);
            }
            catch
            {
                // Windows Game Mode and Xbox gaming components can be disabled or
                // unavailable. The power-plan optimization remains independently usable.
                state = state with { WindowsGameModeApplied = false };
                SaveState(state);
            }

            var highPerformance = await RunPowerCfgAsync(["/setactive", "SCHEME_MIN"], cancellationToken);
            if (!highPerformance.Success)
                throw new InvalidOperationException("Windows did not allow the High performance power plan to be activated.");

            return state.WindowsGameModeApplied
                ? new(true, "High performance power and Windows Game Mode are active. Your previous settings are saved for restoration.")
                : new(true, "High performance power is active. Windows Game Mode was unavailable, so SystemPulse safely skipped that setting.");
        }
        catch (Exception exception)
        {
            await RestoreAsync(state, CancellationToken.None);
            DeleteState();
            return new(false, $"Game Mode could not be enabled: {exception.Message}");
        }
    }

    public async Task<GameModeResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        if (state is null)
            return new(true, "Normal Windows settings are already active.");

        try
        {
            var restored = await RestoreAsync(state, cancellationToken);
            if (!restored)
                return new(false, "Windows did not restore the previous power plan. The saved baseline was kept so you can try again.");

            DeleteState();
            return new(true, "Your previous power plan and Windows Game Mode settings were restored.");
        }
        catch (Exception exception)
        {
            return new(false, $"Normal settings could not be fully restored: {exception.Message}");
        }
    }

    public void SetSessionAwake(bool enabled) => _ = SetThreadExecutionState(enabled
        ? EsContinuous | EsSystemRequired | EsDisplayRequired
        : EsContinuous);

    private async Task<bool> RestoreAsync(PersistedGameModeState state, CancellationToken cancellationToken)
    {
        if (state.WindowsGameModeApplied)
        {
            RestoreRegistryValue(AutoGameModeValue, state.AutoGameModeEnabled);
            RestoreRegistryValue(AllowGameModeValue, state.AllowAutoGameMode);
        }
        var power = await RunPowerCfgAsync(["/setactive", state.PreviousPowerScheme], cancellationToken);
        return power.Success;
    }

    private static RegistryValueState ReadRegistryValue(string valueName)
    {
        try
        {
            using var gameBar = Registry.CurrentUser.OpenSubKey(GameBarKeyPath, writable: false);
            var value = gameBar?.GetValue(valueName);
            return value is null
                ? new(false, 0)
                : new(true, Convert.ToInt32(value));
        }
        catch
        {
            return new(false, 0);
        }
    }

    private static void RestoreRegistryValue(string valueName, RegistryValueState state)
    {
        using var gameBar = Registry.CurrentUser.CreateSubKey(GameBarKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows Game Mode settings could not be restored.");
        if (state.Existed)
            gameBar.SetValue(valueName, state.Value, RegistryValueKind.DWord);
        else
            gameBar.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private void SaveState(PersistedGameModeState state)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    private PersistedGameModeState? LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<PersistedGameModeState>(File.ReadAllText(_statePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void DeleteState()
    {
        try { File.Delete(_statePath); } catch { }
    }

    private static async Task<PowerCfgResult> RunPowerCfgAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, string.Empty);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = string.Join(Environment.NewLine, await outputTask, await errorTask).Trim();
            return new(process.ExitCode == 0, output);
        }
        catch
        {
            return new(false, string.Empty);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);

    private sealed record PersistedGameModeState(
        string PreviousPowerScheme,
        RegistryValueState AutoGameModeEnabled,
        RegistryValueState AllowAutoGameMode,
        bool WindowsGameModeApplied);
    private sealed record RegistryValueState(bool Existed, int Value);
    private sealed record PowerCfgResult(bool Success, string Output);
}

internal sealed record GameModeResult(bool Success, string Message);
