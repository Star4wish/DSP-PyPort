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
# Without -ContentDir the small schema sample under .\content is deployed (NOT the full PY content;
# the full content bundle ships in GitHub Releases / dist zip).
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

$scriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($scriptPath)) { $scriptPath = $PSCommandPath }
$root = if ([string]::IsNullOrEmpty($scriptPath)) { (Get-Location).Path } else { Split-Path -Parent $scriptPath }
Set-Location -Path $root

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

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

$srcContent = if ([string]::IsNullOrEmpty($ContentDir)) { Join-Path $root 'content' } else { $ContentDir }
if (-not (Test-Path $srcContent)) { throw "content dir missing: $srcContent" }

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
