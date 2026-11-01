using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Hammer5Git.Core.Models;

namespace Hammer5Git.Core.Services;

public class Cs2EnvironmentService
{
    /// <summary>
    /// Built-in read-only/template addons shipped with CS2 Workshop Tools that should never be listed or monitored.
    /// </summary>
    public static readonly HashSet<string> BuiltInExcludedAddons = new(StringComparer.OrdinalIgnoreCase)
    {
        "addon_template",
        "cs_script_demo",
        "workshop_items",
        "core",
        "hlvr",
        "csgo",
        "_toolsautosave"
    };

    public static bool IsExcludedAddonName(string? addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return true;

        string trimmed = addonName.Trim();
        return trimmed.StartsWith('.') ||
               trimmed.StartsWith('_') ||
               BuiltInExcludedAddons.Contains(trimmed);
    }

    public bool ValidateAddonFolder(string folderPath, string cs2InstallPath, out string addonName, out string error)
    {
        addonName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            error = "所选文件夹不存在。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(cs2InstallPath) || !Directory.Exists(cs2InstallPath))
        {
            error = "未检测到有效的 CS2 安装目录，请确认 Steam 及 CS2 已正确安装。";
            return false;
        }

        string fullFolder = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expectedParent = Path.GetFullPath(Path.Combine(cs2InstallPath, "content", "csgo_addons")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string? actualParent = Path.GetDirectoryName(fullFolder);
        if (actualParent == null || !string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            error = $"所选项目必须位于 CS2 的 content/csgo_addons 目录下！\n\n当前选取: {fullFolder}\n要求目录: {expectedParent}";
            return false;
        }

        addonName = Path.GetFileName(fullFolder);
        if (IsExcludedAddonName(addonName))
        {
            error = $"项目【{addonName}】为 CS2 原生只读/模板目录，不可作为自定义项目纳管。";
            return false;
        }

        return true;
    }

    public bool ValidateRepoStructure(string repoDir, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(repoDir) || !Directory.Exists(repoDir))
        {
            error = "仓库目录不存在。";
            return false;
        }

        string contentDir = Path.Combine(repoDir, "content");
        if (!Directory.Exists(contentDir))
        {
            error = "仓库根目录下缺少【content】文件夹，不符合当前项目的规范要求。";
            return false;
        }

        try
        {
            var vmapFiles = Directory.GetFiles(repoDir, "*.vmap", SearchOption.AllDirectories);
            if (vmapFiles.Length == 0)
            {
                error = "仓库中未找到任何 .vmap 地图文件，不符合当前项目的规范要求。";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"检查仓库文件异常: {ex.Message}";
            return false;
        }

        return true;
    }

    public bool ValidateCs2Install(string cs2InstallPath, out string dmxConvertPath, out string error)
    {
        dmxConvertPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(cs2InstallPath) || !Directory.Exists(cs2InstallPath))
        {
            error = $"CS2 安装目录不存在: {cs2InstallPath}";
            return false;
        }

        dmxConvertPath = Path.Combine(cs2InstallPath, "game", "bin", "win64", "dmxconvert.exe");
        if (!File.Exists(dmxConvertPath))
        {
            error = $"未找到 dmxconvert.exe: {dmxConvertPath}";
            return false;
        }

        return true;
    }

    public virtual bool IsHammerOrCs2Running(out List<string> runningProcessNames)
    {
        runningProcessNames = new List<string>();
        string[] targetNames = ["cs2", "hammer", "resourcecompiler"];
        foreach (var procName in targetNames)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    runningProcessNames.Add($"{procName}.exe (PID: {string.Join(",", procs.Select(p => p.Id))})");
                }
            }
            catch
            {
                // Ignore process query permission errors
            }
            finally
            {
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }
        return runningProcessNames.Count > 0;
    }
}
