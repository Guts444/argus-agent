param(
    [switch]$Silent
)

$ErrorActionPreference = "Stop"
$repository = "Guts444/argus-agent"
$headers = @{
    Accept = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent" = "Argus-Installer"
}

Write-Host "Argus installer"
Write-Host "Fetching the latest release from GitHub..."

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers
$asset = $release.assets | Where-Object { $_.name -eq "ArgusAgentSetup-x64.exe" } | Select-Object -First 1
if (-not $asset) {
    throw "The latest release does not contain ArgusAgentSetup-x64.exe."
}

$checksumAsset = $release.assets | Where-Object { $_.name -eq "SHA256SUMS.txt" } | Select-Object -First 1
if (-not $checksumAsset) {
    throw "The latest release does not contain SHA256SUMS.txt."
}

$downloadDirectory = Join-Path $env:TEMP "Argus"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$installerPath = Join-Path $downloadDirectory "ArgusAgentSetup-x64.exe"
$checksumPath = Join-Path $downloadDirectory "SHA256SUMS.txt"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath -Headers $headers
Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $checksumPath -Headers $headers

$escapedInstallerName = [regex]::Escape($asset.name)
$checksumPattern = "^\s*([0-9a-fA-F]{64})\s+\*?$escapedInstallerName\s*$"
$checksumLine = Get-Content -LiteralPath $checksumPath |
    Where-Object { $_ -match $checksumPattern } |
    Select-Object -First 1
if (-not $checksumLine) {
    Remove-Item -LiteralPath $installerPath -Force
    throw "SHA256SUMS.txt does not contain a valid checksum for $($asset.name)."
}

$expectedHash = [regex]::Match($checksumLine, $checksumPattern).Groups[1].Value
$actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
if (-not $actualHash.Equals($expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $installerPath -Force
    throw "The downloaded installer failed SHA-256 verification."
}

$arguments = if ($Silent) {
    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
} else {
    "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
}

$process = Start-Process -FilePath $installerPath -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Argus setup exited with code $($process.ExitCode)."
}

Write-Host "Argus is installed."
