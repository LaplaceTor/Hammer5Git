using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hammer5Git.Core.Models;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AppConfig : ObservableModel
{
    private string _cs2InstallPath = string.Empty;
    private string _defaultReposRoot = Path.Combine(AppContext.BaseDirectory, "repos");
    private int _debounceMilliseconds = 3500;
    private int _maxAutoCommits = 1000;
    private bool _minimizeToTrayOnClose = true;
    private string _themeMode = "Dark"; // "Dark", "Light", "Default"
    private bool _reconcileDiscardedAutoSave = true;
    private int _autosaveDiscardGraceSeconds = 300;

    [JsonIgnore]
    public string Cs2InstallPath
    {
        get => _cs2InstallPath;
        set => SetProperty(ref _cs2InstallPath, value);
    }

    public string DefaultReposRoot
    {
        get => _defaultReposRoot;
        set => SetProperty(ref _defaultReposRoot, value);
    }

    public int DebounceMilliseconds
    {
        get => _debounceMilliseconds;
        set => SetProperty(ref _debounceMilliseconds, Math.Clamp(value, 500, 30000));
    }

    public int MaxAutoCommits
    {
        get => _maxAutoCommits;
        set => SetProperty(ref _maxAutoCommits, Math.Clamp(value, 20, 5000));
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public string ThemeMode
    {
        get => _themeMode;
        set => SetProperty(ref _themeMode, string.IsNullOrWhiteSpace(value) ? "Dark" : value);
    }

    /// <summary>
    /// 是否自动对齐并回退被用户放弃保存的 Hammer 自动保存草稿。
    /// </summary>
    public bool ReconcileDiscardedAutoSave
    {
        get => _reconcileDiscardedAutoSave;
        set => SetProperty(ref _reconcileDiscardedAutoSave, value);
    }

    /// <summary>
    /// 自动保存草稿判定丢弃的宽限等待时间（秒），范围 [30, 7200]，默认 300 秒。
    /// </summary>
    public int AutosaveDiscardGraceSeconds
    {
        get => _autosaveDiscardGraceSeconds;
        set => SetProperty(ref _autosaveDiscardGraceSeconds, Math.Clamp(value, 30, 7200));
    }

    public List<AddonProfile> Addons { get; set; } = new();

    public string GetDmxConvertPath()
    {
        if (string.IsNullOrWhiteSpace(Cs2InstallPath))
            return string.Empty;
        return Path.Combine(Cs2InstallPath, "game", "bin", "win64", "dmxconvert.exe");
    }

    public static string GetDefaultConfigPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "hammer5git.config.json");
    }

    public static AppConfig Load(string? path = null)
    {
        var target = path ?? GetDefaultConfigPath();
        if (!File.Exists(target))
        {
            return new AppConfig();
        }
        try
        {
            var json = File.ReadAllText(target);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = $"{target}.corrupted_{timestamp}.bak";
                File.Copy(target, backupPath, overwrite: true);
            }
            catch
            {
                // Fallback backup failure must not throw
            }
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? GetDefaultConfigPath();
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = target + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, target, overwrite: true);
    }
}

public class AddonProfile : ObservableModel
{
    private string _addonName = string.Empty;
    private string _contentAddonPath = string.Empty;
    private string _gameAddonPath = string.Empty;
    private string _shadowRepoPath = string.Empty;
    private bool _isMonitoringEnabled;
    private bool _trackGameSideConfigs = true;
    private string _remoteUrl = string.Empty;
    private bool _autoPushOnTag;
    private List<string> _lfsTrackedExtensions = new();
    private string _statusText = "未启动";

    public string AddonName
    {
        get => _addonName;
        set => SetProperty(ref _addonName, value);
    }

    public string ContentAddonPath
    {
        get => _contentAddonPath;
        set => SetProperty(ref _contentAddonPath, value);
    }

    public string GameAddonPath
    {
        get => _gameAddonPath;
        set => SetProperty(ref _gameAddonPath, value);
    }

