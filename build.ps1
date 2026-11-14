param(
    [string]$Version = "0.1.0",
    # Relative to the repo root. Use bin\dev while an installed widget is running from bin\.
    [string]$OutDir = "bin",
    [switch]$Run
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must be x.y.z, got '$Version'"
    exit 1
}

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$bin = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $bin | Out-Null

# AssemblyInfo with the requested version.
$asmInfo = Join-Path $bin "AssemblyInfo.cs"
@"
using System.Reflection;

[assembly: AssemblyTitle("AI Usage Widget")]
[assembly: AssemblyDescription("Always-on-top usage widget for AI subscriptions")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("AI Usage Widget")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
"@ | Set-Content -Path $asmInfo -Encoding ASCII

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    Write-Error "in-box csc.exe not found at $csc"
    exit 1
}

$sources = Get-ChildItem -Path (Join-Path $root "src") -Recurse -Filter *.cs |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }
if (-not $sources) {
    Write-Error "no sources found under src\"
    exit 1
}

$exe = Join-Path $bin "AiUsageWidget.exe"
$log = Join-Path $bin "selftest.log"

# Windows PowerShell 5.1 splits a computed element ("x" + expr) inside a multi-line array
# literal into two elements, so csc would see /win32manifest: with no value. Precompute it.
$manifestArg = "/win32manifest:" + (Join-Path $root "app.manifest")
$iconPath = Join-Path $root "assets\app.ico"
$iconArg = "/win32icon:" + $iconPath                       # exe icon
$iconResArg = "/resource:" + $iconPath + ",AiUsageWidget.app.ico"   # tray icon, loaded at tray size
$cscArgs = @(
    $iconArg,
    $iconResArg,
    "/nologo",
    "/noconfig",
    "/langversion:5",
    "/warnaserror+",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/codepage:65001",
    $manifestArg,
    "/out:$exe",
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll",
    "/r:System.Net.Http.dll",
    "/r:System.Web.Extensions.dll",
    "/r:System.Security.dll"
) + @($sources) + @($asmInfo)

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "compile failed with exit code $LASTEXITCODE"
    exit 1
}

# The exe is a winexe (no stdout), so the self-test writes its log and we read the file.
if (Test-Path $log) { Remove-Item $log -Force }
$selfTest = Start-Process -FilePath $exe `
    -ArgumentList @("--selftest", "--selftest-out", "`"$log`"") `
    -Wait -PassThru -WindowStyle Hidden
if ($selfTest.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log | Write-Host }
    Write-Error "selftest failed with exit code $($selfTest.ExitCode)"
    exit 1
}
if (-not (Test-Path $log)) {
    Write-Error "selftest produced no log at $log"
    exit 1
}
$lines = @(Get-Content $log)
if ($lines.Count -eq 0) {
    Write-Error "selftest log is empty"
    exit 1
}
$bad = @($lines | Where-Object { -not $_.StartsWith("PASS ") })
if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host ("build OK: " + $lines.Count + " selftest checks passed, v" + $Version)
if ($Run) { Start-Process -FilePath $exe }
exit 0
