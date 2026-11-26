# DSP-PyPort build script
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoDeploy
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -GameDir "D:\Steam\steamapps\common\Dyson Sphere Program"
#   $env:DSP_GAME_DIR = "D:\...\Dyson Sphere Program"; .\build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -ContentDir "C:\WorkWork\DSP MOD\_analysis\py-port\content"
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -ContentDir "..." -SkipIdCacheClean
#
# Game dir resolution order: -GameDir > env DSP_GAME_DIR > default Steam path.
# Content resolution order: -ContentDir > -ContentZip > assets/*content*.zip (auto-extract to build\content)
#                          > .\content (8-file schema sample).
# The full PY content bundle is a single assets/*.zip in the repo; only 8 sample files are tracked as text.
#
# After deploying content it strips OUR OWN entries from LDBTool id caches
#   BepInEx/config/LDBTool/LDBTool.CustomID.cfg / LDBTool.CustomGridIndex.cfg
# so content JSON stays the single source of truth for IDs (LDBTool 3.0.3 has no
# switch to disable ID persistence; it silently overrides proto.ID with cached values).
# Other mods' cached IDs are never touched.
#
# Builds the plugin and (by default) deploys it idempotently to
#   <GameDir>\BepInEx\plugins\DSP-PyPort\DSP-PyPort.dll
#   <GameDir>\BepInEx\plugins\DSP-PyPort\content\...
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less files as ANSI,
#       so non-ASCII comments can corrupt parsing when invoked with -File.
param(
    [string]$GameDir = '',
    [string]$Configuration = 'Release',
    [string]$ContentDir = '',
    [string]$ContentZip = '',
    [switch]$NoDeploy,
    [switch]$SkipIdCacheClean
)

$ErrorActionPreference = 'Stop'

# Game dir resolution: -GameDir > env DSP_GAME_DIR > default Steam path
if ([string]::IsNullOrEmpty($GameDir)) {
    if (-not [string]::IsNullOrEmpty($env:DSP_GAME_DIR)) {
        $GameDir = $env:DSP_GAME_DIR
    } else {
        $GameDir = 'C:\Games\Steam\steamapps\common\Dyson Sphere Program'
    }
}

function Expand-ContentArchive([string]$ZipPath, [string]$TargetDir) {
    if (-not (Test-Path $ZipPath)) { throw "content archive not found: $ZipPath" }
    if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    Write-Host "[content] extracting: $ZipPath -> $TargetDir"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $TargetDir)
    if (-not (Test-Path (Join-Path $TargetDir 'manifest.json'))) {
        throw "content archive has no manifest.json at root: $ZipPath"
    }
}

$scriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($scriptPath)) { $scriptPath = $PSCommandPath }
$root = if ([string]::IsNullOrEmpty($scriptPath)) { (Get-Location).Path } else { Split-Path -Parent $scriptPath }
Set-Location -Path $root

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

# --- pre-build dependency check: fail with an actionable message instead of cryptic CS0246 ---
$managedDir = Join-Path $GameDir 'DSPGAME_Data\Managed'
if (-not (Test-Path (Join-Path $managedDir 'Assembly-CSharp.dll'))) {
    throw "game assemblies not found: $managedDir\Assembly-CSharp.dll (pass -GameDir or set DSP_GAME_DIR; DSP 0.10.35.x required)"
}
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInEx 5.4.x not found: $GameDir\BepInEx\core\BepInEx.dll"
}
$pluginsDir = Join-Path $GameDir 'BepInEx\plugins'
$commonCandidates = @(
    (Join-Path $pluginsDir 'CommonAPI-CommonAPI\CommonAPI.dll'),
    (Join-Path $pluginsDir 'CommonAPI\CommonAPI.dll'),
    (Join-Path $pluginsDir 'CommonAPI.dll')
)
$commonFound = $false
foreach ($p in $commonCandidates) { if (Test-Path $p) { $commonFound = $true } }
if (-not $commonFound) {
    throw "CommonAPI.dll not found under $pluginsDir (install CommonAPI 1.6.7+)"
}
$ldbCandidates = @(
    (Join-Path $pluginsDir 'xiaoye97-LDBTool\LDBTool.dll'),
    (Join-Path $pluginsDir 'LDBTool\LDBTool.dll'),
    (Join-Path $pluginsDir 'LDBTool.dll')
)
$ldbFound = $false
foreach ($p in $ldbCandidates) { if (Test-Path $p) { $ldbFound = $true } }
if (-not $ldbFound) {
    throw "LDBTool.dll not found under $pluginsDir (install LDBTool 3.0.3+)"
}
Write-Host "[build] deps ok: $GameDir"

