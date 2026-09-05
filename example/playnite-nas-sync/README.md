# NAS Sync for Playnite

利用 Playnite 的脚本模块，使游戏安装数据在本地 PC 跟 NAS 之间进行同步。 

可以实现：

- 游戏启动前，尝试从 NAS 拉取游戏数据到本地 PC。
- 游戏启动后，尝试将游戏数据更新到 NAS。

无法实现：

- 同步安装目录外的数据（非实现目标）。


## 使用方法

此方案由两个 Powershell 脚本构成：

- `playnite_archive_to_nas.ps1`：从本地复制游戏数据到远端 NAS。
- `playnite_cache_from_nas.ps1`：从 NAS 复制游戏数据到本地目录。

将脚本放置于任意目录（推荐放到 Playnite 安装目录），分别编辑两个文件，按照实际情况修改 `$nasRootPath` 和 `$localCacheRoot` 指向的路径。

打开 Playnite 设置 > 脚本，分别在 `启动游戏前执行` 和 `启动游戏后执行` 区域中写入对应的触发命令。

### 启动游戏前执行

```powershell
$ScriptPath = "C:\Users\...\playnite_cache_from_nas.ps1"
if (Test-Path $ScriptPath) { & $ScriptPath }
```

具体流程为：

1. 在 Playnite 中启动游戏。
2. 进入 `启动游戏前` 状态，触发 `playnite_cache_from_nas.ps1`。
	1. 跳过 `$Game.InstallDirectory` 为空的项目。
	2. 跳过不在缓存目录下的项目（避免缓存来自 Steam 等平台的游戏）。
	3. 跳过安装目录下已经存在数据的项目（单向同步）。
3. 使用 `robocopy` 拉取数据到 `$Game.InstallDirectory`。
4. 等待 `robocopy` 完成拉取后，启动游戏。
5. 进入 `启动游戏后` 状态。

### 启动游戏后执行

```powershell
$ScriptPath = "C:\Users\...\playnite_archive_to_nas.ps1"
if (Test-Path $ScriptPath) { & $ScriptPath }
```

具体流程为：

1. 进入 `启动游戏后` 状态，触发 `playnite_cache_from_nas.ps1`。
	1. 跳过 `$Game.InstallDirectory` 为空的项目。
	2. 跳过不在缓存目录下的项目（避免缓存来自 Steam 等平台的游戏）。
	3. 跳过安装目录下不存在数据的项目。
2. 使用 `robocopy` 以后台形式缓缓传输数据到 NAS 目录。
