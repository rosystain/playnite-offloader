# AGENTS.md — Offloader 项目交接文档

> 第一份总结文档（2026-09-05，一期实施结束）。后续 agent 接手前请先读此文件。

## 1. 项目简介

Playnite 6 通用插件（GenericPlugin），给“不舍得删游戏但 SSD 吃紧”的收藏党用：

- 按游戏**手动启用**同步，本地安装目录 → 远端仓库（HDD/NAS）。
- 通关/长期不玩后可**释放本地**：整个删除本地目录，远端保留唯一副本，游戏标记为未安装。
- 想玩时通过 Playnite 的**安装入口恢复**（从远端拉回）。
- 无任何自动同步（防本地损坏污染远端）：所有写入远端都必须经右键菜单手动触发；`OnGameStopped` 自动推已于未上线前移除。

前身是 `example/playnite-nas-sync/` 里的两个 PowerShell 脚本（启动前拉取 / 启动后推送），行为（robocopy 参数、退出码判定）照抄它们。

## 2. 目录结构

| 路径 | 说明 |
|---|---|
| `Offloader.cs` | 插件主体：右键菜单、安装动作、释放/推送/恢复编排、事件 |
| `OffloaderInstallController.cs` | 自定义安装控制器（未安装游戏的“从 Offloader 仓库恢复”） |
| `OffloaderSettings.cs` | 设置模型 + ViewModel（含 `VerifySettings` 校验） |
| `OffloaderSettingsView.xaml(.cs)` | 设置页（远端路径+浏览按钮、已启用清单） |
| `Models/GameSyncState.cs` | 单游戏状态（Enabled / LocalPath 快照 / LastPushUtc） |
| `Models/SyncStateStore.cs` | 状态持久化（`states.json`，见 §5 踩坑） |
| `Services/RobocopySyncService.cs` | robocopy 封装（拉取/推送/远端校验） |
| `extension.yaml` | 扩展清单（Id 含 GUID，后缀即实例标识） |
| `example/playnite-nas-sync/` | 旧版 ps1 实现，只读参考，不参与构建 |
| `packages/PlayniteSDK.6.15.0/` | SDK（已提交，无需还原即可构建） |

## 3. 关键决策（一期已定，不用再问）

1. 释放 = 终推+校验 → 整个删除本地目录 → `IsInstalled=false`（`InstallDirectory` 保留在库记录里供恢复）。
2. 远端路径 = `RemoteRoot/{GameId}`（防重名/改名，用纯函数，不存库）。
3. 不要全局白名单——手动“启用同步”本身就是白名单。
4. 无自动推/拉：`OnGameStopped` 自动推已移除，不再提供 `EnableAutoPushOnStopped` 开关。
5. 不拦截启动，用 `GetInstallActions` 介入未安装游戏的安装流程。
6. robocopy 参数沿用脚本版：拉取 `/E /R:2 /W:5 /MT:16`，推送 `/E /XO /IPG:50 /R:1 /W:3 /NP /NDL`（永不删远端，禁用 `/MIR`）。

## 4. 构建与安装

- 必须用 VS 的 MSBuild（`.../MSBuild/Current/Bin/MSBuild.exe`），`dotnet msbuild` 编不过 WPF XAML。
- 配置：Debug/Release 均可；产物取 `Offloader.dll`、`extension.yaml`、`icon.png`、`Localization/` 四项（不要拷 `Playnite.SDK.dll`/`pdb`）。
- 安装：拷到 `Playnite/Extensions/Offloader_5180751b-c8af-41cf-b9de-76253984e71c/`，**完全退出 Playnite（含托盘）后再覆盖更新**，否则 DLL 被锁构建失败。
- 本机 Playnite 在 `C:/Portable/Playnite`（便携版），扩展数据在 `ExtensionsData/5180751b-…/`。

## 5. 踩坑记录（血泪，勿重蹈）

1. **`Properties = new GenericPluginProperties { HasSettings = true }` 必须在构造函数里设。**
   扩展管理器「通用」分区的筛选条件（反编译 `AddonsViewModel` 确认）是
   `manifest.Type == Generic && ((GenericPlugin)plugin).Properties.HasSettings`。
   漏掉它 → 插件照常加载、右键菜单照常用，但管理器里无行、无设置，且全程无报错。一期就栽在这里。
2. **别用 `Playnite.SDK.Data.Serialization` 做自己的文件持久化。**
   它依赖宿主运行时程序集，宿主外直接 NRE（连 `ToJson("hi")` 都挂）。`SyncStateStore` 改用 `DataContractJsonSerializer`（`System.Runtime.Serialization`，框架内置），并用 `List<GameSyncState>` 而非 `Dictionary<Guid,…>`（Guid 键序列化不可靠）。
3. **`GlobalProgressOptions` 没有无参构造**，用 `new GlobalProgressOptions(text, cancelable)`。
4. **主菜单项放不进「附加组件」下。** 桌面端 `MainMenuItem` 只能落汉堡顶级（写分组名成子菜单，不写名单项直摆）。一期结论：不提供主菜单入口，设置只走扩展管理器。一期曾加过又删掉，见 git 历史。
5. **bash 里跑 powershell 时 `$_` 会被 bash 展开**，涉及 `$` 的命令一律写成 `.ps1` 文件再 `-File` 执行，用完即删。
6. `robocopy` 成功判定是 **`ExitCode < 8`**（0–7 都是成功），不要按 0 判断。

## 6. 已实测 / 待验证

- 已验证：Debug+Release 0 警告构建；robocopy 推/拉往返（退出码 1、文件数一致）；`states.json` 写入+重载；设置视图可实例化；`extension.yaml` 解析（同目录正常插件逐项比对一致）。
- 待用户实机验证：小游戏完整走一遍 启用→推送→释放→安装恢复；`GetInstallActions` 是否出现在未安装游戏的安装按钮里（若不出现，降级为右键恢复菜单）；UNC 路径。

## 7. 二期候选（未定，需用户拍板再做）

- 远端完整性校验增强（目前只判非空：文件数/大小展示）。
- 恢复目标不存在时的改良交互；重名/删库重导后的孤儿远端清理工具。
- 释放前磁盘空间/远端空间预检；推送失败重试。
