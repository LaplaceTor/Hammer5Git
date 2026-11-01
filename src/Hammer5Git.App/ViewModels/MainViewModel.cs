using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hammer5Git.Core.Models;
using Hammer5Git.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hammer5Git.App.ViewModels;

public enum DiffLineKind
{
    FileHeader,
    Context,
    Addition,
    Deletion,
    HunkHeader,
    Meta,
    TruncationNotice
}

public sealed class DiffLineViewModel
{
    public static bool UseLightPalette { get; set; }

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    // Dark palette (GitHub Dark style)
    private static readonly SolidColorBrush DarkAddBgBrush = new(Color.FromArgb(42, 46, 160, 67));
    private static readonly SolidColorBrush DarkDelBgBrush = new(Color.FromArgb(42, 248, 81, 73));
    private static readonly SolidColorBrush DarkHunkBgBrush = new(Color.FromArgb(35, 56, 139, 253));
    private static readonly SolidColorBrush DarkAddFgBrush = new(Color.FromArgb(255, 86, 211, 100));
    private static readonly SolidColorBrush DarkDelFgBrush = new(Color.FromArgb(255, 255, 123, 114));
    private static readonly SolidColorBrush DarkHunkFgBrush = new(Color.FromArgb(255, 121, 192, 255));
    private static readonly SolidColorBrush DarkMetaFgBrush = new(Color.FromArgb(180, 139, 148, 158));
    private static readonly SolidColorBrush DarkContextFgBrush = new(Color.FromArgb(225, 201, 209, 217));

    // Light palette (GitHub Light style)
    private static readonly SolidColorBrush LightAddBgBrush = new(Color.FromArgb(55, 46, 160, 67));
    private static readonly SolidColorBrush LightDelBgBrush = new(Color.FromArgb(50, 255, 129, 130));
    private static readonly SolidColorBrush LightHunkBgBrush = new(Color.FromArgb(40, 84, 174, 255));
    private static readonly SolidColorBrush LightAddFgBrush = new(Color.FromArgb(255, 17, 99, 41));
    private static readonly SolidColorBrush LightDelFgBrush = new(Color.FromArgb(255, 164, 14, 38));
    private static readonly SolidColorBrush LightHunkFgBrush = new(Color.FromArgb(255, 9, 105, 218));
    private static readonly SolidColorBrush LightMetaFgBrush = new(Color.FromArgb(200, 87, 96, 106));
    private static readonly SolidColorBrush LightContextFgBrush = new(Color.FromArgb(235, 36, 41, 47));

    public DiffLineKind Kind { get; init; }

    // FileHeader & TruncationNotice properties
    public string FileStatus { get; init; } = "M";
    public string RepoRelativePath { get; init; } = string.Empty;
    public string FullDiskPath { get; init; } = string.Empty;
    public string FileStatsText { get; init; } = string.Empty;
    public int RemainingLinesCount { get; init; }

    // Code line properties
    public int LineIndex { get; init; }
    public string LineIndexText => LineIndex > 0 ? LineIndex.ToString() : string.Empty;
    public string Prefix { get; init; } = " ";
    public string Content { get; init; } = string.Empty;

    public Visibility FileHeaderVisibility => Kind == DiffLineKind.FileHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CodeRowVisibility => Kind is DiffLineKind.Context or DiffLineKind.Addition or DiffLineKind.Deletion or DiffLineKind.HunkHeader or DiffLineKind.Meta
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility TruncationRowVisibility => Kind == DiffLineKind.TruncationNotice ? Visibility.Visible : Visibility.Collapsed;

    public string TruncationButtonText => $"展开剩余 {RemainingLinesCount} 行变更";

    public SolidColorBrush RowBackground => (UseLightPalette, Kind) switch
    {
        (false, DiffLineKind.Addition) => DarkAddBgBrush,
        (false, DiffLineKind.Deletion) => DarkDelBgBrush,
        (false, DiffLineKind.HunkHeader) => DarkHunkBgBrush,
        (true, DiffLineKind.Addition) => LightAddBgBrush,
        (true, DiffLineKind.Deletion) => LightDelBgBrush,
        (true, DiffLineKind.HunkHeader) => LightHunkBgBrush,
        _ => TransparentBrush
    };

