# Blocks git commit and git push when what would be published contains internal terms.
# Terms: .claude/data-guard.local.txt (not in git), one term per line, # starts a comment.
# Exit 2 blocks the command and shows stderr. Any failure also blocks (fail closed).
# Only added lines of diffs count, so removing a term is not blocked. Binary files are scanned as text
# without NUL characters, so UTF-16 text (PowerShell 5.1 writes it with >) matches for terms in Latin letters.
#
# Claude Code mode (no -Mode): a PreToolUse hook. Stdin is the hook JSON, with tool_input.command
# (Bash or PowerShell tool) and cwd. When a command mentions git or gh together with commit or push,
# everything that could be published is scanned: the index, the working tree, untracked files, local
# commits and tags that are not on a remote, the author and committer. It does not see git aliases,
# scripts that run git indirectly, message files passed with -F, or commits from other tools.
#
# Git mode: the hooks in .githooks call it for commits from any tool, once a clone enables them with
# git config core.hooksPath .githooks. git commit --no-verify skips them, and so do a checkout without
# .githooks (one from before they were added) and commits made by cherry-pick, rebase or merge.
# pre-push is the last gate: it checks every pushed commit, however it was made.
#   -Mode pre-commit           the index (what the commit will contain), the author and committer
#   -Mode commit-msg <file>    the commit message, which pre-commit does not see
#   -Mode pre-push <remote>    the pushed ref names, and the commits and tags the remote does not have

param(
    [string]$Mode,
    # commit-msg: the message file; pre-push: the remote name or URL
    [string]$HookArg
)

function Block([string]$message) {
    [Console]::Error.WriteLine("data-guard: $message")
    exit 2
}

# Runs git in the repository and throws if git fails. Stderr is dropped, so git warnings stay out of the hook output.
# The error names only the git command: the other arguments can hold a push URL with a password.
# Not named Git: function names are case-insensitive and win over git.exe, so it would call itself.
function GitText {
    $out = & git.exe -C $script:root -c core.quotePath=false -c core.safecrlf=false -c log.showRoot=true @args 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git $($args[0]) failed with exit code $LASTEXITCODE" }
    $out -join "`n"
}

# What a diff adds: the added lines, including the +++ lines, and the names of added or changed files,
# because an empty file or a renamed one has no added lines. Colour, external diff tools, text
# conversion, binary detection and rename detection would change the format or hide content, so they are
# off; then a renamed file counts as added under its new name.
function Added([string[]]$diff) {
    $plain = '--no-color', '--no-ext-diff', '--no-textconv', '--text', '--no-renames'
    $lines = ((GitText @diff -p @plain) -replace "`0", '') -split "`n" | Where-Object { $_.StartsWith('+') }
    $names = GitText @diff --name-only --diff-filter=d @plain
    (@($lines) + $names) -join "`n"
}

# The author and committer of the next commit. git commit sets --author in the environment before pre-commit.
function Ident {
    (GitText var GIT_AUTHOR_IDENT) + "`n" + (GitText var GIT_COMMITTER_IDENT)
}

# Authors, messages and changes of the commits in a range of revisions
function Outgoing([string[]]$range) {
    $script:parts['commit messages to push'] = GitText log '--format=%an <%ae>%n%cn <%ce>%n%B' @range
    $script:parts['commit changes to push'] = Added (@('log', '--cc', '--format=') + $range)
}

