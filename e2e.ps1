# Runs the E2E tests (tests\Kiler.E2E) against the published Kiler.exe in Playwright's Chromium. They run only when
# KILER_SMOKE_EXE names the exe, so test.ps1 skips them, as it skips the smoke tests.
# Run publish.ps1 first. CI runs both as steps, so the same commands work locally and on any CI system.
# Run: powershell -ExecutionPolicy Bypass -File .\e2e.ps1 [-Exe <path>] [-NoBuild]
#   -Exe       another Kiler.exe, such as one from the CI artifact; by default artifacts\publish\Kiler.exe
#   -NoBuild   the tests are already built in Release, as in CI after build.ps1
# Each test runs a copy of the exe from a temporary folder with a data folder of its own, so it does not meet the Kiler you run.
# The trace of a test that fails goes to artifacts\e2e-traces; open it with https://trace.playwright.dev or the show-trace
# command of the Playwright CLI.

param(
    [string]$Exe = (Join-Path $PSScriptRoot 'artifacts\publish\Kiler.exe'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# A relative -Exe is taken from the current folder, before the script moves to the repository root.
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "There is no $Exe. Run publish.ps1 first." }
$exePath = (Resolve-Path -LiteralPath $Exe).ProviderPath
$traces = Join-Path $PSScriptRoot 'artifacts\e2e-traces'

Push-Location -LiteralPath $PSScriptRoot
$env:KILER_SMOKE_EXE = $exePath
$env:KILER_E2E_TRACES = $traces
try {
    if (Test-Path -LiteralPath $traces) { Remove-Item -LiteralPath $traces -Recurse -Force }

    $testArgs = @('test', '--project', 'tests\Kiler.E2E', '--configuration', 'Release')
    if ($NoBuild) { $testArgs += '--no-build' }
    # The tests install the headless shell of Chromium into %LOCALAPPDATA%\ms-playwright when it is not there. Through a
    # proxy that inspects HTTPS, the download needs HTTPS_PROXY and NODE_EXTRA_CA_CERTS with its root certificate.
    # --fail-skips: a test that skips, because it did not get the exe, fails the run instead of passing it.
    $testArgs += @('--', '--fail-skips', 'on')
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "The E2E tests failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath Env:KILER_SMOKE_EXE, Env:KILER_E2E_TRACES -ErrorAction SilentlyContinue
    Pop-Location
}
