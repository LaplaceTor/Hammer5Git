using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hammer5Git.Core.Models;
using System.Text.Json;

namespace Hammer5Git.Core.Services;

public class AddonWatcherService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ShadowGitService _gitService;
    private readonly VmapConverterAndSanitizer _vmapConverter;
    private readonly Cs2EnvironmentService _envService;
    private bool _disposed;

    private readonly Dictionary<string, AddonWatchSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);

    // Key: "{addonName}|{canonicalRelPath}" (OrdinalIgnoreCase), Value: 已同步源文件的元数据与单调水位线
    private readonly ConcurrentDictionary<string, VmapProvenanceMetadata> _provenanceStore = new(StringComparer.OrdinalIgnoreCase);

    private static string GetWatermarkKey(string addonName, string canonicalRelPath)
        => $"{addonName.Trim()}|{canonicalRelPath.Replace('\\', '/').TrimStart('/')}";

    private static readonly TimeSpan WatermarkTolerance = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FutureClockSkewThreshold = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 判断候选 VMAP 文件是否满足水位线准入条件（严格无副作用判定）。
    /// </summary>
    public bool ShouldAcceptCandidateByWatermark(
        string addonName,
        string canonicalRelPath,
        DateTime candidateWriteTimeUtc,
        VmapSourceProvenance provenance)
    {
        // DEF-03 修复：如果是磁盘正式保存 (FormalDiskSave)，永远放行。
        // 水位线设计的初衷是压制重复滞后的自动保存草稿事件，绝对不能阻拦作为真实数据源 (Single Source of Truth) 的正式文件。
        if (provenance == VmapSourceProvenance.FormalDiskSave)
            return true;

        string key = GetWatermarkKey(addonName, canonicalRelPath);
        if (!_provenanceStore.TryGetValue(key, out var recorded))
            return true;

        // DEF-05 修复：时钟回拨保护，纯只读判定，绝不在判定阶段产生副作用修改状态。
        // 如果已记录的水位线比当前系统 UTC 时间还超前超过 2 分钟，说明发生过时钟回拨，直接放行而不篡改记录。
        if (recorded.LastSyncedWriteTimeUtc > DateTime.UtcNow.Add(FutureClockSkewThreshold))
            return true;

        return candidateWriteTimeUtc >= recorded.LastSyncedWriteTimeUtc - WatermarkTolerance;
    }

    /// <summary>
    /// 兼容重载（默认按 HammerAutoSave 判定）。
    /// </summary>
    public bool ShouldAcceptCandidateByWatermark(
        string addonName,
        string canonicalRelPath,
        DateTime candidateWriteTimeUtc)
        => ShouldAcceptCandidateByWatermark(addonName, canonicalRelPath, candidateWriteTimeUtc, VmapSourceProvenance.HammerAutoSave);

    /// <summary>
    /// 推进 VMAP 地图的水位线与来源归属元数据。
    /// </summary>
    public void AdvanceWatermark(
        string addonName,
        string canonicalRelPath,
        VmapSourceProvenance provenance,
        DateTime sourceWriteTimeUtc,
        string sourceFullPath,
        DateTime formalDiskWriteTimeUtc)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _provenanceStore.AddOrUpdate(
            key,
            _ => new VmapProvenanceMetadata
            {
                LastSyncedWriteTimeUtc = sourceWriteTimeUtc,
                LastSourceKind = provenance,
                LastSourcePath = sourceFullPath,
                LastAppliedWallClockUtc = DateTime.UtcNow,
                FormalDiskBaselineWriteTimeUtc = provenance == VmapSourceProvenance.FormalDiskSave ? sourceWriteTimeUtc : formalDiskWriteTimeUtc,
                LastFormalCommitHash = string.Empty,
                DiscardConditionFirstSeenUtc = default
            },
            (_, existing) =>
            {
                existing.LastSourceKind = provenance;
                existing.LastSourcePath = sourceFullPath;
                existing.LastAppliedWallClockUtc = DateTime.UtcNow;
                existing.DiscardConditionFirstSeenUtc = default;

                if (provenance == VmapSourceProvenance.FormalDiskSave)
                {
                    existing.FormalDiskBaselineWriteTimeUtc = sourceWriteTimeUtc;
                }
                else
                {
                    existing.FormalDiskBaselineWriteTimeUtc = formalDiskWriteTimeUtc;
                }

                // 时钟回拨重置：若历史记录超前过多，直接重置为当前源时间戳
                if (existing.LastSyncedWriteTimeUtc > DateTime.UtcNow.Add(FutureClockSkewThreshold))
                {
                    existing.LastSyncedWriteTimeUtc = sourceWriteTimeUtc;
                }
                else if (sourceWriteTimeUtc > existing.LastSyncedWriteTimeUtc)
                {
                    existing.LastSyncedWriteTimeUtc = sourceWriteTimeUtc;
                }

                return existing;
            });
    }

    /// <summary>
    /// 兼容重载。
    /// </summary>
    public void AdvanceWatermark(string addonName, string canonicalRelPath, DateTime candidateWriteTimeUtc)
        => AdvanceWatermark(addonName, canonicalRelPath, VmapSourceProvenance.HammerAutoSave, candidateWriteTimeUtc, string.Empty, default);

    public void RemoveWatermark(string addonName, string canonicalRelPath)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _provenanceStore.TryRemove(key, out _);
    }

    internal static string DetermineCommitPrefix(bool isInitialBaseline, bool anyAutoSave)
    {
        if (isInitialBaseline)
            return "[Init]";
        return anyAutoSave ? "[AutoSave]" : "[Save]";
    }

    internal List<VmapConverterAndSanitizer.VmapSourceCandidate> CollectAllVmapCandidatesInAddon(AddonProfile profile)
    {
        var results = new List<VmapConverterAndSanitizer.VmapSourceCandidate>();
        string?[] roots = [profile.ContentAddonPath, profile.AutoSaveAddonPath];

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var vmapFiles = Directory.EnumerateFiles(root, "*.vmap", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.vmap_autosave", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var file in vmapFiles)
            {
                if (IsPathSuppressed(file))
                    continue;

                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: false, out string relInSource, out bool isFromAutoSaveRoot))
                    continue;

                var (canonicalRel, isAutoSave) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(relInSource, isFromAutoSaveRoot);
                DateTime mtimeUtc;
                try
                {
                    mtimeUtc = File.GetLastWriteTimeUtc(file);
                }
                catch
                {
                    continue;
                }

                int slot = isAutoSave ? VmapConverterAndSanitizer.ExtractAutoSaveSlotNumber(file) : -1;
                results.Add(new VmapConverterAndSanitizer.VmapSourceCandidate(
                    file, canonicalRel, isAutoSave, mtimeUtc, slot));
            }
        }

        return results;
    }

    public event Action<string, CommitRecord>? CommitCreated;
    public event Action<CorruptedFileAlert>? CorruptedFileDetected;
    public event Action<string, string>? LogMessage;
    public event Action<string>? TrackedFilesChanged;

    public AddonWatcherService(
        AppConfig config,
        ShadowGitService gitService,
        VmapConverterAndSanitizer vmapConverter,
        Cs2EnvironmentService? envService = null)
    {
        _config = config;
        _gitService = gitService;
        _vmapConverter = vmapConverter;
        _envService = envService ?? new Cs2EnvironmentService();
    }

    public bool IsWatching(string addonName) => _sessions.ContainsKey(addonName);

    public int ActiveWatchCount => _sessions.Count;

    /// <summary>
    /// Temporarily suppresses FileSystemWatcher events for a specific path (e.g., when overwriting a file during restore)
    /// to avoid triggering a self-induced conversion/commit loop.
    /// </summary>
    public void SuppressPathEvents(string fullPath, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        string normalized = Path.GetFullPath(fullPath);
        _suppressedPaths[normalized] = DateTime.UtcNow.Add(duration);
    }

    public bool IsPathSuppressed(string fullPath)
    {
        if (_suppressedPaths.IsEmpty || string.IsNullOrWhiteSpace(fullPath))
            return false;

        string normalized = Path.GetFullPath(fullPath);
        if (_suppressedPaths.TryGetValue(normalized, out var until))
        {
            if (DateTime.UtcNow <= until)
                return true;

            _suppressedPaths.TryRemove(normalized, out _);
        }
        return false;
    }

    public async Task StartWatchingAsync(AddonProfile profile, bool performInitialSync = true, CancellationToken ct = default)
    {
        if (Cs2EnvironmentService.IsExcludedAddonName(profile.AddonName))
            return;

        StopWatching(profile.AddonName);

        await _gitService.EnsureInitializedAsync(profile, ct);
        LoadProvenanceStore(profile);

        if (performInitialSync)
        {
            await SyncFullAddonAsync(profile, ct);
        }

        // 新会话启动时，上一轮编辑会话已必然结束；若存在未保存的孤立 AutoSave 草稿，立即对齐回退
        try
        {
            await ReconcileDiscardedAutosavesAsync(profile, ignoreGracePeriod: true, ct);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(profile.AddonName, $"启动时草稿对齐异常: {ex.Message}");
        }

        var session = new AddonWatchSession(this, profile, _config, _gitService);
        session.Start();
        _sessions[profile.AddonName] = session;
        profile.IsMonitoringEnabled = true;
        profile.StatusText = "监控中";
        LogMessage?.Invoke(profile.AddonName, $"实时守护中: {profile.ContentAddonPath}");
    }

    /// <summary>
    /// 清理指定 Addon 在内存水位线/溯源记录 (_provenanceStore) 中的所有条目，防止内存泄漏。
    /// </summary>
    public void ClearProvenanceForAddon(string addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return;

        string prefix = $"{addonName.Trim()}|";
        var keysToRemove = _provenanceStore.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _provenanceStore.TryRemove(key, out _);
        }
    }

    public void StopWatching(string addonName, bool clearProvenance = false)
    {
        if (_sessions.Remove(addonName, out var session))
        {
            session.Profile.IsMonitoringEnabled = false;
            session.Profile.StatusText = "已停止";
            session.Dispose();
        }

        if (clearProvenance)
        {
            ClearProvenanceForAddon(addonName);
        }
    }

    /// <summary>
    /// Scans all trackable files in the addon (content + game), cleans up orphaned files in the shadow repo,
    /// and syncs any changes into the shadow repository.
    /// </summary>
    public async Task<CommitRecord?> SyncFullAddonAsync(AddonProfile profile, CancellationToken ct = default)
    {
        await _gitService.EnsureInitializedAsync(profile, ct);
        bool isInitialBaseline = !await _gitService.HasAnyCommitsAsync(profile, ct);

        var changedSummaries = new List<string>();
        bool anyAutoSave = false;
        var activeShadowRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 扫描 ContentAddonPath 下的非 VMAP 文件
        if (Directory.Exists(profile.ContentAddonPath))
        {
            var nonVmapContentFiles = Directory.EnumerateFiles(profile.ContentAddonPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !VmapConverterAndSanitizer.IsVmapRelatedFile(f))
                .OrderBy(File.GetLastWriteTimeUtc);

            foreach (var file in nonVmapContentFiles)
            {
                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: false, out string relInContent))
                    continue;

                activeShadowRelativePaths.Add("content/" + relInContent);

                var res = await ProcessSingleFileChangeAsync(profile, file, isGameSide: false, isDeleted: false, ct);
                if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                {
                    changedSummaries.Add(res.SummaryFragment);
                }
            }
        }

        // 2. 收集 ContentAddonPath + AutoSaveAddonPath 下所有 VMAP 候选，按 CanonicalRelativePath 分组仲裁
        var vmapCandidates = CollectAllVmapCandidatesInAddon(profile);
        foreach (var group in vmapCandidates.GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            activeShadowRelativePaths.Add(group.Key);

            // DEF-04 类比修复：若候选组中缺少正式保存候选，尝试直接探测磁盘真实文件并补全，防止仅依赖目录遍历遗漏
            var candidatesList = group.ToList();
            if (!candidatesList.Any(c => !c.IsAutoSave))
            {
                try
                {
                    string formalDiskPath = profile.ResolveDiskFilePath(group.Key);
                    if (File.Exists(formalDiskPath))
                    {
                        candidatesList.Add(new VmapConverterAndSanitizer.VmapSourceCandidate(
                            formalDiskPath,
                            group.Key,
                            IsAutoSave: false,
                            File.GetLastWriteTimeUtc(formalDiskPath),
                            SlotNumber: -1));
                    }
                }
                catch
                {
                    // 保护磁盘读取异常，避免中断整体同步
                }
            }

            var winner = VmapConverterAndSanitizer.SelectNewestVmapCandidate(candidatesList);

            VmapSourceProvenance provenance = winner.IsAutoSave ? VmapSourceProvenance.HammerAutoSave : VmapSourceProvenance.FormalDiskSave;
            if (!ShouldAcceptCandidateByWatermark(profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc, provenance))
                continue;

            var res = await ProcessSingleFileChangeAsync(profile, winner.FullSourcePath, isGameSide: false, isDeleted: false, ct);
            if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
            {
                changedSummaries.Add(res.SummaryFragment);
                if (res.WasAutoSave)
                    anyAutoSave = true;
            }
        }

        // 3. 扫描 GameAddonPath 配置文件
        if (profile.TrackGameSideConfigs && Directory.Exists(profile.GameAddonPath))
        {
            var gameFiles = Directory.EnumerateFiles(profile.GameAddonPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in gameFiles)
            {
                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: true, out string relInGame))
                    continue;

                activeShadowRelativePaths.Add("game/" + relInGame);

                var res = await ProcessSingleFileChangeAsync(profile, file, isGameSide: true, isDeleted: false, ct);
                if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                {
                    changedSummaries.Add(res.SummaryFragment);
                }
            }
        }

        CleanOrphanedShadowFiles(profile, activeShadowRelativePaths, changedSummaries);

        if (changedSummaries.Count == 0)
        {
            TrackedFilesChanged?.Invoke(profile.AddonName);
            return null;
        }

        string commitPrefix = DetermineCommitPrefix(isInitialBaseline, anyAutoSave);
        string commitMessage = BuildAggregateCommitMessage(commitPrefix, changedSummaries);

        var record = await _gitService.CommitPendingChangesAsync(profile, commitMessage, _config.MaxAutoCommits, ct);
        TrackedFilesChanged?.Invoke(profile.AddonName);

        if (record != null)
        {
            // 当次提交不包含任何 AutoSave 草稿时，说明内容源自正式保存，更新 refs/hammer5git/last-formal 锚点
            if (!anyAutoSave)
            {
                await _gitService.UpdateFormalAnchorRefAsync(profile, record.Hash, ct);
            }

            SaveProvenanceStore(profile);

            if (isInitialBaseline || ShadowGitService.IsInitOrMetadataCommitSubject(record.Subject))
            {
                LogMessage?.Invoke(profile.AddonName, $"已建立初始基准快照 [{record.ShortHash}]");
            }
            else
            {
                CommitCreated?.Invoke(profile.AddonName, record);
                LogMessage?.Invoke(profile.AddonName, $"已提交版本 [{record.ShortHash}]: {record.Subject}");
            }
        }
        return record;
    }

    private void CleanOrphanedShadowFiles(
        AddonProfile profile,
        HashSet<string> activeShadowRelativePaths,
        List<string> changedSummaries)
    {
        string[] subTrees = profile.TrackGameSideConfigs ? ["content", "game"] : ["content"];
        foreach (var sub in subTrees)
        {
            string shadowSubDir = Path.Combine(profile.ShadowRepoPath, sub);
            if (!Directory.Exists(shadowSubDir))
                continue;

            foreach (var shadowFile in Directory.EnumerateFiles(shadowSubDir, "*.*", SearchOption.AllDirectories))
            {
                string relToRepo = Path.GetRelativePath(profile.ShadowRepoPath, shadowFile).Replace('\\', '/');
                if (!activeShadowRelativePaths.Contains(relToRepo))
                {
                    try
                    {
                        File.Delete(shadowFile);
                        RemoveWatermark(profile.AddonName, relToRepo);
                        changedSummaries.Add($"清理已删文件 {relToRepo}");
                    }
                    catch
                    {
                        // Ignore locked or inaccessible file
                    }
                }
            }
        }
    }

    public async Task<(bool Processed, bool WasAutoSave, string SummaryFragment)> ProcessSingleFileChangeAsync(
        AddonProfile profile,
        string fullSourcePath,
        bool isGameSide,
        bool isDeleted,
        CancellationToken ct = default)
    {
        if (!_gitService.ShouldTrackFile(profile, fullSourcePath, isGameSide, out string relativeInSource, out bool isFromAutoSaveRoot))
            return (false, false, string.Empty);

        if (!isGameSide && VmapConverterAndSanitizer.IsVmapRelatedFile(fullSourcePath))
        {
            var (canonicalRelPath, isAutoSave) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(relativeInSource, isFromAutoSaveRoot);
            string targetShadowFile = Path.Combine(profile.ShadowRepoPath, canonicalRelPath.Replace('/', Path.DirectorySeparatorChar));

            if (isDeleted)
            {
                if (!isAutoSave && File.Exists(targetShadowFile))
                {
                    File.Delete(targetShadowFile);
                    RemoveWatermark(profile.AddonName, canonicalRelPath);
                    return (true, false, $"删除 {canonicalRelPath}");
                }
                return (false, isAutoSave, string.Empty);
            }

            if (!await WaitForFileUnlockedAsync(fullSourcePath, TimeSpan.FromSeconds(8), ct))
            {
                LogMessage?.Invoke(profile.AddonName, $"文件仍被锁定，跳过本次读取: {Path.GetFileName(fullSourcePath)}");
                return (false, isAutoSave, string.Empty);
            }

            DateTime sourceWriteTimeUtc = File.GetLastWriteTimeUtc(fullSourcePath);

            string? previousKv2 = File.Exists(targetShadowFile)
                ? await File.ReadAllTextAsync(targetShadowFile, Encoding.UTF8, ct)
                : null;

            var conv = await _vmapConverter.ConvertAndSanitizeToKv2Async(fullSourcePath, ct);
            if (!conv.Success)
            {
                if (isAutoSave)
                {
                    LogMessage?.Invoke(profile.AddonName, $"[跳过损坏的 AutoSave] {Path.GetFileName(fullSourcePath)}: {conv.ErrorMessage}");
                    return (false, true, string.Empty);
                }
                else
                {
                    string dumpDir = Path.Combine(profile.ShadowRepoPath, ".corrupted_dumps");
                    Directory.CreateDirectory(dumpDir);
                    string dumpPath = Path.Combine(dumpDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(fullSourcePath)}");
                    try
                    {
                        if (File.Exists(fullSourcePath))
                            File.Copy(fullSourcePath, dumpPath, overwrite: true);
                    }
                    catch { }

                    var alert = new CorruptedFileAlert
                    {
                        AddonName = profile.AddonName,
                        OriginalFilePath = fullSourcePath,
                        DumpFilePath = dumpPath,
                        ErrorDetail = conv.ErrorMessage,
                        Timestamp = DateTime.Now,
                        IsAutoSave = false
                    };

                    LogMessage?.Invoke(profile.AddonName, $"[严重警告: 拦截到损坏的 VMAP] {fullSourcePath} -> {conv.ErrorMessage}");
                    CorruptedFileDetected?.Invoke(alert);
                    return (false, false, string.Empty);
                }
            }

            DateTime formalDiskWriteTimeUtc = default;
            try
            {
                string formalDiskPath = profile.ResolveDiskFilePath(canonicalRelPath);
                if (File.Exists(formalDiskPath))
                {
                    formalDiskWriteTimeUtc = File.GetLastWriteTimeUtc(formalDiskPath);
                }
            }
            catch { }

            VmapSourceProvenance provenance = isAutoSave ? VmapSourceProvenance.HammerAutoSave : VmapSourceProvenance.FormalDiskSave;
            // 文件合法且转换成功，立即推进单调水位线与来源归属（即便后续因无语义差异不产生 Git 提交，也记录该最新状态）
            AdvanceWatermark(
                profile.AddonName,
                canonicalRelPath,
                provenance,
                sourceWriteTimeUtc,
                fullSourcePath,
                formalDiskWriteTimeUtc);

            if (previousKv2 != null && string.Equals(previousKv2, conv.SanitizedKv2Content, StringComparison.Ordinal))
            {
                return (false, isAutoSave, string.Empty);
            }

            var (hasMeaningfulChange, _, semanticSummary) = VmapConverterAndSanitizer.AnalyzeVmapSemanticDiff(
                previousKv2,
                conv.SanitizedKv2Content);

            if (!hasMeaningfulChange)
            {
                // Ignore saves that only changed camera/selection/visibility/prefix metadata without any entity/mesh changes
                return (false, isAutoSave, string.Empty);
            }

            string? targetDir = Path.GetDirectoryName(targetShadowFile);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            await File.WriteAllTextAsync(targetShadowFile, conv.SanitizedKv2Content, new UTF8Encoding(false), ct);

            string displayMapName = Path.GetFileName(canonicalRelPath);
            return (true, isAutoSave, $"{displayMapName} ({semanticSummary})");
        }
        else
        {
            // 防御：若属于自动保存目录下的文件，绝不能作为常规二进制/文本资产写入或删除
            if (isFromAutoSaveRoot)
                return (false, false, string.Empty);

            string repoRelPath = (isGameSide ? "game/" : "content/") + relativeInSource;
            string targetShadowFile = Path.Combine(profile.ShadowRepoPath, repoRelPath.Replace('/', Path.DirectorySeparatorChar));

            if (isDeleted)
            {
                if (File.Exists(targetShadowFile))
                {
                    File.Delete(targetShadowFile);
                    return (true, false, $"删除 {repoRelPath}");
                }
                return (false, false, string.Empty);
            }

            if (!File.Exists(fullSourcePath))
                return (false, false, string.Empty);

            if (!await WaitForFileUnlockedAsync(fullSourcePath, TimeSpan.FromSeconds(5), ct))
                return (false, false, string.Empty);

            string? targetDir = Path.GetDirectoryName(targetShadowFile);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            if (File.Exists(targetShadowFile) && FilesAreEqual(fullSourcePath, targetShadowFile))
                return (false, false, string.Empty);

            try
            {
                File.Copy(fullSourcePath, targetShadowFile, overwrite: true);
                return (true, false, $"更新 {repoRelPath}");
            }
            catch (IOException ex)
            {
                NotifyLogMessage(profile.AddonName, $"文件复制失败 (可能被短暂锁定或访问冲突)，跳过本次处理: {repoRelPath} ({ex.Message})");
                return (false, false, string.Empty);
            }
        }
    }

    private static bool FilesAreEqual(string path1, string path2)
    {
        var f1 = new FileInfo(path1);
        var f2 = new FileInfo(path2);
        if (!f1.Exists || !f2.Exists || f1.Length != f2.Length)
            return false;

        if (f1.Length == 0)
            return true;

        const int bufferSize = 64 * 1024;
        byte[] buf1 = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] buf2 = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            using var fs1 = new FileStream(path1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
            using var fs2 = new FileStream(path2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);

            while (true)
            {
                int r1 = fs1.Read(buf1, 0, bufferSize);
                int r2 = fs2.Read(buf2, 0, bufferSize);
                if (r1 != r2)
                    return false;
                if (r1 == 0)
                    return true;
                if (!buf1.AsSpan(0, r1).SequenceEqual(buf2.AsSpan(0, r2)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf1);
            ArrayPool<byte>.Shared.Return(buf2);
        }
    }

    private static async Task<bool> WaitForFileUnlockedAsync(string filePath, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
                return false;

            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 0)
                    return true;
            }
            catch (IOException)
            {
                // Still locked by Hammer write
            }
            await Task.Delay(250, ct);
        }
        return false;
    }

    internal static string BuildAggregateCommitMessage(string prefix, List<string> fragments)
    {
        if (fragments.Count == 1)
            return $"{prefix} {fragments[0]}";

        var vmaps = fragments.Where(f => f.Contains(".vmap", StringComparison.OrdinalIgnoreCase)).ToList();
        if (vmaps.Count > 0)
        {
            string main = string.Join(" | ", vmaps.Take(2));
            int others = fragments.Count - vmaps.Take(2).Count();
            return others > 0 ? $"{prefix} {main} (+其余 {others} 个文件)" : $"{prefix} {main}";
        }

        string joined = string.Join(", ", fragments.Take(3));
        return fragments.Count > 3
            ? $"[Asset] {joined} 等 {fragments.Count} 个文件"
            : $"[Asset] {joined}";
    }

    private void NotifyCommitCreated(string addonName, CommitRecord record) => CommitCreated?.Invoke(addonName, record);
    private void NotifyLogMessage(string addonName, string message) => LogMessage?.Invoke(addonName, message);
    private void NotifyTrackedFilesChanged(string addonName) => TrackedFilesChanged?.Invoke(addonName);

    private static string GetProvenanceFilePath(AddonProfile profile)
    {
        return Path.Combine(profile.ShadowRepoPath, ".git", "hammer5git_provenance.json");
    }

    private void LoadProvenanceStore(AddonProfile profile)
    {
        try
        {
            string targetPath = GetProvenanceFilePath(profile);
            if (!File.Exists(targetPath))
                return;

            string json = File.ReadAllText(targetPath, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<Dictionary<string, VmapProvenanceMetadata>>(json);
            if (entries != null)
            {
                foreach (var kvp in entries)
                {
                    _provenanceStore[kvp.Key] = kvp.Value;
                }
            }
        }
        catch
        {
            // 静默吞掉所有 IO / JSON 异常
        }
    }

    private void SaveProvenanceStore(AddonProfile profile)
    {
        try
        {
            string gitDir = Path.Combine(profile.ShadowRepoPath, ".git");
            if (!Directory.Exists(gitDir))
                return;

            string targetPath = GetProvenanceFilePath(profile);
            string prefix = $"{profile.AddonName.Trim()}|";

            var addonEntries = _provenanceStore
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            var json = JsonSerializer.Serialize(addonEntries, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // 静默吞掉所有 IO / JSON 异常
        }
    }

    /// <summary>
    /// 内容校验兜底：确保影子仓库中指定地图的内容与当前真实磁盘正式保存文件完全一致。
    /// 若内容存在差异（Ordinal 比较），则写入转换后的 KV2 内容并创建前向对齐提交；若已一致，则不产生空提交。
    /// </summary>
    private async Task<(bool Committed, string? ErrorMessage)> EnsureShadowMapMatchesFormalDiskAsync(
        AddonProfile profile,
        string canonicalRel,
        string formalDiskPath,
        CancellationToken ct)
    {
        try
        {
            var conv = await _vmapConverter.ConvertAndSanitizeToKv2Async(formalDiskPath, ct);
            if (!conv.Success)
            {
                return (false, $"无法转换正式地图文件: {conv.ErrorMessage}");
            }

            string targetShadowFile = Path.Combine(profile.ShadowRepoPath, canonicalRel.Replace('/', Path.DirectorySeparatorChar));
            string? shadowContent = File.Exists(targetShadowFile)
                ? await File.ReadAllTextAsync(targetShadowFile, Encoding.UTF8, ct)
                : null;

            // 比较影子仓库目标文件与磁盘真实文件转换后的内容 (Ordinal 比较)
            if (string.Equals(shadowContent, conv.SanitizedKv2Content, StringComparison.Ordinal))
            {
                // 内容已完全一致，无需额外提交
                return (false, null);
            }

            string? targetDir = Path.GetDirectoryName(targetShadowFile);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            await File.WriteAllTextAsync(targetShadowFile, conv.SanitizedKv2Content, new UTF8Encoding(false), ct);

            string commitSubject = "[Reconcile] 丢弃未保存的自动保存草稿，回退至磁盘正式存档";
            var commit = await _gitService.CommitPendingChangesAsync(profile, commitSubject, _config.MaxAutoCommits, ct);
            if (commit != null)
            {
                await _gitService.UpdateFormalAnchorRefAsync(profile, commit.Hash, ct);
                NotifyCommitCreated(profile.AddonName, commit);
                return (true, null);
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 检查并对齐被用户丢弃的自动保存草稿（例如在 Hammer 中编辑触发了自动保存，但用户关闭时选择“不保存”）。
    /// </summary>
    public async Task<int> ReconcileDiscardedAutosavesAsync(
        AddonProfile profile,
        bool ignoreGracePeriod = false,
        CancellationToken ct = default)
    {
        int reconciledCount = 0;
        try
        {
            // 若 Hammer 或 CS2 正在运行，绝不能误判定草稿被废弃
            if (_envService.IsHammerOrCs2Running(out _))
            {
                // 若此前在 Hammer 关闭时曾记录过废弃观测时间，重置为 default 并持久化，
                // 确保用户重新打开 Hammer 编辑时草稿不会因为此前累计的观测时间而被误杀回滚。
                bool changed = false;
                string pfx = $"{profile.AddonName.Trim()}|";
                foreach (var kvp in _provenanceStore.Where(kvp => kvp.Key.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)))
                {
                    if (kvp.Value.DiscardConditionFirstSeenUtc != default)
                    {
                        kvp.Value.DiscardConditionFirstSeenUtc = default;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveProvenanceStore(profile);
                }

                return 0;
            }

            string prefix = $"{profile.AddonName.Trim()}|";
            var matchingEntries = _provenanceStore
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var kvp in matchingEntries)
            {
                if (ct.IsCancellationRequested)
                    break;

                string key = kvp.Key;
                var entry = kvp.Value;
                string canonicalRel = key[prefix.Length..];

                string formalDiskPath;
                try
                {
                    formalDiskPath = profile.ResolveDiskFilePath(canonicalRel);
                }
                catch
                {
                    continue;
                }

                if (!File.Exists(formalDiskPath))
                    continue;

                DateTime formalMtimeUtc;
                try
                {
                    formalMtimeUtc = File.GetLastWriteTimeUtc(formalDiskPath);
                }
                catch
                {
                    continue;
                }

                // 判断是否为被丢弃的草稿：当前内容来自 AutoSave，且磁盘正式文件修改时间未晚于基线（1秒容差内），
                // 说明用户在此后未曾按过保存，Hammer 关闭后草稿已被废弃。
                // 关键健壮性保护：若 Hammer / CS2 正在运行，绝不能误判定草稿被废弃！
                bool isHammerRunning = _envService.IsHammerOrCs2Running(out _);
                bool discarded = !isHammerRunning
                    && entry.LastSourceKind == VmapSourceProvenance.HammerAutoSave
                    && formalMtimeUtc <= entry.FormalDiskBaselineWriteTimeUtc.AddSeconds(1);

                if (!discarded)
                {
                    // 若用户已手动保存或正式文件已更新，或 Hammer 正在运行，重置首次观测时间
                    if (entry.DiscardConditionFirstSeenUtc != default)
                    {
                        entry.DiscardConditionFirstSeenUtc = default;
                        SaveProvenanceStore(profile);
                    }
                    continue;
                }

                if (entry.DiscardConditionFirstSeenUtc == default)
                {
                    entry.DiscardConditionFirstSeenUtc = DateTime.UtcNow;
                    SaveProvenanceStore(profile);
                }

                if (!ignoreGracePeriod)
                {
                    int graceSeconds = Math.Clamp(_config.AutosaveDiscardGraceSeconds, 30, 7200);
                    if (DateTime.UtcNow - entry.DiscardConditionFirstSeenUtc < TimeSpan.FromSeconds(graceSeconds))
                    {
                        // 尚在宽限期内，等待用户可能的操作
                        continue;
                    }
                }

                // 到期执行对齐，检查配置是否开启
                if (!_config.ReconcileDiscardedAutoSave)
                    continue;

                // 准备执行硬重置前做最终检查：Hammer 绝不能处于运行状态
                if (_envService.IsHammerOrCs2Running(out _))
                    return reconciledCount;

                string mapDisplayName = Path.GetFileName(canonicalRel);
                var (rollbackResult, message) = await _gitService.RollbackToFormalAnchorAsync(profile, mapDisplayName, ct);

                switch (rollbackResult)
                {
                    case FormalAnchorRollbackResult.RolledBack:
                    {
                        // PART2-b: 硬重置成功后执行内容校验兜底，确保影子仓库文件与真实磁盘文件字节一致
                        var (committed, err) = await EnsureShadowMapMatchesFormalDiskAsync(profile, canonicalRel, formalDiskPath, ct);
                        if (err != null)
                        {
                            LogMessage?.Invoke(profile.AddonName, $"[对齐失败] 地图 {mapDisplayName} 虽然已硬重置，但内容校验/转换异常: {err}");
                            break;
                        }

                        entry.LastSourceKind = VmapSourceProvenance.FormalDiskSave;
                        entry.LastSyncedWriteTimeUtc = formalMtimeUtc;
                        entry.FormalDiskBaselineWriteTimeUtc = formalMtimeUtc;
                        entry.LastAppliedWallClockUtc = DateTime.UtcNow;
                        entry.DiscardConditionFirstSeenUtc = default;
                        reconciledCount++;
                        LogMessage?.Invoke(profile.AddonName, $"[对齐成功] 地图 {mapDisplayName} 草稿已丢弃，已通过 Git 硬重置回退至正式存档: {message}");
                        break;
                    }

                    case FormalAnchorRollbackResult.AlreadyAligned:
                    {
                        var (committed, err) = await EnsureShadowMapMatchesFormalDiskAsync(profile, canonicalRel, formalDiskPath, ct);
                        if (err != null)
                        {
                            LogMessage?.Invoke(profile.AddonName, $"[对齐失败] 无法转换正式地图文件 {formalDiskPath}: {err}");
                        }
                        else if (committed)
                        {
                            entry.LastSourceKind = VmapSourceProvenance.FormalDiskSave;
                            entry.LastSyncedWriteTimeUtc = formalMtimeUtc;
                            entry.FormalDiskBaselineWriteTimeUtc = formalMtimeUtc;
                            entry.LastAppliedWallClockUtc = DateTime.UtcNow;
                            entry.DiscardConditionFirstSeenUtc = default;
                            reconciledCount++;
                            LogMessage?.Invoke(profile.AddonName, $"[前向对齐成功] 地图 {mapDisplayName} HEAD 虽与锚点一致但地图内容存在差异，已前向提交正式存档快照");
                        }
                        else
                        {
                            // 影子仓库与磁盘文件已完全一致，不产生空提交，亦不虚报对齐
                            entry.LastSourceKind = VmapSourceProvenance.FormalDiskSave;
                            entry.LastSyncedWriteTimeUtc = formalMtimeUtc;
                            entry.FormalDiskBaselineWriteTimeUtc = formalMtimeUtc;
                            entry.LastAppliedWallClockUtc = DateTime.UtcNow;
                            entry.DiscardConditionFirstSeenUtc = default;
                            LogMessage?.Invoke(profile.AddonName, $"[跳过对齐] 地图 {mapDisplayName} 影子仓库内容已与磁盘正式文件一致，无需额外提交");
                        }
                        break;
                    }

                    // WHY: 即使 RollbackToFormalAnchorAsync 返回 Failed（最常见原因是首次接入 addon 时最新修改为 AutoSave，
                    // 导致未曾生成 refs/hammer5git/last-formal 锚点引用），该状态亦属正常初始业务场景而非致命异常。
                    // 前向对齐路径（EnsureShadowMapMatchesFormalDiskAsync）直接将真实磁盘文件转换并覆盖写入影子仓库，
                    // 完全独立且不依赖任何 Git 引用或历史锚点。因此 Failed 分支绝不能直接跳过，而必须同 RequiresForwardReconcile
                    // 一样执行前向对齐校验。
                    case FormalAnchorRollbackResult.RequiresForwardReconcile:
                    case FormalAnchorRollbackResult.Failed:
                    default:
                    {
                        var (committed, err) = await EnsureShadowMapMatchesFormalDiskAsync(profile, canonicalRel, formalDiskPath, ct);
                        if (err != null)
                        {
                            LogMessage?.Invoke(profile.AddonName, $"[对齐失败] 无法转换正式地图文件 {formalDiskPath}: {err}");
                        }
                        else if (committed)
                        {
                            entry.LastSourceKind = VmapSourceProvenance.FormalDiskSave;
                            entry.LastSyncedWriteTimeUtc = formalMtimeUtc;
                            entry.FormalDiskBaselineWriteTimeUtc = formalMtimeUtc;
                            entry.LastAppliedWallClockUtc = DateTime.UtcNow;
                            entry.DiscardConditionFirstSeenUtc = default;
                            reconciledCount++;
                            LogMessage?.Invoke(profile.AddonName, $"[前向对齐成功] 地图 {mapDisplayName} 草稿已丢弃，已前向提交正式存档快照至仓库 ({message})");
                        }
                        else
                        {
                            // 影子仓库与磁盘文件已完全一致，不产生空提交，亦不虚报对齐
                            entry.LastSourceKind = VmapSourceProvenance.FormalDiskSave;
                            entry.LastSyncedWriteTimeUtc = formalMtimeUtc;
                            entry.FormalDiskBaselineWriteTimeUtc = formalMtimeUtc;
                            entry.LastAppliedWallClockUtc = DateTime.UtcNow;
                            entry.DiscardConditionFirstSeenUtc = default;
                            LogMessage?.Invoke(profile.AddonName, $"[跳过对齐] 地图 {mapDisplayName} 影子仓库内容已与磁盘正式文件一致，无需额外提交");
                        }
                        break;
                    }
                }
            }

            SaveProvenanceStore(profile);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(profile.AddonName, $"草稿对齐异常: {ex.Message}");
        }

        return reconciledCount;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            foreach (var s in _sessions.Values)
            {
                s.Dispose();
            }
            _sessions.Clear();
            _provenanceStore.Clear();
            _suppressedPaths.Clear();
        }

        _disposed = true;
    }

    private sealed class AddonWatchSession : IDisposable
    {
        public AddonProfile Profile { get; }
        private readonly AddonWatcherService _parent;
        private readonly AppConfig _config;
        private readonly ShadowGitService _gitService;
        private readonly ConcurrentDictionary<string, (bool IsGameSide, bool IsDeleted)> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? _contentWatcher;
        private FileSystemWatcher? _gameWatcher;
        private FileSystemWatcher? _autoSaveWatcher;
        private FileSystemWatcher? _autoSaveBootstrapWatcher;
        private readonly object _watcherSync = new();
        private Timer? _debounceTimer;
        private Timer? _reconcileTimer;
        private readonly ConcurrentDictionary<string, int> _deferralCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public AddonWatchSession(
            AddonWatcherService parent,
            AddonProfile profile,
            AppConfig config,
            ShadowGitService gitService)
        {
            _parent = parent;
            Profile = profile;
            _config = config;
            _gitService = gitService;
        }

        public void Start()
        {
            _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _reconcileTimer = new Timer(OnReconcileTimerElapsed, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            if (Directory.Exists(Profile.ContentAddonPath))
            {
                _contentWatcher = CreateWatcher(Profile.ContentAddonPath, isGameSide: false);
            }

            if (Profile.TrackGameSideConfigs && Directory.Exists(Profile.GameAddonPath))
            {
                _gameWatcher = CreateWatcher(Profile.GameAddonPath, isGameSide: true);
            }

            TryAttachOrBootstrapAutoSaveWatcher(scanExistingOnAttach: false);
        }

        private void TryAttachOrBootstrapAutoSaveWatcher(bool scanExistingOnAttach)
        {
            string autoSavePath = Profile.AutoSaveAddonPath;
            if (string.IsNullOrWhiteSpace(autoSavePath))
                return;

            lock (_watcherSync)
            {
                if (_disposed || _autoSaveWatcher != null)
                    return;

                if (Directory.Exists(autoSavePath))
                {
                    DisposeBootstrapWatcherNoLock();
                    _autoSaveWatcher = CreateWatcher(autoSavePath, isGameSide: false);

                    if (scanExistingOnAttach)
                    {
                        try
                        {
                            var autoSaveFiles = Directory.EnumerateFiles(autoSavePath, "*.vmap", SearchOption.AllDirectories)
                                .Concat(Directory.EnumerateFiles(autoSavePath, "*.vmap_autosave", SearchOption.AllDirectories))
                                .Distinct(StringComparer.OrdinalIgnoreCase);

                            foreach (var file in autoSaveFiles)
                            {
                                EnqueueEvent(file, isGameSide: false, isDeleted: false);
                            }
                        }
                        catch
                        {
                            // 忽略枚举瞬态异常
                        }
                    }
                    return;
                }

                if (_autoSaveBootstrapWatcher != null)
                    return;

                string? existingAncestor = FindNearestExistingAncestor(autoSavePath);
                if (string.IsNullOrWhiteSpace(existingAncestor))
                    return;

                try
                {
                    var bootstrap = new FileSystemWatcher(existingAncestor)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName
                    };

                    void OnBootstrapFsEvent(object sender, FileSystemEventArgs e)
                    {
                        if (_disposed)
                            return;
                        if (Directory.Exists(autoSavePath))
                        {
                            TryAttachOrBootstrapAutoSaveWatcher(scanExistingOnAttach: true);
                        }
                    }

                    bootstrap.Created += OnBootstrapFsEvent;
                    bootstrap.Renamed += (s, e) => OnBootstrapFsEvent(s, e);
                    bootstrap.EnableRaisingEvents = true;
                    _autoSaveBootstrapWatcher = bootstrap;
                }
                catch
                {
                    // 若祖先目录权限受限或瞬态不可访问，静默降级
                }
            }
        }

        private static string? FindNearestExistingAncestor(string targetPath)
        {
            string? current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetPath));
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                    return current;
                current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            }
            return null;
        }

        private void DisposeBootstrapWatcherNoLock()
        {
            if (_autoSaveBootstrapWatcher != null)
            {
                try
                {
                    _autoSaveBootstrapWatcher.EnableRaisingEvents = false;
                    _autoSaveBootstrapWatcher.Dispose();
                }
                catch { }
                _autoSaveBootstrapWatcher = null;
            }
        }

        private FileSystemWatcher CreateWatcher(string path, bool isGameSide)
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 65536
            };

            watcher.Changed += (_, e) => EnqueueEvent(e.FullPath, isGameSide, isDeleted: false);
            watcher.Created += (_, e) => EnqueueEvent(e.FullPath, isGameSide, isDeleted: false);
            watcher.Deleted += (_, e) => EnqueueEvent(e.FullPath, isGameSide, isDeleted: true);
            watcher.Renamed += (_, e) =>
            {
                EnqueueEvent(e.OldFullPath, isGameSide, isDeleted: true);
                EnqueueEvent(e.FullPath, isGameSide, isDeleted: false);
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void EnqueueEvent(string fullPath, bool isGameSide, bool isDeleted)
        {
            if (_parent.IsPathSuppressed(fullPath))
                return;

            if (!_gitService.ShouldTrackFile(Profile, fullPath, isGameSide, out _))
                return;

            _pendingChanges[fullPath] = (isGameSide, isDeleted);
            int debounceMs = Math.Clamp(_config.DebounceMilliseconds, 500, 15000);
            try
            {
                _debounceTimer?.Change(debounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnDebounceTimerElapsed(object? state)
        {
            try
            {
                _ = FlushPendingChangesAsync();
            }
            catch
            {
                // 该方法本身不抛异常，绝不能让任何异常逃逸至线程池
            }
        }

        private void OnReconcileTimerElapsed(object? state)
        {
            try
            {
                if (_disposed || _cts.IsCancellationRequested)
                    return;

                _ = _parent.ReconcileDiscardedAutosavesAsync(Profile, ignoreGracePeriod: false, _cts.Token);
            }
            catch
            {
                // 绝不让任何异常逃逸至线程池
            }
        }

        private async Task FlushPendingChangesAsync()
        {
            if (_pendingChanges.IsEmpty || _cts.IsCancellationRequested || _disposed)
                return;

            try
            {
                await _flushLock.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_cts.IsCancellationRequested || _disposed)
                    return;

                var snapshot = _pendingChanges.ToArray();
                foreach (var kv in snapshot)
                {
                    _pendingChanges.TryRemove(kv.Key, out _);
                }

                bool isInitialBaseline = !await _gitService.HasAnyCommitsAsync(Profile, _cts.Token);
                var summaries = new List<string>();
                bool anyAutoSave = false;

                var vmapUpsertCandidates = new List<VmapConverterAndSanitizer.VmapSourceCandidate>();
                var vmapDeleteItems = new List<string>();
                var otherItems = new List<KeyValuePair<string, (bool IsGameSide, bool IsDeleted)>>();

                foreach (var item in snapshot)
                {
                    if (_parent.IsPathSuppressed(item.Key))
                        continue;

                    if (!item.Value.IsGameSide && VmapConverterAndSanitizer.IsVmapRelatedFile(item.Key))
                    {
                        if (!_gitService.ShouldTrackFile(Profile, item.Key, isGameSide: false, out string relInSource, out bool isFromAutoSaveRoot))
                            continue;

                        var (canonicalRel, isAutoSave) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(relInSource, isFromAutoSaveRoot);

                        if (item.Value.IsDeleted || !File.Exists(item.Key))
                        {
                            if (!isAutoSave)
                                vmapDeleteItems.Add(item.Key);
                            continue;
                        }

                        try
                        {
                            DateTime mtimeUtc = File.GetLastWriteTimeUtc(item.Key);
                            int slot = isAutoSave ? VmapConverterAndSanitizer.ExtractAutoSaveSlotNumber(item.Key) : -1;
                            vmapUpsertCandidates.Add(new VmapConverterAndSanitizer.VmapSourceCandidate(
                                item.Key, canonicalRel, isAutoSave, mtimeUtc, slot));
                        }
                        catch
                        {
                            // 文件在瞬态被移除，跳过
                        }
                    }
                    else
                    {
                        otherItems.Add(item);
                    }
                }

                void DeferGroup(IEnumerable<VmapConverterAndSanitizer.VmapSourceCandidate> groupToDefer)
                {
                    foreach (var cand in groupToDefer)
                    {
                        _pendingChanges[cand.FullSourcePath] = (IsGameSide: false, IsDeleted: false);
                    }
                    int debounceMs = Math.Clamp(_config.DebounceMilliseconds, 500, 15000);
                    try
                    {
                        _debounceTimer?.Change(debounceMs, Timeout.Infinite);
                    }
                    catch (ObjectDisposedException) { }
                }

                // 1. 按 CanonicalRelativePath 分组仲裁：每张图只取最新 1 个候选调用 1 次 dmxconvert
                foreach (var group in vmapUpsertCandidates.GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    var candidatesWithFormal = group.ToList();
                    string? formalDiskPath = null;
                    try
                    {
                        formalDiskPath = Profile.ResolveDiskFilePath(group.Key);
                    }
                    catch { }

                    bool isSuppressed = !string.IsNullOrWhiteSpace(formalDiskPath) && _parent.IsPathSuppressed(formalDiskPath);

                    // DEF-01 修复：在恢复文件 (Restore) 时，MainWindow 会调用 SuppressPathEvents 设置 8 秒压制窗口。
                    // 若在此期间 debounce 触发，旧逻辑会因 IsPathSuppressed 为 true 而直接不把正式文件加入候选列表，
                    // 导致候选列表降级为仅包含旧 autosave 草稿，从而使得旧草稿覆盖较新的正式恢复文件。
                    // 此处改为延期 (Defer) 整个候选组：重新将事件推回 _pendingChanges 并重启 debounce 计时器。
                    // 计数器最多允许重试 12 次，超时后自动放行，杜绝死锁。
                    if (isSuppressed)
                    {
                        int attempts = _deferralCounts.AddOrUpdate(group.Key, 1, (_, c) => c + 1);
                        if (attempts <= 12)
                        {
                            DeferGroup(group);
                            continue;
                        }
                        _deferralCounts.TryRemove(group.Key, out _);
                    }
                    else
                    {
                        _deferralCounts.TryRemove(group.Key, out _);
                    }

                    if (!string.IsNullOrWhiteSpace(formalDiskPath) &&
                        File.Exists(formalDiskPath) &&
                        !candidatesWithFormal.Any(c => string.Equals(c.FullSourcePath, formalDiskPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            candidatesWithFormal.Add(new VmapConverterAndSanitizer.VmapSourceCandidate(
                                formalDiskPath, group.Key, IsAutoSave: false, File.GetLastWriteTimeUtc(formalDiskPath), SlotNumber: -1));
                        }
                        catch { }
                    }

                    var winner = VmapConverterAndSanitizer.SelectNewestVmapCandidate(candidatesWithFormal);

                    VmapSourceProvenance provenance = winner.IsAutoSave ? VmapSourceProvenance.HammerAutoSave : VmapSourceProvenance.FormalDiskSave;
                    if (!_parent.ShouldAcceptCandidateByWatermark(Profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc, provenance))
                    {
                        continue;
                    }

                    var res = await _parent.ProcessSingleFileChangeAsync(
                        Profile, winner.FullSourcePath, isGameSide: false, isDeleted: false, _cts.Token);

                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        summaries.Add(res.SummaryFragment);
                        if (res.WasAutoSave)
                            anyAutoSave = true;
                    }
                }

                // 2. 处理正式 VMAP 删除事件（仅当本批次没有同图的有效更新且磁盘上确实已删除）
                var updatedCanonicalPaths = new HashSet<string>(
                    vmapUpsertCandidates.Select(c => c.CanonicalRelativePath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var delPath in vmapDeleteItems)
                {
                    if (_gitService.ShouldTrackFile(Profile, delPath, isGameSide: false, out string rel, out bool fromAutoSave))
                    {
                        var (canonicalRel, _) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(rel, fromAutoSave);
                        if (updatedCanonicalPaths.Contains(canonicalRel))
                            continue;
                    }

                    var res = await _parent.ProcessSingleFileChangeAsync(
                        Profile, delPath, isGameSide: false, isDeleted: true, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        summaries.Add(res.SummaryFragment);
                    }
                }

                // 3. 处理非 VMAP 文件变更
                foreach (var item in otherItems)
                {
                    var res = await _parent.ProcessSingleFileChangeAsync(
                        Profile, item.Key, item.Value.IsGameSide, item.Value.IsDeleted, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        summaries.Add(res.SummaryFragment);
                    }
                }

                if (summaries.Count > 0)
                {
                    string prefix = DetermineCommitPrefix(isInitialBaseline, anyAutoSave);
                    string msg = BuildAggregateCommitMessage(prefix, summaries);
                    var commit = await _gitService.CommitPendingChangesAsync(Profile, msg, _config.MaxAutoCommits, _cts.Token);
                    _parent.NotifyTrackedFilesChanged(Profile.AddonName);

                    if (commit != null)
                    {
                        // 当次提交不包含任何 AutoSave 草稿时，说明内容源自正式保存，更新 refs/hammer5git/last-formal 锚点
                        if (!anyAutoSave)
                        {
                            await _gitService.UpdateFormalAnchorRefAsync(Profile, commit.Hash, _cts.Token);
                        }

                        _parent.SaveProvenanceStore(Profile);

                        if (!isInitialBaseline && !ShadowGitService.IsInitOrMetadataCommitSubject(commit.Subject))
                        {
                            _parent.NotifyCommitCreated(Profile.AddonName, commit);
                            _parent.NotifyLogMessage(Profile.AddonName, $"自动提交成功 [{commit.ShortHash}]: {commit.Subject}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消视为正常退出，静默 return
                return;
            }
            catch (ObjectDisposedException)
            {
                // 对象销毁视为正常退出，静默 return
                return;
            }
            catch (Exception ex)
            {
                // 其他异常照旧上报日志，但绝不能逃逸到线程池
                try
                {
                    _parent.NotifyLogMessage(Profile.AddonName, $"监控处理异常: {ex.Message}");
                }
                catch
                {
                    // 确保日志上报异常也不逃逸
                }
            }
            finally
            {
                try { _flushLock.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _contentWatcher?.Dispose();
            _contentWatcher = null;

            _gameWatcher?.Dispose();
            _gameWatcher = null;

            lock (_watcherSync)
            {
                DisposeBootstrapWatcherNoLock();
                _autoSaveWatcher?.Dispose();
                _autoSaveWatcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _reconcileTimer?.Dispose();
            _reconcileTimer = null;

            // 删除 _flushLock.Dispose() 与 _cts.Dispose() 的立即同步调用：
            // 这两个对象没有非托管句柄（SemaphoreSlim 未使用 AvailableWaitHandle、CTS 未挂计时器），
            // 交给 GC 回收即可；提前 Dispose 才是竞态的根源。
        }
    }
}
