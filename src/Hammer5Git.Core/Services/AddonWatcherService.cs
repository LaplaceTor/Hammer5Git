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

namespace Hammer5Git.Core.Services;

public class AddonWatcherService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ShadowGitService _gitService;
    private readonly VmapConverterAndSanitizer _vmapConverter;

    private readonly Dictionary<string, AddonWatchSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _rootAddonsWatcher;

    // Key: "{addonName}|{canonicalRelPath}" (OrdinalIgnoreCase), Value: 已同步源文件的最大 LastWriteTimeUtc
    private readonly ConcurrentDictionary<string, DateTime> _lastSyncedWriteTimeUtc = new(StringComparer.OrdinalIgnoreCase);

    private static string GetWatermarkKey(string addonName, string canonicalRelPath)
        => $"{addonName.Trim()}|{canonicalRelPath.Replace('\\', '/').TrimStart('/')}";

    private static readonly TimeSpan WatermarkTolerance = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FutureClockSkewThreshold = TimeSpan.FromMinutes(2);

    public bool ShouldAcceptCandidateByWatermark(
        string addonName,
        string canonicalRelPath,
        DateTime candidateWriteTimeUtc)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        if (!_lastSyncedWriteTimeUtc.TryGetValue(key, out DateTime recordedUtc))
            return true;

        // 时钟回拨保护：如果已记录的水位线比当前系统 UTC 时间还超前超过 2 分钟，说明发生过时钟回拨，重置放行
        if (recordedUtc > DateTime.UtcNow.Add(FutureClockSkewThreshold))
        {
            _lastSyncedWriteTimeUtc[key] = candidateWriteTimeUtc;
            return true;
        }

        return candidateWriteTimeUtc >= recordedUtc - WatermarkTolerance;
    }

    public void AdvanceWatermark(string addonName, string canonicalRelPath, DateTime candidateWriteTimeUtc)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _lastSyncedWriteTimeUtc.AddOrUpdate(
            key,
            candidateWriteTimeUtc,
            (_, existing) =>
            {
                if (existing > DateTime.UtcNow.Add(FutureClockSkewThreshold))
                    return candidateWriteTimeUtc;
                return candidateWriteTimeUtc > existing ? candidateWriteTimeUtc : existing;
            });
    }

    public void RemoveWatermark(string addonName, string canonicalRelPath)
    {
        string key = GetWatermarkKey(addonName, canonicalRelPath);
        _lastSyncedWriteTimeUtc.TryRemove(key, out _);
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

            foreach (var file in Directory.EnumerateFiles(root, "*.vmap", SearchOption.AllDirectories))
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
    public event Action<AddonProfile>? NewAddonDiscovered;

    public AddonWatcherService(
        AppConfig config,
        ShadowGitService gitService,
        VmapConverterAndSanitizer vmapConverter)
    {
        _config = config;
        _gitService = gitService;
        _vmapConverter = vmapConverter;
    }

    public bool IsWatching(string addonName) => _sessions.ContainsKey(addonName);

    public int ActiveWatchCount => _sessions.Count;

    /// <summary>
    /// Watches the CS2 content/csgo_addons root folder so newly created addons are automatically discovered and monitored.
    /// </summary>
    public void StartRootAddonsDirectoryWatcher(string cs2InstallPath)
    {
        StopRootAddonsDirectoryWatcher();

        if (string.IsNullOrWhiteSpace(cs2InstallPath))
            return;

        string contentAddonsDir = Path.Combine(cs2InstallPath, "content", "csgo_addons");
        if (!Directory.Exists(contentAddonsDir))
            return;

        _rootAddonsWatcher = new FileSystemWatcher(contentAddonsDir)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.DirectoryName
        };

        _rootAddonsWatcher.Created += (_, e) => _ = TryDiscoverNewAddonDirAsync(e.FullPath);
        _rootAddonsWatcher.Renamed += (_, e) => _ = TryDiscoverNewAddonDirAsync(e.FullPath);
        _rootAddonsWatcher.EnableRaisingEvents = true;
    }

    public void StopRootAddonsDirectoryWatcher()
    {
        if (_rootAddonsWatcher != null)
        {
            _rootAddonsWatcher.EnableRaisingEvents = false;
            _rootAddonsWatcher.Dispose();
            _rootAddonsWatcher = null;
        }
    }

    private async Task TryDiscoverNewAddonDirAsync(string fullDirPath)
    {
        try
        {
            await Task.Delay(600);
            if (!Directory.Exists(fullDirPath))
                return;

            string name = Path.GetFileName(fullDirPath);
            if (Cs2EnvironmentService.IsExcludedAddonName(name) || IsWatching(name))
                return;

            var gameAddonsDir = Path.Combine(_config.Cs2InstallPath, "game", "csgo_addons");
            var profile = new AddonProfile
            {
                AddonName = name,
                ContentAddonPath = fullDirPath,
                GameAddonPath = Path.Combine(gameAddonsDir, name),
                ShadowRepoPath = Path.Combine(_config.DefaultReposRoot, name),
                IsMonitoringEnabled = true,
                TrackGameSideConfigs = true,
                StatusText = "监控中"
            };

            await StartWatchingAsync(profile, performInitialSync: true);
            LogMessage?.Invoke(name, $"自动检测到新创建的 Addon 项目并纳入实时守护: {name}");
            NewAddonDiscovered?.Invoke(profile);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke("System", $"自动纳管新 Addon 异常: {ex.Message}");
        }
    }

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

    public void StopWatching(string addonName)
    {
        if (_sessions.Remove(addonName, out var session))
        {
            session.Profile.IsMonitoringEnabled = false;
            session.Profile.StatusText = "已停止";
            session.Dispose();
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
                .Where(f => !f.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
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
            var winner = VmapConverterAndSanitizer.SelectNewestVmapCandidate(group);

            if (!ShouldAcceptCandidateByWatermark(profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc))
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

        if (!isGameSide && fullSourcePath.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
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

            // 文件合法且转换成功，立即推进单调水位线（即便后续因无语义差异不产生 Git 提交，也记录该最新时间戳）
            AdvanceWatermark(profile.AddonName, canonicalRelPath, sourceWriteTimeUtc);

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
        StopRootAddonsDirectoryWatcher();
        foreach (var s in _sessions.Values)
        {
            s.Dispose();
        }
        _sessions.Clear();
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
                            foreach (var file in Directory.EnumerateFiles(autoSavePath, "*.vmap", SearchOption.AllDirectories))
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
                var summaries = new List<string>();
                bool anyAutoSave = false;

                var vmapUpsertCandidates = new List<VmapConverterAndSanitizer.VmapSourceCandidate>();
                var vmapDeleteItems = new List<string>();
                var otherItems = new List<KeyValuePair<string, (bool IsGameSide, bool IsDeleted)>>();

                foreach (var item in snapshot)
                {
                    if (_parent.IsPathSuppressed(item.Key))
                        continue;

                    if (!item.Value.IsGameSide && item.Key.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
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

                // 1. 按 CanonicalRelativePath 分组仲裁：每张图只取最新 1 个候选调用 1 次 dmxconvert
                foreach (var group in vmapUpsertCandidates.GroupBy(c => c.CanonicalRelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    // 若正式地图文件在磁盘上也存在，将其一并纳入候选对比，防止滞后的旧 autosave 事件覆盖刚刚手动保存的正式文件
                    var candidatesWithFormal = group.ToList();
                    string? formalDiskPath = null;
                    try
                    {
                        formalDiskPath = Profile.ResolveDiskFilePath(group.Key);
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(formalDiskPath) &&
                        File.Exists(formalDiskPath) &&
                        !_parent.IsPathSuppressed(formalDiskPath) &&
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

                    if (!_parent.ShouldAcceptCandidateByWatermark(Profile.AddonName, winner.CanonicalRelativePath, winner.LastWriteTimeUtc))
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

                    if (commit != null && !isInitialBaseline && !ShadowGitService.IsInitOrMetadataCommitSubject(commit.Subject))
                    {
                        _parent.NotifyCommitCreated(Profile.AddonName, commit);
                        _parent.NotifyLogMessage(Profile.AddonName, $"自动提交成功 [{commit.ShortHash}]: {commit.Subject}");
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

            // 删除 _flushLock.Dispose() 与 _cts.Dispose() 的立即同步调用：
            // 这两个对象没有非托管句柄（SemaphoreSlim 未使用 AvailableWaitHandle、CTS 未挂计时器），
            // 交给 GC 回收即可；提前 Dispose 才是竞态的根源。
        }
    }
}
