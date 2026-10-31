using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hammer5Git.Core.Services;

public static class Cs2InstallationLocator
{
    public static string? LocateCs2InstallPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string? steamInstallPath = LocateSteamInstallPath();
        if (string.IsNullOrWhiteSpace(steamInstallPath) || !Directory.Exists(steamInstallPath))
            return null;

        // 1. Steam 主库 steamapps\common\Counter-Strike Global Offensive
        string mainDefault = Path.Combine(steamInstallPath, "steamapps", "common", "Counter-Strike Global Offensive");
        if (IsValidCs2Directory(mainDefault))
            return mainDefault;

        string mainAcf = Path.Combine(steamInstallPath, "steamapps", "appmanifest_730.acf");
        if (File.Exists(mainAcf))
        {
            string? installDir = ParseAcfInstallDir(mainAcf);
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string candidate = Path.Combine(steamInstallPath, "steamapps", "common", installDir);
                if (IsValidCs2Directory(candidate))
                    return candidate;
            }
        }

        // 2. libraryfolders.vdf 各库（优先 apps 含 730 的）
        string vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            string altVdf = Path.Combine(steamInstallPath, "config", "libraryfolders.vdf");
            if (File.Exists(altVdf))
                vdfPath = altVdf;
        }

        var libraries = ParseLibraryFolders(vdfPath, "730");

        foreach (var lib in libraries)
        {
            string acfPath = Path.Combine(lib, "steamapps", "appmanifest_730.acf");
            if (File.Exists(acfPath))
            {
                string? installDir = ParseAcfInstallDir(acfPath);
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    string candidate = Path.Combine(lib, "steamapps", "common", installDir);
                    if (IsValidCs2Directory(candidate))
                        return candidate;
                }
            }

            string defaultCandidate = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive");
            if (IsValidCs2Directory(defaultCandidate))
                return defaultCandidate;
        }

        // 3. 兜底仍扫描所有库的 common\Counter-Strike Global Offensive
        foreach (var lib in libraries)
        {
            string fallbackCandidate = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive");
            if (IsValidCs2Directory(fallbackCandidate))
                return fallbackCandidate;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    public static string? LocateSteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // 1. HKCU\Software\Valve\Steam -> SteamPath
        string? path = TryReadRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (path != null)
            return path;

        // 2. HKLM\SOFTWARE\Valve\Steam -> InstallPath
        path = TryReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (path != null)
            return path;

        // 3. HKLM\SOFTWARE\WOW6432Node\Valve\Steam -> InstallPath
        path = TryReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (path != null)
            return path;

        // 4. 兜底 C:\Program Files (x86)\Steam
        const string fallback = @"C:\Program Files (x86)\Steam";
        if (Directory.Exists(fallback))
        {
            return Path.GetFullPath(fallback);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryReadRegistryPath(RegistryKey rootKey, string subKeyName, string valueName)
    {
        try
        {
            using var key = rootKey.OpenSubKey(subKeyName);
            var val = key?.GetValue(valueName) as string;
            if (!string.IsNullOrWhiteSpace(val) && Directory.Exists(val))
            {
                return Path.GetFullPath(val);
            }
        }
        catch
        {
            // Ignore registry read errors
        }
        return null;
    }

    public static List<string> ParseLibraryFolders(string vdfPath, string? targetAppId = "730")
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(vdfPath) || !File.Exists(vdfPath))
            return result;

        var prioritized = new List<string>();
        var others = new List<string>();

        try
        {
            var lines = File.ReadAllLines(vdfPath);
            int depth = 0;
            string? currentPath = null;
            bool currentHasTarget = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                // Modern format: "path" "..."
                var pathMatch = Regex.Match(line, @"^\s*""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                if (pathMatch.Success)
                {
                    currentPath = UnescapeVdfPath(pathMatch.Groups[1].Value);
                }

                // Legacy format at depth 1: "1" "D:\\SteamLibrary"
                if (depth == 1)
                {
                    var legacyMatch = Regex.Match(line, @"^\s*""\d+""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (legacyMatch.Success)
                    {
                        string legacyPath = UnescapeVdfPath(legacyMatch.Groups[1].Value);
                        if (!string.IsNullOrWhiteSpace(legacyPath) && !others.Contains(legacyPath, StringComparer.OrdinalIgnoreCase))
                        {
                            others.Add(legacyPath);
                        }
                    }
                }

                // Target app id check, e.g. "730" "..."
                if (!string.IsNullOrWhiteSpace(targetAppId))
                {
                    var appMatch = Regex.Match(line, @"^\s*""(\d+)""", RegexOptions.IgnoreCase);
                    if (appMatch.Success && string.Equals(appMatch.Groups[1].Value, targetAppId, StringComparison.OrdinalIgnoreCase))
                    {
                        currentHasTarget = true;
                    }
                }

                // Track depth
                foreach (char ch in line)
                {
                    if (ch == '{')
                    {
                        depth++;
                    }
                    else if (ch == '}')
                    {
                        depth--;
                        // When depth returns to 1, library block is closed
                        if (depth == 1 && !string.IsNullOrWhiteSpace(currentPath))
                        {
                            if (currentHasTarget)
                            {
                                if (!prioritized.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                                    prioritized.Add(currentPath);
                            }
                            else
                            {
                                if (!others.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                                    others.Add(currentPath);
                            }

                            currentPath = null;
                            currentHasTarget = false;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                if (currentHasTarget)
                {
                    if (!prioritized.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                        prioritized.Add(currentPath);
                }
                else
                {
                    if (!others.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                        others.Add(currentPath);
                }
            }

            foreach (var p in prioritized)
            {
                if (!result.Contains(p, StringComparer.OrdinalIgnoreCase))
                    result.Add(p);
            }
            foreach (var p in others)
            {
                if (!result.Contains(p, StringComparer.OrdinalIgnoreCase))
                    result.Add(p);
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return result;
    }

    public static string? ParseAcfInstallDir(string acfPath)
    {
        if (string.IsNullOrWhiteSpace(acfPath) || !File.Exists(acfPath))
            return null;

        try
        {
            foreach (var line in File.ReadLines(acfPath))
            {
                var match = Regex.Match(line, @"^\s*""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string dir = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(dir))
                        return dir;
                }
            }
        }
        catch
        {
            // Ignore file read or regex errors
        }

        return null;
    }

    public static bool IsValidCs2Directory(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return false;

        string binDir = Path.Combine(dirPath, "game", "bin", "win64");
        if (!Directory.Exists(binDir))
            return false;

        string cs2Exe = Path.Combine(binDir, "cs2.exe");
        string dmxConvertExe = Path.Combine(binDir, "dmxconvert.exe");

        return File.Exists(cs2Exe) || File.Exists(dmxConvertExe);
    }

    private static string UnescapeVdfPath(string val)
    {
        if (string.IsNullOrWhiteSpace(val))
            return string.Empty;

        val = val.Replace(@"\\", @"\");
        try
        {
            return Path.GetFullPath(val).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return val.TrimEnd('\\', '/');
        }
    }
}