    public SolidColorBrush TextForeground => (UseLightPalette, Kind) switch
    {
        (false, DiffLineKind.Addition) => DarkAddFgBrush,
        (false, DiffLineKind.Deletion) => DarkDelFgBrush,
        (false, DiffLineKind.HunkHeader) => DarkHunkFgBrush,
        (false, DiffLineKind.Meta) => DarkMetaFgBrush,
        (false, _) => DarkContextFgBrush,
        (true, DiffLineKind.Addition) => LightAddFgBrush,
        (true, DiffLineKind.Deletion) => LightDelFgBrush,
        (true, DiffLineKind.HunkHeader) => LightHunkFgBrush,
        (true, DiffLineKind.Meta) => LightMetaFgBrush,
        (true, _) => LightContextFgBrush
    };
}

public sealed class FileTreeNodeViewModel
{
    public string DisplayName { get; init; } = string.Empty;
    public string? FilterRepoRelativePath { get; init; }
    public bool IsRoot { get; init; }
    public bool IsFolder { get; init; }
    public bool IsFile { get; init; }

    public string IconGlyph => IsRoot
        ? "\uE8B7"
        : IsFolder
            ? "\uE8D5"
            : DisplayName.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase)
                ? "\uE809"
                : "\uE8A5";
}

public sealed class FileTreeViewNode : TreeViewNode
{
    public FileTreeNodeViewModel NodeData { get; }

    public FileTreeViewNode(FileTreeNodeViewModel data)
    {
        NodeData = data;
        Content = data;
    }
}

public sealed class MainViewModel : ObservableModel, IDisposable
{
    private const int DefaultMaxDiffLinesPerFile = 100;

    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _diffLoadCts;

    // Cached diff blocks for the currently selected commit so expanding a single file is instant
    private readonly List<(ChangedFileEntry File, string RawDiff)> _currentCommitFileDiffs = new();
    private readonly HashSet<string> _expandedDiffFiles = new(StringComparer.OrdinalIgnoreCase);

    // The currently active filter path (null = Root / all files; otherwise specific file relative path)
    private string? _activeFilterFilePath;

    public AppConfig Config { get; }
    public Cs2EnvironmentService EnvService { get; }
    public ShadowGitService GitService { get; }
    public VmapConverterAndSanitizer VmapConverter { get; }
    public AddonWatcherService WatcherService { get; }
    public RepoImportService RepoImportService { get; }

    public ObservableCollection<AddonProfile> Addons { get; } = new();
    public ObservableCollection<FileTreeViewNode> RootTreeNodes { get; } = new();
    public ObservableCollection<CommitRecord> Commits { get; } = new();
    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = new();

    public event Action<FileTreeViewNode>? TargetNodeSelected;
    public event Action? FileTreeRebuilt;

    private AddonProfile? _selectedAddon;
    public AddonProfile? SelectedAddon
    {
        get => _selectedAddon;
        set
        {
            if (SetProperty(ref _selectedAddon, value))
            {
                _activeFilterFilePath = null;
                OnPropertyChanged(nameof(HasSelectedAddon));
                OnPropertyChanged(nameof(SelectedAddonEmptyVisibility));
                OnPropertyChanged(nameof(SelectedAddonWorkspaceVisibility));
                OnPropertyChanged(nameof(CurrentFilterTitle));
                _ = RebuildFileTreeForSelectedAddonAsync();
                _ = LoadCommitHistoryForSelectedAddonAsync();
            }
        }
    }

    public bool HasSelectedAddon => SelectedAddon != null;
    public Visibility SelectedAddonEmptyVisibility => SelectedAddon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedAddonWorkspaceVisibility => SelectedAddon != null ? Visibility.Visible : Visibility.Collapsed;

