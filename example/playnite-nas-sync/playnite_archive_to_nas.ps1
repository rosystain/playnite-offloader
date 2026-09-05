# Playnite "游戏运行时" 脚本：后台归档到 NAS (V2 - Background Sync)
# -----------------------------------------------------------
# 功能：游戏运行时在后台将本地更新推送到 NAS
# 用途：多 PC 场景下保持 NAS 数据为最新状态
# 触发：游戏启动后自动执行（后台运行）
# 特点：
#   - 单向同步（本地 → NAS）
#   - 只传输变化的文件（增量同步）
#   - 不删除 NAS 上的任何文件（安全）
#   - 低资源占用（单线程 + 限速 + 低优先级）
#   - 后台静默运行，不影响游戏性能
# -----------------------------------------------------------

$log = [Playnite.SDK.LogManager]::GetLogger("NasArchiveScript")

$nasRootPath = "\\10.0.0.3\games\Local Games"
$localCacheRoot = "C:\Games\Local Games"
$robocopyPath = Join-Path $env:WINDIR "System32\robocopy.exe"
$robocopyArgsTemplate = '"{0}" "{1}" /E /XO /IPG:50 /R:1 /W:3 /NP /NDL'

function Invoke-GameArchive {
    
    try {
        $localGamePath = $Game.InstallDirectory
    } catch {
        $log.Error("ArchiveScript ERROR: Get `$Game.InstallDirectory failed.")
        $log.Error($_)
        try { $PlayniteApi.Dialogs.ShowErrorMessage("ArchiveScript ERROR: Could not get InstallDirectory for '$($Game.Name)'.`n`nDetails: $($_.Exception.Message)", "Archive Script Error") } catch {}
        return
    }

    if (-not $localGamePath) { $log.Info("ArchiveScript: Game path is empty. Skipping."); return }
    if (-not $localGamePath.StartsWith($localCacheRoot)) { $log.Info("ArchiveScript: Game '$($Game.Name)' is not in cache root. Skipping."); return }
    if (-not (Test-Path $localGamePath)) { $log.Info("ArchiveScript: Game '$($Game.Name)' not found locally. Skipping."); return }

    $log.Info("ArchiveScript: Preparing to archive game '$($Game.Name)' to NAS...")
    
    $gameName = Split-Path -Leaf $localGamePath
    $nasGamePath = Join-Path $nasRootPath $gameName

    # 确保 NAS 目标目录存在（Robocopy 会自动创建，但提前检查可以给出更好的错误提示）
    try {
        if (-not (Test-Path (Split-Path $nasGamePath -Parent))) {
            $log.Error("ArchiveScript ERROR: NAS root path not accessible: '$nasRootPath'!")
            try { $PlayniteApi.Dialogs.ShowErrorMessage("ArchiveScript ERROR: Cannot access NAS root path:`n$nasRootPath`n`nPlease check network connection.", "Archive Script Error") } catch {}
            return
        }
    } catch {
        $log.Error("ArchiveScript ERROR: Failed to check NAS path accessibility.")
        $log.Error($_)
        return
    }

    $robocopyArgs = $robocopyArgsTemplate -f $localGamePath, $nasGamePath
    
    try {
        $log.Info("ArchiveScript: Starting Robocopy (Background Archive): $robocopyPath $robocopyArgs")
        
        # 启动 Robocopy 进程（完全异步，不阻塞 Playnite）
        $process = Start-Process -FilePath $robocopyPath -ArgumentList $robocopyArgs -PassThru -WindowStyle Hidden -ErrorAction Stop
        
        # 设置进程优先级为低
        try {
            $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
            $log.Info("ArchiveScript: Process priority set to BelowNormal (PID: $($process.Id))")
        } catch {
            $log.Warn("ArchiveScript: Could not set process priority: $($_.Exception.Message)")
        }
        
        # 不等待进程完成，让它在后台继续运行
        # 这样 Playnite 不会被阻塞，游戏退出后可以立即响应
        $log.Info("ArchiveScript: Robocopy started in background (PID: $($process.Id)). Playnite will remain responsive.")
        $log.Info("ArchiveScript: Sync will continue even after game exits. Check Task Manager for robocopy.exe process.")
        
    } catch {
        $log.Error("ArchiveScript ERROR: A critical error occurred during archive operation.")
        $log.Error($_)
        try { $PlayniteApi.Dialogs.ShowErrorMessage("ArchiveScript ERROR: Failed to archive game:`n`n$($_.Exception.Message)", "Archive Script Error") } catch {}
        return
    }

    $log.Info("ArchiveScript: Operation completed successfully.")
}

$log.Info("--------------------------------------")
$log.Info("ArchiveScript(V2-Background) starting...")
if ($null -eq $Game) {
    $log.Error("ArchiveScript ERROR: `$Game object is null.")
    try { $PlayniteApi.Dialogs.ShowErrorMessage("ArchiveScript ERROR: Playnite did not pass the `$Game object to the script. Cannot continue.", "Archive Script Error") } catch {}
} else {
    Invoke-GameArchive
}
$log.Info("ArchiveScript(V2-Background) finished.")
$log.Info("--------------------------------------")
