# Tests for data-guard.ps1 with made-up terms. Run from anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\data-guard.tests.ps1
# The Claude hook tests create files, a staged change and a local branch in the repository, and remove them
# afterwards. The git hook tests commit and push in a temporary repository, which is deleted at the end.

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
$termsText = "# made-up terms`n$hostTerm`n$nameTerm`n$domainTerm`n"
[IO.File]::WriteAllText($termsFile, $termsText, $utf8)
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

function Write-TestFile([string]$name, [string]$text, [string]$dir = $project) {
    [IO.File]::WriteAllText((Join-Path $dir $name), $text, $utf8)
}

# Runs git in a test repository, where the hooks in .githooks call data-guard.ps1.
# $blockedIn is the part where the term must be found, or '' when git must succeed.
function RunGit([string]$name, [string]$dir, [string]$blockedIn, [string[]]$gitArgs) {
    $out = & git.exe -C $dir @gitArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $message = ($out | Where-Object { $_ -match 'data-guard' }) -join ' '
    # A block for another reason, such as a failed check, does not count
    $ok = if ($blockedIn) { $code -ne 0 -and $message -match ("found internal terms: .*' in " + [regex]::Escape($blockedIn)) } else { $code -eq 0 }
    $status = if ($ok) { 'PASS' } else { 'FAIL' }
    $detail = if ($message -or $ok) { $message } else { $out -join ' ' }
    $script:results.Add(("{0} {1,-40} exit={2} {3}" -f $status, $name, $code, $detail))
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

# Git hooks: a temporary repository set up like a clone, with copies of .githooks, .gitattributes and
# the script, and the made-up terms where a clone keeps the real ones. No DATA_GUARD_TERMS_FILE here.
# The space in the name is on purpose: the project path has spaces too
$base = Join-Path ([IO.Path]::GetTempPath()) ("data-guard repo " + [guid]::NewGuid())
$repo = Join-Path $base 'repo'
$bare = Join-Path $base 'remote.git'
$worktree = Join-Path $base 'worktree'
try {
    & git.exe init -q $repo
    & git.exe init -q --bare $bare
    $null = New-Item -ItemType Directory (Join-Path $repo '.claude\hooks')
    Copy-Item (Join-Path $project '.githooks') $repo -Recurse
    Copy-Item (Join-Path $project '.gitattributes') $repo
    Copy-Item $guard (Join-Path $repo '.claude\hooks')
    Write-TestFile '.claude\data-guard.local.txt' $termsText $repo
    Write-TestFile '.gitignore' ".claude/data-guard.local.txt`n" $repo
    Write-TestFile 'readme.md' "clean`n" $repo
    $settings = @('user.name', 'Test User'), @('user.email', 'test@example.com'),
        @('core.hooksPath', '.githooks'), @('commit.gpgSign', 'false'), @('core.safecrlf', 'false')
    foreach ($setting in $settings) { & git.exe -C $repo config @setting }
    & git.exe -C $repo remote add origin $bare
    Write-TestFile 'notes.md' "host $hostTerm`n" $repo
    & git.exe -C $repo add -A
    RunGit '17 git: first commit, staged term' $repo 'the index' @('commit', '-q', '-m', 'Initial version')
    & git.exe -C $repo rm -q --cached notes.md
    Remove-Item -LiteralPath (Join-Path $repo 'notes.md')
    RunGit '18 git: clean first commit' $repo '' @('commit', '-q', '-m', 'Initial version')

    Write-TestFile 'notes.md' "host $hostTerm`n" $repo
    & git.exe -C $repo add notes.md
    RunGit '19 git: term in a staged file' $repo 'the index' @('commit', '-q', '-m', 'Add notes')
    RunGit '20 git: same with color.ui=always' $repo 'the index' @('-c', 'color.ui=always', 'commit', '-q', '-m', 'Add notes')
    & git.exe -C $repo reset -q --hard

    # File names without added lines: an empty file, and a rename of an unchanged file
    $termName = $nameTerm.ToLower() + '.md'
    Write-TestFile $termName '' $repo
    & git.exe -C $repo add $termName
    RunGit '21 git: empty file named with a term' $repo 'the index' @('commit', '-q', '-m', 'Add a file')
    & git.exe -C $repo reset -q --hard
    & git.exe -C $repo mv readme.md $termName
    RunGit '22 git: rename to a name with a term' $repo 'the index' @('commit', '-q', '-m', 'Rename readme')
    & git.exe -C $repo reset -q --hard
    # git treats UTF-16 text as binary
    [IO.File]::WriteAllText((Join-Path $repo 'report.txt'), "host $hostTerm`r`n", [Text.Encoding]::Unicode)
    & git.exe -C $repo add report.txt
    RunGit '22a git: UTF-16 file with a term' $repo 'the index' @('commit', '-q', '-m', 'Add a report')
    & git.exe -C $repo reset -q --hard

    Write-TestFile 'notes.md' "clean`n" $repo
    & git.exe -C $repo add notes.md
    RunGit '23 git: term in the message' $repo 'the commit message' @('commit', '-q', '-m', "Add notes for $nameTerm")
    RunGit '24 git: term in --author' $repo 'author or committer' @('commit', '-q', '-m', 'Add notes', "--author=Someone <someone@$domainTerm>")
    RunGit '25 git: clean commit' $repo '' @('commit', '-q', '-m', 'Add notes')

    # commit -a commits a change that is not staged yet, through a temporary index
    Write-TestFile 'readme.md' "host $hostTerm`n" $repo
    RunGit '26 git: commit -a, term not staged' $repo 'the index' @('commit', '-q', '-a', '-m', 'Update readme')
    & git.exe -C $repo checkout -q -- readme.md

    RunGit '27 git: clean push of two branches' $repo '' @('push', '-q', 'origin', 'HEAD:refs/heads/main', 'HEAD:refs/heads/old')
    RunGit '28 git: delete a remote branch' $repo '' @('push', '-q', 'origin', ':refs/heads/old')
    # A URL matches no tracking branches, so the whole history is scanned
    RunGit '29 git: clean push to a URL' $repo '' @('push', '-q', $bare, 'HEAD:refs/heads/by-url')

    # Commits made with plumbing commands, which run no hooks, like commits from a tool that skips them
    $pushed = & git.exe -C $repo rev-parse HEAD
    Write-TestFile 'notes.md' "host $hostTerm`n" $repo
    & git.exe -C $repo add notes.md
    $tree = & git.exe -C $repo write-tree
    & git.exe -C $repo update-ref HEAD (& git.exe -C $repo commit-tree $tree -p HEAD -m 'Update notes')
    RunGit '30 git: push, local commit has a term' $repo 'commit changes to push' @('push', '-q', 'origin', 'HEAD:refs/heads/main')
    # Removing the term in the editor with commit -v: the diff below the message shows the removed line
    Write-TestFile 'notes.md' "clean`n" $repo
    & git.exe -C $repo add notes.md
    RunGit '31 git: commit -v that removes a term' $repo '' @('-c', 'core.editor=true', 'commit', '-q', '-v', '-e', '-m', 'Remove the host')
    & git.exe -C $repo reset -q --hard $pushed

    & git.exe -C $repo update-ref HEAD (& git.exe -C $repo commit-tree 'HEAD^{tree}' -p HEAD -m "note $nameTerm")
    RunGit '32 git: push, local message has a term' $repo 'commit messages to push' @('push', '-q', 'origin', 'HEAD:refs/heads/main')
    & git.exe -C $repo reset -q --hard $pushed
    & git.exe -C $repo tag -a v1 -m "note $nameTerm"
    RunGit '33 git: push, tag message has a term' $repo 'tag messages to push' @('push', '-q', 'origin', 'refs/tags/v1')
    & git.exe -C $repo tag -d v1 >$null
    RunGit '34 git: push to a branch named with a term' $repo 'the pushed ref names' @('push', '-q', 'origin', ('HEAD:refs/heads/fix-' + $nameTerm.ToLower()))

    # A linked worktree has no terms file of its own; the hooks use the one in the main working tree
    & git.exe -C $repo worktree add -q -b side $worktree 2>$null
    Write-TestFile 'side.md' "clean`n" $worktree
    & git.exe -C $worktree add side.md
    RunGit '35 git: worktree, clean commit' $worktree '' @('commit', '-q', '-m', 'Add side notes')
    Write-TestFile 'side.md' "host $hostTerm`n" $worktree
    & git.exe -C $worktree add side.md
    RunGit '36 git: worktree, term in a staged file' $worktree 'the index' @('commit', '-q', '-m', 'Update side notes')
}
finally {
    if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
}

$results
$passed = @($results | Where-Object { $_ -like 'PASS*' }).Count
"--- $passed of $($results.Count) passed"
if ($passed -ne $results.Count) { exit 1 }
