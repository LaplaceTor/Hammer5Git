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

    // Key: "{addonName}|{canonicalRelPath}" (OrdinalIgnoreCase), Value: 已同步至 autosave 分支的源文件最新 LastWriteTimeUtc
    private readonly ConcurrentDictionary<string, DateTime> _autoSaveWatermarks = new(StringComparer.OrdinalIgnoreCase);

    private static string GetWatermarkKey(string addonName, string canonicalRelPath)
        => $"{addonName.Trim()}|{canonicalRelPath.Replace('\\', '/').TrimStart('/')}";

    private static readonly TimeSpan WatermarkTolerance = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FutureClockSkewThreshold = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 判断候选 AutoSave 草稿是否满足单调递增水位线准入条件。
    /// </summary>
    public bool ShouldAcceptAutoSaveCandidate(
        string addonName,
        string canonicalRelPath,
        DateTime candidateWriteTimeUtc)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        if (!_autoSaveWatermarks.TryGetValue(key, out var recorded))
            return true;

        if (recorded > DateTime.UtcNow.Add(FutureClockSkewThreshold))
            return true;

        return candidateWriteTimeUtc >= recorded - WatermarkTolerance;
    }

    /// <summary>
    /// 推进指定地图在 autosave 分支的最新水位线。
    /// </summary>
    public void AdvanceAutoSaveWatermark(
        string addonName,
        string canonicalRelPath,
        DateTime sourceWriteTimeUtc)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _autoSaveWatermarks.AddOrUpdate(
            key,
            sourceWriteTimeUtc,
            (_, existing) =>
            {
                if (existing > DateTime.UtcNow.Add(FutureClockSkewThreshold))
                    return sourceWriteTimeUtc;
                return sourceWriteTimeUtc > existing ? sourceWriteTimeUtc : existing;
            });
    }

    public void RemoveAutoSaveWatermark(string addonName, string canonicalRelPath)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _autoSaveWatermarks.TryRemove(key, out _);
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

        if (performInitialSync)
        {
            await SyncFullAddonAsync(profile, ct);
        }

        var session = new AddonWatchSession(this, profile, _config, _gitService);
        session.Start();
        _sessions[profile.AddonName] = session;
        profile.IsMonitoringEnabled = true;
        profile.StatusText = "监控中";
        LogMessage?.Invoke(profile.AddonName, $"实时守护中: {profile.ContentAddonPath}");
    }

    /// <summary>
    /// 清理指定 Addon 在内存水位线中的所有条目，防止内存泄漏。
    /// </summary>
    public void ClearProvenanceForAddon(string addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return;

        string prefix = $"{addonName.Trim()}|";
        var keysToRemove = _autoSaveWatermarks.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _autoSaveWatermarks.TryRemove(key, out _);
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

        var mainChangedSummaries = new List<string>();
        var activeMainShadowRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 主仓库通道：扫描 ContentAddonPath 下的非 VMAP 文件
        if (Directory.Exists(profile.ContentAddonPath))
        {
            var nonVmapContentFiles = Directory.EnumerateFiles(profile.ContentAddonPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !VmapConverterAndSanitizer.IsVmapRelatedFile(f))
                .OrderBy(File.GetLastWriteTimeUtc);

            foreach (var file in nonVmapContentFiles)
            {
                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: false, out string relInContent))
                    continue;

                activeMainShadowRelativePaths.Add("content/" + relInContent);

                var res = await ProcessSingleFileChangeAsync(profile, file, isGameSide: false, isDeleted: false, ct);
                if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                {
                    mainChangedSummaries.Add(res.SummaryFragment);
                }
            }
        }

        // 2. 主仓库通道：扫描 ContentAddonPath 下的正式 .vmap 文件
        if (Directory.Exists(profile.ContentAddonPath))
        {
            var formalVmapFiles = Directory.EnumerateFiles(profile.ContentAddonPath, "*.vmap", SearchOption.AllDirectories)
                .Where(f => !f.Contains("_restored_", StringComparison.OrdinalIgnoreCase));

            foreach (var file in formalVmapFiles)
            {
                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: false, out string relInContent, out bool isFromAutoSaveRoot))
                    continue;

                if (isFromAutoSaveRoot)
                    continue;

                var (canonicalRel, isAutoSave) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(relInContent, isFromAutoSaveRoot);
                if (isAutoSave)
                    continue;

                activeMainShadowRelativePaths.Add(canonicalRel);

                var res = await ProcessSingleFileChangeAsync(profile, file, isGameSide: false, isDeleted: false, ct);
                if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                {
                    mainChangedSummaries.Add(res.SummaryFragment);
                }
            }
        }

        // 3. 主仓库通道：扫描 GameAddonPath 配置文件
        if (profile.TrackGameSideConfigs && Directory.Exists(profile.GameAddonPath))
        {
            var gameFiles = Directory.EnumerateFiles(profile.GameAddonPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in gameFiles)
            {
                if (!_gitService.ShouldTrackFile(profile, file, isGameSide: true, out string relInGame))
                    continue;

                activeMainShadowRelativePaths.Add("game/" + relInGame);

                var res = await ProcessSingleFileChangeAsync(profile, file, isGameSide: true, isDeleted: false, ct);
                if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                {
                    mainChangedSummaries.Add(res.SummaryFragment);
                }
            }
        }

        CleanOrphanedShadowFiles(profile, activeMainShadowRelativePaths, mainChangedSummaries);

        CommitRecord? mainRecord = null;
        if (mainChangedSummaries.Count > 0)
        {
            string commitPrefix = isInitialBaseline ? "[Init]" : "[Save]";
            string commitMessage = BuildAggregateCommitMessage(commitPrefix, mainChangedSummaries);
            mainRecord = await _gitService.CommitPendingChangesAsync(profile, commitMessage, _config.MaxAutoCommits, ct);
            if (mainRecord != null)
            {
                if (isInitialBaseline || ShadowGitService.IsInitOrMetadataCommitSubject(mainRecord.Subject))
                {
                    LogMessage?.Invoke(profile.AddonName, $"已建立初始基准快照 [{mainRecord.ShortHash}]");
                }
                else
                {
                    CommitCreated?.Invoke(profile.AddonName, mainRecord);
                    LogMessage?.Invoke(profile.AddonName, $"已提交版本 [{mainRecord.ShortHash}]: {mainRecord.Subject}");
                }
            }
        }

        // 4. 自动保存通道 (autosave Worktree)：扫描 AutoSave 目录，按 CanonicalRelativePath 槽位仲裁
        var autoChangedSummaries = new List<string>();
        var allVmaps = CollectAllVmapCandidatesInAddon(profile);
        var autoSaveGroups = allVmaps.Where(c => c.IsAutoSave).GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in autoSaveGroups)
        {
            var winner = VmapConverterAndSanitizer.SelectNewestVmapCandidate(group);
            if (!ShouldAcceptAutoSaveCandidate(profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc))
                continue;

            var res = await ProcessSingleFileChangeAsync(profile, winner.FullSourcePath, isGameSide: false, isDeleted: false, ct);
            if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
            {
                autoChangedSummaries.Add(res.SummaryFragment);
                AdvanceAutoSaveWatermark(profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc);
            }
        }

        CommitRecord? autoRecord = null;
        if (autoChangedSummaries.Count > 0)
        {
            string autoCommitMessage = BuildAggregateCommitMessage("[AutoSave]", autoChangedSummaries);
            autoRecord = await _gitService.CommitAutoSaveChangesAsync(profile, autoCommitMessage, _config.MaxAutoCommits, ct);
            if (autoRecord != null)
            {
                CommitCreated?.Invoke(profile.AddonName, autoRecord);
                LogMessage?.Invoke(profile.AddonName, $"已同步自动保存草稿 [{autoRecord.ShortHash}]: {autoRecord.Subject}");
            }
        }

        if (mainChangedSummaries.Count > 0 || autoChangedSummaries.Count > 0)
        {
            TrackedFilesChanged?.Invoke(profile.AddonName);
        }

        return mainRecord ?? autoRecord;
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
            if (isAutoSave)
            {
                await _gitService.EnsureAutoSaveWorktreeInitializedAsync(profile, ct);
            }

            string baseDir = isAutoSave ? profile.AutoSaveWorktreePath : profile.ShadowRepoPath;
            if (string.IsNullOrWhiteSpace(baseDir))
                return (false, isAutoSave, string.Empty);

            string targetShadowFile = Path.Combine(baseDir, canonicalRelPath.Replace('/', Path.DirectorySeparatorChar));

            if (isDeleted)
            {
                if (!isAutoSave && File.Exists(targetShadowFile))
                {
                    File.Delete(targetShadowFile);
                    return (true, false, $"删除 {canonicalRelPath}");
                }
                return (false, isAutoSave, string.Empty);
            }

            if (!await WaitForFileUnlockedAsync(fullSourcePath, TimeSpan.FromSeconds(8), ct))
            {
                LogMessage?.Invoke(profile.AddonName, $"文件仍被锁定，跳过本次读取: {Path.GetFileName(fullSourcePath)}");
                return (false, isAutoSave, string.Empty);
            }

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
            _autoSaveWatermarks.Clear();
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
                var mainSummaries = new List<string>();
                var autoSummaries = new List<string>();

                var formalVmapUpsertCandidates = new List<VmapConverterAndSanitizer.VmapSourceCandidate>();
                var formalVmapDeleteItems = new List<string>();
                var autoSaveCandidates = new List<VmapConverterAndSanitizer.VmapSourceCandidate>();
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
                                formalVmapDeleteItems.Add(item.Key);
                            continue;
                        }

                        try
                        {
                            DateTime mtimeUtc = File.GetLastWriteTimeUtc(item.Key);
                            int slot = isAutoSave ? VmapConverterAndSanitizer.ExtractAutoSaveSlotNumber(item.Key) : -1;
                            var cand = new VmapConverterAndSanitizer.VmapSourceCandidate(item.Key, canonicalRel, isAutoSave, mtimeUtc, slot);
                            if (isAutoSave)
                            {
                                autoSaveCandidates.Add(cand);
                            }
                            else
                            {
                                formalVmapUpsertCandidates.Add(cand);
                            }
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

                // --- 1. 主仓库通道 (main 分支) ---
                // 正式 VMAP 更新 (按地图分组取最新)
                foreach (var group in formalVmapUpsertCandidates.GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    var winner = group.OrderByDescending(c => c.LastWriteTimeUtc).First();
                    var res = await _parent.ProcessSingleFileChangeAsync(Profile, winner.FullSourcePath, isGameSide: false, isDeleted: false, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        mainSummaries.Add(res.SummaryFragment);
                    }
                }

                // 正式 VMAP 删除
                var updatedCanonicalFormal = new HashSet<string>(formalVmapUpsertCandidates.Select(c => c.CanonicalRelativePath), StringComparer.OrdinalIgnoreCase);
                foreach (var delPath in formalVmapDeleteItems)
                {
                    if (_gitService.ShouldTrackFile(Profile, delPath, isGameSide: false, out string rel, out bool fromAutoSave))
                    {
                        var (canonicalRel, _) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(rel, fromAutoSave);
                        if (updatedCanonicalFormal.Contains(canonicalRel))
                            continue;
                    }

                    var res = await _parent.ProcessSingleFileChangeAsync(Profile, delPath, isGameSide: false, isDeleted: true, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        mainSummaries.Add(res.SummaryFragment);
                    }
                }

                // 其他常规资产 / 配置文件
                foreach (var item in otherItems)
                {
                    var res = await _parent.ProcessSingleFileChangeAsync(Profile, item.Key, item.Value.IsGameSide, item.Value.IsDeleted, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        mainSummaries.Add(res.SummaryFragment);
                    }
                }

                if (mainSummaries.Count > 0)
                {
                    string prefix = isInitialBaseline ? "[Init]" : "[Save]";
                    string msg = BuildAggregateCommitMessage(prefix, mainSummaries);
                    var commit = await _gitService.CommitPendingChangesAsync(Profile, msg, _config.MaxAutoCommits, _cts.Token);
                    if (commit != null)
                    {
                        if (!isInitialBaseline && !ShadowGitService.IsInitOrMetadataCommitSubject(commit.Subject))
                        {
                            _parent.NotifyCommitCreated(Profile.AddonName, commit);
                            _parent.NotifyLogMessage(Profile.AddonName, $"正式提交成功 [{commit.ShortHash}]: {commit.Subject}");
                        }
                    }
                }

                // --- 2. 自动保存通道 (autosave Worktree) ---
                foreach (var group in autoSaveCandidates.GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    var winner = VmapConverterAndSanitizer.SelectNewestVmapCandidate(group);
                    if (!_parent.ShouldAcceptAutoSaveCandidate(Profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc))
                        continue;

                    var res = await _parent.ProcessSingleFileChangeAsync(Profile, winner.FullSourcePath, isGameSide: false, isDeleted: false, _cts.Token);
                    if (res.Processed && !string.IsNullOrEmpty(res.SummaryFragment))
                    {
                        autoSummaries.Add(res.SummaryFragment);
                        _parent.AdvanceAutoSaveWatermark(Profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc);
                    }
                }

                if (autoSummaries.Count > 0)
                {
                    string autoMsg = BuildAggregateCommitMessage("[AutoSave]", autoSummaries);
                    var autoCommit = await _gitService.CommitAutoSaveChangesAsync(Profile, autoMsg, _config.MaxAutoCommits, _cts.Token);
                    if (autoCommit != null)
                    {
                        _parent.NotifyCommitCreated(Profile.AddonName, autoCommit);
                        _parent.NotifyLogMessage(Profile.AddonName, $"自动保存提交成功 [{autoCommit.ShortHash}]: {autoCommit.Subject}");
                    }
                }

                if (mainSummaries.Count > 0 || autoSummaries.Count > 0)
                {
                    _parent.NotifyTrackedFilesChanged(Profile.AddonName);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                try
                {
                    _parent.NotifyLogMessage(Profile.AddonName, $"监控处理异常: {ex.Message}");
                }
                catch
                {
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
        }
    }
}
