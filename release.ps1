# Build, self-test, sign update metadata, and publish a GitHub release.
# Run manually; requires a clean git tree. NOT run by the build.
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".ai-usage-widget\update-signing-key.xml")
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got '$Version'" }
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$dirty = & git -C $root status --porcelain
if ($dirty) { throw "git tree is not clean; commit or stash first" }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = Join-Path $root "bin\AiUsageWidget.exe"
if (-not (Test-Path $exe)) { throw "built exe missing at $exe" }
$sha256 = (Get-FileHash -Path $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$latest = [pscustomobject]@{ version = $Version; sha256 = $sha256; asset = "AiUsageWidget.exe" }
$latestPath = Join-Path $root "bin\latest.json"
$latestBytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($latest | ConvertTo-Json -Compress))
[System.IO.File]::WriteAllBytes($latestPath, $latestBytes)
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) { throw "Update signing key not found at $KeyPath" }

$keyXml = [System.IO.File]::ReadAllText($KeyPath)
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString($keyXml)
    $signature = $rsa.SignData($latestBytes, [System.Security.Cryptography.CryptoConfig]::MapNameToOID("SHA256"))
    $signatureBase64 = [Convert]::ToBase64String($signature)
    $signaturePath = Join-Path $root "bin\latest.json.sig"
    [System.IO.File]::WriteAllText($signaturePath, $signatureBase64, [System.Text.Encoding]::ASCII)

    $keySource = Get-Content -Raw -Path (Join-Path $root "src\Core\UpdateKey.cs")
    $keyMatch = [regex]::Match($keySource, 'PublicKeyXml\s*=\s*"((?:[^"\\]|\\.)*)"')
    if (-not $keyMatch.Success) { throw "Could not read UpdateKey.PublicKeyXml" }
    $publicKeyXml = $keyMatch.Groups[1].Value.Replace('\"', '"').Replace('\\', '\')
    if ([string]::IsNullOrEmpty($publicKeyXml)) { throw "UpdateKey.PublicKeyXml is empty" }
    $verifyRsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    try {
        $verifyRsa.FromXmlString($publicKeyXml)
        if (-not $verifyRsa.VerifyData($latestBytes, [System.Security.Cryptography.CryptoConfig]::MapNameToOID("SHA256"), $signature)) {
            throw "Release signature does not match the public key compiled into the exe"
        }
    }
    finally { $verifyRsa.Dispose() }
}
finally { $rsa.Dispose() }

Push-Location $root
try {
    & gh release create ("v" + $Version) $exe $latestPath $signaturePath --title ("v" + $Version) --notes ("AI Usage Widget v" + $Version) --target (git rev-parse HEAD)
}
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Host "released v$Version (sha256 $sha256)"
