# AGENTS.md — Offloader 项目交接文档

> 总结文档（首版 2026-09-05；2026-09-06 发布准备时更新）。后续 agent 接手前请先读此文件。
>
> **命名约定（发布时拍板）**：GitHub 仓库与本地目录名 = `playnite-offloader`；插件一切内部标识（extension.yaml 的 Name/Id、程序集名、`LOCoffloader*` key、菜单分区、日志前缀）**保持 Offloader 不变**。
>
> **提交约定**：commit message 一律用**英文**书写（summary 与 body 都不得用中文），禁止混入中文 commit。

## 1. 项目简介

Playnite 6 通用插件（GenericPlugin），给“不舍得删游戏但 SSD 吃紧”的收藏党用：

- 按游戏**手动启用**同步，本地安装目录 → 远端仓库（HDD/NAS）。
- 通关/长期不玩后可**释放本地**：整个删除本地目录，远端保留唯一副本，游戏标记为未安装。
- 想玩时通过 Playnite 的**安装入口恢复**（从远端拉回）。
- 无任何自动同步（防本地损坏污染远端）：所有事件处理器（`OnGameStarted`/`OnGameStopped` 等）全空；启用同步时会自动发起一次后台推送，此后一切写远端都必须经右键菜单手动触发。

面向用户的完整说明（含 robocopy 参数表、冲突策略、断点续传）见 `README.md`（中英双语，以其为准）。

## 2. 目录结构

| 路径 | 说明 |
|---|---|
| `Offloader.cs` | 插件主体：右键菜单、安装动作、释放/推送/恢复编排、事件 |
| `OffloaderInstallController.cs` | 自定义安装控制器（未安装游戏的“从 Offloader 仓库恢复”） |
| `OffloaderSettings.cs` | 设置模型 + ViewModel（含 `VerifySettings` 校验） |
| `OffloaderSettingsView.xaml(.cs)` | 设置页（远端路径+浏览按钮、已启用清单入口） |
| `EnrolledGamesView.xaml(.cs)` | 已启用清单弹窗（计数/排序/安装状态列/移除） |
| `Models/EnrolledGameEntry.cs` | 清单展示条目 |
| `Models/GameSyncState.cs` | 单游戏状态（Enabled / LocalPath 快照 / LastPushUtc） |
| `Models/SyncStateStore.cs` | 状态持久化（`states.json`，见 §5 踩坑） |
| `Services/RobocopyPresets.cs` | **robocopy 参数唯一真相源**（三组预设，README 参数表同步自此处） |
| `Services/RobocopySyncService.cs` | robocopy 封装（拉取/推送/远端校验） |
| `README.md` | 中英双语用户文档（功能 + robocopy 专题） |
| `extension.yaml` | 扩展清单（Id 含 GUID，后缀即实例标识；Version 是发版唯一真相源，决定 pext 包名） |
| `manifest.yaml` | 官方插件中心安装包清单（AddonId + 累积 Packages 列表）：**由 CI 维护，人不手改，本地首次发布前不存在此文件**（Publish 后由 update-manifest.ps1 新建/前插，git pull 同步）；不预写种子是为了消灭「manifest 先于 Release 存在」的 404 窗口 |
| `update-manifest.ps1` | CI 侧生成器（纯参数化，本地可手调排障）：Release tag/资产真实 URL/正文 bullet → manifest 首条；校验 tag↔extension 版本、包名、URL 前缀；同版本=整条替换（幂等） |
| `release.ps1` | 本地构建打包脚本（WinPS 5.1/pwsh 7，零依赖）：预检（干净/HEAD 已含新 Version/远端无同名 tag；`-AllowDirty` 仅供演练）→ vswhere 定位 MSBuild → 白名单打包 pext+包内审查 → 打开预填草稿页并打印网页/CI 步骤指引 |
| `.github/workflows/publish-manifest.yml` | release published/edited 触发（prerelease 跳过）：ubuntu-latest + pwsh 步骤调 update-manifest.ps1，变更则机器人 commit 推回 main；需仓库 Actions 权限「Read and write」 |
| `Localization/en_US.xaml` | 英文基线文案（98 个 `LOCoffloader*` key，唯一真相源） |
| `Localization/zh_CN.xaml` | 中文文案（key/占位符必须与 en_US 逐项对齐） |
| `packages/PlayniteSDK.6.15.0/` | SDK（已提交，无需还原即可构建） |

