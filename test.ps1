# Runs all tests with coverage (coverlet.MTP) and builds the coverage report (ReportGenerator).
# CI runs it as a step, so the same command works locally and on any CI system.
# Everything goes to TestResults\, which is emptied first, so an old run does not get into the report:
#   TestResults\<project>.coverage.cobertura.*.xml   one file per test project
#   TestResults\coverage-report\                     index.html, Summary.txt, SummaryGithub.md (for the CI summary)
# Run: powershell -ExecutionPolicy Bypass -File .\test.ps1 [-Configuration Debug] [-NoBuild]
#   -NoBuild   the solution is already built with the same configuration, as in CI after build.ps1

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$results = 'TestResults'
$report = Join-Path $results 'coverage-report'

# Relative paths and the tool manifest (dotnet-tools.json) are found from the repository root.
Push-Location -LiteralPath $PSScriptRoot
try {
    if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Recurse -Force }

    $testArgs = @(
        'test', '--solution', 'Kiler.slnx', '--configuration', $Configuration,
        '--results-directory', $results,
        '--coverlet', '--coverlet-output-format', 'cobertura'
    )
    if ($NoBuild) { $testArgs += '--no-build' }
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

    # ReportGenerator takes whatever files it finds, so a project without coverage would just drop out of the report.
    $testProjects = (Select-Xml -LiteralPath 'Kiler.slnx' -XPath '//Project/@Path').Node.Value |
        Where-Object { $_ -like 'tests/*' } |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }
    $missing = @($testProjects | Where-Object { -not (Get-ChildItem -LiteralPath $results -Filter "$_.coverage.cobertura.*.xml") })
    if ($missing.Count -gt 0) { throw "No coverage file from: $($missing -join ', ')." }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE." }

    dotnet tool run reportgenerator "-reports:$results/*.coverage.cobertura.*.xml" "-targetdir:$report" `
        '-reporttypes:Html;TextSummary;MarkdownSummaryGithub' '-verbosity:Warning'
    if ($LASTEXITCODE -ne 0) { throw "ReportGenerator failed with exit code $LASTEXITCODE." }

    (Get-Content -LiteralPath (Join-Path $report 'Summary.txt')) -match 'coverage:' | ForEach-Object { $_.Trim() }
    "Coverage report: $(Join-Path $PSScriptRoot $report)\index.html"
}
finally {
    Pop-Location
}