# DATA_GUARD_TERMS_FILE lets data-guard.tests.ps1 use made-up terms instead of the real ones.
# A linked worktree has no copy of the ignored terms file, so the one in the main working tree is used.
function ReadTerms {
    $file = $env:DATA_GUARD_TERMS_FILE
    if (-not $file) {
        $file = Join-Path $script:project '.claude\data-guard.local.txt'
        if (-not (Test-Path -LiteralPath $file)) {
            $commonDir = GitText rev-parse --path-format=absolute --git-common-dir
            $file = Join-Path (Split-Path $commonDir -Parent) '.claude\data-guard.local.txt'
        }
    }
    if (-not (Test-Path -LiteralPath $file)) {
        Block "missing $file. Create it with one internal term per line."
    }
    $terms = @(Get-Content -LiteralPath $file -Encoding UTF8 -ErrorAction Stop |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($terms.Count -eq 0) { Block "$file has no terms." }
    $terms
}

try {
    [Console]::InputEncoding = [Text.Encoding]::UTF8
    [Console]::OutputEncoding = [Text.Encoding]::UTF8

    # The script lives in <project>\.claude\hooks; the terms file belongs to that project
    $script:project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    # Label -> text to scan
    $script:parts = [ordered]@{}

    if (-not $Mode) {
        $hook = [Console]::In.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
        $command = [string]$hook.tool_input.command

        # Case-sensitive and limited to the add command itself: 'git commit -F -' later on the line is not 'add -f'
        if ($command -cmatch '\bgit\s+add\b[^\r\n;&|]*\s(-f|--force)\b') {
            Block "git add -f stages ignored files, which are ignored on purpose. Change .gitignore instead."
        }
        if (-not ($command -match '\b(git|gh)\b' -and $command -match '\b(commit|push)\b')) { exit 0 }

        # Scan the repository the command runs in (a worktree, for example); fall back to the project
        $script:root = $script:project
        if ($hook.cwd) {
            $top = & git.exe -C ([string]$hook.cwd) rev-parse --show-toplevel 2>$null
            if ($LASTEXITCODE -eq 0 -and $top) { $script:root = [string]$top }
        }

        $parts['the command'] = $command
        $parts['author or committer'] = Ident
        $parts['the index'] = Added @('diff', '--cached')
        $null = & git.exe -C $script:root rev-parse --verify --quiet HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { $parts['the working tree'] = Added @('diff', 'HEAD') }

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
        Outgoing @('--branches', '--tags', '--not', '--remotes')
        $parts['tag messages'] = GitText for-each-ref refs/tags '--format=%(contents)'
    }
    else {
        # Git runs hooks in the root of the working tree
        $script:root = (Get-Location).ProviderPath
        switch ($Mode) {
            'pre-commit' {
                # For git commit -a or a commit of given paths, git points GIT_INDEX_FILE at a temporary index
                $parts['author or committer'] = Ident
                $parts['the index'] = Added @('diff', '--cached')
            }
            'commit-msg' {
                # With commit -v the diff follows a scissors line, and git drops it from the message.
                # Comment lines are scanned: git keeps them in a message given with -m.
                $message = [IO.File]::ReadAllText([IO.Path]::Combine($script:root, $HookArg))
                $parts['the commit message'] = [regex]::Split($message, '(?m)^\S -{24} >8 -{24}\r?$')[0]
            }
            'pre-push' {
                # Stdin: one line per pushed ref, '<local ref> <local sha> <remote ref> <remote sha>'
                $lines = @([Console]::In.ReadToEnd() -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                $parts['the pushed ref names'] = ($lines | ForEach-Object { ($_ -split ' ')[0, 2] -join "`n" }) -join "`n"
                # A zero sha deletes the remote ref, so there is nothing to scan
                $tips = @($lines | ForEach-Object { ($_ -split ' ')[1] } | Where-Object { $_ -notmatch '^0+$' })
                if ($tips.Count -gt 0) {
                    # Commits on the remote's tracking branches are already published. A push to a URL
                    # matches no tracking branches, so the whole history of the pushed refs is scanned.
                    Outgoing ($tips + @('--not', "--remotes=$HookArg"))
                    $tags = $tips | Where-Object { (GitText cat-file -t $_) -eq 'tag' }
                    $parts['tag messages to push'] = ($tags | ForEach-Object { GitText cat-file tag $_ }) -join "`n"
                }
            }
            default { Block "unknown mode '$Mode'." }
        }
    }

    $terms = ReadTerms
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