## 3. 关键决策（一期已定，不用再问）

1. 释放 = 终推+校验 → 整个删除本地目录 → `IsInstalled=false`（`InstallDirectory` 保留在库记录里供恢复）。
2. 远端路径 = `RemoteRoot/{GameId}`（防重名/改名，用纯函数，不存库）。
3. 不要全局白名单——手动“启用同步”本身就是白名单。
4. 无自动推/拉：`OnGameStopped` 自动推已移除，不再提供 `EnableAutoPushOnStopped` 开关。
5. 不拦截启动，用 `GetInstallActions` 介入未安装游戏的安装流程。
6. robocopy 参数以 `RobocopyPresets.cs` 为准（旧脚本的 `/IPG:50` 已废弃：`/IPG` 与 `/MT` 互斥，同用直接退出码 16）：拉取 `/E /R:2 /W:5 /MT:16`；前台推 `/E /XO /R:1 /W:3 /MT:16 /NDL`（故意无 `/NP`，逐文件 % 行驱动实时进度）；后台推 `/E /XO /R:1 /W:3 /MT:8 /NP /NDL` + `BelowNormal` 优先级。永不删远端，禁用 `/MIR`。

## 4. 构建与安装

- 必须用 VS 的 MSBuild（`.../MSBuild/Current/Bin/MSBuild.exe`），`dotnet msbuild` 编不过 WPF XAML。
- 配置：Debug/Release 均可；产物取 `Offloader.dll`、`extension.yaml`、`icon.png`、`Localization/` 四项（不要拷 `Playnite.SDK.dll`/`pdb`）。
- 安装：拷到 `Playnite/Extensions/Offloader_5180751b-c8af-41cf-b9de-76253984e71c/`，**完全退出 Playnite（含托盘）后再覆盖更新**，否则 DLL 被锁构建失败。
- 本机 Playnite 在 `C:/Portable/Playnite`（便携版，实为 v10.56，兼容加载 6.x 插件），扩展数据在 `ExtensionsData/5180751b-…/`。

### 发布流程（Release 先行，manifest 由 CI 后置生成；.pext 就是裸 zip，Toolbox 已弃用）

实测确认：Toolbox pack 产物无任何额外元数据，与自制 zip 完全等价；但 Playnite 拖拽安装**只认 `.pext` 后缀**，包名约定 `{Id}_{Version点→下划线}.pext` 维持（CI 按此核对资产名）。

顺序设防：manifest 只会在 Release（含真实资产）已存在后才更新，**PackageUrl 结构上不可能指向 404**；中途放弃发布/漏传资产只会让 manifest 停在旧版，无需回滚。

每次发版：
1. 本地：改代码 + bump `extension.yaml` Version → commit + push main；
2. 本地：`powershell -File release.ps1` → 校验/构建/打包，自动打开预填 tag/标题的草稿页；
3. 网页：正文写顶层 `- ` bullet（**逐条即 Changelog，用户可见更新说明的唯一来源**）→ 上传 pext（**勿改名**）→ Publish；
4. CI：`publish-manifest.yml` 自动生成/刷新 manifest 首条并推回 main（edited 事件可修正正文后刷新同版本条目；prerelease 跳过）；
5. 本地：`git pull` 同步 manifest，闭环完成。

一次性前置：仓库 Settings→Actions→Workflow permissions 若为只读需改「Read and write」；首次 Release 后向官方库（PlayniteAddonLibrary）提 PR 收录 `https://raw.githubusercontent.com/rosystain/playnite-offloader/main/manifest.yaml`。

