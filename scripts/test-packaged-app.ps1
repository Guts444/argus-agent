param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [Parameter()]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$publish = [System.IO.Path]::GetFullPath($PublishDirectory)
$executable = Join-Path $publish "Argus.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Packaged smoke test could not find Argus.exe in: $publish"
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("Argus-PackagedSmoke-" + [Guid]::NewGuid().ToString("N"))
$smokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
if (-not $smokeRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a packaged smoke directory outside the system temp directory."
}

$databasePath = Join-Path $smokeRoot "argus.db"
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

function Invoke-ArgusSmokeScenario {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario
    )

    $resultPath = Join-Path $smokeRoot "$Scenario.result"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $publish
    $startInfo.UseShellExecute = $false
    $startInfo.Environment["ARGUS_DATABASE_PATH"] = $databasePath
    $startInfo.Environment["ARGUS_SMOKE_TEST_SCENARIO"] = $Scenario
    $startInfo.Environment["ARGUS_SMOKE_TEST_RESULT_PATH"] = $resultPath

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Packaged smoke test could not start Argus for scenario '$Scenario'."
    }

    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "Packaged smoke test timed out for scenario '$Scenario'."
        }
    }
    finally {
        $process.Dispose()
    }

    if (-not (Test-Path -LiteralPath $resultPath)) {
        throw "Packaged smoke test did not produce a result for scenario '$Scenario'."
    }

    $actual = (Get-Content -Raw -LiteralPath $resultPath).Trim()
    $expected = if ($Scenario -eq "fresh") {
        "fresh:onboarding-ready"
    }
    else {
        "existing:dashboard-ready"
    }

    if ($actual -ne $expected) {
        throw "Packaged smoke scenario '$Scenario' returned '$actual'; expected '$expected'."
    }

    Write-Host "Packaged smoke passed: $actual"
}

try {
    Invoke-ArgusSmokeScenario -Scenario "fresh"
    if (-not (Test-Path -LiteralPath $databasePath)) {
        throw "Fresh packaged smoke test did not create a database."
    }

    Invoke-ArgusSmokeScenario -Scenario "existing"
}
finally {
    $resolvedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
    if ($resolvedSmokeRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSmokeRoot)) {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
