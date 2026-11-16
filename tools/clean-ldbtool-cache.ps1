# Clean DSP-PyPort entries from LDBTool ID caches.
#
# Why: LDBTool 3.0.3 has NO option to disable ID persistence. It Bind()s every proto's
# ID into BepInEx/config/LDBTool/LDBTool.CustomID.cfg with the mod-requested ID as the
# default, then overwrites proto.ID with the cached value. That means once an ID has been
# cached, changing it in content JSON would be silently reverted on the next launch.
#
# Equivalent to "disable caching": strip ONLY our own protos from the cache before a
# launch, so the content JSON stays the single source of truth. Other mods' cached IDs
# are left untouched (they need them for save compatibility).
#
# NOTE: ASCII-only on purpose (Windows PowerShell 5.1 decodes BOM-less files as ANSI).
param(
    [string]$GameDir = 'C:\Games\Steam\steamapps\common\Dyson Sphere Program',
    [string]$ContentDir = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---- locate manifest -> id ranges -------------------------------------------
if ([string]::IsNullOrEmpty($ContentDir)) {
    $ContentDir = Join-Path $PSScriptRoot '..\content'
}
$manifestPath = Join-Path $ContentDir 'manifest.json'
if (-not (Test-Path $manifestPath)) {
    throw "manifest.json not found: $manifestPath (pass -ContentDir)"
}
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$itemMin = [int]$manifest.id_ranges.items[0];   $itemMax = [int]$manifest.id_ranges.items[1]
$recipeMin = [int]$manifest.id_ranges.recipes[0]; $recipeMax = [int]$manifest.id_ranges.recipes[1]
$techMin = [int]$manifest.id_ranges.techs[0];   $techMax = [int]$manifest.id_ranges.techs[1]
$modelMin = [int]$manifest.id_ranges.models[0]; $modelMax = [int]$manifest.id_ranges.models[1]
Write-Host ("[idcache] ranges: items {0}-{1}, recipes {2}-{3}, techs {4}-{5}, models {6}-{7}" -f `
    $itemMin, $itemMax, $recipeMin, $recipeMax, $techMin, $techMax, $modelMin, $modelMax)

$cfgDir = Join-Path $GameDir 'BepInEx\config\LDBTool'
if (-not (Test-Path $cfgDir)) {
    Write-Host "[idcache] no LDBTool config dir ($cfgDir) - nothing to clean"
    return
}

function Backup-File([string]$path) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bak = "$path.bak-$stamp"
    Copy-Item $path $bak -Force
    return $bak
}

# Is this key one of ours?
function Test-Ours([string]$section, [string]$key) {
    if ($key -like 'PY_*') { return $true }          # localization-name keys for item/recipe/tech
    $n = 0
    if ([int]::TryParse($key, [ref]$n)) {
        switch ($section) {
            'Model'  { return ($n -ge $modelMin  -and $n -le $modelMax) }
            'Item'   { return ($n -ge $itemMin   -and $n -le $itemMax) }
            'Recipe' { return ($n -ge $recipeMin -and $n -le $recipeMax) }
            'Tech'   { return ($n -ge $techMin   -and $n -le $techMax) }
        }
    }
    return $false
}

# ---- clean LDBTool.CustomID.cfg ---------------------------------------------
function Clean-CustomId([string]$path) {
    if (-not (Test-Path $path)) { Write-Host "[idcache] missing: $path"; return }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $nl = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $lines = $raw -split "`r?`n"

    $out = New-Object System.Collections.Generic.List[string]
    $section = ''
    $removed = @{}
    $kept = @{}
    foreach ($line in $lines) {
        $t = $line.Trim()
        if ($t -match '^\[(.+)\]$') {
            $section = $Matches[1]
            if (-not $removed.ContainsKey($section)) { $removed[$section] = 0; $kept[$section] = 0 }
            $out.Add($line); continue
        }
        if ($t -match '^(.+?)\s*=\s*(-?\d+)\s*$') {
            $key = $Matches[1].Trim()
            if (Test-Ours $section $key) { $removed[$section] = 1 + $removed[$section]; continue }
            $kept[$section] = 1 + $kept[$section]
        }
        $out.Add($line)
    }

    $totalRemoved = ($removed.Values | Measure-Object -Sum).Sum
    if ($DryRun) {
        Write-Host "[idcache] DRY-RUN CustomID: would remove $totalRemoved entries; per-section:"
        foreach ($k in $removed.Keys) { Write-Host ("    [{0}] remove={1} keep={2}" -f $k, $removed[$k], $kept[$k]) }
        return
    }
    if ($totalRemoved -eq 0) { Write-Host '[idcache] CustomID: nothing of ours to remove'; return }
    $bak = Backup-File $path
    [System.IO.File]::WriteAllText($path, ($out -join $nl), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("[idcache] CustomID: removed {0} entries (backup: {1})" -f $totalRemoved, (Split-Path -Leaf $bak))
    foreach ($k in $removed.Keys) { Write-Host ("    [{0}] remove={1} keep={2}" -f $k, $removed[$k], $kept[$k]) }
}

# ---- clean LDBTool.CustomGridIndex.cfg --------------------------------------
# Blocks look like:
#   ## Default Grid Index = 5011
#   ## Item Name = PY_Item_6300_Name
#   6300 = 0
# Drop a whole block when any line mentions PY_ or the numeric key is ours.
function Clean-CustomGridIndex([string]$path) {
    if (-not (Test-Path $path)) { Write-Host "[idcache] missing: $path"; return }
    $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $nl = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
    $lines = $raw -split "`r?`n"

    $out = New-Object System.Collections.Generic.List[string]
    $section = ''
    $block = New-Object System.Collections.Generic.List[string]
    $removed = 0
    $kept = 0

    foreach ($line in $lines) {
        $t = $line.Trim()
        if ($t -match '^\[(.+)\]$') {
            # flush pending block before switching section
            if ($block.Count -gt 0) {
                $ours = $false
                foreach ($b in $block) {
                    if ($b -like '*PY_*') { $ours = $true }
                    $bt = $b.Trim()
                    if ($bt -match '^(\d+)\s*=\s*-?\d+\s*$') { if (Test-Ours $section $Matches[1]) { $ours = $true } }
                }
                if ($ours) { $removed++ } else { foreach ($b in $block) { $out.Add($b) }; $kept++ }
                $block.Clear()
            }
            $section = $Matches[1]
            $out.Add($line)
            continue
        }
        $block.Add($line)
        if ($t -match '^(\d+)\s*=\s*-?\d+\s*$') {
            $ours = $false
            foreach ($b in $block) {
                if ($b -like '*PY_*') { $ours = $true }
                $bt = $b.Trim()
                if ($bt -match '^(\d+)\s*=\s*-?\d+\s*$') { if (Test-Ours $section $Matches[1]) { $ours = $true } }
            }
            if ($ours) { $removed++ } else { foreach ($b in $block) { $out.Add($b) }; $kept++ }
            $block.Clear()
        }
    }
    if ($block.Count -gt 0) {
        $ours = $false
        foreach ($b in $block) { if ($b -like '*PY_*') { $ours = $true } }
        if ($ours) { $removed++ } else { foreach ($b in $block) { $out.Add($b) }; $kept++ }
        $block.Clear()
    }

    if ($DryRun) {
        Write-Host "[idcache] DRY-RUN GridIndex: would remove $removed blocks, keep $kept"
        return
    }
    if ($removed -eq 0) { Write-Host '[idcache] GridIndex: nothing of ours to remove'; return }
    $bak = Backup-File $path
    [System.IO.File]::WriteAllText($path, ($out -join $nl), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("[idcache] GridIndex: removed {0} blocks, kept {1} (backup: {2})" -f $removed, $kept, (Split-Path -Leaf $bak))
}

Write-Host '[idcache] cleaning DSP-PyPort entries from LDBTool caches...'
Clean-CustomId      (Join-Path $cfgDir 'LDBTool.CustomID.cfg')
Clean-CustomGridIndex (Join-Path $cfgDir 'LDBTool.CustomGridIndex.cfg')
Write-Host '[idcache] done. Our content IDs are now authoritative from content JSON.'
