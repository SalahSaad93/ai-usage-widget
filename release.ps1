# Release v0.1: build + selftest, hash the exe, write latest.json, publish a GitHub release.
# Run manually; requires a clean git tree. NOT run by the build.
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must be x.y.z, got '$Version'"
    exit 1
}

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

# Clean tree required (bin\ is gitignored, so build output does not dirty it).
$dirty = & git -C $root status --porcelain
if ($dirty) {
    Write-Error "git tree is not clean; commit or stash first"
    exit 1
}

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) {
    Write-Error "build failed"
    exit 1
}

$exe = Join-Path $root "bin\AiUsageWidget.exe"
if (-not (Test-Path $exe)) {
    Write-Error "built exe missing at $exe"
    exit 1
}
$sha256 = (Get-FileHash -Path $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$latest = [pscustomobject]@{
    version = $Version
    sha256  = $sha256
    asset   = "AiUsageWidget.exe"
}
$latestPath = Join-Path $root "bin\latest.json"
$latest | ConvertTo-Json | Set-Content -Path $latestPath -Encoding ASCII

& gh release create ("v" + $Version) $exe $latestPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "gh release create failed"
    exit 1
}

Write-Host "released v$Version (sha256 $sha256)"