`RequiredApiVersion` 实测下限为 **6.14.0**（nuget 历史 SDK 逐版本 diff：GenericPlugin/InstallController 系 6.0.0，ShowErrorMessage 单参重载 6.3.0，`InvokeOnInstallationCancelled` 6.14.0 新增且在用）。若用上更新 SDK 成员须同步上调 release.ps1 常量与 workflow env `REQUIRED_API` 两处。

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
7. **`/XO` 的半截文件陷阱（发布前实测确认）**：推送中途被杀会留下时间戳新于源文件的半截目标文件，此后每次 `/XO` 推送都会跳过它，远端静默残留损坏文件。拉取方向无 `/XO` 不受影响。未用 `/Z`（局域网收益小、拉低吞吐），已在 README 如实记录，不要当 bug 修。
8. **含中文的 .ps1 必须存为 UTF-8 with BOM**。WinPS 5.1 对无 BOM 文件按 GBK 解码：轻则字符串乱码，重则多字节尾字节吞掉下一个 ASCII 字符导致花括号失配、语法报错。用编辑工具写完一律复查 BOM（`release.ps1`/`update-manifest.ps1` 均已带）。
9. **PS 5.1 两个小坑**：① `Compress-Archive` 写目录条目用反斜杠（`Localization\x.xaml`），违反 zip 规范，打包一律用 `[IO.Compression.ZipFile]` 显式指定 '/' 条目名；② 原生命令无输出时 `[string](& cmd)` 结果是 **$null 而非 ''**（实测复现），后续 `.Trim()` 直接炸——用 `'' + (& cmd)` 或真值判断，勿链式方法调用。另：`git ls-remote` 无匹配时退出码非 0，EAP=Stop 下需局部降级包裹。
10. **`$Seed` 是 PowerShell 只读自动变量**，脚本里赋普通局部变量同名会静默失败（实测坑过测试 harness）。

## 6. 本地化约定（二期首轮已落地，新 UI 文本必须遵守）

- **英语为基线**：所有面向用户的字符串住在 `Localization/en_US.xaml`，key 前缀 `LOCoffloader` + 分类（`Menu*`/`Notify*`/`Prog*`/`Dlg*`/`Err*`/`Set*`/`List*`/`Verify*`/`Fmt*`/`State*`/`Sync*`）；`zh_CN.xaml` 逐 key 对齐翻译。新增文案＝两个字典各加一条，禁止在代码里硬编码任何语言 UI 字符串。
- 代码侧用 `Playnite.SDK.ResourceProvider.GetString("LOCoffloaderXxx")`（缺失时 Playnite 回落 en_US，再缺失返回 key 本身）；静态 XAML 用 `{DynamicResource}`；带参用 `string.Format(GetString(...), args)`，占位符 `{0}{1}` 两语言必须一致。
- **多行文本不进字典**：XAML 资源串一律单行，换行在 C# 侧用 `\n` 拼接（确认框拆 Head/字段 Fmt*/Note 三段）；以冒号结尾的错误 key，由代码追加 `"\n" + 详情`。
- 不本地化的部分：`logger.*` 日志、代码注释、robocopy 输出解析正则（`新文件|New File` 等匹配的是 Windows 系统 locale）、`OutputTail` 透传的 robocopy 原始输出。
- 已启用清单 UI：主面板只剩「远端目录 + 管理按钮」；计数/不可达回退提示显示在弹窗副标题（绑 `EnrolledStatus`），刷新由弹窗构造/刷新按钮触发，主面板不再预刷。
- csproj 的 `Localization\*.xaml` 是通配 `CopyToOutputDirectory=PreserveNewest`，新语言文件直接放进目录即可，无需改工程。

## 7. 已实测 / 待验证

- 已验证：Debug+Release 0 警告构建；robocopy 推/拉往返（退出码 1、文件数一致）；`states.json` 写入+重载；设置视图可实例化；`extension.yaml` 解析（同目录正常插件逐项比对一致）。
- 待用户实机验证：小游戏完整走一遍 启用→推送→释放→安装恢复；`GetInstallActions` 是否出现在未安装游戏的安装按钮里（若不出现，降级为右键恢复菜单）；UNC 路径。

## 8. 二期候选（未定，需用户拍板再做）

- 远端完整性校验增强（目前只判非空：文件数/大小展示）。
- 恢复目标不存在时的改良交互；重名/删库重导后的孤儿远端清理工具。
- 释放前磁盘空间/远端空间预检；推送失败重试。
