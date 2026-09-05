# Playnite "启动前" 脚本：NAS 缓存拉取器 (V17 - ASCII-Safe)
# -----------------------------------------------------------
# V17:
# 1. 【【修复】】: 将所有中文提示符替换为纯英文 ASCII，
#    以防止 "智能引号" (“...”) 导致的语法错误。
# 2. 保留 V16 的 $log.Error() 修复。
# -----------------------------------------------------------

$log = [Playnite.SDK.LogManager]::GetLogger("NasCacheScript")

$nasRootPath = "\\10.0.0.3\games\Local Games"
$localCacheRoot = "C:\Games\Local Games"
$robocopyPath = Join-Path $env:WINDIR "System32\robocopy.exe"
$robocopyArgsTemplate = '"{0}" "{1}" /E /R:2 /W:5 /MT:16'

function Invoke-GameCache {
    
    try {
        $localGamePath = $Game.InstallDirectory
    } catch {
        $log.Error("CacheScript ERROR: Get $Game.InstallDirectory failed.")
        $log.Error($_)
        try { $PlayniteApi.Dialogs.ShowErrorMessage("CacheScript ERROR: Could not get InstallDirectory for '$($Game.Name)'.`n`nDetails: $($_.Exception.Message)", "Cache Script Error") } catch {}
        return
    }

    if (-not $localGamePath) { $log.Info("CacheScript: Game path is empty. Skipping."); return }
    if (-not $localGamePath.StartsWith($localCacheRoot)) { $log.Info("CacheScript: Game '$($Game.Name)' is not in cache root. Skipping."); return }
    if (Test-Path $localGamePath) { $log.Info("CacheScript: Game '$($Game.Name)' is already in cache. Skipping."); return }

    $log.Info("CacheScript: Game '$($Game.Name)' not found locally. Preparing to copy from NAS...")
    
    $gameName = Split-Path -Leaf $localGamePath
    $nasGamePath = Join-Path $nasRootPath $gameName

    if (-not (Test-Path $nasGamePath)) {
        $log.Error("CacheScript ERROR: Source path not found on NAS: '$nasGamePath'!")
        try { $PlayniteApi.Dialogs.ShowErrorMessage("CacheScript ERROR: Source path not found on NAS:`n$nasGamePath`n`nWARNING: Script will continue, but game launch may fail.", "Cache Script Error") } catch {}
        return
    }

    $robocopyArgs = $robocopyArgsTemplate -f $nasGamePath, $localGamePath
    
    try {
        $log.Info("CacheScript: Starting Robocopy (Sync): $robocopyPath $robocopyArgs")
        $process = Start-Process -FilePath $robocopyPath -ArgumentList $robocopyArgs -Wait -PassThru -ErrorAction Stop
        
        if ($process.ExitCode -ge 8) {
            $log.Error("CacheScript ERROR: Robocopy reported errors (ExitCode: $($process.ExitCode)).")
            throw "Robocopy copy failed (Code: $($process.ExitCode)). Check permissions or NAS connection."
        }
        $log.Info("CacheScript: Robocopy copy successful.")
    } catch {
        $log.Error("CacheScript ERROR: A critical error occurred during copy operation.")
        $log.Error($_)
        try { $PlayniteApi.Dialogs.ShowErrorMessage("CacheScript ERROR: Failed to copy game:`n`n$($_.Exception.Message)", "Cache Script Error") } catch {}
        return
    }

    $log.Info("CacheScript: Operation completed successfully.")
}

$log.Info("--------------------------------------")
$log.Info("CacheScript(V17) starting...")
if ($null -eq $Game) {
    $log.Error("CacheScript ERROR: $Game object is null.")
    try { $PlayniteApi.Dialogs.ShowErrorMessage("CacheScript ERROR: Playnite did not pass the $Game object to the script. Cannot continue.", "Cache Script Error") } catch {}
} else {
    Invoke-GameCache
}
$log.Info("CacheScript(V17) finished.")
$log.Info("--------------------------------------")