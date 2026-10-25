# Checks the UI in src\ui: the format (Prettier), ESLint, the type check and the Vitest tests. It changes no source file.
# CI runs it as a step, so the same command works locally and on any CI system. It needs Node.js 24.15 or newer (npm).
# npm ci runs first only when package-lock.json is newer than node_modules, the same rule as the Release build
# (Kiler.App.csproj), so it does not replace the node_modules that a running dev server uses.
# Run: powershell -ExecutionPolicy Bypass -File .\check-ui.ps1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Push-Location -LiteralPath (Join-Path $PSScriptRoot 'src\ui')
try {
    # npm.cmd, not npm: under the AllSigned policy of the developer machines, npm resolves to the unsigned npm.ps1.
    $installed = 'node_modules\.package-lock.json'
    if (-not (Test-Path -LiteralPath $installed) -or
        (Get-Item -LiteralPath 'package-lock.json').LastWriteTimeUtc -gt (Get-Item -LiteralPath $installed).LastWriteTimeUtc) {
        # No install scripts, as in the build: no package runs code here.
        npm.cmd ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    }

    foreach ($script in 'format:check', 'lint', 'typecheck', 'test') {
        npm.cmd run $script
        if ($LASTEXITCODE -ne 0) { throw "npm run $script failed with exit code $LASTEXITCODE." }
    }
}
finally {
    Pop-Location
}
