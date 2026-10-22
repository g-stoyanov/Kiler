# Restores and builds the solution. Warnings are errors (Directory.Build.props).
# CI runs it as a step, so the same command works locally and on any CI system.
# Run: powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Configuration Debug]
# The default is Release, as in CI.

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet build Kiler.slnx --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
