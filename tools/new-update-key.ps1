param([switch]$Force)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$keyDir = Join-Path $env:USERPROFILE ".ai-usage-widget"
$privatePath = Join-Path $keyDir "update-signing-key.xml"
$publicPath = Join-Path $root "src\Core\UpdateKey.cs"
if ((Test-Path -LiteralPath $privatePath) -and -not $Force) {
    throw "Key already exists at $privatePath; use -Force to replace it"
}
if (-not (Test-Path -LiteralPath $keyDir)) { New-Item -ItemType Directory -Path $keyDir -Force | Out-Null }

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
try {
    $privateXml = $rsa.ToXmlString($true)
    $publicXml = $rsa.ToXmlString($false)
    [System.IO.File]::WriteAllText($privatePath, $privateXml, (New-Object System.Text.UTF8Encoding($false)))
}
finally { $rsa.Dispose() }
$escapedPublic = $publicXml.Replace('\', '\\').Replace('"', '\"')
$source = "namespace AiUsageWidget.Core`r`n{`r`n    public static class UpdateKey`r`n    {`r`n        public const string PublicKeyXml = `"$escapedPublic`";`r`n    }`r`n}`r`n"
[System.IO.File]::WriteAllText($publicPath, $source, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Private update key: $privatePath"
Write-Host "Back up this private key securely; never commit it."
Write-Host "Public key written to $publicPath"
