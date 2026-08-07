using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SystemPulse.Models;

namespace SystemPulse.Services;

/// <summary>
/// Uses positive game evidence instead of treating every 3D-presenting program as a game.
/// This prevents browsers, chat clients, launchers, editors, and SystemPulse itself from
/// appearing in Live Game Monitoring.
/// </summary>
internal static class GameProcessClassifier
{
    private static readonly ConcurrentDictionary<string, bool> PathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HashSet<string>> WindowsRecordedGames = new(ReadWindowsRecordedGamePaths);

    private static readonly string[] ExcludedNames =
    [
        "SYSTEMPULSE", "CHATGPT", "OPENAI", "CHROME", "MSEDGE", "MICROSOFT EDGE",
        "FIREFOX", "BRAVE", "OPERA", "VIVALDI", "WATERFOX", "ARC BROWSER", "WEBVIEW",
        "DISCORD", "SLACK", "TEAMS", "ZOOM", "SPOTIFY", "TELEGRAM", "WHATSAPP", "SIGNAL",
        "OBS", "STREAMLABS", "VLC", "MEDIA PLAYER", "PLEX", "KODI",
        "STEAM CLIENT", "STEAMWEBHELPER", "EPIC GAMES LAUNCHER", "BATTLE.NET", "EADESKTOP",
        "EA APP", "UBISOFT CONNECT", "UPLAY", "RIOT CLIENT", "GOG GALAXY", "ROCKSTAR GAMES LAUNCHER",
        "VISUAL STUDIO", "DEVENV", "VS CODE", "CODE.EXE", "BLENDER", "UNITY EDITOR",
        "UNREAL EDITOR", "GODOT EDITOR", "PHOTOSHOP", "AFTER EFFECTS", "PREMIERE",
        "EXPLORER.EXE", "DWM.EXE", "SEARCHHOST", "STARTMENUEXPERIENCEHOST", "SHELLEXPERIENCEHOST"
    ];

    private static readonly string[] GamePathMarkers =
    [
        "\\STEAMAPPS\\COMMON\\", "\\EPIC GAMES\\", "\\GOG GAMES\\", "\\GOG GALAXY\\GAMES\\",
        "\\XBOXGAMES\\", "\\RIOT GAMES\\", "\\EA GAMES\\", "\\ORIGIN GAMES\\",
        "\\UBISOFT GAME LAUNCHER\\GAMES\\", "\\ROCKSTAR GAMES\\", "\\BETHESDA.NET LAUNCHER\\GAMES\\",
        "\\AMAZON GAMES\\LIBRARY\\", "\\ITCH\\APPS\\", "\\GAMES\\"
    ];

    private static readonly string[] GameRuntimeModules =
    [
        "UNITYPLAYER.DLL", "GAMEASSEMBLY.DLL", "STEAM_API.DLL", "STEAM_API64.DLL",
        "EOSSDK-WIN32-SHIPPING.DLL", "EOSSDK-WIN64-SHIPPING.DLL", "GALAXY.DLL", "GALAXY64.DLL",
        "GFSDK_SSAO_D3D11.WIN32.DLL", "GFSDK_SSAO_D3D11.WIN64.DLL"
    ];

    public static bool IsGame(FrameApplicationSnapshot application)
    {
        if (application.ProcessId <= 0 || ContainsAny(application.DisplayName.ToUpperInvariant(), ExcludedNames))
            return false;

        try
        {
            using var process = Process.GetProcessById(application.ProcessId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            path = Path.GetFullPath(path).Replace('/', '\\');
            var executableName = Path.GetFileName(path).ToUpperInvariant();
            if (ContainsAny(executableName, ExcludedNames))
                return false;

            return PathCache.GetOrAdd(path, _ => HasGameEvidence(process, path, executableName));
        }
        catch
        {
            // Strict classification intentionally avoids guessing when the executable cannot be inspected.
            return false;
        }
    }

    private static bool HasGameEvidence(Process process, string path, string executableName)
    {
        var upperPath = path.ToUpperInvariant();
        if (ContainsAny(upperPath, GamePathMarkers) || WindowsRecordedGames.Value.Contains(path))
            return true;

        if (executableName.Contains("-WIN64-SHIPPING", StringComparison.OrdinalIgnoreCase) ||
            executableName.Contains("-WIN32-SHIPPING", StringComparison.OrdinalIgnoreCase) ||
            executableName.Contains("-WINGDK-SHIPPING", StringComparison.OrdinalIgnoreCase) ||
            executableName.StartsWith("UE4GAME", StringComparison.OrdinalIgnoreCase) ||
            executableName.StartsWith("UE5GAME", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var moduleName = module.ModuleName?.ToUpperInvariant();
                if (moduleName is not null && GameRuntimeModules.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Some anti-cheat protected games block module enumeration; store/Windows evidence still works.
        }

        return false;
    }

    private static HashSet<string> ReadWindowsRecordedGamePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children is null)
                return paths;

            foreach (var childName in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(childName);
                if (child is null)
                    continue;

                foreach (var valueName in child.GetValueNames())
                {
                    if (child.GetValue(valueName) is not string value ||
                        !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { paths.Add(Path.GetFullPath(value).Replace('/', '\\')); } catch { }
                }
            }
        }
        catch
        {
            // Windows game records are an optional positive signal.
        }

        return paths;
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) => terms.Any(value.Contains);
}