    public string ShadowRepoPath
    {
        get => _shadowRepoPath;
        set => SetProperty(ref _shadowRepoPath, value);
    }

    public bool IsMonitoringEnabled
    {
        get => _isMonitoringEnabled;
        set
        {
            if (SetProperty(ref _isMonitoringEnabled, value))
            {
                StatusText = value ? "监控中" : "已停止";
            }
        }
    }

    public bool TrackGameSideConfigs
    {
        get => _trackGameSideConfigs;
        set => SetProperty(ref _trackGameSideConfigs, value);
    }

    public string RemoteUrl
    {
        get => _remoteUrl;
        set
        {
            if (SetProperty(ref _remoteUrl, value))
            {
                OnPropertyChanged(nameof(HasRemoteUrl));
            }
        }
    }

    public bool AutoPushOnTag
    {
        get => _autoPushOnTag;
        set => SetProperty(ref _autoPushOnTag, value);
    }

    public List<string> LfsTrackedExtensions
    {
        get => _lfsTrackedExtensions;
        set
        {
            if (SetProperty(ref _lfsTrackedExtensions, value))
            {
                OnPropertyChanged(nameof(LfsSummaryText));
            }
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    [JsonIgnore]
    public bool HasRemoteUrl => !string.IsNullOrWhiteSpace(RemoteUrl);

    [JsonIgnore]
    public string LfsSummaryText => LfsTrackedExtensions.Count == 0
        ? "未启用 LFS 二进制追踪"
        : $"LFS 追踪 {LfsTrackedExtensions.Count} 类二进制格式";

    [JsonIgnore]
    public string AutoSaveAddonPath => TryResolveAutoSaveAddonPath(ContentAddonPath, AddonName, out string resolved)
        ? resolved
        : string.Empty;

    /// <summary>
    /// 从 ContentAddonPath (&lt;CS2&gt;\content\csgo_addons\&lt;name&gt;) 推导
    /// &lt;CS2&gt;\content\_toolsautosave\hammer\csgo_addons\&lt;name&gt;。
    /// 注意：只做路径拓扑验证与拼接，不要求目标目录在磁盘上已存在。
    /// </summary>
    public static bool TryResolveAutoSaveAddonPath(
        string? contentAddonPath,
        string? fallbackAddonName,
        out string autoSaveAddonPath)
    {
        autoSaveAddonPath = string.Empty;
        if (string.IsNullOrWhiteSpace(contentAddonPath))
            return false;

        try
        {
            string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentAddonPath));
            string addonName = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(addonName))
                addonName = fallbackAddonName?.Trim() ?? string.Empty;

            string? csgoAddonsDir = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(csgoAddonsDir))
                return false;

