# PreToolUse hook: blocks git commit and git push when pending changes contain internal terms.
# Input (stdin): the hook JSON from Claude Code, with tool_input.command (Bash or PowerShell tool) and cwd.
# Terms: .claude/data-guard.local.txt (not in git), one term per line, # starts a comment.
# Exit 2 blocks the command and shows stderr to Claude. Any failure also blocks (fail closed).
#
# When a command mentions git or gh together with commit or push, everything that could be published
# is scanned: the index, the working tree, untracked files, local commits and tags that are not on a
# remote, the author and committer. Only added lines of diffs count, so removing a term is not blocked.
# Not covered: commits made outside Claude's Bash and PowerShell tools (the app's buttons, other
# terminals), git aliases and scripts that run git indirectly, message files passed with -F.

[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Block([string]$message) {
    [Console]::Error.WriteLine("data-guard: $message")
    exit 2
}

# Runs git in the repository and throws if git fails. Stderr is dropped, so git warnings stay out of the hook output.
# Not named Git: function names are case-insensitive and win over git.exe, so it would call itself.
function GitText {
    $out = & git.exe -C $script:root -c core.quotePath=false -c core.safecrlf=false @args 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
    $out -join "`n"
}

# Added lines of a diff, including the +++ lines with new file names
function Added([string]$diff) {
    ($diff -split "`n" | Where-Object { $_.StartsWith('+') }) -join "`n"
}

try {
    $hook = [Console]::In.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
    $command = [string]$hook.tool_input.command

    # Case-sensitive and limited to the add command itself: 'git commit -F -' later on the line is not 'add -f'
    if ($command -cmatch '\bgit\s+add\b[^\r\n;&|]*\s(-f|--force)\b') {
        Block "git add -f stages ignored files, which are ignored on purpose. Change .gitignore instead."
    }
    if (-not ($command -match '\b(git|gh)\b' -and $command -match '\b(commit|push)\b')) { exit 0 }

    # The script lives in <project>\.claude\hooks; the terms file belongs to that project.
    # DATA_GUARD_TERMS_FILE lets data-guard.tests.ps1 use made-up terms instead of the real ones.
    $project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $termsFile = if ($env:DATA_GUARD_TERMS_FILE) { $env:DATA_GUARD_TERMS_FILE } else { Join-Path $project '.claude\data-guard.local.txt' }
    if (-not (Test-Path -LiteralPath $termsFile)) {
        Block "missing $termsFile. Create it with one internal term per line."
    }
    $terms = @(Get-Content -LiteralPath $termsFile -Encoding UTF8 -ErrorAction Stop |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($terms.Count -eq 0) { Block "$termsFile has no terms." }

    # Scan the repository the command runs in (a worktree, for example); fall back to the project
    $script:root = $project
    if ($hook.cwd) {
        $top = & git.exe -C ([string]$hook.cwd) rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and $top) { $script:root = [string]$top }
    }

    # Label -> text to scan
    $parts = [ordered]@{}
    $parts['the command'] = $command
    $parts['author or committer'] = (GitText var GIT_AUTHOR_IDENT) + "`n" + (GitText var GIT_COMMITTER_IDENT)
    $parts['the index'] = Added (GitText diff --cached)
    $null = & git.exe -C $script:root rev-parse --verify --quiet HEAD 2>$null
    if ($LASTEXITCODE -eq 0) { $parts['the working tree'] = Added (GitText diff HEAD) }

    $untracked = (GitText ls-files -z --others --exclude-standard) -split "`0" | Where-Object { $_ }
    foreach ($name in $untracked) {
        $path = Join-Path $script:root $name
        $text = $name
        $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        # Large files are not read; binary formats are read as text and rarely match
        if ($item -and $item.Length -le 5MB) { $text += "`n" + [IO.File]::ReadAllText($item.FullName) }
        $parts["new file $name"] = $text
    }

    # Local commits and tags that are not on any remote yet
    $outgoing = @('--branches', '--tags', '--not', '--remotes')
    $parts['commit messages to push'] = GitText log '--format=%an <%ae>%n%cn <%ce>%n%B' @outgoing
    $parts['commit changes to push'] = Added (GitText log -p --cc '--format=' @outgoing)
    $parts['tag messages'] = GitText for-each-ref refs/tags '--format=%(contents)'

    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($term in $terms) {
        $escaped = [regex]::Escape($term)
        # A plain word must not touch other letters, so a short term does not match inside a longer word.
        # Digits and _ are boundaries on purpose: a system name followed by _ or digits must still match.
        $pattern = if ($term -match '^[\p{L}\p{N}_]+$') { "(?<!\p{L})$escaped(?!\p{L})" } else { $escaped }
        foreach ($label in $parts.Keys) {
            if ([regex]::IsMatch([string]$parts[$label], $pattern, 'IgnoreCase')) { $hits.Add("'$term' in $label") }
        }
    }
    if ($hits.Count -gt 0) {
        Block ("found internal terms: " + ($hits -join '; ') + ". Remove them, then commit or push again.")
    }
    exit 0
}
catch {
    Block "check failed: $($_.Exception.Message)"
}
