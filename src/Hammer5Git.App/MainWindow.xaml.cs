using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Hammer5Git.App.Services;
using Hammer5Git.App.ViewModels;
using Hammer5Git.Core.Models;
using Hammer5Git.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Hammer5Git.App;

public sealed partial class MainWindow : Window
{
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private IntPtr _hwnd;
    private WindowId _windowId;
    private AppWindow? _appWindow;
    private TrayIconService? _trayService;
    private bool _isForceExiting;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);

        this.InitializeComponent();

        TrySetFluentBackdrop();
        InitializeWindowTitleBarAndTray();
        if (string.IsNullOrWhiteSpace(ViewModel.Config.ThemeMode) || ViewModel.Config.ThemeMode == "Default")
        {
            ViewModel.Config.ThemeMode = Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Dark";
        }
        ApplyTheme(ViewModel.Config.ThemeMode);

        ViewModel.CorruptedFileAlertRaised += OnCorruptedFileAlertRaised;
        ViewModel.FileTreeRebuilt += () =>
        {
            DispatcherQueue.TryEnqueue(UpdateFileTreeViewRootNodes);
        };
        ViewModel.TargetNodeSelected += node =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                FileTreeView.SelectedNode = node;
            });
        };

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateTitleBarButtonColors();
            ViewModel.RefreshDiffPalette(RootGrid.ActualTheme == ElementTheme.Light);
        };

        _ = ViewModel.RefreshEnvironmentAndAddonsAsync();
    }

    private void UpdateFileTreeViewRootNodes()
    {
        FileTreeView.RootNodes.Clear();
        foreach (var node in ViewModel.RootTreeNodes)
        {
            FileTreeView.RootNodes.Add(node);
        }
    }

    private void TrySetFluentBackdrop()
    {
        if (MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            this.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    private void InitializeWindowTitleBarAndTray()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        _appWindow.Resize(new SizeInt32(1380, 860));
        _appWindow.Closing += OnAppWindowClosing;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            var titleBar = _appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            RightCaptionInsetColumn.Width = new GridLength(titleBar.RightInset > 0 ? titleBar.RightInset : 138);

            AppTitleBar.Loaded += (_, _) => UpdateTitleBarPassthroughRegions();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarPassthroughRegions();
            TitleBarLeftInteractivePanel.SizeChanged += (_, _) => UpdateTitleBarPassthroughRegions();
            TitleBarRightInteractivePanel.SizeChanged += (_, _) => UpdateTitleBarPassthroughRegions();
        }

        _trayService = new TrayIconService(_hwnd);
        _trayService.CanSyncProvider = () => ViewModel.HasSelectedAddon;
        _trayService.CanPushProvider = () => ViewModel.HasSelectedAddon && !string.IsNullOrWhiteSpace(ViewModel.SelectedAddon?.RemoteUrl);

        _trayService.RestoreRequested += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ShowWindow(_hwnd, SW_RESTORE);
                SetForegroundWindow(_hwnd);
            });
        };

        _trayService.SyncRequested += () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var addon = ViewModel.SelectedAddon;
                if (addon == null)
                    return;

                ViewModel.AppendLog(addon.AddonName, "托盘触发立即同步...");
                var rec = await ViewModel.WatcherService.SyncFullAddonAsync(addon);
                if (rec == null)
                {
                    ViewModel.AppendLog(addon.AddonName, "所有文件均已是最新状态");
                }
                await ViewModel.RebuildFileTreeForSelectedAddonAsync();
                await ViewModel.LoadCommitHistoryForSelectedAddonAsync();
                _trayService.ShowNotification("同步完成", $"{addon.AddonName} 同步成功");
            });
        };

        _trayService.PushRequested += () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var addon = ViewModel.SelectedAddon;
                if (addon == null)
                    return;

                if (string.IsNullOrWhiteSpace(addon.RemoteUrl))
                {
                    _trayService.ShowNotification("未配置远程仓库", "请先在主界面点击【仓库配置】填写远程 Git URL", true);
                    return;
                }

                ViewModel.AppendLog(addon.AddonName, $"托盘触发推送: {addon.RemoteUrl} ...");
                var res = await ViewModel.GitService.PushToRemoteAsync(addon);
                _trayService.ShowNotification(res.Success ? "推送成功" : "推送失败", res.Message, !res.Success);
            });
        };

        _trayService.ExitRequested += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isForceExiting = true;
                this.Close();
            });
        };

        this.Closed += (_, _) =>
        {
            ViewModel.Dispose();
            _trayService?.Dispose();
        };
    }

    private void UpdateTitleBarPassthroughRegions()
    {
        if (_appWindow == null || RootGrid.XamlRoot == null)
            return;

        try
        {
            double scale = RootGrid.XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();

            if (TitleBarLeftInteractivePanel.ActualWidth > 0 && TitleBarLeftInteractivePanel.ActualHeight > 0)
            {
                var leftTransform = TitleBarLeftInteractivePanel.TransformToVisual(null);
                var leftBounds = leftTransform.TransformBounds(new Windows.Foundation.Rect(
                    0, 0, TitleBarLeftInteractivePanel.ActualWidth, TitleBarLeftInteractivePanel.ActualHeight));

                rects.Add(new RectInt32(
                    (int)Math.Round(leftBounds.X * scale),
                    (int)Math.Round(leftBounds.Y * scale),
                    (int)Math.Round(leftBounds.Width * scale),
                    (int)Math.Round(leftBounds.Height * scale)));
            }

            if (TitleBarRightInteractivePanel.ActualWidth > 0 && TitleBarRightInteractivePanel.ActualHeight > 0)
            {
                var rightTransform = TitleBarRightInteractivePanel.TransformToVisual(null);
                var rightBounds = rightTransform.TransformBounds(new Windows.Foundation.Rect(
                    0, 0, TitleBarRightInteractivePanel.ActualWidth, TitleBarRightInteractivePanel.ActualHeight));

                rects.Add(new RectInt32(
                    (int)Math.Round(rightBounds.X * scale),
                    (int)Math.Round(rightBounds.Y * scale),
                    (int)Math.Round(rightBounds.Width * scale),
                    (int)Math.Round(rightBounds.Height * scale)));
            }

            if (rects.Count > 0)
            {
                var nonClientSrc = InputNonClientPointerSource.GetForWindowId(_windowId);
                nonClientSrc.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
            }
        }
        catch
        {
            // Ignore transient layout transform errors during window minimize/restore
        }
    }

    private void ApplyTheme(string themeMode)
    {
        ElementTheme target = string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Light : ElementTheme.Dark;

        RootGrid.RequestedTheme = target;
        bool isLight = RootGrid.ActualTheme == ElementTheme.Light;
        ThemeIcon.Glyph = isLight ? "\uE706" : "\uE708";

        UpdateTitleBarButtonColors();
        ViewModel.RefreshDiffPalette(isLight);
    }

    private void UpdateTitleBarButtonColors()
    {
        if (_appWindow == null || !AppWindowTitleBar.IsCustomizationSupported())
            return;

        bool isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonForegroundColor = isLight ? Color.FromArgb(255, 24, 24, 27) : Colors.White;
        titleBar.ButtonHoverForegroundColor = isLight ? Colors.Black : Colors.White;
        titleBar.ButtonHoverBackgroundColor = isLight
            ? Color.FromArgb(20, 0, 0, 0)
            : Color.FromArgb(24, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = isLight
            ? Color.FromArgb(35, 0, 0, 0)
            : Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonInactiveForegroundColor = isLight
            ? Color.FromArgb(140, 24, 24, 27)
            : Color.FromArgb(140, 255, 255, 255);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isForceExiting && ViewModel.Config.MinimizeToTrayOnClose)
        {
            args.Cancel = true;
            ShowWindow(_hwnd, SW_HIDE);
            _trayService?.ShowNotification(
                "CS2 Hammer5Git",
                "已在后台托盘持续守护您的所有地图项目。点击托盘图标可重新打开主界面。");
        }
    }

    private void OnFileTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        if (e.InvokedItem is FileTreeViewNode node)
        {
            ViewModel.HandleTreeNodeInvoked(node.NodeData, node);
        }
        else if (e.InvokedItem is TreeViewNode treeNode && treeNode.Content is FileTreeNodeViewModel content)
        {
            ViewModel.HandleTreeNodeInvoked(content, treeNode);
        }
    }

    private async void OnRemoveAddonClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null)
            return;

        var deleteShadowRepoBox = new CheckBox
        {
            Content = "同时删除影子 Git 仓库数据 (.git 与版本快照)",
            IsChecked = false
        };

        var deleteLocalFilesBox = new CheckBox
        {
            Content = "同时清理 CS2 Addon 本地文件目录 (content 与 game 目录)",
            IsChecked = false
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"确定要从列表中移除项目【{addon.AddonName}】吗？",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "默认仅将该项目从本工具列表中移除，停止守护。如需彻底清理文件，可勾选下方选项：",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                },
                deleteShadowRepoBox,
                deleteLocalFilesBox
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "移除项目",
            Content = panel,
            PrimaryButtonText = "确认移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveAddonAsync(addon, deleteShadowRepoBox.IsChecked == true, deleteLocalFilesBox.IsChecked == true);
        }
    }

    private async void OnAddAddonClick(object sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync(_hwnd);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        if (!ViewModel.EnvService.ValidateAddonFolder(folder, ViewModel.Config.Cs2InstallPath, out string addonName, out string error))
        {
            await ShowDialogAsync("添加项目失败", error);
            return;
        }

        var (success, msg) = await ViewModel.AddAddonFromFolderAsync(folder, addonName);
        if (!success)
        {
            await ShowDialogAsync("添加项目提示", msg);
        }
    }

    private async void OnLoadRepoClick(object sender, RoutedEventArgs e)
    {
        var localRadio = new RadioButton { Content = "本地 Git 仓库", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
        var remoteRadio = new RadioButton { Content = "远程 Git 仓库 (URL)", IsChecked = false };

        var radioPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { localRadio, remoteRadio }
        };

        var localPathBox = new TextBox
        {
            PlaceholderText = @"选取包含 content 文件夹与 .vmap 的本地仓库",
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var browseLocalBtn = new Button
        {
            Content = "浏览...",
            Padding = new Thickness(12, 5, 12, 5)
        };
        var localRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { localPathBox, browseLocalBtn }
        };
        Grid.SetColumn(localPathBox, 0);
        Grid.SetColumn(browseLocalBtn, 1);

        var remoteUrlBox = new TextBox
        {
            PlaceholderText = "https://github.com/user/my_cs2_map.git",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };

        var addonNameBox = new TextBox
        {
            PlaceholderText = "输入在 CS2 中显示的 Addon 名称 (如 de_custom)",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        localRadio.Checked += (_, _) =>
        {
            localRow.Visibility = Visibility.Visible;
            remoteUrlBox.Visibility = Visibility.Collapsed;
        };

        remoteRadio.Checked += (_, _) =>
        {
            localRow.Visibility = Visibility.Collapsed;
            remoteUrlBox.Visibility = Visibility.Visible;
        };

        browseLocalBtn.Click += async (_, _) =>
        {
            string? picked = await PickFolderAsync(_hwnd);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                localPathBox.Text = picked;
                if (string.IsNullOrWhiteSpace(addonNameBox.Text))
                {
                    addonNameBox.Text = Path.GetFileName(picked.TrimEnd('\\', '/'));
                }
            }
        };

        remoteUrlBox.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(addonNameBox.Text) && !string.IsNullOrWhiteSpace(remoteUrlBox.Text))
            {
                string u = remoteUrlBox.Text.Trim().TrimEnd('/');
                if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    u = u[..^4];
                int lastSlash = u.LastIndexOfAny(new[] { '/', '\\', ':' });
                if (lastSlash >= 0 && lastSlash < u.Length - 1)
                {
                    addonNameBox.Text = u[(lastSlash + 1)..];
                }
            }
        };

        var contentPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                radioPanel,
                CreateFieldLabel("仓库路径 / URL:"),
                localRow,
                remoteUrlBox,
                CreateFieldLabel("CS2 项目名称 (Addon Name):"),
                addonNameBox,
                new TextBlock
                {
                    Text = "提示：载入的仓库必须符合 CS2 规范，根目录必须包含 content 文件夹，且包含至少一个 .vmap 地图文件。导入后将自动同步至 CS2 对应目录。",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "载入仓库",
            Content = contentPanel,
            PrimaryButtonText = "确认载入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            bool isRemote = remoteRadio.IsChecked == true;
            string targetPathOrUrl = isRemote ? remoteUrlBox.Text.Trim() : localPathBox.Text.Trim();
            string addonName = addonNameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(targetPathOrUrl))
            {
                await ShowDialogAsync("载入失败", isRemote ? "请输入有效的远程仓库 URL。" : "请先选择本地仓库文件夹。");
                return;
            }

            if (string.IsNullOrWhiteSpace(addonName))
            {
                await ShowDialogAsync("载入失败", "Addon 名称不能为空。");
                return;
            }

            var (loadOk, loadMsg) = await ViewModel.LoadRepoAsync(targetPathOrUrl, isRemote, addonName);
            if (!loadOk)
            {
                await ShowDialogAsync("载入失败", loadMsg);
            }
        }
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        string current = ViewModel.Config.ThemeMode;
        if (!string.Equals(current, "Dark", StringComparison.OrdinalIgnoreCase) && !string.Equals(current, "Light", StringComparison.OrdinalIgnoreCase))
        {
            current = RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        }

        string next = string.Equals(current, "Dark", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        ViewModel.Config.ThemeMode = next;
        ViewModel.Config.Save();
        ApplyTheme(next);
    }

    private static async Task<string?> PickFolderAsync(IntPtr hwnd)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
    }

    private async void OnGlobalSettingsClick(object sender, RoutedEventArgs e)
    {
        var reposGrid = CreatePathPickerRow(ViewModel.Config.DefaultReposRoot, out var reposRootBox);

        var reposContainer = new StackPanel { Spacing = 4 };
        reposContainer.Children.Add(CreateFieldLabel("默认影子仓库根目录"));
        reposContainer.Children.Add(reposGrid);

        var debounceBox = new NumberBox
        {
            Header = "刷新间隔 (毫秒)",
            Value = ViewModel.Config.DebounceMilliseconds,
            Minimum = 500,
            Maximum = 15000,
            SmallChange = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var maxCommitsBox = new NumberBox
        {
            Header = "最大保留提交数",
            Value = ViewModel.Config.MaxAutoCommits,
            Minimum = 50,
            Maximum = 5000,
            SmallChange = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var panel = new StackPanel { Spacing = 14, Width = 460 };
        panel.Children.Add(reposContainer);

        var numGrid = new Grid { ColumnSpacing = 12 };
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(debounceBox, 0);
        Grid.SetColumn(maxCommitsBox, 1);
        numGrid.Children.Add(debounceBox);
        numGrid.Children.Add(maxCommitsBox);
        panel.Children.Add(numGrid);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "全局设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (!string.IsNullOrWhiteSpace(reposRootBox.Text))
                ViewModel.Config.DefaultReposRoot = reposRootBox.Text.Trim();
            if (!double.IsNaN(debounceBox.Value))
                ViewModel.Config.DebounceMilliseconds = (int)debounceBox.Value;
            if (!double.IsNaN(maxCommitsBox.Value))
                ViewModel.Config.MaxAutoCommits = (int)maxCommitsBox.Value;

            ViewModel.Config.Save();
            ViewModel.AppendLog("System", "设置已保存");
        }
    }

    private async void OnManualSyncNowClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null)
            return;

        try
        {
            ViewModel.AppendLog(addon.AddonName, "正在扫描变更并同步...");
            var rec = await ViewModel.WatcherService.SyncFullAddonAsync(addon);
            if (rec == null)
            {
                ViewModel.AppendLog(addon.AddonName, "无新实质变更。");
            }
            ViewModel.RebuildFileTreeForSelectedAddon();
            await ViewModel.LoadCommitHistoryForSelectedAddonAsync();
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog(addon.AddonName, $"同步异常: {ex.Message}");
            await ShowDialogAsync("同步失败", ex.Message);
        }
    }

    private async void OnPushRemoteClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null)
            return;

        if (string.IsNullOrWhiteSpace(addon.RemoteUrl))
        {
            await ShowDialogAsync("尚未配置远程仓库", "请先点击顶栏「仓库配置」按钮填写远程仓库 URL。");
            return;
        }

        try
        {
            ViewModel.AppendLog(addon.AddonName, $"正在推送至: {addon.RemoteUrl} ...");
            var res = await ViewModel.GitService.PushToRemoteAsync(addon);
            ViewModel.AppendLog(addon.AddonName, res.Message);
            await ShowDialogAsync(res.Success ? "推送成功" : "推送失败", res.Message);
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog(addon.AddonName, $"推送异常: {ex.Message}");
            await ShowDialogAsync("推送异常", ex.Message);
        }
    }

    private void OnOpenShadowRepoDirClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null)
            return;

        Directory.CreateDirectory(addon.ShadowRepoPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = addon.ShadowRepoPath,
            UseShellExecute = true
        });
    }

    private async void OnAddonConfigClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null)
            return;

        var shadowGrid = CreatePathPickerRow(addon.ShadowRepoPath, out var shadowPathBox);

        var shadowContainer = new StackPanel { Spacing = 4 };
        shadowContainer.Children.Add(CreateFieldLabel("影子 Git 仓库路径"));
        shadowContainer.Children.Add(shadowGrid);

        var remoteUrlBox = new TextBox
        {
            Header = "远程 Git 仓库 URL",
            Text = addon.RemoteUrl,
            PlaceholderText = "https://github.com/user/map-backup.git"
        };

        var trackGameCheckBox = new CheckBox
        {
            Content = "追踪 game 侧配置文件 (addoninfo / cfg / Panorama)",
            IsChecked = addon.TrackGameSideConfigs
        };

        var autoPushOnTagCheckBox = new CheckBox
        {
            Content = "打里程碑标签时自动推送远程",
            IsChecked = addon.AutoPushOnTag
        };

        var lfsHeader = new TextBlock
        {
            Text = "Git LFS 二进制素材追踪扩展名：",
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 2)
        };

        var wrapGrid = new Grid { ColumnSpacing = 14, RowSpacing = 6 };
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wrapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var checkboxes = new List<CheckBox>();
        int idx = 0;
        foreach (var ext in ShadowGitService.OptionalBinaryAssetExtensions)
        {
            int row = idx / 3;
            int col = idx % 3;
            while (wrapGrid.RowDefinitions.Count <= row)
                wrapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cb = new CheckBox
            {
                Content = ext,
                Tag = ext,
                IsChecked = addon.LfsTrackedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            };
            Grid.SetRow(cb, row);
            Grid.SetColumn(cb, col);
            wrapGrid.Children.Add(cb);
            checkboxes.Add(cb);
            idx++;
        }

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(shadowContainer);
        panel.Children.Add(remoteUrlBox);
        panel.Children.Add(trackGameCheckBox);
        panel.Children.Add(autoPushOnTagCheckBox);
        panel.Children.Add(lfsHeader);
        panel.Children.Add(wrapGrid);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"仓库配置 — {addon.AddonName}",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string newRepoPath = shadowPathBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newRepoPath))
                addon.ShadowRepoPath = newRepoPath;

            addon.RemoteUrl = remoteUrlBox.Text.Trim();
            addon.TrackGameSideConfigs = trackGameCheckBox.IsChecked == true;
            addon.AutoPushOnTag = autoPushOnTagCheckBox.IsChecked == true;
            addon.LfsTrackedExtensions = checkboxes
                .Where(c => c.IsChecked == true && c.Tag is string)
                .Select(c => (string)c.Tag)
                .ToList();
            addon.NotifyLfsChanged();

            ViewModel.Config.Save();
            await ViewModel.GitService.UpdateIgnoreAndLfsRulesAsync(addon);
            ViewModel.AppendLog(addon.AddonName, "已更新仓库配置。");
        }
    }

    private async void OnRefreshHistoryClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RebuildFileTreeForSelectedAddon();
        await ViewModel.LoadCommitHistoryForSelectedAddonAsync();
    }

    private async void OnAddTagClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        var commit = ViewModel.SelectedCommit;
        if (addon == null || commit == null)
            return;

        string tag = TagInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tag))
            return;

        AddTagFlyout.Hide();
        var res = await ViewModel.GitService.AddMilestoneTagAsync(addon, commit.Hash, tag);
        ViewModel.AppendLog(addon.AddonName, res.Message);
        if (res.Success)
        {
            TagInputBox.Text = string.Empty;
            await ViewModel.LoadCommitHistoryForSelectedAddonAsync();
        }
        else
        {
            await ShowDialogAsync("打标失败", res.Message);
        }
    }

    private void OnOpenFileFromDiffClick(object sender, RoutedEventArgs e)
    {
        var addon = ViewModel.SelectedAddon;
        if (addon == null || sender is not Button { Tag: string repoRelPath } || string.IsNullOrWhiteSpace(repoRelPath))
            return;

        string fullDiskPath = addon.ResolveDiskFilePath(repoRelPath);
        try
        {
            if (File.Exists(fullDiskPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullDiskPath,
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(fullDiskPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog(addon.AddonName, $"打开文件失败: {ex.Message}");
        }
    }

    private void OnFilterFileHistoryFromDiffClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string repoRelPath } && !string.IsNullOrWhiteSpace(repoRelPath))
        {
            ViewModel.SelectFileInTreeAndFilterHistory(repoRelPath);
        }
    }

    private async void OnRestoreSaveAsCopyFromDiffClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string repoRelPath } && !string.IsNullOrWhiteSpace(repoRelPath))
        {
            await ExecuteRestoreForFileAsync(repoRelPath, overwriteOriginal: false);
        }
    }

    private async void OnRestoreOverwriteFromDiffClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string repoRelPath } && !string.IsNullOrWhiteSpace(repoRelPath))
        {
            await ExecuteRestoreForFileAsync(repoRelPath, overwriteOriginal: true);
        }
    }

    private void OnExpandFileDiffClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string repoRelPath } && !string.IsNullOrWhiteSpace(repoRelPath))
        {
            ViewModel.ExpandFileDiffLines(repoRelPath);
        }
    }

    private async Task ExecuteRestoreForFileAsync(string repoRelativePath, bool overwriteOriginal)
    {
        var addon = ViewModel.SelectedAddon;
        var commit = ViewModel.SelectedCommit;
        if (addon == null || commit == null || string.IsNullOrWhiteSpace(repoRelativePath))
            return;

        if (overwriteOriginal)
        {
            bool isRunning = ViewModel.EnvService.IsHammerOrCs2Running(out var runningProcs);
            string warningText = isRunning
                ? $"⚠️ 检测到正在运行: {string.Join(", ", runningProcs)}！\n建议先在 Hammer 中关闭当前地图。\n\n确定将 [{commit.ShortHash}] 的 {repoRelativePath} 直接覆盖回原文件吗？"
                : $"确定将历史版本 [{commit.ShortHash}] 的 {repoRelativePath} 直接覆盖回原文件吗？";

            var confirm = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "确认覆盖恢复",
                Content = warningText,
                PrimaryButtonText = "确认覆盖",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        string targetPath = addon.ResolveDiskFilePath(repoRelativePath);
        if (overwriteOriginal && !string.IsNullOrEmpty(targetPath))
        {
            ViewModel.WatcherService.SuppressPathEvents(targetPath, TimeSpan.FromSeconds(8));
        }

        var res = await ViewModel.GitService.RestoreFileFromCommitAsync(
            addon,
            commit.Hash,
            repoRelativePath,
            overwriteOriginal,
            ViewModel.VmapConverter);

        ViewModel.AppendLog(addon.AddonName, res.Message);
        await ShowDialogAsync(res.Success ? "恢复成功" : "恢复失败", res.Message);
    }

    private async void OnEmergencyRestoreLatestClick(object sender, RoutedEventArgs e)
    {
        var alert = ViewModel.LatestAlert;
        if (alert == null)
            return;

        var addon = ViewModel.Addons.FirstOrDefault(a =>
            a.AddonName.Equals(alert.AddonName, StringComparison.OrdinalIgnoreCase));
        if (addon == null)
            return;

        var history = await ViewModel.GitService.GetCommitHistoryAsync(addon, 10);
        var latestCommit = history.FirstOrDefault();
        if (latestCommit == null)
        {
            await ShowDialogAsync("无法恢复", "影子仓库中尚无有效历史提交。");
            return;
        }

        string relInContent = Path.GetRelativePath(addon.ContentAddonPath, alert.OriginalFilePath);
        var (canonicalRel, _) = VmapConverterAndSanitizer.NormalizeVmapRelativePath(relInContent);

        ViewModel.WatcherService.SuppressPathEvents(alert.OriginalFilePath, TimeSpan.FromSeconds(8));

        var res = await ViewModel.GitService.RestoreFileFromCommitAsync(
            addon,
            latestCommit.Hash,
            canonicalRel,
            overwriteOriginal: true,
            ViewModel.VmapConverter);

        ViewModel.AppendLog(addon.AddonName, $"[紧急恢复] {res.Message}");
        CrashAlertBanner.IsOpen = false;
        await ShowDialogAsync(res.Success ? "紧急恢复完成" : "紧急恢复失败", res.Message);
    }

    private void OnCorruptedFileAlertRaised(CorruptedFileAlert alert)
    {
        CrashAlertBanner.Title = $"[{alert.AddonName}] 拦截到损坏的 VMAP: {Path.GetFileName(alert.OriginalFilePath)}";
        CrashAlertBanner.Message = alert.ErrorDetail;
        CrashAlertBanner.IsOpen = true;

        _trayService?.ShowNotification(
            "⚠️ 拦截到损坏的 VMAP 文件！",
            $"{Path.GetFileName(alert.OriginalFilePath)} 校验未通过，已保护 Git 历史。",
            isError: true);
    }

    private static TextBlock CreateFieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };

    private Grid CreatePathPickerRow(string initialPath, out TextBox box)
    {
        var pathBox = new TextBox
        {
            Text = initialPath,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        box = pathBox;
        ScrollViewer.SetHorizontalScrollBarVisibility(pathBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(pathBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(pathBox, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollMode(pathBox, ScrollMode.Disabled);
        ToolTipService.SetToolTip(pathBox, pathBox.Text);

        var browseBtn = new Button
        {
            Content = "浏览...",
            VerticalAlignment = VerticalAlignment.Center
        };
        browseBtn.Click += async (_, _) =>
        {
            var path = await PickFolderAsync(_hwnd);
            if (!string.IsNullOrWhiteSpace(path))
            {
                pathBox.Text = path;
                ToolTipService.SetToolTip(pathBox, path);
            }
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseBtn, 1);
        grid.Children.Add(pathBox);
        grid.Children.Add(browseBtn);

        return grid;
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
