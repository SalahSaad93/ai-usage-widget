# PROTOTYPE build: compiles the prototypes with the in-box .NET Framework compiler.
#   default  -> PrototypeWinForms.cs (the approved direction)   -Wpf -> Prototype.cs (first attempt, ~60 MB)
#   -Run starts the built exe.
param([switch]$Run, [switch]$Wpf)
$ErrorActionPreference = 'Stop'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$name = if ($Wpf) { 'UsageWidgetPrototype' } else { 'UsageWidgetPrototypeWF' }
$src = Join-Path $PSScriptRoot $(if ($Wpf) { 'Prototype.cs' } else { 'PrototypeWinForms.cs' })
$out = Join-Path $PSScriptRoot "$name.exe"
Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out } | Stop-Process -Force
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
if ($Wpf) {
    $refs += "$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll"
}
$refs = $refs | ForEach-Object { "/reference:$_" }
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 "/out:$out" @refs $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "built $out"
if ($Run) { Start-Process $out }