    public void HandleTreeNodeInvoked(FileTreeNodeViewModel? node, TreeViewNode? treeNode)
    {
        if (node == null)
            return;

        if (node.IsFile)
        {
            if (!string.Equals(_activeFilterFilePath, node.FilterRepoRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                _activeFilterFilePath = node.FilterRepoRelativePath;
                OnPropertyChanged(nameof(CurrentFilterTitle));
                _ = LoadCommitHistoryForSelectedAddonAsync();
            }
        }
        else if (node.IsRoot)
        {
            if (_activeFilterFilePath != null)
            {
                _activeFilterFilePath = null;
                OnPropertyChanged(nameof(CurrentFilterTitle));
                _ = LoadCommitHistoryForSelectedAddonAsync();
            }
        }
    }

    public string CurrentFilterTitle => string.IsNullOrEmpty(_activeFilterFilePath)
        ? "提交历史"
        : Path.GetFileName(_activeFilterFilePath);

    private CommitRecord? _selectedCommit;
    public CommitRecord? SelectedCommit
    {
        get => _selectedCommit;
        set
        {
            if (SetProperty(ref _selectedCommit, value))
            {
                OnPropertyChanged(nameof(HasSelectedCommit));
                _ = LoadMultiFileDiffsForSelectedCommitAsync();
            }
        }
    }

    public bool HasSelectedCommit => SelectedCommit != null;

    private bool _isDmxConvertReady = true;
    public bool IsDmxConvertReady
    {
        get => _isDmxConvertReady;
        set
        {
            if (SetProperty(ref _isDmxConvertReady, value))
            {
                OnPropertyChanged(nameof(DmxWarningVisibility));
            }
        }
    }

    public Visibility DmxWarningVisibility => IsDmxConvertReady ? Visibility.Collapsed : Visibility.Visible;

    private CorruptedFileAlert? _latestAlert;
    public CorruptedFileAlert? LatestAlert
    {
        get => _latestAlert;
        set => SetProperty(ref _latestAlert, value);
    }

    public event Action<CorruptedFileAlert>? CorruptedFileAlertRaised;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Config = AppConfig.Load();
        EnvService = new Cs2EnvironmentService();
        GitService = new ShadowGitService();
        VmapConverter = new VmapConverterAndSanitizer(Config.GetDmxConvertPath());
        WatcherService = new AddonWatcherService(Config, GitService, VmapConverter, EnvService);
        RepoImportService = new RepoImportService(GitService, EnvService, WatcherService, VmapConverter);

        WatcherService.LogMessage += (addon, msg) =>
        {
            _dispatcher.TryEnqueue(() => AppendLog(addon, msg));
        };

        WatcherService.TrackedFilesChanged += addonName =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (SelectedAddon != null && SelectedAddon.AddonName.Equals(addonName, StringComparison.OrdinalIgnoreCase))
                {
                    RebuildFileTreeForSelectedAddon();
                }
            });
        };

        WatcherService.NewAddonDiscovered += profile =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!Addons.Any(a => a.AddonName.Equals(profile.AddonName, StringComparison.OrdinalIgnoreCase)))
                {
                    Addons.Add(profile);
                    Config.Addons.Add(profile);
                    Config.Save();
                    if (SelectedAddon == null)
                    {
                        SelectedAddon = profile;
                    }
                }
            });
        };

        WatcherService.CommitCreated += (addonName, record) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (SelectedAddon != null && SelectedAddon.AddonName.Equals(addonName, StringComparison.OrdinalIgnoreCase))
                {
                    _ = LoadCommitHistoryForSelectedAddonAsync();
                }
            });
        };

        WatcherService.CorruptedFileDetected += alert =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                LatestAlert = alert;
                CorruptedFileAlertRaised?.Invoke(alert);
            });
        };
    }

    public async Task RefreshEnvironmentAndAddonsAsync(bool performInitialSync = false)
    {
        if (string.IsNullOrWhiteSpace(Config.Cs2InstallPath) || !Directory.Exists(Config.Cs2InstallPath))
        {
            string? detected = Cs2InstallationLocator.LocateCs2InstallPath();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                Config.Cs2InstallPath = detected;
                AppendLog("System", $"自动检测到 CS2 安装目录: {detected}");
            }
            else
            {
                AppendLog("System", "未检测到有效的 CS2 安装目录，请确认 Steam 及 CS2 已正确安装。");
            }
        }

        VmapConverter.DmxConvertPath = Config.GetDmxConvertPath();

        if (EnvService.ValidateCs2Install(Config.Cs2InstallPath, out string dmxPath, out string err))
        {
            IsDmxConvertReady = true;
            AppendLog("System", $"已就绪 dmxconvert: {dmxPath}");
        }
        else
        {
            IsDmxConvertReady = false;
            AppendLog("System", $"环境警告: {err}");
        }

        string? previousSelectedName = SelectedAddon?.AddonName;

        // Only load addons that are explicitly registered in Config.Addons
        var registeredAddons = new List<AddonProfile>();
        foreach (var addon in Config.Addons)
        {
            if (Cs2EnvironmentService.IsExcludedAddonName(addon.AddonName))
                continue;
            registeredAddons.Add(addon);
        }

        Addons.Clear();
        foreach (var addon in registeredAddons)
        {
            Addons.Add(addon);
        }

        if (Addons.Count > 0)
        {
            var match = previousSelectedName != null
                ? Addons.FirstOrDefault(a => a.AddonName.Equals(previousSelectedName, StringComparison.OrdinalIgnoreCase))
                : null;
            SelectedAddon = match ?? Addons[0];
        }
        else
        {
            SelectedAddon = null;
        }

        foreach (var addon in registeredAddons)
        {
            try
            {
                await WatcherService.StartWatchingAsync(addon, performInitialSync: false);
            }
            catch (Exception ex)
            {
                AppendLog(addon.AddonName, $"启动监控异常: {ex.Message}");
            }
        }

        if (SelectedAddon != null)
        {
            await RebuildFileTreeForSelectedAddonAsync();
            await LoadCommitHistoryForSelectedAddonAsync();
        }
    }

    public void SelectFileInTreeAndFilterHistory(string repoRelativePath)
    {
        var addon = SelectedAddon;
        if (addon == null || string.IsNullOrWhiteSpace(repoRelativePath))
            return;

        string normalized = repoRelativePath.Replace('\\', '/').TrimStart('/');
        _activeFilterFilePath = normalized;
        OnPropertyChanged(nameof(CurrentFilterTitle));
        _ = LoadCommitHistoryForSelectedAddonAsync();

        var targetNode = FindNodeByPath(RootTreeNodes, normalized);
        if (targetNode != null)
        {
            var p = targetNode.Parent;
            while (p != null)
            {
                p.IsExpanded = true;
                p = p.Parent;
            }
            TargetNodeSelected?.Invoke(targetNode);
        }
    }

    private static FileTreeViewNode? FindNodeByPath(IEnumerable<FileTreeViewNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.NodeData.FilterRepoRelativePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var childNodes = node.Children.OfType<FileTreeViewNode>();
            var found = FindNodeByPath(childNodes, path);
            if (found != null)
                return found;
        }
        return null;
    }

    public void RebuildFileTreeForSelectedAddon()
    {
        _ = RebuildFileTreeForSelectedAddonAsync();
    }

    public async Task RebuildFileTreeForSelectedAddonAsync()
    {
        var addon = SelectedAddon;
        if (addon == null)
        {
            _dispatcher.TryEnqueue(() =>
            {
                RootTreeNodes.Clear();
                FileTreeRebuilt?.Invoke();
            });
            return;
        }

        var trackedFiles = await Task.Run(() => GitService.GetShadowTrackedFiles(addon));

        _dispatcher.TryEnqueue(() =>
        {
            var rootVm = new FileTreeNodeViewModel
            {
                DisplayName = addon.AddonName,
                FilterRepoRelativePath = null,
                IsRoot = true
            };
            var rootNode = new FileTreeViewNode(rootVm) { IsExpanded = true };

            var folderNodes = new Dictionary<string, FileTreeViewNode>(StringComparer.OrdinalIgnoreCase);

            var orderedFiles = trackedFiles
                .OrderBy(f => f.StartsWith("content/maps/", StringComparison.OrdinalIgnoreCase) ? 0 : f.StartsWith("content/", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

            var fileNodes = new List<FileTreeViewNode>();

            foreach (var repoRel in orderedFiles)
            {
                // Preserve content and game as top-level folders under addon root
                string[] segments = repoRel.Split('/', StringSplitOptions.RemoveEmptyEntries);

                FileTreeViewNode currentParent = rootNode;
                string accumDisplay = string.Empty;

                for (int i = 0; i < segments.Length - 1; i++)
                {
                    accumDisplay = string.IsNullOrEmpty(accumDisplay) ? segments[i] : $"{accumDisplay}/{segments[i]}";
                    string folderRepoRel = accumDisplay;

                    if (!folderNodes.TryGetValue(folderRepoRel, out var folderNode))
                    {
                        var folderVm = new FileTreeNodeViewModel
                        {
                            DisplayName = segments[i],
                            FilterRepoRelativePath = folderRepoRel,
                            IsFolder = true
                        };
                        folderNode = new FileTreeViewNode(folderVm)
                        {
                            IsExpanded = false // Default to collapsed
                        };
                        currentParent.Children.Add(folderNode);
                        folderNodes[folderRepoRel] = folderNode;
                    }

                    currentParent = folderNode;
                }

                string fileName = segments[^1];
                var fileVm = new FileTreeNodeViewModel
                {
                    DisplayName = fileName,
                    FilterRepoRelativePath = repoRel,
                    IsFile = true
                };
                var fileNode = new FileTreeViewNode(fileVm);
                currentParent.Children.Add(fileNode);
                fileNodes.Add(fileNode);
            }

            // Expand all folders leading to .vmap files
            foreach (var fn in fileNodes)
            {
                if (fn.NodeData.DisplayName.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase))
                {
                    string? rel = fn.NodeData.FilterRepoRelativePath;
                    if (rel != null)
                    {
                        string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        string acc = string.Empty;
                        for (int i = 0; i < parts.Length - 1; i++)
                        {
                            acc = string.IsNullOrEmpty(acc) ? parts[i] : $"{acc}/{parts[i]}";
                            if (folderNodes.TryGetValue(acc, out var fnNode))
                            {
                                fnNode.IsExpanded = true;
                            }
                        }
                    }

                    var p = fn.Parent;
                    while (p != null)
                    {
                        p.IsExpanded = true;
                        p = p.Parent;
                    }
                }
            }

            RootTreeNodes.Clear();
            RootTreeNodes.Add(rootNode);
            FileTreeRebuilt?.Invoke();

            if (!string.IsNullOrEmpty(_activeFilterFilePath))
            {
                var matched = FindNodeByPath(RootTreeNodes, _activeFilterFilePath);
                if (matched != null)
                {
                    TargetNodeSelected?.Invoke(matched);
                }
            }
        });
    }

    public async Task<(bool Success, string Message)> AddAddonFromFolderAsync(string folderPath, string addonName)
    {
        var existing = Addons.FirstOrDefault(a => string.Equals(a.AddonName, addonName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedAddon = existing;
            return (true, $"项目【{addonName}】已在列表中，已为您切换到该项目。");
        }

        var gameAddonsDir = Path.Combine(Config.Cs2InstallPath, "game", "csgo_addons");
        var newAddon = new AddonProfile
        {
            AddonName = addonName,
            ContentAddonPath = folderPath,
            GameAddonPath = Path.Combine(gameAddonsDir, addonName),
            ShadowRepoPath = Path.Combine(Config.DefaultReposRoot, addonName),
            IsMonitoringEnabled = true,
            TrackGameSideConfigs = true,
            StatusText = "监控中"
        };

        Config.Addons.Add(newAddon);
        Config.Save();

        Addons.Add(newAddon);
        SelectedAddon = newAddon;

        try
        {
            await WatcherService.StartWatchingAsync(newAddon, performInitialSync: true);
            await RebuildFileTreeForSelectedAddonAsync();
            await LoadCommitHistoryForSelectedAddonAsync();
            AppendLog(addonName, "成功添加项目并启动守护");
            return (true, $"已成功添加项目【{addonName}】");
        }
        catch (Exception ex)
        {
            AppendLog(addonName, $"启动守护异常: {ex.Message}");
            return (false, $"添加项目成功，但在启动守护时遇到问题: {ex.Message}");
        }
    }

    public async Task RemoveAddonAsync(AddonProfile addon, bool deleteShadowRepo, bool deleteLocalFiles)
    {
        string name = addon.AddonName;
        WatcherService.StopWatching(name, clearProvenance: true);

        if (deleteShadowRepo && !string.IsNullOrWhiteSpace(addon.ShadowRepoPath) && Directory.Exists(addon.ShadowRepoPath))
        {
            try
            {
                Directory.Delete(addon.ShadowRepoPath, true);
                AppendLog(name, $"已删除影子仓库: {addon.ShadowRepoPath}");
            }
            catch (Exception ex)
            {
                AppendLog(name, $"删除影子仓库失败: {ex.Message}");
            }
        }

        if (deleteLocalFiles)
        {
            if (!string.IsNullOrWhiteSpace(addon.ContentAddonPath) && Directory.Exists(addon.ContentAddonPath))
            {
                try
                {
                    Directory.Delete(addon.ContentAddonPath, true);
                    AppendLog(name, $"已清理本地 content 目录: {addon.ContentAddonPath}");
                }
                catch (Exception ex)
                {
                    AppendLog(name, $"清理 content 目录失败: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(addon.GameAddonPath) && Directory.Exists(addon.GameAddonPath))
            {
                try
                {
                    Directory.Delete(addon.GameAddonPath, true);
                    AppendLog(name, $"已清理本地 game 目录: {addon.GameAddonPath}");
                }
                catch (Exception ex)
                {
                    AppendLog(name, $"清理 game 目录失败: {ex.Message}");
                }
            }
        }

        Config.Addons.RemoveAll(a => string.Equals(a.AddonName, name, StringComparison.OrdinalIgnoreCase));
        Config.Save();

        Addons.Remove(addon);
        SelectedAddon = Addons.FirstOrDefault();

        if (SelectedAddon != null)
        {
            await RebuildFileTreeForSelectedAddonAsync();
            await LoadCommitHistoryForSelectedAddonAsync();
        }
        else
        {
            RootTreeNodes.Clear();
            FileTreeRebuilt?.Invoke();
            Commits.Clear();
            DiffLines.Clear();
        }

        AppendLog("System", $"已从监控列表中移除项目【{name}】");
    }

    public async Task<(bool Success, string Message)> LoadRepoAsync(string sourcePathOrUrl, bool isRemote, string addonName)
    {
        var result = await RepoImportService.ImportAsync(new RepoImportRequest
        {
            SourcePathOrUrl = sourcePathOrUrl,
            IsRemote = isRemote,
            AddonName = addonName,
            Cs2InstallPath = Config.Cs2InstallPath,
            DefaultReposRoot = Config.DefaultReposRoot,
            MaxAutoCommits = Config.MaxAutoCommits,
            ExistingAddonNames = Addons.Select(a => a.AddonName).ToList(),
            Log = AppendLog
        });

        if (!result.Success || result.Addon == null)
            return (false, result.Message);

        var newAddon = result.Addon;
        Config.Addons.Add(newAddon);
        Config.Save();

        Addons.Add(newAddon);
        SelectedAddon = newAddon;

        await WatcherService.StartWatchingAsync(newAddon, performInitialSync: false);
        await RebuildFileTreeForSelectedAddonAsync();
        await LoadCommitHistoryForSelectedAddonAsync();

        AppendLog(addonName, "成功载入仓库并同步至 CS2！");
        return (true, result.Message);
    }

    public async Task LoadCommitHistoryForSelectedAddonAsync()
    {
        var addon = SelectedAddon;
        if (addon == null)
        {
            Commits.Clear();
            DiffLines.Clear();
            return;
        }

        string? filterPath = _activeFilterFilePath;

        try
        {
            var history = await GitService.GetFilteredCommitHistoryAsync(
                addon,
                Config.MaxAutoCommits,
                filterRepoRelativePath: filterPath,
                hideInitAndMetadataCommits: true);

            if (SelectedAddon != addon)
                return;

            string? prevCommitHash = SelectedCommit?.Hash;
            Commits.Clear();
            foreach (var item in history)
            {
                Commits.Add(item);
            }

            if (Commits.Count > 0)
            {
                var matched = prevCommitHash != null
                    ? Commits.FirstOrDefault(c => c.Hash.Equals(prevCommitHash, StringComparison.OrdinalIgnoreCase))
                    : null;
                SelectedCommit = matched ?? Commits[0];
            }
            else
            {
                SelectedCommit = null;
                RenderSingleMessageInDiff("暂无增量修改提交记录");
            }
        }
        catch (Exception ex)
        {
            AppendLog(addon.AddonName, $"读取提交历史失败: {ex.Message}");
        }
    }

    private async Task LoadMultiFileDiffsForSelectedCommitAsync()
    {
        var addon = SelectedAddon;
        var commit = SelectedCommit;
        if (addon == null || commit == null)
        {
            DiffLines.Clear();
            return;
        }

        _diffLoadCts?.Cancel();
        _diffLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _diffLoadCts = cts;

        try
        {
            RenderSingleMessageInDiff("正在加载变更明细...");

            var files = await GitService.GetCommitChangedFilesAsync(addon, commit.Hash, cts.Token);
            if (cts.IsCancellationRequested || SelectedCommit != commit)
                return;

            string? filterPath = _activeFilterFilePath;
            var targetFiles = string.IsNullOrEmpty(filterPath)
                ? files
                : files.Where(f => f.RelativePath.Equals(filterPath, StringComparison.OrdinalIgnoreCase)).ToList();

            if (targetFiles.Count == 0 && files.Count > 0)
            {
                targetFiles = files;
            }

            _currentCommitFileDiffs.Clear();
            _expandedDiffFiles.Clear();

            foreach (var file in targetFiles)
            {
                string rawDiff = await GitService.GetFileDiffForCommitAsync(addon, commit.Hash, file.RelativePath, cts.Token);
                if (cts.IsCancellationRequested || SelectedCommit != commit)
                    return;

                _currentCommitFileDiffs.Add((file, rawDiff));
            }

            RebuildMultiFileDiffRows();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog(addon.AddonName, $"加载变更明细失败: {ex.Message}");
        }
    }

    public void ExpandFileDiffLines(string repoRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(repoRelativePath) && _expandedDiffFiles.Add(repoRelativePath))
        {
            RebuildMultiFileDiffRows();
        }
    }

    public void RefreshDiffPalette(bool isLight)
    {
        if (DiffLineViewModel.UseLightPalette != isLight)
        {
            DiffLineViewModel.UseLightPalette = isLight;
            if (_currentCommitFileDiffs.Count > 0)
            {
                RebuildMultiFileDiffRows();
            }
        }
    }


    private void RebuildMultiFileDiffRows()
    {
        var addon = SelectedAddon;
        DiffLines.Clear();

        if (addon == null || _currentCommitFileDiffs.Count == 0)
        {
            RenderSingleMessageInDiff("当前提交无文件差异明细");
            return;
        }

        foreach (var (file, rawDiff) in _currentCommitFileDiffs)
        {
            string[] lines = string.IsNullOrWhiteSpace(rawDiff)
                ? []
                : rawDiff.Split(["\r\n", "\n"], StringSplitOptions.None);

            var contentLines = new List<(int LineNum, DiffLineKind Kind, string Prefix, string Text)>();
            int addedCount = 0;
            int deletedCount = 0;
            int lineNum = 0;

            foreach (var line in lines)
            {
                // Skip git diff header metadata lines since the FileHeader row already displays the path cleanly
                if (line.StartsWith("diff --git", StringComparison.Ordinal) ||
                    line.StartsWith("index ", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal) ||
                    line.StartsWith("+++", StringComparison.Ordinal))
                {
                    continue;
                }

                lineNum++;
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    contentLines.Add((lineNum, DiffLineKind.HunkHeader, "@", line));
                }
                else if (line.StartsWith('+'))
                {
                    addedCount++;
                    contentLines.Add((lineNum, DiffLineKind.Addition, "+", line.Length > 1 ? line[1..] : string.Empty));
                }
                else if (line.StartsWith('-'))
                {
                    deletedCount++;
                    contentLines.Add((lineNum, DiffLineKind.Deletion, "-", line.Length > 1 ? line[1..] : string.Empty));
                }
                else if (line.StartsWith(' '))
                {
                    contentLines.Add((lineNum, DiffLineKind.Context, " ", line.Length > 1 ? line[1..] : string.Empty));
                }
                else
                {
                    contentLines.Add((lineNum, DiffLineKind.Meta, " ", line));
                }
            }

            // 1. Add File Header Row
            string fullDiskPath = addon.ResolveDiskFilePath(file.RelativePath);
            DiffLines.Add(new DiffLineViewModel
            {
                Kind = DiffLineKind.FileHeader,
                FileStatus = file.Status,
                RepoRelativePath = file.RelativePath,
                FullDiskPath = fullDiskPath,
                FileStatsText = $"+{addedCount}  -{deletedCount}"
            });

            if (contentLines.Count == 0)
            {
                DiffLines.Add(new DiffLineViewModel
                {
                    Kind = DiffLineKind.Meta,
                    LineIndex = 1,
                    Prefix = " ",
                    Content = "(二进制文件或无文本行变更)"
                });
                continue;
            }

            bool isExpanded = _expandedDiffFiles.Contains(file.RelativePath);
            int limit = isExpanded ? contentLines.Count : Math.Min(contentLines.Count, DefaultMaxDiffLinesPerFile);

            for (int i = 0; i < limit; i++)
            {
                var item = contentLines[i];
                DiffLines.Add(new DiffLineViewModel
                {
                    Kind = item.Kind,
                    LineIndex = item.LineNum,
                    Prefix = item.Prefix,
                    Content = item.Text,
                    RepoRelativePath = file.RelativePath
                });
            }

            if (!isExpanded && contentLines.Count > DefaultMaxDiffLinesPerFile)
            {
                DiffLines.Add(new DiffLineViewModel
                {
                    Kind = DiffLineKind.TruncationNotice,
                    RepoRelativePath = file.RelativePath,
                    RemainingLinesCount = contentLines.Count - DefaultMaxDiffLinesPerFile
                });
            }
        }
    }

    private void RenderSingleMessageInDiff(string message)
    {
        _currentCommitFileDiffs.Clear();
        DiffLines.Clear();
        DiffLines.Add(new DiffLineViewModel
        {
            LineIndex = 1,
            Prefix = " ",
            Content = message,
            Kind = DiffLineKind.Meta
        });
    }

    /// <summary>
    /// 诊断日志。界面没有日志面板（原 Logs 集合与 LatestLogSummary 均无 XAML 绑定，已移除），
    /// 因此只写入调试输出：附加调试器时可见，发布版为零开销。
    /// </summary>
    public void AppendLog(string source, string text)
    {
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss} [{source}] {text}");
    }

    public void Dispose()
    {
        _diffLoadCts?.Cancel();
        _diffLoadCts?.Dispose();
        WatcherService.Dispose();
    }
}
