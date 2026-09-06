# release.ps1 - Offloader 本地构建打包脚本（WinPS 5.1 / pwsh 7 均可，零第三方依赖）
# 职责：发版链条的「本地段」——校验工作区与版本 → MSBuild → 白名单打包 pext → 打开预填草稿页。
# 网页段（人工）：Draft Release（tag=v{Version}，正文顶层 "- " bullet 即 Changelog）→ 上传 pext（勿改名）→ Publish。
# CI 段（自动）：release published/edited 触发 publish-manifest.yml 生成/刷新 manifest.yaml，之后本地 git pull 即可。

[CmdletBinding()]
param(
    [switch]$AllowDirty   # 演练构建用：跳过工作区干净检查（产物不可用于发布）
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$RequiredApiVersion = '6.14.0'   # 实测 API 成员下限（nuget 历史 SDK diff，见 AGENTS.md §4）；须与 workflow env 一致

function Fail([string]$msg) { throw "中止: $msg" }

# ---------- 1. 预检 ----------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail '未找到 git' }

$dirty = & git status --porcelain
if ($dirty) {
    if ($AllowDirty) { Write-Warning "工作区不干净（-AllowDirty 演练模式，产物不可发布）:`n$($dirty -join "`n")" }
    else { Fail "工作区不干净：请先 commit 并 push 待发布改动（发布以 main 上的代码为准）:`n$($dirty -join "`n")" }
}

# ---------- 2. 读 extension.yaml ----------
$extYaml = Get-Content extension.yaml -Raw -Encoding UTF8
$extId   = if ($extYaml -match '(?m)^\s*Id:\s*(.+?)\s*$')     { $Matches[1].Trim("'`"") }   else { Fail 'extension.yaml 缺 Id' }
$extVer  = if ($extYaml -match '(?m)^\s*Version:\s*(.+?)\s*$') { $Matches[1].Trim("'`"") } else { Fail 'extension.yaml 缺 Version' }
$pkgName = "{0}_{1}.pext" -f $extId, ($extVer -replace '\.', '_')
$tagName = "v$extVer"
Write-Host "扩展 Id=$extId  Version=$extVer  包名=$pkgName"

# HEAD 应已包含当前 extension.yaml（Release 资产必须来自已推送代码）
# PS5.1 坑：原生命令无输出时 [string](& cmd) 结果是 $null 而非 ''，一律用 ''+(& cmd) 兼容
$originUrl = '' + (& git remote get-url origin 2>$null)
$repoPath  = ($originUrl -replace '\.git$', '') -replace '^.*github\.com[:/]', ''
if ($repoPath) {
    $headVer = '' + (& git show "HEAD:extension.yaml" 2>$null)
    if ($headVer -notmatch [regex]::Escape("Version: $extVer")) {
        Fail "extension.yaml 的 Version=$extVer 尚未提交（HEAD 与将发布代码不一致）"
    }
    $remoteTag = $null
    try {
        $ErrorActionPreference = 'Continue'
        $remoteTag = & git ls-remote --tags origin "refs/tags/$tagName"
    } finally { $ErrorActionPreference = 'Stop' }
    if ($remoteTag) {
        Fail "远端 tag $tagName 已存在（版本号需 bump，或去网页端重发该 Release 以刷新 manifest）"
    }
    $draftUrl = "https://github.com/$repoPath/releases/new?tag=$tagName&title=" + [uri]::EscapeDataString("Offloader $extVer")
} else {
    Write-Warning 'origin 未配置（非 GitHub？），跳过 HEAD/远端 tag 校验，稍后需手动复制草稿页地址'
    $draftUrl = ''
}

# ---------- 3. MSBuild Release ----------
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = (& $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1)
}
if (-not $msbuild) {
    $msbuild = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $msbuild) { Fail '找不到 VS MSBuild（勿用 dotnet msbuild）' }
Write-Host "MSBuild: $msbuild"
& $msbuild (Join-Path $PSScriptRoot 'Offloader.csproj') '/p:Configuration=Release' '/v:minimal' '/nologo'
if ($LASTEXITCODE -ne 0) { Fail 'MSBuild Release 构建失败（若 DLL 被锁，完全退出 Playnite 含托盘后重试）' }

# ---------- 4. 打包白名单 -> output/{pkgName} ----------
$binDir = Join-Path $PSScriptRoot 'bin\Release'
foreach ($w in 'extension.yaml', 'icon.png', 'Offloader.dll', 'Localization') {
    if (-not (Test-Path (Join-Path $binDir $w))) { Fail "bin\Release 缺少 $w（构建输出异常）" }
}

$outDir = Join-Path $PSScriptRoot 'output'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

# 不用 Compress-Archive：PS 5.1 目录条目写反斜杠，违反 zip 规范；手写 ZipFile 保证 '/'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pkgPath = Join-Path $outDir $pkgName
$zip = [System.IO.Compression.ZipFile]::Open($pkgPath, 'Create')
try {
    foreach ($f in 'extension.yaml', 'icon.png', 'Offloader.dll') {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $binDir $f), $f) | Out-Null
    }
    Get-ChildItem (Join-Path $binDir 'Localization') -Filter '*.xaml' | ForEach-Object {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, "Localization/$($_.Name)") | Out-Null
    }
} finally { $zip.Dispose() }

$zr = [System.IO.Compression.ZipFile]::OpenRead($pkgPath)
try { $entries = @($zr.Entries | ForEach-Object { $_.FullName }) } finally { $zr.Dispose() }
$banned = $entries | Where-Object { $_ -match '\.(pdb|xml)$' -or ($_ -match '\.dll$' -and $_ -ne 'Offloader.dll') }
if ($banned) { Remove-Item $pkgPath -Force; Fail "包内含违禁文件: $($banned -join ', ')" }
foreach ($must in 'extension.yaml', 'icon.png', 'Offloader.dll') {
    if ($must -notin $entries) { Remove-Item $pkgPath -Force; Fail "包内缺少 $must" }
}
if (-not ($entries | Where-Object { $_ -like 'Localization/*' })) { Remove-Item $pkgPath -Force; Fail '包内缺少 Localization/' }
Write-Host "打包完成: $pkgPath"
Write-Host ("包内条目: " + ($entries -join ', '))

# ---------- 5. 发布指引（后续全在网页 + CI） ----------
Write-Host @"

==== 接下来到网页完成 ====
1. 打开草稿页（已预填 tag/标题）：$draftUrl
2. 正文写顶层 "- " bullet —— 逐条即 manifest Changelog 与用户可见更新说明；
3. Assets 上传：$pkgName（勿改名，CI 按此核对）；
4. Publish release → CI (publish-manifest.yml) 自动生成 manifest 提交回 main；
5. 本地 git pull 同步 manifest。发布闭环完成，官方库收录后用户端自动提示更新。
========================
"@ -ForegroundColor Cyan
if ($draftUrl -and -not $AllowDirty) { Start-Process $draftUrl }
