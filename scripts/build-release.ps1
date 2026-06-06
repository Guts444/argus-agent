param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.2"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\win-x64"
$installer = Join-Path $artifacts "installer"

foreach ($path in @($publish, $installer)) {
    $resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $path))
    if (-not $resolvedParent.StartsWith([System.IO.Path]::GetFullPath($artifacts), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $path"
    }

    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publish, $installer -Force | Out-Null

dotnet restore (Join-Path $root "Argus.slnx")
dotnet test (Join-Path $root "Argus.slnx") -c Release --no-restore
dotnet publish (Join-Path $root "Argus.App\Argus.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publish `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:PublishSingleFile=false

Get-ChildItem -LiteralPath $publish -Recurse -Filter *.pdb |
    Remove-Item -Force

$requiredPublishFiles = @(
    "Argus.exe",
    "Argus.dll",
    "Argus.pri",
    "App.xbf",
    "MainPage.xbf",
    "MainWindow.xbf",
    "Assets\AppIcon.ico"
)
foreach ($requiredFile in $requiredPublishFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $requiredFile))) {
        throw "The release publish is incomplete. Missing: $requiredFile"
    }
}

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup --exact"
}

& $iscc "/DAppVersion=$Version" (Join-Path $root "installer\Argus.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$zipPath = Join-Path $installer "ArgusAgent-win-x64.zip"
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hashLines = Get-ChildItem -LiteralPath $installer -File |
    Where-Object { $_.Extension -in ".exe", ".zip" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$hashLines | Set-Content -LiteralPath (Join-Path $installer "SHA256SUMS.txt") -Encoding ascii

Write-Host "Release artifacts:"
Get-ChildItem -LiteralPath $installer -File | Select-Object Name, Length, LastWriteTime
