# Publishes Kiler.exe: Native AOT for win-x64, with the UI embedded (Kiler.App.csproj).
# CI runs it as a step, so the same command works locally and on any CI system. Both folders are emptied first:
#   artifacts\publish\Kiler.exe   the exe, alone; this is what users get
#   artifacts\symbols\Kiler.pdb   its native PDB, kept apart, for a crash dump
# The UI build needs Node.js (npm), and node_modules is installed anew. Native AOT links with the Visual Studio C++ tools, which it finds through vswhere.exe,
# so the script puts the Visual Studio Installer folder on PATH when vswhere.exe is not there already.
# Run: powershell -ExecutionPolicy Bypass -File .\publish.ps1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publish = 'artifacts\publish'
$symbols = 'artifacts\symbols'

Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        if (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$installer;$env:PATH" }
    }

    foreach ($folder in $publish, $symbols) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    }

    # KilerFreshUi: a clean npm ci and a new UI build, whatever node_modules and dist hold now (Kiler.App.csproj).
    # No backslash at the end of the folder: before a closing quote it would escape the quote.
    dotnet publish src\Kiler.App\Kiler.App.csproj --configuration Release --output $publish -p:KilerFreshUi=true "-p:KilerSymbolsDir=$PSScriptRoot\$symbols"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # Anything else next to the exe comes from a new dependency or a change in the SDK, and needs a decision.
    $published = @(Get-ChildItem -LiteralPath $publish -Recurse -Force | ForEach-Object { $_.Name })
    if ($published.Count -ne 1 -or $published[0] -ne 'Kiler.exe') {
        throw "The publish folder must hold only Kiler.exe, but it holds: $($published -join ', ')."
    }
    if (-not (Test-Path -LiteralPath "$symbols\Kiler.pdb")) { throw "There is no $symbols\Kiler.pdb." }

    $exe = Get-Item -LiteralPath "$publish\Kiler.exe"
    "Kiler.exe: $([math]::Round($exe.Length / 1MB, 1)) MB in $($exe.DirectoryName)"
}
finally {
    Pop-Location
}
