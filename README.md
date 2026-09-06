# Offloader

[English](#documentation) | [简体中文](#说明)

## 说明

**Offloader** 是面向 [Playnite](https://playnite.link/) 的本地游戏冷数据归档扩展。当本地 SSD 存储紧张时，可将游戏目录安全迁移至外置 HDD 或 NAS 共享存储，释放本地空间并保留游戏条目元数据；需要游玩时，可通过 Playnite 原生安装入口一键拉回恢复。

### 核心功能

- **按需冷归档**：按游戏 ID（`GameId`）隔离存储于远端目录，避免同名或重命名冲突。完成终期校验后清理本地数据，客户端状态无缝标记为“未安装”。

- **原生安装集成**：对已归档的游戏，Offloader 直接作为安装提供程序挂载，通过原生“安装”入口全速拉回本地。

- **安全受控同步**：所有远端写入均需手动触发，杜绝静默覆写；单游戏配备并发锁，避免多任务交叉写入。

- **增量高效传输**：基于系统内置多线程 `robocopy` 引擎，自动比对跳过一致文件，支持中断后毫秒级校验续传。

### 菜单指令

| 菜单项                            | 功能描述                                                                         |
| --------------------------------- | -------------------------------------------------------------------------------- |
| **启用同步**                      | 登记游戏并启动后台推送（`BelowNormal` 进程优先级，让出系统资源）。               |
| **立即推送到远端**                | 启动前台同步，支持逐文件实时进度显示。                                           |
| **释放本地空间**                  | 执行终期推送与一致性校验，确认无误后删除本地目录，游戏保留在远端并标记为未安装。 |
| **安装（从 Offloader）**          | 对未安装但存在远端归档的游戏，一键拉回并还原至本地环境。                         |
| **打开远端目录 / 已链接游戏清单** | 浏览远端存储目录或集中查看已纳管的游戏条目。                                     |

### 环境要求

- **操作系统**：Windows 10 / 11（依赖系统内置 `robocopy.exe`）

- **客户端版本**：Playnite 6.x（基于 SDK 6.15.0）

- **目标存储**：具备写入权限的 HDD、移动存储设备或网络共享存储（支持 UNC 路径）

### 安装与配置

1. 到本仓库 **Releases** 页下载最新的 `Offloader_…_x_y.pext`，直接**拖入 Playnite 桌面窗口**即可安装（Playnite 9/10 支持；仅认 `.pext` 后缀）。

   备选：`.pext` 实为 zip，可改后缀后解压至 `Playnite\Extensions\Offloader_5180751b-c8af-41cf-b9de-76253984e71c\`。

2. **升级时须完全退出 Playnite**（确保系统托盘图标已退出，防止 DLL 文件被锁定），再拖入新版 pext 覆盖（或手动解压覆盖）。

3. 启动 Playnite，进入 **设置 → 附加组件 → Offloader** 配置远端根目录（Remote Root）。

### 传输机制与容灾设计

数据传输完全由 Windows 原生 `robocopy` 驱动，在兼顾 SMB/局域网性能的同时提供容灾保护：

- **非破坏性同步策略**：

  - **推送**：采用 `/E /XO` 策略，仅追加新增或修改的文件，永不覆盖远端更新的数据，且不使用 `/MIR` 或 `/PURGE` 破坏性参数，避免本地异常删除波及远端。

  - **拉取**：以 16 线程（`/MT:16`）执行比对合并，确保本地文件状态完全与归档源同步。

- **退出码与断点续传**：

  - 采用 `ExitCode < 8` 作为幂等成功判定标准（0–7 均为正常同步状态）；仅在返回值 ≥ 8 时报错并输出关键日志。

  - 传输意外中断（如掉网、断电或退出程序）后直接重新触发，系统将根据时间戳和文件大小自动跳过已完成的文件。

- **传输边界说明**：若传输进程在执行中被强行终止（Kill Process），目标端可能会残留时间戳偏新的不完整文件，导致后续推送跳过该文件。遇此情况，建议在重推前清理目标端的未完成文件。恢复（拉取）流程不设 `/XO`，不受此影响。

本工程包含 WPF XAML 界面资源，需通过 Visual Studio 的 MSBuild 工具链进行编译：

Bash

```
msbuild Offloader.csproj /p:Configuration=Release
```

编译输出位于 `bin\Release\`，部署时提取 `Offloader.dll` 并与资源文件一同打包。

### 数据安全与隐私

Offloader 为纯本地工具，不包含任何外网连接或遥测。插件运行状态（游戏映射 ID、本地路径快照及推送时间戳）均存储于 Playnite 扩展目录下的 `states.json`。

## Documentation

**Offloader** is a cold-storage archiving extension designed for [Playnite](https://playnite.link/). When SSD storage is constrained, Offloader allows you to safely migrate space-consuming games to secondary HDD or NAS storage while preserving full library metadata. Games can be restored at any time via Playnite's native installation workflow.

### Key Features

- **On-Demand Cold Storage**: Archives games into isolated directories by `GameId` to avoid naming conflicts. Once verified, local files are safely purged, and the game status smoothly transitions to "Uninstalled."

- **Native Restore Flow**: Registered archives act directly as Playnite install providers, allowing one-click restoration back to local storage.

- **Controlled Non-Destructive Sync**: All push operations require explicit user interaction with zero silent background overwrites. Concurrency locks per game prevent cross-process collisions.

- **Efficient Incremental Transfer**: Powered by the native multithreaded `robocopy` engine with millisecond-level skip checks and safe resume capabilities.

### Actions & Commands

| Menu Item                             | Description                                                                                                          |
| ------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| **Enable Sync**                       | Registers the game and starts a low-priority background push (`BelowNormal` thread priority).                        |
| **Push to Remote Now**                | Triggers an immediate foreground transfer with real-time per-file progress tracking.                                 |
| **Free Up Local Space**               | Performs a final push, validates integrity, and removes local files. The archive is retained and marked uninstalled. |
| **Install (from Offloader)**          | Available for uninstalled games with an existing archive. Pulls files back to restore local installation.            |
| **Open Remote Folder / Linked Games** | Inspects remote storage directories or reviews all managed game mappings.                                            |

### System Requirements

- **Operating System**: Windows 10 / 11 (requires built-in `robocopy.exe`)

- **Platform**: Playnite 6.x (built on SDK 6.15.0)

- **Storage Target**: Local secondary drive, external volume, or network share / NAS (UNC paths fully supported)

### Installation & Setup

1. Download the latest `Offloader_…_x_y.pext` from this repo's **Releases** page and simply **drag it into the Playnite desktop window** to install (supported on Playnite 9/10; only the `.pext` extension is accepted).

   Alternative: a `.pext` is a plain zip — rename it to `.zip` and extract into `Playnite\Extensions\Offloader_5180751b-c8af-41cf-b9de-76253984e71c\`.

2. **When upgrading, exit Playnite completely** (including the system tray icon, to prevent DLL file locks) before dropping in the new pext (or extracting over the old files).

3. Launch Playnite, then navigate to **Settings → Add-ons → Offloader** to configure the Remote Root path.

### Transmission Architecture & Reliability

Data transfers are handled directly by the Windows `robocopy` engine, configured for stability and throughput over local storage and SMB shares:

- **Safety & Merge Policies**:

  - **Push**: Utilizes `/E /XO` parameters to append new or modified files without overwriting newer remote items. Destructive switches (`/MIR`, `/PURGE`) are explicitly omitted, ensuring local deletions never cascade into remote backup losses.

  - **Pull**: Employs a full 16-thread copy (`/MT:16`) without `/XO`, ensuring the restored local directory strictly reflects the archived state.

- **Exit Codes & Resumability**:

  - Uses `ExitCode < 8` as the benchmark for successful execution. Errors are raised only on exit codes ≥ 8, accompanied by transmission log tails.

  - Transfers interrupted by network drops, power loss, or application termination can be restarted immediately. Robocopy will verify size and timestamps to resume the remaining delta.

- **Known Boundary Condition**: Force-killing an ongoing transfer may leave an incomplete file with a newer timestamp on the destination. Because of `/XO`, subsequent pushes might skip this partial file. If a push is abnormally terminated, delete the incomplete file on the remote destination before restarting. The pull/restore flow does not use `/XO` and remains unaffected.

This project includes WPF XAML components and requires the Visual Studio MSBuild toolchain:

Bash

```
msbuild Offloader.csproj /p:Configuration=Release
```

Build outputs reside in `bin\Release\`. Extract `Offloader.dll` and bundle it with the extension assets for deployment.

### Privacy & Storage

Offloader performs all file operations locally with zero external network requests or telemetry. Internal mapping state (registered IDs, local directory snapshots, and timestamps) is stored locally within Playnite's `states.json`.
