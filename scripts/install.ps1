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

$downloadDirectory = Join-Path $env:TEMP "Argus"
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$installerPath = Join-Path $downloadDirectory "ArgusAgentSetup-x64.exe"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath -Headers $headers

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
