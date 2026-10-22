# Tests for data-guard.ps1 with made-up terms. Run from anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\data-guard.tests.ps1
# Keep this file UTF-8 with a BOM: Windows PowerShell 5.1 reads a script without one in the ANSI code
# page, and the Cyrillic file name below would turn into other characters.
# The Claude hook tests create files, a staged change and a local branch in the repository, and remove them
# afterwards. The git hook tests commit and push in a temporary repository, which is deleted at the end.

[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Read without the BOM, this one letter becomes two characters
if ('ф'.Length -ne 1) { throw 'Save data-guard.tests.ps1 as UTF-8 with a BOM.' }
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$guard = Join-Path $PSScriptRoot 'data-guard.ps1'
$termsFile = Join-Path ([IO.Path]::GetTempPath()) ("data-guard-terms-" + [guid]::NewGuid() + '.txt')
$utf8 = New-Object Text.UTF8Encoding $false

# Terms and git commands are built by concatenation: this file is in the repository and is scanned
# by the live hook itself, so it must never contain a term or a whole git commit command.
$hostTerm = 'fake' + 'host01'
$nameTerm = 'AC' + 'ME'
$domainTerm = 'ac' + 'me-corp.example'
$cyrillicTerm = 'фалш' + 'хост'
$termsText = "# made-up terms`n$hostTerm`n$nameTerm`n$domainTerm`n$cyrillicTerm`n"
[IO.File]::WriteAllText($termsFile, $termsText, $utf8)
$env:DATA_GUARD_TERMS_FILE = $termsFile

$commit = 'git ' + 'commit -m test'
$push = 'git ' + 'push origin main'
$results = New-Object System.Collections.Generic.List[string]

# $mustSay is a part of the hook's message that must be there, such as the name of the file.
# $cwd and $hookFile point a run at another repository and its copy of the hook, with that copy's terms.
function Run([string]$name, [string]$command, [int]$expected, [string]$rawInput = $null, [string]$mustSay = $null,
    [string]$cwd = $project, [string]$hookFile = $guard, [string]$tool = 'Bash') {
    $json = if ($rawInput) { $rawInput } else { @{ tool_name = $tool; cwd = $cwd; tool_input = @{ command = $command } } | ConvertTo-Json -Compress }
    # The console code page is shared with the child process. Without this, the hook would inherit
    # UTF-8 from the line at the top, and a hook that does not set UTF-8 itself would still pass the
    # Cyrillic tests. chcp leaves this script's own [Console]::OutputEncoding as it is.
    chcp.com 437 > $null
    if ($LASTEXITCODE -ne 0) { throw 'chcp failed, so the hook would inherit the UTF-8 code page' }
    $err = $json | powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $hookFile 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $message = ($err | Where-Object { $_ -match 'data-guard' }) -join ' '
    $ok = $code -eq $expected -and (-not $mustSay -or $message.Contains($mustSay))
    $status = if ($ok) { 'PASS' } else { 'FAIL' }
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

# Stages the bytes as guard-test.md, then puts clean text in the working tree, so only the index has them
function Write-Staged([byte[]]$bytes) {
    [IO.File]::WriteAllBytes((Join-Path $project 'guard-test.md'), $bytes)
    git add guard-test.md 2>$null
    Write-TestFile 'guard-test.md' 'clean now'
}

$cp1251 = [Text.Encoding]::GetEncoding(1251)
$forceMessage = 'stages ignored files'
$softName = "guard-te$([char]0xAD)st.md"
$testFiles = 'guard-test.md', 'проба-guard.md', 'guard-hidden.md', 'guard-nested', 'guard-link.md', $softName
$oddName = 'guard-test.md.'
$oddPath = '\\?\' + (Join-Path $project $oddName)
# The cleanup removes these names, so they must be the test's own
$taken = @($testFiles | Where-Object { Test-Path -LiteralPath (Join-Path $project $_) })
if ([IO.File]::Exists($oddPath)) { $taken += "$oddName (delete it with: cmd /c del `"$oddPath`")" }
if ($taken) {
    Remove-Item -LiteralPath $termsFile
    throw "Already in the project: $($taken -join ', '). Move them away, then run the tests."
}
Push-Location $project
try {
    Run '01 command without git' 'dir' 0
    Run '02 clean commit' $commit 0
    Run '03 clean push' $push 0

    Write-TestFile 'guard-test.md' ("host $hostTerm")
    Run '04 term in an untracked file' $commit 2 -mustSay "'$hostTerm' in new file guard-test.md"
    Run '05 add + commit, message says push' ('git ' + 'add -A && ' + $commit + ' -m "Run CI on push"') 2 -mustSay "'$hostTerm' in new file guard-test.md"
    Run '06 commit && push' ($commit + ' && ' + $push) 2 -mustSay "'$hostTerm' in new file guard-test.md"

    git add guard-test.md
    Write-TestFile 'guard-test.md' 'clean now'
    Run '07 term only in the index' $commit 2 -mustSay "'$hostTerm' in the index"
    git reset -q -- guard-test.md

    Write-TestFile 'guard-test.md' 'acmeology is a longer word'
    Run '08 term inside a longer word' $commit 0
    Write-TestFile 'guard-test.md' ($nameTerm.ToLower() + '_read and ' + $nameTerm + '2')
    Run '09 term followed by _ or a digit' $commit 2 -mustSay "'$nameTerm' in new file guard-test.md"
    Remove-Item -LiteralPath 'guard-test.md'

    Run '10 term in the commit message' ($commit + ' via ' + $domainTerm) 2 -mustSay "'$domainTerm' in the command"

    # The hook scans the repository of the command's folder, so a commit in another one must not pass.
    # Not under TEMP: its 8.3 path has a ~, which blocks for another reason.
    $otherRepo = Join-Path $env:LOCALAPPDATA ('Temp\data-guard other ' + [guid]::NewGuid())
    & git.exe init -q $otherRepo
    $bashProject = '/' + $project.Substring(0, 1).ToLower() + $project.Substring(2).Replace('\', '/')
    Run '10a cd into the project, then commit' ('cd "' + $project + '" && ' + $commit) 0
    Run '10b cd into a subfolder' ('cd .claude && ' + $commit) 0
    Run '10c git -C the project' ('git ' + '-C "' + $project + '" commit -m test') 0
    Run '10d cd with a Git Bash path' ('cd "' + $bashProject + '" && ' + $commit) 0
    Run '10e git -C another repository' ('git ' + '-C "' + $otherRepo + '" commit -m test') 2 -mustSay 'another folder'
    Run '10f cd to another repository' ('cd "' + $otherRepo + '" && ' + $commit) 2 -mustSay 'another folder'
    Run '10g Set-Location to another repository' ('Set-Location "' + $otherRepo + '"; ' + $commit) 2 -mustSay 'another folder'
    Run '10h pushd to a folder outside any repo' ('pushd "' + (Split-Path $project -Parent) + '" && ' + $commit) 2 -mustSay 'another folder'
    Run '10i cd to a folder in a variable' ('cd "$dir" && ' + $commit) 2 -mustSay 'another folder'
    Run '10j a second -C' ('git ' + '-C . -C "' + $otherRepo + '" commit -m test') 2 -mustSay 'another folder'
    Run '10k cd inside braces' ('{ cd "' + $otherRepo + '"; ' + $commit + '; }') 2 -mustSay 'another folder'
    Run '10l GIT_DIR in the environment' ('GIT_DIR=x ' + $commit) 2 -mustSay 'GIT_DIR'
    Run '10m --work-tree' ('git ' + '--work-tree=x commit -m test') 2 -mustSay '--work-tree'
    Run '10n words in the message do not count' ($commit + ' -m "blocks git -C to another folder; cd x"') 0
    Run '10o words in a heredoc do not count' ('git ' + "commit -F - <<'EOF'`ncd somewhere`nblocks git -C x`nEOF") 0
    Run '10p git -C on the heredoc line' ("cat <<'EOF' | git " + '-C "' + $otherRepo + "`" commit -F -`nmessage`nEOF") 2 -mustSay 'another folder'
    Run '10q cd after if' ('if cd "' + $otherRepo + '"; then ' + $commit + '; fi') 2 -mustSay 'another folder'
    Run '10r -c value with a space, then -C' ('git ' + '-c user.name="A B" -C "' + $otherRepo + '" commit -m test') 2 -mustSay 'another folder'
    Run '10s PowerShell quotes do not hide a command' ('git ' + 'commit -m "say \" ; Set-Location "' + $otherRepo + '" ; git ' + 'commit -am x ; echo "y"') 2 -mustSay 'another folder' -tool 'PowerShell'
    Run '10t git -C with a trailing backslash' ('git ' + '-C "' + $project + '\" commit -m test') 0 -tool 'PowerShell'
    Run '10u env -C' ('env -C "' + $otherRepo + '" ' + $commit) 2 -mustSay 'env -C'
    Run '10v words in a here-string do not count' ("@'`nRemoves git -C x`ncd y`n'@ | git " + 'commit -F -') 0 -tool 'PowerShell'
    Run '10w $env: and -c are not env -C' ('& "$env:ProgramFiles\Git\cmd\git.exe" -c core.quotepath=off ' + 'commit -m test') 0 -tool 'PowerShell'
    Remove-Item -LiteralPath $otherRepo -Recurse -Force

    Run '11 git add -f' ('git ' + 'add -f secret.tmp') 2 -mustSay $forceMessage
    Run '11a add, then commit -F from stdin' ('git ' + 'add docs && ' + 'git ' + 'commit -q -F -') 0
    Run '11b add --force in a chain' ('git ' + 'status && git ' + 'add --force x.tmp') 2 -mustSay $forceMessage
    Run '11c add with bundled -Af' ('git ' + 'add -Af') 2 -mustSay $forceMessage
    Run '11d add --forc, a prefix of --force' ('git ' + 'add --forc x.tmp') 2 -mustSay $forceMessage
    Run '11e git stage -f' ('git ' + 'stage -f x.tmp') 2 -mustSay $forceMessage
    Run '11f git -C . add -f' ('git ' + '-C . add -f x.tmp') 2 -mustSay $forceMessage
    Run '11g git update-index --add' ('git ' + 'update-index --add x.tmp') 2 -mustSay 'update-index --add'
    Run '11h GIT add -f, in capitals' ('GIT ' + 'add -f x.tmp') 2 -mustSay $forceMessage
    Run '11i git.exe add -f' ('git.exe ' + 'add -f x.tmp') 2 -mustSay $forceMessage
    Run '11j add -f inside bash -c' ("bash -c 'git " + "add -A -f'") 2 -mustSay $forceMessage
    Run '11k update-index --ad, a prefix' ('git ' + 'update-index --ad x.tmp') 2 -mustSay 'update-index --add'
    Run '11l commit --no-verify' ($commit + ' --no-verify') 2 -mustSay 'turns off the git hooks'
    Run '11m commit -n' ('git ' + 'commit -n -m test') 2 -mustSay 'turns off the git hooks'
    Run '11n push --no-verify' ($push + ' --no-verify') 2 -mustSay 'turns off the git hooks'
    Run '11o -c core.hooksPath' ('git ' + '-c core.hooksPath=x commit -m test') 2 -mustSay 'turns off the git hooks'
    Run '11p commit -nm' ('git ' + 'commit -nm "x"') 2 -mustSay 'turns off the git hooks'
    Run '11q reading core.hooksPath' ('git ' + 'config --get core.hooksPath') 0
    Run '11r setting core.hooksPath elsewhere' ('git ' + 'config core.hooksPath x') 2 -mustSay 'turns off the git hooks'
    Run '11s setting core.hooksPath to .githooks' ('git ' + 'config core.hooksPath .githooks') 0
    Run '11t PowerShell quote in a message' ('git ' + "commit -m 'the hook''s -n flag'") 0 -tool 'PowerShell'
    Run '11u bash quote in a message' ('git ' + "commit -m 'the hook'\''s --no-verify check'") 0
    Run '11v reading core.hooksPath with 2>' ('git ' + 'config core.hooksPath 2>/dev/null') 0
    Run '11w config --remove-section core' ('git ' + 'config --remove-section core') 2 -mustSay 'turns off the git hooks'
    Run '11x --config-env for core.hooksPath' ('git ' + '--config-env=core.hooksPath=X commit -m test') 2 -mustSay 'turns off the git hooks'
    Run '11y push -n, then --no-verify' ($push + ' -n --no-verify') 2 -mustSay 'turns off the git hooks'

    Write-TestFile 'проба-guard.md' 'clean'
    Run '12 Cyrillic file name, clean' $commit 0
    Write-TestFile 'проба-guard.md' ("host $hostTerm")
    Run '13 Cyrillic file name, term' $commit 2 -mustSay 'in new file проба-guard.md'
    Remove-Item -LiteralPath 'проба-guard.md'

    # The diff comes from git output too, decoded like the file names
    Write-TestFile 'guard-test.md' ("хост $cyrillicTerm")
    git add guard-test.md
    Write-TestFile 'guard-test.md' 'clean now'
    Run '13a Cyrillic term in the index' $commit 2 -mustSay "'$cyrillicTerm' in the index"
    git reset -q -- guard-test.md
    Remove-Item -LiteralPath 'guard-test.md'

    # Git lists a name with a trailing dot, but a normal Windows path drops the dot and reaches guard-test.md
    # instead. Only a \\?\ path reaches the file; if a run stops here, delete it with
    #   cmd /c del "\\?\<project folder>\guard-test.md."
    Write-TestFile 'guard-test.md' 'clean'
    [IO.File]::WriteAllText($oddPath, 'clean')
    Run '13b name that ends with a dot' $commit 2 -mustSay "the new file '$oddName' has a name that ends with a dot"
    [IO.File]::Delete($oddPath)
    Remove-Item -LiteralPath 'guard-test.md'

    # Get-Item does not see hidden files without -Force, and git publishes them like any other
    Write-TestFile 'guard-hidden.md' ("host $hostTerm")
    (Get-Item -LiteralPath 'guard-hidden.md').Attributes = 'Hidden'
    Run '13c hidden new file with a term' $commit 2 -mustSay "'$hostTerm' in new file guard-hidden.md"
    Remove-Item -LiteralPath 'guard-hidden.md' -Force

    # A nested repository is listed as a folder; there is no file to read
    git init -q guard-nested
    Run '13d nested repository' $commit 2 -mustSay "cannot read the new file 'guard-nested/'"
    Remove-Item -LiteralPath 'guard-nested' -Recurse -Force

    # Big files are not read here; staged files go through the diff, which has no size limit
    Write-TestFile 'guard-test.md' ('x' * (5MB + 1))
    Run '13e new file over 5 MB' $commit 2 -mustSay "the new file 'guard-test.md' is over 5 MB"
    Remove-Item -LiteralPath 'guard-test.md'

    # A Claude hook that runs out of time does not block, so too much to check blocks at once
    Write-Staged ([Text.Encoding]::ASCII.GetBytes('x' * (100MB + 1)))
    Run '13e2 staged changes over 100 MB' $commit 2 -mustSay 'over 100 MB'
    git reset -q -- guard-test.md
    Remove-Item -LiteralPath 'guard-test.md'
    # ... and so does a check that would outlast the timeout
    $env:DATA_GUARD_SECONDS = '0.05'
    Run '13e3 a check that runs out of time' $commit 2 -mustSay 'took longer than'
    Remove-Item Env:DATA_GUARD_SECONDS

    # PowerShell splits native output on a lone CR too, and only lines that start with + count
    Write-Staged ([Text.Encoding]::ASCII.GetBytes("clean`r$hostTerm`r"))
    Run '13f term after a lone CR, in the index' $commit 2 -mustSay "'$hostTerm' in the index"
    git reset -q -- guard-test.md

    # Set-Content in PowerShell 5.1 writes the ANSI code page (windows-1251 here), and > writes UTF-16
    Write-Staged ($cp1251.GetBytes("хост $cyrillicTerm"))
    Run '13g windows-1251 term in the index' $commit 2 -mustSay "'$cyrillicTerm' in the index"
    git reset -q -- guard-test.md
    Write-Staged ([Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes("хост $cyrillicTerm`r`n"))
    Run '13h UTF-16 Cyrillic term in the index' $commit 2 -mustSay "'$cyrillicTerm' in the index"
    git reset -q -- guard-test.md
    [IO.File]::WriteAllBytes((Join-Path $project 'guard-test.md'), $cp1251.GetBytes("хост $cyrillicTerm"))
    Run '13i windows-1251 term in a new file' $commit 2 -mustSay "'$cyrillicTerm' in new file guard-test.md"
    [IO.File]::WriteAllBytes((Join-Path $project 'guard-test.md'), [Text.Encoding]::Unicode.GetBytes("host $hostTerm"))
    Run '13j UTF-16 without a BOM in a new file' $commit 2 -mustSay "'$hostTerm' in new file guard-test.md"
    # Cyrillic only, as Set-Content -Encoding Unicode -NoNewline writes it: no NUL byte at all
    [IO.File]::WriteAllBytes((Join-Path $project 'guard-test.md'), [Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes($cyrillicTerm))
    Run '13m UTF-16 Cyrillic only, no NUL' $commit 2 -mustSay "'$cyrillicTerm' in new file guard-test.md"
    [IO.File]::WriteAllBytes((Join-Path $project 'guard-test.md'), [Text.Encoding]::BigEndianUnicode.GetPreamble() + [Text.Encoding]::BigEndianUnicode.GetBytes($cyrillicTerm))
    Run '13n UTF-16 big-endian Cyrillic' $commit 2 -mustSay "'$cyrillicTerm' in new file guard-test.md"
    Remove-Item -LiteralPath 'guard-test.md'

    # gh publishes text on GitHub directly; no git hook sees it
    Run '13o gh pr create, term in the body' ('gh pr create --title x --body "host ' + $hostTerm + '"') 2 -mustSay "'$hostTerm' in the command"
    Write-TestFile 'guard-test.md' ("host $hostTerm")
    Run '13p gh release create --notes-file' 'gh release create v1 --notes-file guard-test.md' 2 -mustSay "in the file 'guard-test.md' named in the gh command"
    Remove-Item -LiteralPath 'guard-test.md'
    Run '13q gh api -F is a field, not a file' 'gh api repos/x/y/issues -F title=clean' 0
    Run '13r gh on the heredoc line' ("cat <<'EOF' | gh issue create --title x --body-file -`nhost $hostTerm`nEOF") 2 -mustSay "'$hostTerm' in the command"
    Run '13s gh words in the title do not count' 'gh pr create --title "cd x" --body "git -C y"' 0
    # Files outside the repository, which only the gh check reads
    $ghFile = Join-Path $env:LOCALAPPDATA ('Temp\data-guard-gh-' + [guid]::NewGuid() + '.md')
    Write-TestFile (Split-Path $ghFile -Leaf) ("host $hostTerm") (Split-Path $ghFile -Parent)
    Run '13t gh gist create, a file argument' ('gh gist create "' + $ghFile + '"') 2 -mustSay 'named in the gh command'
    Run '13u gh api, key=@file' ('gh api repos/x/y/issues -F body=@' + $ghFile) 2 -mustSay 'named in the gh command'
    Run '13v gh pr create, body from < file' ('gh pr create --title x --body-file - < "' + $ghFile + '"') 2 -mustSay 'named in the gh command'
    Run '13x gh, quoted name after =' ('gh pr create --title x --body-file="' + $ghFile + '"') 2 -mustSay 'named in the gh command'
    Run '13y gh, ~ for the profile' ('gh gist create ~/' + $ghFile.Substring($env:USERPROFILE.Length + 1).Replace('\', '/')) 2 -mustSay 'named in the gh command'
    $tmpName = 'data-guard-gh-' + [guid]::NewGuid() + '.md'
    Write-TestFile $tmpName ("host $hostTerm") ([IO.Path]::GetTempPath())
    Run '13z gh, Git Bash /tmp' ('gh gist create /tmp/' + $tmpName) 2 -mustSay 'named in the gh command'
    Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $tmpName)
    Remove-Item -LiteralPath $ghFile
    Run '13aa gh, command substitution' 'gh pr create --title x --body "$(cat notes.md)"' 2 -mustSay 'variable or a command'
    Run '13ab gh, wildcard' 'gh gist create notes/*.md' 2 -mustSay 'wildcard'
    Run '13ac gh, $ in a quoted heredoc' ("cat <<'EOF' | gh issue create --title x --body-file -`nCosts " + '$5' + "`nEOF") 0
    # A name relative to a folder the command changes to
    $null = New-Item -ItemType Directory 'guard-nested'
    Write-TestFile 'guard-nested\notes.md' ("host $hostTerm")
    Run '13ad gh after cd, relative name' 'cd guard-nested && gh release create v1 --notes-file notes.md' 2 -mustSay "the file 'notes.md' named in the gh command"
    Remove-Item -LiteralPath 'guard-nested' -Recurse -Force
    Run '13w command over 256 KB' ($commit + ' ' + ('x' * 256KB)) 2 -mustSay 'over 256 KB'

    # Two names that a culture-aware comparer takes as one: it ignores the soft hyphen
    Write-TestFile 'guard-test.md' ("host $hostTerm")
    Write-TestFile $softName 'clean'
    Run '13k names that differ by a soft hyphen' $commit 2 -mustSay "'$hostTerm' in new file guard-test.md"
    Remove-Item -LiteralPath 'guard-test.md', $softName

    # Git publishes a symlink as the path of its target. The target is clean; its path has a term.
    $linkDir = Join-Path ([IO.Path]::GetTempPath()) ("data-guard-$hostTerm-" + [guid]::NewGuid())
    $null = New-Item -ItemType Directory $linkDir
    Write-TestFile 'clean.md' 'clean' $linkDir
    cmd /c mklink guard-link.md "$linkDir\clean.md" > $null 2>&1
    if (Test-Path -LiteralPath 'guard-link.md') {
        Run '13l symlink, term in the target path' $commit 2 -mustSay "'$hostTerm' in new file guard-link.md"
        # What the test assumes: git stores the link as the path of its target
        git add guard-link.md 2>$null
        $stored = (git ls-files -s guard-link.md) -match '^120000 ' -and (git cat-file -p ':guard-link.md') -eq "$linkDir\clean.md".Replace('\', '/')
        git reset -q -- guard-link.md
        $results.Add(("{0} {1,-40} git stores the target path" -f $(if ($stored) { 'PASS' } else { 'FAIL' }), '13l2 symlink in the index'))
        Remove-Item -LiteralPath 'guard-link.md' -Force
    }
    else {
        $results.Add(("SKIP {0,-40} mklink needs Developer Mode or admin rights" -f '13l symlink, term in the target path'))
    }
    Remove-Item -LiteralPath $linkDir -Recurse -Force

    $sha = git commit-tree 'HEAD^{tree}' -p HEAD -m ('note ' + $nameTerm)
    git update-ref refs/heads/guard-test $sha
    Run '14 push, local commit message has term' $push 2 -mustSay "'$nameTerm' in commit messages to push"
    git update-ref -d refs/heads/guard-test

    Run '15 invalid JSON' '' 2 'not json' -mustSay 'check failed'

    [IO.File]::WriteAllText($termsFile, "# only comments`n", $utf8)
    Run '16 empty terms list' $commit 2 -mustSay 'has no terms'
}
finally {
    foreach ($name in $testFiles) {
        if (Test-Path -LiteralPath $name) { git reset -q -- $name 2>$null; Remove-Item -LiteralPath $name -Recurse -Force }
    }
    if ([IO.File]::Exists($oddPath)) { [IO.File]::Delete($oddPath) }
    foreach ($folder in $linkDir, $otherRepo, $ghFile) {
        if ($folder -and (Test-Path -LiteralPath $folder)) { Remove-Item -LiteralPath $folder -Recurse -Force }
    }
    Remove-Item Env:DATA_GUARD_SECONDS -ErrorAction SilentlyContinue
    if (git rev-parse --verify --quiet refs/heads/guard-test) { git update-ref -d refs/heads/guard-test }
    Remove-Item -LiteralPath $termsFile -ErrorAction SilentlyContinue
    Remove-Item Env:DATA_GUARD_TERMS_FILE -ErrorAction SilentlyContinue
    # chcp may have left the console at 437, and the results printed at the end have Cyrillic in them
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
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
    [IO.File]::WriteAllBytes((Join-Path $repo 'data.csv'), [Text.Encoding]::ASCII.GetBytes("clean`r$hostTerm`r"))
    & git.exe -C $repo add data.csv
    RunGit '22b git: term after a lone CR' $repo 'the index' @('commit', '-q', '-m', 'Add data')
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

    # The Claude hook in this repository, with its copy of the script and its made-up terms
    $repoGuard = Join-Path $repo '.claude\hooks\data-guard.ps1'
    $branch = & git.exe -C $repo symbolic-ref --short HEAD
    & git.exe -C $repo reset -q --hard
    # A stash pop that conflicts, resolved with the stash's side: git diff leaves those lines out, and the
    # stash is on no branch
    Write-TestFile 'conflict.md' "base`n" $repo
    & git.exe -C $repo add conflict.md
    & git.exe -C $repo @('commit', '-q', '-m', 'Add a file to change')
    Write-TestFile 'conflict.md' "host $hostTerm`n" $repo
    & git.exe -C $repo stash -q
    Write-TestFile 'conflict.md' "ours`n" $repo
    & git.exe -C $repo @('commit', '-q', '-a', '-m', 'Change the file')
    & git.exe -C $repo stash pop -q 2>$null
    & git.exe -C $repo checkout -q --theirs conflict.md
    Run '37 Claude: stash pop conflict, stash kept' ('git ' + 'add -A && ' + $commit) 2 -mustSay "'$hostTerm' in the working tree" -cwd $repo -hookFile $repoGuard
    & git.exe -C $repo reset -q --hard
    & git.exe -C $repo stash drop -q
    # A commit on a detached HEAD is on no branch
    & git.exe -C $repo update-ref --no-deref HEAD (& git.exe -C $repo commit-tree 'HEAD^{tree}' -p HEAD -m "note $nameTerm")
    Run '38 Claude: commit on a detached HEAD' $push 2 -mustSay "'$nameTerm' in commit messages to push" -cwd $repo -hookFile $repoGuard
    & git.exe -C $repo checkout -q $branch
}
finally {
    if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
}

$results
$passed = @($results | Where-Object { $_ -like 'PASS*' }).Count
$skipped = @($results | Where-Object { $_ -like 'SKIP*' }).Count
"--- $passed of $($results.Count) passed" + $(if ($skipped) { ", $skipped skipped" })
if ($passed + $skipped -ne $results.Count) { exit 1 }
