# update-manifest.ps1 - 将 GitHub Release 信息转换为 manifest.yaml 首条（版本一致性/资产命名/Changelog 抽取全在此校验）
# 由 .github/workflows/publish-manifest.yml 在 release published/edited 时调用；也支持本地手调排障。
# 约定（见 AGENTS.md §4）：
#   - Release 正文的顶层 "- " bullet 逐条即 manifest Changelog（代码块内除外；正文为空则省略 Changelog 键）；
#   - 资产文件名必须等于 {Id}_{Version点→下划线}.pext；
#   - 同版本重发（edited）= 整条替换，不产生重复条目；
#   - 本脚本只动首条所在版本，其余条目原样保留。

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,                       # 形如 v1.2
    [Parameter(Mandatory)][string]$AssetUrl,                  # pext 资产的 browser_download_url
    [string]$Body = '',                                        # Release 正文（Changelog 来源；可为空=本条不写 Changelog）
    [string]$ReleaseDate = '',                                # 接受完整 ISO（取日期段）；空则用 UTC 今日
    [string]$RequiredApiVersion = '6.14.0',                   # 与 release.ps1 同一真相源约定，上调需同步
    [string]$ManifestPath = 'manifest.yaml',
    [string]$ExtensionPath = 'extension.yaml',                # 存在则做版本/包名交叉校验
    [string]$Repo = ''                                        # owner/repo，提供则校验 URL 前缀
)

$ErrorActionPreference = 'Stop'
function Fail([string]$m) { throw "manifest 更新中止: $m" }

# ---------- 1. 参数规范化 ----------
if ($Tag -notmatch '^v\d+(\.\d+)*$') { Fail "Tag 格式非法: $Tag（应为 v{Version}）" }
$ver = $Tag.Substring(1)

if (-not $ReleaseDate) { $ReleaseDate = [datetime]::UtcNow.ToString('yyyy-MM-dd') }
if ($ReleaseDate.Length -gt 10) { $ReleaseDate = $ReleaseDate.Substring(0, 10) }   # published_at 带时间戳
if ($ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') { Fail "ReleaseDate 非法: $ReleaseDate" }

# ---------- 2. Changelog：抽取正文顶层 bullet（跳过 ``` 围栏内） ----------
$changelog = @()
$inFence = $false
foreach ($l in ($Body -split "\r?\n")) {
    if ($l -match '^\s*```') { $inFence = -not $inFence; continue }
    if ($inFence) { continue }
    if ($l -match '^[-*]\s+(.+?)\s*$') { $changelog += $Matches[1] }
}
if ($changelog.Count -lt 1) {
    Write-Warning 'Release 正文为空或无顶层 "- " bullet，本条暂不写 Changelog；之后在 Release 编辑正文保存（edited 事件）会整条替换补上'
}

# ---------- 3. extension.yaml 交叉校验（可得则校） ----------
$extId = $null
if ($ExtensionPath -and (Test-Path $ExtensionPath)) {
    $ext = Get-Content $ExtensionPath -Raw -Encoding UTF8
    $extId  = if ($ext -match '(?m)^\s*Id:\s*(.+?)\s*$')     { $Matches[1].Trim("'`"") } else { Fail 'extension.yaml 缺 Id' }
    $eVer   = if ($ext -match '(?m)^\s*Version:\s*(.+?)\s*$') { $Matches[1].Trim("'`"") } else { Fail 'extension.yaml 缺 Version' }
    if ([version]$eVer -ne [version]$ver) { Fail "Release tag ($ver) != extension.yaml Version ($eVer)" }
}
$pkgName = if ($extId) { "{0}_{1}.pext" -f $extId, ($ver -replace '\.', '_') } else { $null }

# ---------- 4. AssetUrl 校验 ----------
if (-not ($AssetUrl -match '/releases/download/[^/]+/[^/]+\.pext$')) { Fail "AssetUrl 非 pext 下载链: $AssetUrl" }
$seg = $AssetUrl.Split('/')
if ($seg[-2] -ne $Tag) { Fail "AssetUrl tag 段 ($($seg[-2])) != $Tag" }
if ($Repo -and -not $AssetUrl.StartsWith("https://github.com/$Repo/")) { Fail "AssetUrl 仓库前缀与 $Repo 不符: $AssetUrl" }
if ($pkgName -and $seg[-1] -ne $pkgName) { Fail "资产文件名 ($($seg[-1])) != 约定包名 $pkgName" }

# ---------- 5. 渲染首条并写回 ----------
$entry = @(
    "  - Version: $ver",
    "    RequiredApiVersion: $RequiredApiVersion",
    "    ReleaseDate: $ReleaseDate",
    "    PackageUrl: $AssetUrl"
)
if ($changelog.Count) {
    $entry += '    Changelog:'
    $entry += @($changelog | ForEach-Object { "      - $_" })
}

if (Test-Path $ManifestPath) {
    $lines = @(Get-Content $ManifestPath -Encoding UTF8)
} else {
    if (-not $extId) { Fail "manifest 不存在且无 extension.yaml 可推导 AddonId" }
    Write-Host "manifest 不存在，按 AddonId=$extId 新建"
    $lines = @("AddonId: $extId", 'Packages:')
}

# 条目边界：以 "  - Version:" 为界（轻量行解析，格式自维护）
$starts = @(); for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^  -\s+Version:') { $starts += $i } }

$dupIdx = -1
for ($k = 0; $k -lt $starts.Count; $k++) {
    if ($lines[$starts[$k]] -match '^  -\s+Version:\s*(.+?)\s*$') {
        if ([version]$Matches[1] -eq [version]$ver) { $dupIdx = $k; break }
    }
}

if ($dupIdx -ge 0) {
    $s = $starts[$dupIdx]
    $e = if ($dupIdx + 1 -lt $starts.Count) { $starts[$dupIdx + 1] - 1 } else { $lines.Count - 1 }
    while ($e -ge $s -and -not $lines[$e].Trim()) { $e-- }        # 吃掉块尾空行
    $head = if ($s -gt 0) { $lines[0..($s - 1)] } else { @() }
    $tail = if ($e -lt $lines.Count - 1) { $lines[($e + 1)..($lines.Count - 1)] } else { @() }
    $new = @($head) + $entry + @($tail)
    $action = '替换'
} elseif ($starts.Count) {
    $new = @($lines[0..($starts[0] - 1)]) + $entry + @($lines[$starts[0]..($lines.Count - 1)])
    $action = '前插'
} else {
    $new = @($lines) + $entry
    $action = '新建'
}

# LF、UTF-8 无 BOM；与现文件比对（忽略行尾差异）决定写不写
$outText = (($new -join "`n") + "`n")
$curText = if (Test-Path $ManifestPath) { [IO.File]::ReadAllText($ManifestPath) } else { '' }
if ($outText -eq $curText.Replace("`r`n", "`n")) {
    Write-Host "manifest 无变化（Version $ver 条目内容一致）"
    exit 0
}
[IO.File]::WriteAllText($ManifestPath, $outText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "$action 首条 Version $ver（Changelog $($changelog.Count) 条，ReleaseDate $ReleaseDate）"
