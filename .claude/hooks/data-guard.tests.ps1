# Tests for data-guard.ps1 with made-up terms. Run from anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\data-guard.tests.ps1
# The tests create files, a staged change and a local branch in the repository, and remove them afterwards.

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$guard = Join-Path $PSScriptRoot 'data-guard.ps1'
$termsFile = Join-Path ([IO.Path]::GetTempPath()) ("data-guard-terms-" + [guid]::NewGuid() + '.txt')
$utf8 = New-Object Text.UTF8Encoding $false

# Terms and git commands are built by concatenation: this file is in the repository and is scanned
# by the live hook itself, so it must never contain a term or a whole git commit command.
$hostTerm = 'fake' + 'host01'
$nameTerm = 'AC' + 'ME'
$domainTerm = 'ac' + 'me-corp.example'
[IO.File]::WriteAllText($termsFile, "# made-up terms`n$hostTerm`n$nameTerm`n$domainTerm`n", $utf8)
$env:DATA_GUARD_TERMS_FILE = $termsFile

$commit = 'git ' + 'commit -m test'
$push = 'git ' + 'push origin main'
$results = New-Object System.Collections.Generic.List[string]

function Run([string]$name, [string]$command, [int]$expected, [string]$rawInput = $null) {
    $json = if ($rawInput) { $rawInput } else { @{ tool_name = 'Bash'; cwd = $project; tool_input = @{ command = $command } } | ConvertTo-Json -Compress }
    $err = $json | powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $guard 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $status = if ($code -eq $expected) { 'PASS' } else { 'FAIL' }
    $message = ($err | Where-Object { $_ -match 'data-guard' }) -join ' '
    $script:results.Add(("{0} {1,-40} exit={2} {3}" -f $status, $name, $code, $message))
}

function Write-TestFile([string]$name, [string]$text) {
    [IO.File]::WriteAllText((Join-Path $project $name), $text, $utf8)
}

$testFiles = 'guard-test.md', 'проба-guard.md'
Push-Location $project
try {
    Run '01 command without git' 'dir' 0
    Run '02 clean commit' $commit 0
    Run '03 clean push' $push 0

    Write-TestFile 'guard-test.md' ("host $hostTerm")
    Run '04 term in an untracked file' $commit 2
    Run '05 add + commit, message says push' ('git ' + 'add -A && ' + $commit + ' -m "Run CI on push"') 2
    Run '06 commit && push' ($commit + ' && ' + $push) 2

    git add guard-test.md
    Write-TestFile 'guard-test.md' 'clean now'
    Run '07 term only in the index' $commit 2
    git reset -q -- guard-test.md

    Write-TestFile 'guard-test.md' 'acmeology is a longer word'
    Run '08 term inside a longer word' $commit 0
    Write-TestFile 'guard-test.md' ($nameTerm.ToLower() + '_read and ' + $nameTerm + '2')
    Run '09 term followed by _ or a digit' $commit 2
    Remove-Item -LiteralPath 'guard-test.md'

    Run '10 term in the commit message' ($commit + ' via ' + $domainTerm) 2
    Run '11 git add -f' ('git ' + 'add -f secret.tmp') 2
    Run '11a add, then commit -F from stdin' ('git ' + 'add docs && ' + 'git ' + 'commit -q -F -') 0
    Run '11b add --force in a chain' ('git ' + 'status && git ' + 'add --force x.tmp') 2

    Write-TestFile 'проба-guard.md' 'clean'
    Run '12 Cyrillic file name, clean' $commit 0
    Write-TestFile 'проба-guard.md' ("host $hostTerm")
    Run '13 Cyrillic file name, term' $commit 2
    Remove-Item -LiteralPath 'проба-guard.md'

    $sha = git commit-tree 'HEAD^{tree}' -p HEAD -m ('note ' + $nameTerm)
    git update-ref refs/heads/guard-test $sha
    Run '14 push, local commit message has term' $push 2
    git update-ref -d refs/heads/guard-test

    Run '15 invalid JSON' '' 2 'not json'

    [IO.File]::WriteAllText($termsFile, "# only comments`n", $utf8)
    Run '16 empty terms list' $commit 2
}
finally {
    foreach ($name in $testFiles) {
        if (Test-Path -LiteralPath $name) { git reset -q -- $name 2>$null; Remove-Item -LiteralPath $name }
    }
    if (git rev-parse --verify --quiet refs/heads/guard-test) { git update-ref -d refs/heads/guard-test }
    Remove-Item -LiteralPath $termsFile -ErrorAction SilentlyContinue
    Remove-Item Env:DATA_GUARD_TERMS_FILE -ErrorAction SilentlyContinue
    Pop-Location
}

$results
$passed = @($results | Where-Object { $_ -like 'PASS*' }).Count
"--- $passed of $($results.Count) passed"
if ($passed -ne $results.Count) { exit 1 }