            string csgoAddonsFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(csgoAddonsDir));
            string? contentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(csgoAddonsDir));

            // 校验父级结构确实是 .../content/csgo_addons/<addonName>
            if (string.IsNullOrWhiteSpace(contentDir) ||
                !string.Equals(csgoAddonsFolderName, "csgo_addons", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(addonName))
            {
                return false;
            }

            autoSaveAddonPath = Path.Combine(
                contentDir,
                "_toolsautosave",
                "hammer",
                "csgo_addons",
                addonName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void NotifyLfsChanged()
    {
        OnPropertyChanged(nameof(LfsTrackedExtensions));
        OnPropertyChanged(nameof(LfsSummaryText));
    }

    public string ResolveDiskFilePath(string repoRelativePath)
    {
        string normalizedRel = repoRelativePath.Replace('\\', '/').TrimStart('/');
        bool isGameSide = normalizedRel.StartsWith("game/", StringComparison.OrdinalIgnoreCase);
        string subPath = normalizedRel.StartsWith("content/", StringComparison.OrdinalIgnoreCase)
            ? normalizedRel["content/".Length..]
            : isGameSide
                ? normalizedRel["game/".Length..]
                : normalizedRel;

        string baseAddonDir = isGameSide ? GameAddonPath : ContentAddonPath;
        return Path.Combine(baseAddonDir, subPath.Replace('/', Path.DirectorySeparatorChar));
    }
}

public class CommitRecord
{
    public string Hash { get; set; } = string.Empty;
    public string ShortHash { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Subject { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();

    public bool IsAutoSave => Subject.StartsWith("[AutoSave]", StringComparison.OrdinalIgnoreCase);
    public bool HasTag => Tags.Count > 0;
    public string DisplayTime => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string TagSummary => Tags.Count > 0 ? string.Join(", ", Tags) : string.Empty;
}

public class ChangedFileEntry
{
    public string Status { get; set; } = "M"; // A, M, D
    public string RelativePath { get; set; } = string.Empty;
}

public class CorruptedFileAlert
{
    public string AddonName { get; set; } = string.Empty;
    public string OriginalFilePath { get; set; } = string.Empty;
    public string DumpFilePath { get; set; } = string.Empty;
    public string ErrorDetail { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsAutoSave { get; set; }
}

public class VmapConversionResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string SanitizedKv2Content { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

/// <summary>
/// VMAP 变更来源归属：磁盘正式保存 vs Hammer 自动保存草稿。
/// </summary>
public enum VmapSourceProvenance
{
    /// <summary>
    /// 源自用户在磁盘上的正式保存 (content/csgo_addons/...)。
    /// </summary>
    FormalDiskSave,

    /// <summary>
    /// 源自 Hammer 后台生成的自动保存草稿 (_toolsautosave/hammer/...)。
    /// </summary>
    HammerAutoSave
}

/// <summary>
/// 影子仓库回退至正式存档锚点 (refs/hammer5git/last-formal) 的操作结果状态。
/// </summary>
public enum FormalAnchorRollbackResult
{
    /// <summary>
    /// 成功硬重置回退至正式存档锚点，且被丢弃的草稿提交已通过安全 tag 归档。
    /// </summary>
    RolledBack,

    /// <summary>
    /// 当前 HEAD 已与正式存档锚点一致，无需执行回退。
    /// </summary>
    AlreadyAligned,

    /// <summary>
    /// 无法或不应执行 hard reset（例如 HEAD 已推送至远程，或锚点到 HEAD 间存在非纯自动保存的提交），需要前向提交正式文件进行对齐。
    /// </summary>
    RequiresForwardReconcile,

    /// <summary>
    /// 回退操作发生错误或异常失败。
    /// </summary>
    Failed
}

/// <summary>
/// VMAP 地图来源归属与单调水位线元数据，用于识别并对齐未保存的草稿。
/// </summary>
public sealed class VmapProvenanceMetadata
{
    /// <summary>
    /// 最近一次同步到影子仓库的源文件修改时间 (UTC)。
    /// </summary>
    public DateTime LastSyncedWriteTimeUtc { get; set; }

    /// <summary>
    /// 最近一次应用的来源类型（正式文件 vs 自动保存）。
    /// </summary>
    public VmapSourceProvenance LastSourceKind { get; set; }

    /// <summary>
    /// 最近一次应用的源文件完整物理路径。
    /// </summary>
    public string LastSourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 最近一次应用到影子仓库时的挂钟时间 (UTC)。
    /// </summary>
    public DateTime LastAppliedWallClockUtc { get; set; }

    /// <summary>
    /// 最近一次应用时观察到的磁盘正式文件修改时间 (UTC)。
    /// 用于判定此后用户是否在磁盘上手动保存过。
    /// </summary>
    public DateTime FormalDiskBaselineWriteTimeUtc { get; set; }

    /// <summary>
    /// 最近一次源于正式保存的提交哈希。
    /// </summary>
    public string LastFormalCommitHash { get; set; } = string.Empty;

    /// <summary>
    /// 首次检测到该地图草稿可能被丢弃/遗弃的挂钟时间 (UTC)，用于宽限期倒计时。
    /// </summary>
    public DateTime DiscardConditionFirstSeenUtc { get; set; }
}