$project = Join-Path $root 'DSP-PyPort.csproj'
$outDir = Join-Path $root ('bin\' + $Configuration)

Write-Host "[build] dotnet build -c $Configuration"
& dotnet build $project -c $Configuration --nologo -v m "-p:DSPGameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }

$dll = Join-Path $outDir 'DSP-PyPort.dll'
if (-not (Test-Path $dll)) { throw "built dll missing: $dll" }
Write-Host ("[build] ok: {0} ({1} bytes)" -f $dll, (Get-Item $dll).Length)

if ($NoDeploy) {
    Write-Host '[deploy] skipped (-NoDeploy)'
    return
}

if (-not (Test-Path (Join-Path $GameDir 'DSPGAME_Data\Managed'))) {
    throw "game dir not found: $GameDir"
}

$pluginDst = Join-Path $GameDir 'BepInEx\plugins\DSP-PyPort'
New-Item -ItemType Directory -Force -Path $pluginDst | Out-Null

try {
    Copy-Item $dll (Join-Path $pluginDst 'DSP-PyPort.dll') -Force
} catch {
    throw "deploy dll failed (is the game running? stop it first): $_"
}

# Content resolution: -ContentDir > -ContentZip > assets/*content*.zip > .\content (schema sample)
# The full PY content bundle ships in the repo as a single assets/*.zip so that a fresh clone can
# build a playable install without uploading thousands of tiny files; it is extracted to build\content.
$srcContent = ''
if (-not [string]::IsNullOrEmpty($ContentDir)) {
    $srcContent = $ContentDir
} else {
    $zipPath = $ContentZip
    $zipExplicit = -not [string]::IsNullOrEmpty($ContentZip)
    if ([string]::IsNullOrEmpty($zipPath)) {
        $assetsDir = Join-Path $root 'assets'
        if (Test-Path $assetsDir) {
            $candidates = @(Get-ChildItem $assetsDir -Filter '*.zip' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
            if ($candidates.Count -gt 1) {
                Write-Warning ("[content] multiple archives in assets\; using {0} (keep only one, see tools\pack-content.ps1)" -f $candidates[0].Name)
            }
            if ($candidates.Count -gt 0) { $zipPath = $candidates[0].FullName }
        }
    }
    $fullContentMarker = Join-Path $root 'content\pyrawores\recipes.json'
    if (-not [string]::IsNullOrEmpty($zipPath) -and ($zipExplicit -or -not (Test-Path $fullContentMarker))) {
        $extractDir = Join-Path $root 'build\content'
        Expand-ContentArchive $zipPath $extractDir
        $srcContent = $extractDir
    } else {
        $srcContent = Join-Path $root 'content'
    }
}
if (-not (Test-Path $srcContent)) { throw "content dir missing: $srcContent" }
Write-Host "[content] source: $srcContent"

$dstContent = Join-Path $pluginDst 'content'
if (Test-Path $dstContent) { Remove-Item $dstContent -Recurse -Force }
Copy-Item $srcContent $dstContent -Recurse -Force

$fileCount = (Get-ChildItem $dstContent -Recurse -File | Measure-Object).Count
$jsonCount = (Get-ChildItem $dstContent -Recurse -File -Filter '*.json' | Measure-Object).Count
$pngCount = (Get-ChildItem $dstContent -Recurse -File -Filter '*.png' | Measure-Object).Count

Write-Host "[deploy] dll      -> $(Join-Path $pluginDst 'DSP-PyPort.dll')"
Write-Host "[deploy] content  -> $dstContent ($fileCount files: $jsonCount json, $pngCount png)"
# Strip our own entries from LDBTool id caches so content JSON stays authoritative.
if ($SkipIdCacheClean) {
    Write-Host '[idcache] skipped (-SkipIdCacheClean)'
} else {
    $cleaner = Join-Path $root 'tools\clean-ldbtool-cache.ps1'
    if (Test-Path $cleaner) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $cleaner -GameDir $GameDir -ContentDir $srcContent
        if ($LASTEXITCODE -ne 0) { Write-Warning "id cache clean returned $LASTEXITCODE" }
    } else {
        Write-Warning "id cache cleaner not found: $cleaner"
    }
}

Write-Host '[deploy] done. restart the game and check BepInEx\LogOutput.log'
