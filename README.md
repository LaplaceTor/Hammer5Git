# Hammer5Git

<div align="center">

**专为 Counter-Strike 2 (CS2) Hammer 关卡设计打造的自动化 Git 版本管理与影子仓库工具**

[![Build and Package](https://github.com/LaplaceTor/Hammer5Git/actions/workflows/package.yml/badge.svg)](https://github.com/LaplaceTor/Hammer5Git/actions/workflows/package.yml)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL3-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6.svg)](https://github.com/LaplaceTor/Hammer5Git)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

</div>

---

## 📖 项目简介

在开发 Counter-Strike 2 (CS2) Addon 与地图制作时，Valve 的 Hammer 5 编辑器将文件分散存放在 `content/csgo_addons` 与 `game/csgo_addons` 两处，且 `.vmap` 默认为二进制格式，直接纳入 Git 无法进行文本 Diff，且容易发生冲突与体积膨胀。

**Hammer5Git** 基于现代 **WinUI 3** 与 **.NET 10** 构建，采用三层架构。它在后台构建独立的「影子 Git 仓库（Shadow Git Repo）」，实时监控 Addon 变动，并在入库前自动通过 `dmxconvert.exe` 将二进制地图转换为可 Diff 的文本格式，实现静默、无感、安全的地图工程版本控制。

---

## ✨ 核心特性

- 🎯 **自动探测 CS2 环境**：自动解析 Windows 注册表与 Steam `libraryfolders.vdf` / `appmanifest_730.acf`，精准定位多盘符下的 CS2 与 `dmxconvert.exe`。
- 🔄 **自动转换与文本化 Diff**：保存 `.vmap` 时后台静默调用 `dmxconvert` 转换为规范文本格式，剥离随机元数据与易变时间戳，让二进制地图也能进行清晰直观的 Git Diff。
- 🛡️ **双目录聚合与影子仓库**：将 CS2 分离的 `content/` 与 `game/` 目录透明映射入单个统一的 Git 仓库，且不污染原始游戏目录。
- 📦 **仓库导入与外挂映射**：支持将现有的远程 Git 仓库（如 GitHub/GitLab）或外部文件夹一键导入并挂载至本地 CS2 Addon。
- 🔔 **托盘静默运行**：支持单实例防重启动、最小化至系统托盘、后台静默同步与变更通知。
- 🚑 **异常容错与损坏备份**：内置恢复备份机制，自动隔离损坏的地图转储（`.corrupted_dumps`）与恢复备份（`.recovery_backups`）。

---

## 🏛️ 项目架构

项目严格拆分为三层架构，保证高内聚、低耦合与易维护性：

```text
Hammer5Git/
├── src/
│   ├── Hammer5Git.Core/       # 核心业务逻辑层 (纯类库，无 UI 依赖)
│   │   ├── Models/            # 应用配置、Addon 配置、同步状态数据模型
│   │   └── Services/          # 核心服务 (ShadowGit, AddonWatcher, Cs2Locator, RepoImport, VmapConverter)
│   │
│   ├── Hammer5Git.App/        # WinUI 3 界面与交互层 (类库)
│   │   ├── Services/          # 托盘图标服务 (TrayIconService)
│   │   ├── ViewModels/        # MVVM 视图模型 (MainViewModel)
│   │   └── MainWindow.xaml    # 主窗口视图与交互逻辑
│   │
│   └── Hammer5Git.Main/       # 引导启动宿主 (WinExe)
│       ├── Program.cs         # 入口点、单实例 Mutex、崩溃日志捕获
│       └── app.manifest       # 高 DPI、Windows 10/11 兼容性与权限清单
│
├── .github/workflows/         # GitHub Actions 自动化构建与发布流水线
├── Directory.Build.props      # 全局 MSBuild 任务与 WinUI PRI 工具链自动定位
└── Hammer5Git.slnx            # 现代 .slnx 解决方案配置
```

---

## 🚀 快速上手

### 运行环境要求
- **操作系统**：Windows 10 1809 (Build 17763) 或更高版本 / Windows 11 (x64)
- **游戏要求**：已安装 Counter-Strike 2 与 Workshop Tools (用于提供 `dmxconvert.exe`)
- **Git**：系统中已安装 Git 并配置在系统 `PATH` 中

### 获取可执行程序
直接前往 [Releases 页面](https://github.com/LaplaceTor/Hammer5Git/releases) 下载最新打包产物：
- **自包含独立版 (`selfcontained.zip`)**：已打包独立运行时，解压即可运行，体积经严格瘦身裁剪。
- **框架依赖版 (`dependent.zip`)**：超轻量安装包，需本地已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。

---

## 🛠️ 本地构建

确保本地安装了 [.NET 10 SDK](https://dotnet.microsoft.com/download) 与 Visual Studio (包含「Windows 应用开发」工作负载)：

```powershell
# 克隆仓库
git clone https://github.com/LaplaceTor/Hammer5Git.git
cd Hammer5Git

# 使用 Release 配置编译项目
dotnet build -c Release
```

编译输出产物将直接生成至根目录的 `dist/` 文件夹中。

---

## 📄 开源许可证

本项目基于 [GNU General Public License v3.0](LICENSE) 许可证开源。
