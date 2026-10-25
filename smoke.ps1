# Runs the smoke tests against the published Kiler.exe: the tests of Kiler.App.Tests with the trait Category=Smoke
# (SmokeTests), which start the exe as a process. They run only when KILER_SMOKE_EXE names the exe, so test.ps1 skips them.
# Run publish.ps1 first. CI runs both as steps, so the same commands work locally and on any CI system.
# Run: powershell -ExecutionPolicy Bypass -File .\smoke.ps1 [-Exe <path>] [-NoBuild]
#   -Exe       another Kiler.exe, such as one from the CI artifact; by default artifacts\publish\Kiler.exe
#   -NoBuild   the tests are already built in Release, as in CI after build.ps1
# Each test runs a copy of the exe from a temporary folder with a data folder of its own, so it does not meet the Kiler you run.

param(
    [string]$Exe = (Join-Path $PSScriptRoot 'artifacts\publish\Kiler.exe'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# A relative -Exe is taken from the current folder, before the script moves to the repository root.
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "There is no $Exe. Run publish.ps1 first." }
$exePath = (Resolve-Path -LiteralPath $Exe).ProviderPath

Push-Location -LiteralPath $PSScriptRoot
$env:KILER_SMOKE_EXE = $exePath
try {
    $testArgs = @('test', '--project', 'tests\Kiler.App.Tests', '--configuration', 'Release')
    if ($NoBuild) { $testArgs += '--no-build' }
    # --fail-skips: a smoke test that skips, because it did not get the exe, fails the run instead of passing it.
    # A filter that finds no test fails too (exit code 8).
    $testArgs += @('--', '--filter-trait', 'Category=Smoke', '--fail-skips', 'on')
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "The smoke tests failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath Env:KILER_SMOKE_EXE -ErrorAction SilentlyContinue
    Pop-Location
}
