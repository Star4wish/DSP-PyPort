# Pack the generated full content directory into ONE zip under assets/.
#
# Why: the repo tracks only the 8-file content schema sample as text; the full bundle
# (61 json + 3805 png, ~38 MB raw / ~25 MB zip) is a single archive so a fresh clone
# can still build a playable install, without uploading thousands of tiny files.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-content.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-content.ps1 `
#       -ContentDir "D:\path\to\content" -Version 0.9.4
#
# The output file name is consumed by build.ps1 (assets\*.zip, newest first).
param(
    [string]$ContentDir = '',
    [string]$Version = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrEmpty($ContentDir)) {
    $ContentDir = Join-Path (Split-Path -Parent $root) '_analysis\py-port\content'
}
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'assets' }
if ([string]::IsNullOrEmpty($Version)) {
    $csproj = Join-Path $root 'DSP-PyPort.csproj'
    $m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $Version = $m.Matches[0].Groups[1].Value } else { $Version = '0.0.0' }
}

if (-not (Test-Path (Join-Path $ContentDir 'manifest.json'))) {
    throw "not a content dir (manifest.json missing): $ContentDir"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dest = Join-Path $OutDir ("DSP-PyPort-content-v{0}.zip" -f $Version)
if (Test-Path $dest) { Remove-Item $dest -Force }

Write-Host "[pack] content: $ContentDir"
Write-Host "[pack] output : $dest"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sw = [System.Diagnostics.Stopwatch]::StartNew()
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $ContentDir, $dest, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$sw.Stop()

$size = (Get-Item $dest).Length
$hash = (Get-FileHash $dest -Algorithm SHA256).Hash
$zip = [System.IO.Compression.ZipFile]::OpenRead($dest)
$entries = $zip.Entries.Count
$zip.Dispose()

Write-Host ("[pack] entries: {0}" -f $entries)
Write-Host ("[pack] size   : {0:N2} MB ({1:N0} bytes)" -f ($size / 1MB), $size)
Write-Host ("[pack] sha256 : {0}" -f $hash)
Write-Host ("[pack] time   : {0:N1}s" -f $sw.Elapsed.TotalSeconds)

# Keep exactly one content archive in assets/ (build.ps1 picks assets\*.zip).
$destFull = (Resolve-Path $dest).Path
Get-ChildItem $OutDir -Filter 'DSP-PyPort-content-*.zip' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $destFull } |
    ForEach-Object {
        Write-Host "[pack] removing stale archive: $($_.Name)"
        Remove-Item $_.FullName -Force
    }
