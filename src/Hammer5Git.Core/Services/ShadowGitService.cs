using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hammer5Git.Core.Models;

namespace Hammer5Git.Core.Services;

public class ShadowGitService
{
    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(60);

    public static readonly HashSet<string> DefaultTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vmat", ".vmdl", ".vmdl_prefab", ".vsmart", ".vsndevts", ".vpost",
        ".kv3", ".txt", ".cfg", ".vdf", ".js", ".ts", ".xml", ".css",
        ".json", ".vscript", ".lua", ".vpdi", ".vdata", ".vpcf"
    };

    public static readonly string[] OptionalBinaryAssetExtensions =
    [
        ".tga", ".png", ".jpg", ".jpeg", ".psd", ".exr", ".hdr",
        ".wav", ".mp3", ".ogg",
        ".fbx", ".obj", ".dmx", ".smd", ".blend", ".sbs"
    ];

    public static readonly string[] GitIgnoreDirectoryLines =
    [
        ".recovery_backups/",
        ".corrupted_dumps/",
        "_bakeresourcecache/",
        "_iconcache/"
    ];

    public static readonly HashSet<string> IgnoredDirectoryNames = new(
        GitIgnoreDirectoryLines.Select(d => d.TrimEnd('/')).Concat(["screenshots", ".git"]),
        StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools_thumbnail_cache.bin",
        "readonly_tools_asset_info.bin",
        "AssetBrowserSavedSearches.kv3"
    };

    public static readonly string[] GitIgnoreFixedLines =
    [
        .. GitIgnoreDirectoryLines,
        .. IgnoredFileNames,
        "*_c",
        "*.bak",
        "*.tmp",
        "*.log"
    ];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetRepoLock(string repoPath)
    {
        string normalized = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _repoLocks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
    }

    private async Task WithRepoLockAsync(string repoPath, CancellationToken ct, Func<Task> action)
    {
        var sem = GetRepoLock(repoPath);
        await sem.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<T> WithRepoLockAsync<T>(string repoPath, CancellationToken ct, Func<Task<T>> action)
    {
        var sem = GetRepoLock(repoPath);
        await sem.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            sem.Release();
        }
    }

    public Task EnsureInitializedAsync(AddonProfile profile, CancellationToken ct = default)
        => WithRepoLockAsync(profile.ShadowRepoPath, ct, () => EnsureInitializedInternalAsync(profile, ct));

    private async Task EnsureInitializedInternalAsync(AddonProfile profile, CancellationToken ct)
    {
        Directory.CreateDirectory(profile.ShadowRepoPath);
        Directory.CreateDirectory(Path.Combine(profile.ShadowRepoPath, ".recovery_backups"));
        Directory.CreateDirectory(Path.Combine(profile.ShadowRepoPath, ".corrupted_dumps"));

        string gitDir = Path.Combine(profile.ShadowRepoPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            await RunGitAsync(profile.ShadowRepoPath, ["init", "-b", "main"], ct);
            await RunGitAsync(profile.ShadowRepoPath, ["config", "user.name", "Hammer5Git"], ct);
            await RunGitAsync(profile.ShadowRepoPath, ["config", "user.email", "hammer@cs2.local"], ct);
            await RunGitAsync(profile.ShadowRepoPath, ["config", "core.autocrlf", "false"], ct);
            await RunGitAsync(profile.ShadowRepoPath, ["config", "core.quotepath", "false"], ct);
        }

        await UpdateIgnoreAndLfsRulesInternalAsync(profile, ct);
    }

    public Task UpdateIgnoreAndLfsRulesAsync(AddonProfile profile, CancellationToken ct = default)
        => WithRepoLockAsync(profile.ShadowRepoPath, ct, () => UpdateIgnoreAndLfsRulesInternalAsync(profile, ct));

    private async Task UpdateIgnoreAndLfsRulesInternalAsync(AddonProfile profile, CancellationToken ct)
    {
        Directory.CreateDirectory(profile.ShadowRepoPath);

        var trackedLfs = new HashSet<string>(
            profile.LfsTrackedExtensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var gitignore = new StringBuilder();
        gitignore.AppendLine("# Hammer5Git Auto-Generated .gitignore");
        foreach (var line in GitIgnoreFixedLines)
        {
            gitignore.AppendLine(line);
        }
        gitignore.AppendLine();
        gitignore.AppendLine("# Binary assets not selected for Git LFS");

        foreach (var ext in OptionalBinaryAssetExtensions)
        {
            if (!trackedLfs.Contains(ext))
            {
                gitignore.AppendLine($"*{ext}");
            }
        }

        string gitignorePath = Path.Combine(profile.ShadowRepoPath, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, gitignore.ToString(), new UTF8Encoding(false), ct);

        var gitattributes = new StringBuilder();
        gitattributes.AppendLine("# Hammer5Git Auto-Generated .gitattributes");
        gitattributes.AppendLine("*.vmap text eol=lf");
        gitattributes.AppendLine("*.vmat text eol=lf");
        gitattributes.AppendLine("*.vmdl text eol=lf");
        gitattributes.AppendLine("*.vsmart text eol=lf");
        gitattributes.AppendLine("*.kv3 text eol=lf");
        gitattributes.AppendLine("*.vsndevts text eol=lf");
        gitattributes.AppendLine("*.vpost text eol=lf");
        gitattributes.AppendLine("*.vdata text eol=lf");
        gitattributes.AppendLine("*.vpcf text eol=lf");
        gitattributes.AppendLine("*.vmdl_prefab text eol=lf");
        gitattributes.AppendLine("*.vpdi text eol=lf");

        if (trackedLfs.Count > 0)
        {
            await RunGitAsync(profile.ShadowRepoPath, ["lfs", "install", "--local"], ct);
            foreach (var ext in trackedLfs.OrderBy(x => x))
            {
                gitattributes.AppendLine($"*{ext} filter=lfs diff=lfs merge=lfs -text");
            }
        }

        string gitattributesPath = Path.Combine(profile.ShadowRepoPath, ".gitattributes");
        await File.WriteAllTextAsync(gitattributesPath, gitattributes.ToString(), new UTF8Encoding(false), ct);
    }

    public bool ShouldTrackFile(
        AddonProfile profile,
        string fullPath,
        bool isGameSide,
        out string relativeInSource)
        => ShouldTrackFile(profile, fullPath, isGameSide, out relativeInSource, out _);

    public bool ShouldTrackFile(
        AddonProfile profile,
        string fullPath,
        bool isGameSide,
        out string relativeInSource,
        out bool isFromAutoSaveRoot)
    {
        relativeInSource = string.Empty;
        isFromAutoSaveRoot = false;

        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        if (isGameSide)
        {
            if (!TryGetRelativeIfUnderRoot(fullPath, profile.GameAddonPath, out relativeInSource))
                return false;
        }
        else
        {
            if (TryGetRelativeIfUnderRoot(fullPath, profile.ContentAddonPath, out relativeInSource))
            {
                isFromAutoSaveRoot = false;
            }
            else if (TryGetRelativeIfUnderRoot(fullPath, profile.AutoSaveAddonPath, out relativeInSource))
            {
                isFromAutoSaveRoot = true;
            }
            else
            {
                return false;
            }
        }

        string[] pathSegments = relativeInSource.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in pathSegments)
        {
            if (IgnoredDirectoryNames.Contains(seg))
                return false;
        }

        string fileName = Path.GetFileName(fullPath);
        if (fileName.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ||
            IgnoredFileNames.Contains(fileName))
        {
            return false;
        }

        if (fileName.Contains("_restored_", StringComparison.OrdinalIgnoreCase))
            return false;

        string ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext))
            return false;

        // _toolsautosave 根下追踪 .vmap 与 .vmap_autosave 文件
        if (isFromAutoSaveRoot)
            return VmapConverterAndSanitizer.IsTrackableVmapExtension(ext);

        if (!isGameSide && VmapConverterAndSanitizer.IsTrackableVmapExtension(ext))
            return true;

        if (DefaultTextExtensions.Contains(ext))
            return true;

        if (!isGameSide && profile.LfsTrackedExtensions.Any(x =>
            string.Equals(x.StartsWith('.') ? x : "." + x, ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetRelativeIfUnderRoot(string fullPath, string? rootPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath))
            return false;

        try
        {
            string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string normFull = Path.GetFullPath(fullPath);

            if (normFull.Length <= normRoot.Length)
                return false;

            if (!normFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            char boundaryChar = normFull[normRoot.Length];
            if (boundaryChar != Path.DirectorySeparatorChar && boundaryChar != Path.AltDirectorySeparatorChar)
                return false;

            relativePath = Path.GetRelativePath(normRoot, normFull).Replace('\\', '/');
            return !relativePath.StartsWith("..", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Stages all changes in the shadow repository and commits if there are diffs.
    /// Automatically triggers rolling history truncation if commit count exceeds maxAutoCommits.
    /// </summary>
    public Task<CommitRecord?> CommitPendingChangesAsync(
        AddonProfile profile,
        string commitMessage,
        int maxAutoCommits = 500,
        CancellationToken ct = default)
    {
        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            await EnsureInitializedInternalAsync(profile, ct);

            await RunGitAsync(profile.ShadowRepoPath, ["add", "-A"], ct);
            var status = await RunGitAsync(profile.ShadowRepoPath, ["status", "--porcelain"], ct);
            if (string.IsNullOrWhiteSpace(status.StdOut))
            {
                return null; // No actual diff after sanitization
            }

            var commitRes = await RunGitAsync(profile.ShadowRepoPath, ["commit", "-m", commitMessage], ct);
            if (commitRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git commit 失败: {commitRes.StdErr} {commitRes.StdOut}");
            }

            if (maxAutoCommits > 0)
            {
                await EnforceRollingHistoryLimitInternalAsync(profile.ShadowRepoPath, maxAutoCommits, ct);
            }

            var history = await GetCommitHistoryInternalAsync(profile.ShadowRepoPath, 1, ct: ct);
            return history.FirstOrDefault();
        });
    }

    public Task<bool> HasAnyCommitsAsync(AddonProfile profile, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult(false);

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            var res = await RunGitAsync(profile.ShadowRepoPath, ["rev-parse", "--verify", "HEAD"], ct);
            return res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut);
        });
    }

    /// <summary>
    /// Returns all canonical tracked relative paths inside the shadow repository (e.g., "content/maps/arena.vmap", "game/addoninfo.txt").
    /// </summary>
    public List<string> GetShadowTrackedFiles(AddonProfile profile)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.ShadowRepoPath) || !Directory.Exists(profile.ShadowRepoPath))
            return files;

        string[] roots = ["content", "game"];
        foreach (var root in roots)
        {
            string subDir = Path.Combine(profile.ShadowRepoPath, root);
            if (!Directory.Exists(subDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(subDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(profile.ShadowRepoPath, file).Replace('\\', '/');
                files.Add(rel);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public async Task<List<CommitRecord>> GetCommitHistoryAsync(AddonProfile profile, int maxCount = 500, CancellationToken ct = default)
    {
        return await GetFilteredCommitHistoryAsync(
            profile,
            maxCount,
            filterRepoRelativePath: null,
            hideInitAndMetadataCommits: false,
            ct);
    }

    public Task<List<CommitRecord>> GetFilteredCommitHistoryAsync(
        AddonProfile profile,
        int maxCount = 500,
        string? filterRepoRelativePath = null,
        bool hideInitAndMetadataCommits = true,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult(new List<CommitRecord>());

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, () =>
            GetCommitHistoryInternalAsync(
                profile.ShadowRepoPath,
                maxCount,
                filterRepoRelativePath,
                hideInitAndMetadataCommits,
                ct));
    }

    public static bool IsInitOrMetadataCommitSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return false;

        return subject.StartsWith("[Init]", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("初始导入", StringComparison.OrdinalIgnoreCase) ||
               subject.Contains("更新地图属性/全局配置", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<CommitRecord>> GetCommitHistoryInternalAsync(
        string repoPath,
        int maxCount,
        string? filterRepoRelativePath = null,
        bool hideInitAndMetadataCommits = false,
        CancellationToken ct = default)
    {
        var records = new List<CommitRecord>();
        var args = new List<string> { "log", "-n", maxCount.ToString(), "--date=iso-strict", "--format=%H|%h|%ad|%D|%s" };
        if (!string.IsNullOrWhiteSpace(filterRepoRelativePath))
        {
            args.Add("--");
            args.Add(filterRepoRelativePath.Replace('\\', '/').TrimStart('/'));
        }

        var res = await RunGitAsync(repoPath, args, ct);
        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StdOut))
            return records;

        var lines = res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('|', 5);
            if (parts.Length < 5)
                continue;

            DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto);

            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(parts[3]))
            {
                var refs = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var r in refs)
                {
                    if (r.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase))
                        tags.Add(r["tag: ".Length..].Trim());
                }
            }

            string subject = parts[4];
            if (hideInitAndMetadataCommits && tags.Count == 0 && IsInitOrMetadataCommitSubject(subject))
                continue;

            records.Add(new CommitRecord
            {
                Hash = parts[0],
                ShortHash = parts[1],
                Timestamp = dto,
                Tags = tags,
                Subject = subject
            });
        }

        return records;
    }

    public Task<List<ChangedFileEntry>> GetCommitChangedFilesAsync(AddonProfile profile, string commitHash, CancellationToken ct = default)
    {
        var list = new List<ChangedFileEntry>();
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult(list);

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            var res = await RunGitAsync(profile.ShadowRepoPath, ["diff-tree", "--no-commit-id", "--name-status", "-r", "-m", "--root", commitHash], ct);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StdOut))
                return list;

            var lines = res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    if (seenPaths.Add(parts[1]))
                    {
                        list.Add(new ChangedFileEntry
                        {
                            Status = parts[0],
                            RelativePath = parts[1]
                        });
                    }
                }
            }
            return list;
        });
    }

    public Task<string> GetFileDiffForCommitAsync(AddonProfile profile, string commitHash, string? relativeFilePath = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult(string.Empty);

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            var parentRes = await RunGitAsync(profile.ShadowRepoPath, ["rev-parse", "--verify", $"{commitHash}^"], ct);
            var args = new List<string>();
            if (parentRes.ExitCode == 0)
            {
                args.AddRange(["diff", "-U3", $"{commitHash}^", commitHash]);
            }
            else
            {
                args.AddRange(["show", "--format=", "-U3", commitHash]);
            }

            if (!string.IsNullOrWhiteSpace(relativeFilePath))
            {
                args.Add("--");
                args.Add(relativeFilePath);
            }

            var res = await RunGitAsync(profile.ShadowRepoPath, args, ct);
            return string.IsNullOrWhiteSpace(res.StdOut) ? "(无文本差异或二进制文件)" : res.StdOut;
        });
    }

    public async Task<(bool Success, string Message)> AddMilestoneTagAsync(AddonProfile profile, string commitHash, string tagName, string? note = null, CancellationToken ct = default)
    {
        string cleanTag = new string(tagName.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(cleanTag))
            return (false, "标签名称无效。");

        string msg = string.IsNullOrWhiteSpace(note) ? $"Milestone {cleanTag}" : note;

        var res = await WithRepoLockAsync(profile.ShadowRepoPath, ct, () =>
            RunGitAsync(profile.ShadowRepoPath, ["tag", "-a", cleanTag, commitHash, "-m", msg, "-f"], ct));

        if (res.ExitCode != 0)
            return (false, $"创建标签失败: {res.StdErr}");

        string pushSuffix = string.Empty;
        if (profile.AutoPushOnTag && !string.IsNullOrWhiteSpace(profile.RemoteUrl))
        {
            try
            {
                var pushRes = await PushToRemoteAsync(profile, ct);
                pushSuffix = pushRes.Success
                    ? " (自动推送到远程成功)"
                    : $" (自动推送到远程失败: {pushRes.Message})";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pushSuffix = $" (自动推送到远程异常: {ex.Message})";
            }
        }

        return (true, $"已为节点 {commitHash[..Math.Min(7, commitHash.Length)]} 打上里程碑标签: {cleanTag}{pushSuffix}");
    }

    /// <summary>
    /// Restores a file from the shadow Git repository back to the CS2 Addon directory.
    /// Supports both "SaveAsCopy" (exports xxx_restored_<shortHash>.vmap) and "OverwriteWithBackup".
    /// </summary>
    public async Task<(bool Success, string OutputFilePath, string BackupFilePath, string Message)> RestoreFileFromCommitAsync(
        AddonProfile profile,
        string commitHash,
        string repoRelativePath,
        bool overwriteOriginal,
        VmapConverterAndSanitizer vmapConverter,
        CancellationToken ct = default)
    {
        string normalizedRel = repoRelativePath.Replace('\\', '/').TrimStart('/');
        string shortHash = commitHash.Length >= 7 ? commitHash[..7] : commitHash;

        string tempExtractPath = Path.Combine(Path.GetTempPath(), $"hammer_git_extract_{Guid.NewGuid():N}{Path.GetExtension(normalizedRel)}");
        try
        {
            var showRes = await WithRepoLockAsync(profile.ShadowRepoPath, ct, () =>
                RunGitToFileAsync(profile.ShadowRepoPath, ["show", $"{commitHash}:{normalizedRel}"], tempExtractPath, ct));

            if (!showRes.Success)
            {
                return (false, string.Empty, string.Empty, $"从 Git 提取历史文件失败: {showRes.Error}");
            }

            string originalTargetFullPath = profile.ResolveDiskFilePath(normalizedRel);

            string finalTargetFullPath;
            string backupFilePath = string.Empty;

            if (overwriteOriginal)
            {
                finalTargetFullPath = originalTargetFullPath;
                if (File.Exists(originalTargetFullPath))
                {
                    string backupDir = Path.Combine(profile.ShadowRepoPath, ".recovery_backups");
                    Directory.CreateDirectory(backupDir);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backupFilePath = Path.Combine(backupDir, $"{timestamp}_{Path.GetFileName(originalTargetFullPath)}");
                    File.Copy(originalTargetFullPath, backupFilePath, overwrite: true);
                }
            }
            else
            {
                string dir = Path.GetDirectoryName(originalTargetFullPath) ?? (normalizedRel.StartsWith("game/", StringComparison.OrdinalIgnoreCase) ? profile.GameAddonPath : profile.ContentAddonPath);
                string nameNoExt = Path.GetFileNameWithoutExtension(originalTargetFullPath);
                string ext = Path.GetExtension(originalTargetFullPath);
                finalTargetFullPath = Path.Combine(dir, $"{nameNoExt}_restored_{shortHash}{ext}");
            }

            if (normalizedRel.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
            {
                var conv = await vmapConverter.ConvertKv2ToBinaryAsync(tempExtractPath, finalTargetFullPath, ct);
                if (!conv.Success)
                {
                    return (false, string.Empty, backupFilePath, $"将文本 VMAP 转回二进制失败: {conv.ErrorMessage}");
                }
            }
            else
            {
                var targetDir = Path.GetDirectoryName(finalTargetFullPath);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);
                File.Copy(tempExtractPath, finalTargetFullPath, overwrite: true);
            }

            string modeDesc = overwriteOriginal
                ? $"已直接覆盖恢复至原文件: {finalTargetFullPath}" + (string.IsNullOrEmpty(backupFilePath) ? "" : $"\n(覆盖前原文件已安全备份至: {backupFilePath})")
                : $"已安全另存为对比文件: {finalTargetFullPath}";

            return (true, finalTargetFullPath, backupFilePath, modeDesc);
        }
        finally
        {
            if (File.Exists(tempExtractPath))
            {
                try { File.Delete(tempExtractPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Enforces a rolling history limit (e.g., 500 commits) by truncating commits older than the Nth commit
    /// using a graft/replace root commit while cleaning up obsolete replace refs.
    /// </summary>
    private async Task EnforceRollingHistoryLimitInternalAsync(string repoPath, int maxCommits, CancellationToken ct)
    {
        var countRes = await RunGitAsync(repoPath, ["rev-list", "--count", "HEAD"], ct);
        if (countRes.ExitCode != 0 || !int.TryParse(countRes.StdOut.Trim(), out int count))
            return;

        if (count <= maxCommits + 20)
            return;

        var targetRes = await RunGitAsync(repoPath, ["rev-parse", $"HEAD~{maxCommits - 1}"], ct);
        if (targetRes.ExitCode != 0 || string.IsNullOrWhiteSpace(targetRes.StdOut))
            return;

        string cutoffHash = targetRes.StdOut.Trim();

        var treeRes = await RunGitAsync(repoPath, ["rev-parse", $"{cutoffHash}^{{tree}}"], ct);
        if (treeRes.ExitCode != 0 || string.IsNullOrWhiteSpace(treeRes.StdOut))
            return;

        string treeHash = treeRes.StdOut.Trim();
        var commitTreeRes = await RunGitAsync(repoPath, ["commit-tree", treeHash, "-m", $"[Archive] 滚动归档基底 (保留最近 {maxCommits} 次提交)"], ct);
        if (commitTreeRes.ExitCode != 0 || string.IsNullOrWhiteSpace(commitTreeRes.StdOut))
            return;

        string newRootHash = commitTreeRes.StdOut.Trim();

        // Remove old replace refs so they don't accumulate indefinitely
        var existingReplaces = await RunGitAsync(repoPath, ["replace", "-l"], ct);
        if (existingReplaces.ExitCode == 0 && !string.IsNullOrWhiteSpace(existingReplaces.StdOut))
        {
            foreach (var oldRef in existingReplaces.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                await RunGitAsync(repoPath, ["replace", "-d", oldRef.Trim()], ct);
            }
        }

        await RunGitAsync(repoPath, ["replace", "--graft", cutoffHash, newRootHash], ct);
    }

    public async Task<(bool Success, string Message)> PushToRemoteAsync(AddonProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.RemoteUrl))
            return (false, "尚未配置远程仓库 URL。");

        return await WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            var remotes = await RunGitAsync(profile.ShadowRepoPath, ["remote"], ct);
            if (remotes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("origin"))
            {
                await RunGitAsync(profile.ShadowRepoPath, ["remote", "set-url", "origin", profile.RemoteUrl], ct);
            }
            else
            {
                await RunGitAsync(profile.ShadowRepoPath, ["remote", "add", "origin", profile.RemoteUrl], ct);
            }

            var pushRes = await RunGitAsync(profile.ShadowRepoPath, ["push", "-u", "origin", "main", "--tags", "--force-with-lease"], ct);
            if (pushRes.ExitCode != 0)
            {
                return (false, $"推送至远程失败: {pushRes.StdErr} {pushRes.StdOut}".Trim());
            }

            return (true, "已成功推送分支与里程碑标签至远程仓库。");
        });
    }

    /// <summary>
    /// 记录最近一次源于正式保存的提交哈希至专用引用 refs/hammer5git/last-formal。
    /// </summary>
    public Task UpdateFormalAnchorRefAsync(AddonProfile profile, string commitHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
            return Task.CompletedTask;

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            try
            {
                await RunGitAsync(profile.ShadowRepoPath, ["update-ref", "refs/hammer5git/last-formal", commitHash.Trim()], ct);
            }
            catch
            {
                // 静默忽略失败，best-effort
            }
        });
    }

    /// <summary>
    /// 获取 refs/hammer5git/last-formal 记录的正式锚点提交哈希。
    /// </summary>
    public Task<(bool Success, string CommitHash)> TryGetFormalAnchorCommitAsync(AddonProfile profile, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult((false, string.Empty));

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, () => TryGetFormalAnchorCommitInternalAsync(profile.ShadowRepoPath, ct));
    }

    private async Task<(bool Success, string CommitHash)> TryGetFormalAnchorCommitInternalAsync(string repoPath, CancellationToken ct)
    {
        try
        {
            var res = await RunGitAsync(repoPath, ["rev-parse", "--verify", "refs/hammer5git/last-formal"], ct);
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StdOut))
            {
                return (true, res.StdOut.Trim());
            }
            return (false, string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// 检查当前 HEAD 是否已被推送至远程仓库 origin/main 分支。
    /// </summary>
    public Task<bool> IsHeadPushedToRemoteAsync(AddonProfile profile, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult(false);

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, () => IsHeadPushedToRemoteInternalAsync(profile.ShadowRepoPath, ct));
    }

    private async Task<bool> IsHeadPushedToRemoteInternalAsync(string repoPath, CancellationToken ct)
    {
        try
        {
            var remotes = await RunGitAsync(repoPath, ["remote"], ct);
            if (remotes.ExitCode != 0 || !remotes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("origin"))
            {
                return false;
            }

            var refCheck = await RunGitAsync(repoPath, ["rev-parse", "--verify", "origin/main"], ct);
            if (refCheck.ExitCode != 0)
            {
                return false;
            }

            var mergeBase = await RunGitAsync(repoPath, ["merge-base", "--is-ancestor", "HEAD", "origin/main"], ct);
            return mergeBase.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将影子仓库安全回退至正式存档锚点 refs/hammer5git/last-formal。
    /// </summary>
    public Task<(FormalAnchorRollbackResult Result, string Message)> RollbackToFormalAnchorAsync(
        AddonProfile profile, string mapDisplayName, CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(profile.ShadowRepoPath, ".git")))
            return Task.FromResult((FormalAnchorRollbackResult.Failed, "影子仓库未初始化"));

        return WithRepoLockAsync(profile.ShadowRepoPath, ct, async () =>
        {
            try
            {
                var (hasAnchor, anchorHash) = await TryGetFormalAnchorCommitInternalAsync(profile.ShadowRepoPath, ct);
                if (!hasAnchor || string.IsNullOrWhiteSpace(anchorHash))
                {
                    return (FormalAnchorRollbackResult.Failed, "未找到正式存档锚点 (refs/hammer5git/last-formal)");
                }

                var headRes = await RunGitAsync(profile.ShadowRepoPath, ["rev-parse", "HEAD"], ct);
                if (headRes.ExitCode != 0 || string.IsNullOrWhiteSpace(headRes.StdOut))
                {
                    return (FormalAnchorRollbackResult.Failed, "无法获取当前 HEAD 提交");
                }
                string currentHead = headRes.StdOut.Trim();

                if (string.Equals(currentHead, anchorHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (FormalAnchorRollbackResult.AlreadyAligned, "当前 HEAD 已与正式存档锚点一致");
                }

                bool isPushed = await IsHeadPushedToRemoteInternalAsync(profile.ShadowRepoPath, ct);
                if (isPushed)
                {
                    return (FormalAnchorRollbackResult.RequiresForwardReconcile, "当前 HEAD 已推送至远程仓库，不可执行 reset --hard，需要前向对齐提交");
                }

                // PART2-a: 在执行 reset --hard 之前，验证从 anchorHash 到 HEAD 之间的提交区间是否纯净。
                // 运行 git rev-list --format=%s <anchorHash>..HEAD
                // 要求该区间内每一个提交主题都必须以 "[AutoSave]" 开头。
                // 若区间内存在非 [AutoSave] 提交（如其他地图的正式保存、常规资产变更），绝不可 reset --hard，必须回退至前向对齐。
                var revListRes = await RunGitAsync(profile.ShadowRepoPath, ["rev-list", "--format=%s", $"{anchorHash}..HEAD"], ct);
                if (revListRes.ExitCode != 0)
                {
                    return (FormalAnchorRollbackResult.Failed, $"检查提交历史纯净度失败: {revListRes.StdErr}".Trim());
                }

                var lines = revListRes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var subjectLines = lines
                    .Where(l => !l.StartsWith("commit ", StringComparison.Ordinal))
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();

                foreach (var subject in subjectLines)
                {
                    if (!subject.StartsWith("[AutoSave]", StringComparison.OrdinalIgnoreCase))
                    {
                        return (FormalAnchorRollbackResult.RequiresForwardReconcile,
                            $"提交历史区间包含非自动保存草稿的提交 ({subject})，为防止误丢弃其他资产或正式存档，转为前向对齐");
                    }
                }

                string rawTagName = $"abandoned_{mapDisplayName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                string cleanTag = new string(rawTagName.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_').ToArray());
                if (string.IsNullOrWhiteSpace(cleanTag))
                {
                    cleanTag = $"abandoned_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                }

                await RunGitAsync(profile.ShadowRepoPath, ["tag", "-f", cleanTag, currentHead], ct);

                var resetRes = await RunGitAsync(profile.ShadowRepoPath, ["reset", "--hard", "refs/hammer5git/last-formal"], ct);
                if (resetRes.ExitCode != 0)
                {
                    return (FormalAnchorRollbackResult.Failed, $"Git reset 失败: {resetRes.StdErr} {resetRes.StdOut}".Trim());
                }

                return (FormalAnchorRollbackResult.RolledBack, $"已将草稿安全归档至标签 {cleanTag} 并硬重置回退至正式存档 {anchorHash[..Math.Min(7, anchorHash.Length)]}");
            }
            catch (Exception ex)
            {
                return (FormalAnchorRollbackResult.Failed, $"回退异常: {ex.Message}");
            }
        });
    }

    private static readonly Dictionary<string, string> DefaultGitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never"
    };

    public Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        return ProcessRunner.RunAsync("git", arguments, workingDir, DefaultGitTimeout, DefaultGitEnvironment, ct);
    }

    private Task<(bool Success, string Error)> RunGitToFileAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        string outputFilePath,
        CancellationToken ct)
    {
        return ProcessRunner.RunToFileAsync("git", arguments, outputFilePath, workingDir, DefaultGitTimeout, DefaultGitEnvironment, ct);
    }

    public async Task<(bool Success, string Message)> CloneRepositoryAsync(string sourcePathOrUrl, string targetDir, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePathOrUrl))
            {
                return (false, "源仓库地址或路径不能为空。");
            }

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                return (false, "目标目录不能为空。");
            }

            string? parentDir = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var (exitCode, stdout, stderr) = await RunGitAsync(
                parentDir ?? AppContext.BaseDirectory,
                ["clone", "--", sourcePathOrUrl, targetDir],
                ct);

            if (exitCode != 0)
            {
                string msg = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                return (false, $"Git 克隆失败 (退出码 {exitCode}): {msg}");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"克隆仓库发生异常: {ex.Message}");
        }
    }
}
