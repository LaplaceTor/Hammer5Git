using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hammer5Git.Core.Models;

namespace Hammer5Git.Core.Services;

public sealed class RepoImportRequest
{
    public string SourcePathOrUrl { get; init; } = string.Empty;
    public bool IsRemote { get; init; }
    public string AddonName { get; init; } = string.Empty;
    public string Cs2InstallPath { get; init; } = string.Empty;
    public string DefaultReposRoot { get; init; } = string.Empty;
    public int MaxAutoCommits { get; init; }
    /// <summary>已存在的项目名快照，用于重名检查。</summary>
    public IReadOnlyCollection<string> ExistingAddonNames { get; init; } = Array.Empty<string>();
    /// <summary>日志回调 (addonName, message)；为 null 时忽略。</summary>
    public Action<string, string>? Log { get; init; }
}

public sealed record RepoImportResult(bool Success, string Message, AddonProfile? Addon);

public sealed class RepoImportService
{
    private readonly ShadowGitService _gitService;
    private readonly Cs2EnvironmentService _envService;
    private readonly AddonWatcherService _watcherService;
    private readonly VmapConverterAndSanitizer _converter;

    public RepoImportService(
        ShadowGitService gitService,
        Cs2EnvironmentService envService,
        AddonWatcherService watcherService,
        VmapConverterAndSanitizer converter)
    {
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _envService = envService ?? throw new ArgumentNullException(nameof(envService));
        _watcherService = watcherService ?? throw new ArgumentNullException(nameof(watcherService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public async Task<RepoImportResult> ImportAsync(RepoImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Cs2EnvironmentService.IsExcludedAddonName(request.AddonName))
        {
            return new RepoImportResult(false, $"项目名称【{request.AddonName}】为 CS2 原生只读/保留名称，无法使用。", null);
        }

        if (string.IsNullOrWhiteSpace(request.Cs2InstallPath) || !Directory.Exists(request.Cs2InstallPath))
        {
            return new RepoImportResult(false, "未检测到有效的 CS2 安装目录，请确认 Steam 及 CS2 已正确安装。", null);
        }

        if (request.ExistingAddonNames != null && request.ExistingAddonNames.Any(name => string.Equals(name, request.AddonName, StringComparison.OrdinalIgnoreCase)))
        {
            return new RepoImportResult(false, $"项目【{request.AddonName}】已在列表中，请更换名称或先移除已有项目。", null);
        }

        string targetShadowRepo = Path.Combine(request.DefaultReposRoot, request.AddonName);
        if (Directory.Exists(targetShadowRepo))
        {
            if (Directory.EnumerateFileSystemEntries(targetShadowRepo).Any())
            {
                return new RepoImportResult(false, $"目标影子仓库目录已存在且不为空: {targetShadowRepo}\n请清理该目录或修改项目名称。", null);
            }
        }

        try
        {
            if (request.IsRemote)
            {
                request.Log?.Invoke("System", $"正在克隆远程仓库 {request.SourcePathOrUrl} ...");
                var (cloneSuccess, cloneMsg) = await _gitService.CloneRepositoryAsync(request.SourcePathOrUrl, targetShadowRepo);
                if (!cloneSuccess)
                {
                    return new RepoImportResult(false, $"远程仓库克隆失败: {cloneMsg}", null);
                }
            }
            else
            {
                if (!Directory.Exists(request.SourcePathOrUrl))
                {
                    return new RepoImportResult(false, $"本地仓库目录不存在: {request.SourcePathOrUrl}", null);
                }

                if (Directory.Exists(Path.Combine(request.SourcePathOrUrl, ".git")))
                {
                    request.Log?.Invoke("System", $"正在导入本地 Git 仓库: {request.SourcePathOrUrl} ...");
                    var (cloneSuccess, cloneMsg) = await _gitService.CloneRepositoryAsync(request.SourcePathOrUrl, targetShadowRepo);
                    if (!cloneSuccess)
                    {
                        return new RepoImportResult(false, $"导入本地 Git 仓库失败: {cloneMsg}", null);
                    }
                }
                else
                {
                    CopyDirectory(request.SourcePathOrUrl, targetShadowRepo);
                }
            }

            // Validate repository structure
            if (!_envService.ValidateRepoStructure(targetShadowRepo, out string structErr))
            {
                try { Directory.Delete(targetShadowRepo, true); } catch { }
                return new RepoImportResult(false, $"仓库验证失败: {structErr}", null);
            }

            // 本地普通目录导入：此时尚未创建 .git，统一走 Core 的初始化逻辑，
            // 以确保生成 .gitignore/.gitattributes、写入仓库级 git 身份，并在提交失败时抛出异常而非静默继续。
            if (!Directory.Exists(Path.Combine(targetShadowRepo, ".git")))
            {
                var importProfile = new AddonProfile
                {
                    AddonName = request.AddonName,
                    ShadowRepoPath = targetShadowRepo,
                    ContentAddonPath = request.SourcePathOrUrl
                };
                await _gitService.EnsureInitializedAsync(importProfile);
                await _gitService.CommitPendingChangesAsync(importProfile, "[Init] 初始导入 (本地目录)", request.MaxAutoCommits);
            }

            // Setup CS2 addon directories
            string contentAddonPath = Path.Combine(request.Cs2InstallPath, "content", "csgo_addons", request.AddonName);
            string gameAddonPath = Path.Combine(request.Cs2InstallPath, "game", "csgo_addons", request.AddonName);
            Directory.CreateDirectory(contentAddonPath);
            Directory.CreateDirectory(gameAddonPath);

            var newAddon = new AddonProfile
            {
                AddonName = request.AddonName,
                ContentAddonPath = contentAddonPath,
                GameAddonPath = gameAddonPath,
                ShadowRepoPath = targetShadowRepo,
                IsMonitoringEnabled = true,
                TrackGameSideConfigs = true,
                RemoteUrl = request.IsRemote ? request.SourcePathOrUrl : string.Empty,
                StatusText = "监控中"
            };

            request.Log?.Invoke(request.AddonName, "正在同步仓库文件至 CS2 content/game 目录...");
            await CheckoutShadowRepoToCs2Async(newAddon);

            return new RepoImportResult(true, $"成功载入仓库并同步至 CS2 项目【{request.AddonName}】！", newAddon);
        }
        catch (Exception ex)
        {
            return new RepoImportResult(false, $"载入仓库异常: {ex.Message}", null);
        }
    }

    private async Task CheckoutShadowRepoToCs2Async(AddonProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ShadowRepoPath) || !Directory.Exists(profile.ShadowRepoPath))
            return;

        // 优化：仅枚举 content 与 game 目标子目录，避免扫描 .git 庞大的对象树
        string repoContentDir = Path.Combine(profile.ShadowRepoPath, "content");
        string repoGameDir = Path.Combine(profile.ShadowRepoPath, "game");

        if (Directory.Exists(repoContentDir) && !string.IsNullOrWhiteSpace(profile.ContentAddonPath))
        {
            string contentDestRoot = Path.GetFullPath(profile.ContentAddonPath);
            foreach (var file in Directory.EnumerateFiles(repoContentDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repoContentDir, file);
                string destPath = Path.GetFullPath(Path.Combine(contentDestRoot, rel));

                // 安全校验：确保目标绝对路径严格位于目标目录树下，防止路径穿越 (Path Traversal)
                if (!IsSubPathOf(destPath, contentDestRoot))
                    continue;

                string? dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                _watcherService.SuppressPathEvents(destPath, TimeSpan.FromSeconds(5));

                if (rel.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
                {
                    var (convSuccess, _) = await _converter.ConvertKv2ToBinaryAsync(file, destPath);
                    if (!convSuccess)
                    {
                        File.Copy(file, destPath, overwrite: true);
                    }
                }
                else
                {
                    File.Copy(file, destPath, overwrite: true);
                }
            }
        }

        if (Directory.Exists(repoGameDir) && !string.IsNullOrWhiteSpace(profile.GameAddonPath))
        {
            string gameDestRoot = Path.GetFullPath(profile.GameAddonPath);
            foreach (var file in Directory.EnumerateFiles(repoGameDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repoGameDir, file);
                string destPath = Path.GetFullPath(Path.Combine(gameDestRoot, rel));

                // 安全校验：确保目标绝对路径严格位于目标目录树下，防止路径穿越 (Path Traversal)
                if (!IsSubPathOf(destPath, gameDestRoot))
                    continue;

                string? dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                _watcherService.SuppressPathEvents(destPath, TimeSpan.FromSeconds(5));
                File.Copy(file, destPath, overwrite: true);
            }
        }
    }

    internal static bool IsSubPathOf(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
            return false;

        try
        {
            string fullCandidate = Path.GetFullPath(candidatePath);
            string fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return fullCandidate.Length > fullRoot.Length && fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            if (Path.GetFileName(dir).Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }
}
