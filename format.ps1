# Checks that the code follows .editorconfig, without changing any file.
# CI runs it as a step, so the same command works locally and on any CI system.
# Run: powershell -ExecutionPolicy Bypass -File .\format.ps1
# To fix the files instead: dotnet format Kiler.slnx

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet format Kiler.slnx --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "dotnet format found changes (exit code $LASTEXITCODE). Run 'dotnet format Kiler.slnx' to fix them." }
}
finally {
    Pop-Location
}
