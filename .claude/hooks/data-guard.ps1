# Blocks git commit and git push when what would be published contains internal terms.
# Terms: .claude/data-guard.local.txt (not in git), one term per line, # starts a comment.
# Exit 2 blocks the command and shows stderr. Any failure also blocks (fail closed).
# Only added lines of diffs count, so removing a term is not blocked. Git output and files are read as bytes
# and scanned as UTF-8, as windows-1251 and, when they have NUL bytes, as UTF-16, so a term matches in a
# file saved by PowerShell 5.1 (Set-Content writes the ANSI code page, > writes UTF-16).
#
# Claude Code mode (no -Mode): a PreToolUse hook. Stdin is the hook JSON, with tool_input.command
# (Bash or PowerShell tool) and cwd. When a command mentions git or gh together with commit or push,
# everything that could be published is scanned: the index, the working tree, untracked files, local
# commits and tags that are not on a remote, the author and committer. It does not see git aliases,
# scripts that run git indirectly, message files passed with -F, commits from other tools, or files that
# the same command creates before it stages them (cp ... && git add -A && git commit); pre-commit sees those.
# A command that changes to another folder or runs git -C in one blocks, because only the repository of
# its own folder is scanned. So does a command that turns off the git hooks (--no-verify, core.hooksPath).
# gh commands that publish text on GitHub (pr, issue, release, api, ...) are checked too, with the files
# they read the text from; no git hook sees them. A hook that runs out of time does not block, so the
# check gives up and blocks after 25 s, or when there are more than 100 MB of changes to read.
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

function TimeUp {
    Block "the check took longer than $($script:limit.TotalSeconds) s, the time this hook has. Run the commit or push from a terminal, where the git hooks check it without a time limit."
}

# Claude Code stops a hook at its timeout (30 s in .claude/settings.local.json) and then lets the command
# run, so in that mode the hook gives up first and blocks. The git hooks have no time limit.
function Remaining {
    if ($Mode) { return [regex]::InfiniteMatchTimeout }
    $left = $script:limit - $script:clock.Elapsed
    if ($left -le [TimeSpan]::Zero) { TimeUp }
    $left
}

# Quotes one argument for a Windows command line the way git.exe reads it back
function Quote([string]$arg) {
    if ($arg.Length -gt 0 -and $arg -notmatch '[\s"]') { return $arg }
    '"' + ($arg -replace '(\\*)"', '$1$1\"' -replace '(\\+)\z', '$1$1') + '"'
}

# Runs git in the repository and returns its output as bytes. PowerShell would decode the output with the
# console code page and split it on a lone CR as well, which loses the text after the CR in a diff line.
# Throws if git fails. Stderr is dropped, so git warnings stay out of the hook output.
# The error names only the git command: the other arguments can hold a push URL with a password.
function GitBytes {
    $all = @('-C', $script:root, '-c', 'core.quotePath=false', '-c', 'core.safecrlf=false', '-c', 'log.showRoot=true') + $args
    # The full path: given only a name, Windows would look in the current folder first
    $start = New-Object Diagnostics.ProcessStartInfo $script:gitExe, (($all | ForEach-Object { Quote ([string]$_) }) -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $git = [Diagnostics.Process]::Start($start)
    $stderr = $git.StandardError.ReadToEndAsync()
    $out = New-Object IO.MemoryStream
    $copy = $git.StandardOutput.BaseStream.CopyToAsync($out)
    if (-not $copy.Wait((Remaining))) {
        $git.Kill()
        TimeUp
    }
    $git.WaitForExit()
    $null = $stderr.Result
    if ($git.ExitCode -ne 0) { throw "git $($args[0]) failed with exit code $($git.ExitCode)" }
    # The comma keeps PowerShell from sending the array down the pipeline one byte at a time
    , $out.ToArray()
}

# Git output as UTF-8, for file names and other values that git writes in UTF-8
function GitText {
    [Text.Encoding]::UTF8.GetString((GitBytes @args)).TrimEnd("`n")
}

# Bytes as text for the scan: read as UTF-8; as windows-1251, the ANSI code page of Bulgarian Windows
# (Set-Content in PowerShell 5.1 writes it); and as UTF-16, little- and big-endian, at both byte offsets,
# because the lines cut out of a diff need not keep the pairs aligned. UTF-16 is tried when there are NUL
# bytes or bytes that are not UTF-8, such as its byte order mark. NUL characters are removed from the
# first two, so UTF-16 text in Latin letters matches there as well. Plain ASCII reads the same in all of
# them, so it is decoded only once.
function Decoded([byte[]]$bytes) {
    $utf8 = [Text.Encoding]::UTF8.GetString($bytes)
    $texts = @($utf8.Replace("`0", ''))
    if ([regex]::IsMatch($utf8, '[^\x00-\x7F]')) { $texts += $script:cp1251.GetString($bytes).Replace("`0", '') }
    if (($texts[0].Length -lt $utf8.Length -or $utf8.IndexOf([char]0xFFFD) -ge 0) -and $bytes.Length -gt 1) {
        foreach ($utf16 in [Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode) {
            $texts += $utf16.GetString($bytes)
            $texts += $utf16.GetString($bytes, 1, $bytes.Length - 1)
        }
    }
    $null = Remaining
    $texts -join "`n"
}

# Keeps the memory in bounds: about 100 MB of changes take 20 s to check, and GitHub refuses files over
# 100 MB anyway. Only the Claude hook, which has a time limit; the git hooks check any size.
function Budget([long]$bytes, [string]$what) {
    $script:scanned += $bytes
    if (-not $Mode -and $script:scanned -gt 100MB) {
        Block "with $what the changes to check are over 100 MB, more than this hook can check in its time limit. Run the commit or push from a terminal, where the git hooks check it."
    }
}

# Git output that is scanned, such as commit messages: it can hold bytes in any encoding
function GitScan {
    Decoded (GitBytes @args)
}

# What a diff adds: the added lines, including the +++ lines, and the names of added or changed files,
# because an empty file or a renamed one has no added lines. Colour, external diff tools, text
# conversion, binary detection and rename detection would change the format or hide content, so they are
# off; then a renamed file counts as added under its new name.
function Added([string[]]$diff) {
    $plain = '--no-color', '--no-ext-diff', '--no-textconv', '--text', '--no-renames'
    # Latin-1 maps each byte to one character, so lines can be cut out and turned back into the same bytes.
    # Only LF ends a line here. The + is removed, so the added lines of a UTF-16 file join up again.
    $bytes = GitBytes @diff -p @plain
    Budget $bytes.Length ('git ' + (($diff | Select-Object -First 2) -join ' '))
    $diffText = $script:latin1.GetString($bytes)
    $lines = [regex]::Replace($diffText, '(?m)^(?!\+)[^\n]*(\n|$)', '', 'None', (Remaining))
    $lines = [regex]::Replace($lines, '(?m)^\+', '', 'None', (Remaining))
    $names = GitText @diff --name-only --diff-filter=d @plain
    (Decoded $script:latin1.GetBytes($lines)) + "`n" + $names
}

# The author and committer of the next commit. git commit sets --author in the environment before pre-commit.
function Ident {
    (GitScan var GIT_AUTHOR_IDENT) + "`n" + (GitScan var GIT_COMMITTER_IDENT)
}

# Authors, messages and changes of the commits in a range of revisions
function Outgoing([string[]]$range) {
    $script:parts['commit messages to push'] = GitScan log '--format=%an <%ae>%n%cn <%ce>%n%B' @range
    $script:parts['commit changes to push'] = Added (@('log', '--cc', '--format=') + $range)
}

# Patterns for a call of git or gh in a command: the program, case-insensitive and with or without .exe or a
# path, then git's own options such as -C <folder> or -c <name>=<value>. The group is atomic, so a long run of
# options cannot make the match backtrack for ages. Subcommands and their flags stay case-sensitive.
$script:folderArg = '(?<folder>"[^"]*"|''[^'']*''|[^\s;&|)]+)'
$script:callStart = '(?:^|(?<=[\s;&|(){}"''`\\/]))'
$script:gitCall = $script:callStart + '(?i:git)(?:\.exe)?["'']?' +
    '(?<options>(?>(?:\s+-[cC]\s+(?:[^\s;&|"'']|"[^"]*"|''[^'']*'')+|\s+--?[A-Za-z][\w-]*(?:=\S*)?)*))\s+'
$script:ghCall = $script:callStart + '(?i:gh)(?:\.exe)?["'']?\s+'

# The command without the text it hands to git or gh, for the checks of its structure: words in that text,
# such as "git -C" or "cd", are not commands. Removed: heredoc and here-string bodies, and the values of
# -m, --message and gh's text flags. The flags and the heredoc's own line stay, so -nm still shows the n.
# PowerShell escapes a quote with a backtick or a second quote, bash with a backslash.
function Bare([string]$command, [string]$tool) {
    $bare = [regex]::Replace($command, '(?m)(<<-?[ \t]*([''"]?)(?>(\w+))\2[^\n]*\n)(?s:.*?)^\t*\3[ \t]*\r?$', '$1', 'None', (Remaining))
    $bare = [regex]::Replace($bare, '(?ms)@([''"])[ \t]*\r?\n.*?^\1@', '@$1$1@', 'None', (Remaining))
    $quoted = if ($tool -eq 'PowerShell') { '"(?:[^"`]|`.|"")*"|''(?:[^'']|'''')*''' } else { '"(?:[^"\\]|\\.)*"|''[^'']*''|\\.' }
    # A value is one shell word, which can join quoted and plain parts: 'the hook'\''s
    $flags = '(?<=\s)(-[A-Za-z]*m|--message|--title|--body|--notes)(=|\s+)'
    [regex]::Replace($bare, $flags + '(?:' + $quoted + '|[^\s;&|''"])+', '$1 ', 'None', (Remaining))
}

# Blocks when the command changes to another folder or runs git in one: the scan covers only the repository
# of the command's own folder. A folder that cannot be resolved here (a variable, ~, an expression) blocks too.
function CheckFolders([string]$bare, [string]$cwd) {
    if ($bare -match '\b(GIT_DIR|GIT_WORK_TREE|core\.worktree)\b|--(git-dir|work-tree)\b' -or
        $bare -cmatch '(?:^|[;&|(){}\r\n])\s*env\b[^;&|\r\n]*\s(-C|--chdir)\b') {
        Block "the command sets the repository with --git-dir, --work-tree, GIT_DIR, GIT_WORK_TREE, core.worktree or env -C, so this hook cannot tell which repository it changes. Run git from the repository's folder."
    }
    $names = @()
    # Every -C among git's own options
    foreach ($call in [regex]::Matches($bare, $script:gitCall)) {
        foreach ($c in [regex]::Matches($call.Groups['options'].Value, '\s-C\s+' + $script:folderArg)) { $names += $c.Groups['folder'].Value }
    }
    # A cd that starts a command: at the start, or after a separator, a brace, a quote, a backtick or a shell keyword
    $cd = '(?:^|[;&|(){}"''`\r\n])\s*(?:(?:if|then|else|elif|do|while|until|builtin|command|!)\s+)*' +
        '(?i:cd|chdir|pushd|sl|Set-Location|Push-Location)(?:\s+/d|\s+-(?:Literal)?Path)?\s+' + $script:folderArg
    foreach ($c in [regex]::Matches($bare, $cd)) { $names += $c.Groups['folder'].Value }

    foreach ($name in $names) {
        $null = Remaining
        $name = $name.Trim('"', "'")
        # A trailing separator would reach git as a quote: PowerShell 5.1 does not escape it
        if ($name.Length -gt 3) { $name = $name.TrimEnd('\', '/') }
        $same = $false
        if ($name -notmatch '^~|[$%`({]') {
            $folder = [IO.Path]::Combine($cwd, (WindowsPath $name))
            $top = & $script:gitExe -C $folder rev-parse --show-toplevel 2>$null
            $same = $LASTEXITCODE -eq 0 -and $top -and
                [IO.Path]::GetFullPath(([string]$top).Replace('/', '\')).TrimEnd('\') -eq [IO.Path]::GetFullPath($script:root.Replace('/', '\')).TrimEnd('\')
        }
        if (-not $same) {
            Block "the command commits or pushes in another folder ('$name'), and this hook checks only the repository of its own folder. Run git from that folder instead."
        }
        $script:folders += $folder
    }
}

# A Git Bash path as Windows sees it: /d/folder is D:\folder, /tmp is the temp folder, ~ is the profile
function WindowsPath([string]$name) {
    if ($name -match '^/([A-Za-z])(/|$)') { return $Matches[1] + ':' + $name.Substring(2) }
    if ($name -match '^/tmp(/|$)') { return [IO.Path]::GetTempPath() + $name.Substring(4).TrimStart('/') }
    if ($name -match '^~(/|\\|$)') { return $env:USERPROFILE + $name.Substring(1) }
    $name
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
    $script:clock = [Diagnostics.Stopwatch]::StartNew()
    # Below the hook's timeout of 30 s. DATA_GUARD_SECONDS lets the tests make it shorter, never longer.
    $seconds = 25.0
    if ($env:DATA_GUARD_SECONDS) { $seconds = [Math]::Min($seconds, [double]$env:DATA_GUARD_SECONDS) }
    $script:limit = [TimeSpan]::FromSeconds($seconds)
    [Console]::InputEncoding = [Text.Encoding]::UTF8
    [Console]::OutputEncoding = [Text.Encoding]::UTF8

    # The script lives in <project>\.claude\hooks; the terms file belongs to that project
    $script:project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:gitExe = (Get-Command git.exe -CommandType Application -ErrorAction Stop)[0].Source
    $script:latin1 = [Text.Encoding]::GetEncoding(28591)
    $script:cp1251 = [Text.Encoding]::GetEncoding(1251)
    $script:scanned = 0
    # Folders in the repository that the command changes to
    $script:folders = @()
    # Label -> text to scan. Ordinal: [ordered]@{} compares keys by culture, which ignores characters such
    # as a soft hyphen, so the text of one new file could replace another's.
    $script:parts = New-Object Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)

    if (-not $Mode) {
        $hook = [Console]::In.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
        $command = [string]$hook.tool_input.command
        # The checks below take time in proportion to the command
        if ($command.Length -gt 256KB -and $command -match '\b(git|gh)\b') {
            Block "the command is over 256 KB, more than this hook can check in its time limit. Put the text in a file and pass the file."
        }
        $bare = Bare $command ([string]$hook.tool_name)

        # Limited to the add command itself: 'git commit -F -' later on the line is not 'add -f'.
        # Short options can be bundled (-Af), and git takes any unambiguous prefix of a long option.
        $rest = '\b[^\r\n;&|]*\s'
        if ($bare -cmatch ($script:gitCall + '(add|stage)' + $rest + '(-[A-Za-z]*f[A-Za-z]*|--f(o(r(ce?)?)?)?)(?![\w-])')) {
            Block "git add -f stages ignored files, which are ignored on purpose. Change .gitignore instead."
        }
        if ($bare -cmatch ($script:gitCall + 'update-index' + $rest + '--add?(?![\w-])')) {
            Block "git update-index --add stages files without looking at .gitignore. Use git add instead."
        }
        # They turn off the git hooks, which back this hook up. Reading core.hooksPath, or setting it to
        # .githooks as CLAUDE.md says, is fine.
        $hooksOff = $bare -cmatch ($script:gitCall + '(commit|merge|cherry-pick|revert|rebase|am|push)' + $rest + '--no-veri(fy?)?(?![\w-])') -or
            $bare -cmatch ($script:gitCall + 'commit' + $rest + '-[A-Za-z]*n[A-Za-z]*(?![\w-])') -or
            $bare -match '(?i)-c\s+["'']?core\.hooksPath=(?!["'']?\.githooks(["'']|\s|$))|--config-env[= ]["'']?core\.hooksPath|\bGIT_CONFIG_(PARAMETERS|COUNT|KEY_\d+)\b'
        foreach ($config in [regex]::Matches($bare, $script:gitCall + 'config\b(?<args>[^\r\n;&|]*)')) {
            # Redirections such as 2>/dev/null are not values
            $configArgs = $config.Groups['args'].Value -replace '\s\d*>&?\s*\S*', ''
            if ($configArgs -match '(?i)\s--(remove|rename)-section\s+["'']?core\b') { $hooksOff = $true }
            if ($configArgs -match '(?i)core\.hooksPath' -and $configArgs -notmatch '\s(--get\S*|--list|-l)\b' -and
                ($configArgs -match '\s(--(unset|unset-all|add|replace-all|edit)|unset)\b' -or
                 $configArgs -match '(?i)core\.hooksPath\s+(?!["'']?\.githooks(["'']|\s|$))\S')) { $hooksOff = $true }
        }
        if ($hooksOff) {
            Block "the command turns off the git hooks (--no-verify, commit -n or core.hooksPath). Run it without that."
        }

        # gh commands that publish text on GitHub, which no git hook sees. The full command decides,
        # so a gh call on a heredoc's own line counts; a false match only costs a scan.
        $gh = [regex]::IsMatch($command, $script:ghCall + '(pr|issue|release|gist|api|repo|label|discussion|workflow|project)\b', 'None', (Remaining))
        if (-not ($gh -or ($command -match '\b(git|gh)\b' -and $command -match '\b(commit|push)\b'))) { exit 0 }

        # Scan the repository the command runs in (a worktree, for example); fall back to the project
        $cwd = if ($hook.cwd) { [string]$hook.cwd } else { $script:project }
        $script:root = $script:project
        $top = & $script:gitExe -C $cwd rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and $top) { $script:root = [string]$top }
        CheckFolders $bare $cwd

        $parts['the command'] = $command
        if ($gh) {
            # Text from a variable or a command substitution cannot be seen here, and a wildcard names files
            # that only the shell knows. Single quotes and heredocs with a quoted delimiter expand nothing.
            $expanding = [regex]::Replace($command, '(?m)(<<-?[ \t]*([''"])(?>(\w+))\2[^\n]*\n)(?s:.*?)^\t*\3[ \t]*\r?$', '$1', 'None', (Remaining))
            $expanding = [regex]::Replace($expanding, '(?ms)@''[ \t]*\r?\n.*?^''@|''[^'']*''', "''", 'None', (Remaining))
            $substitution = if ($hook.tool_name -eq 'PowerShell') { '\$\(|\$\{|\$[A-Za-z_]' } else { '\$\(|\$\{|\$[A-Za-z_]|`|<\(' }
            if ($expanding -match $substitution) {
                Block "the gh command takes text from a variable or a command substitution, which this hook cannot read. Put the text in the command, or in a file that the command names."
            }
            if ($bare -match '(?:^|\s)[^\s"'']*[*?][^\s"'']*') {
                Block "the gh command has a wildcard, so this hook cannot tell which files it publishes. Name the files."
            }
            # gh can publish any file the command names: --body-file, -F, a gist or a release asset, key=@file
            # for gh api, a < redirection, or a file piped in. Every word, and every part of it between quotes,
            # = or @, is tried as a file name in the command's folder and in each folder it changes to.
            # Text that the same command writes first is in the command itself.
            $words = [regex]::Matches($command, '"[^"]*"|''[^'']*''|[^\s;&|<>()]+', 'None', (Remaining)) | ForEach-Object { $_.Value }
            $names = @($words | ForEach-Object { $_.Trim('"', "'"); $_ -split '[\s"''=@]+' }) | ForEach-Object { $_ -replace '^-F(?=.)', '' } |
                Where-Object { $_ -and $_ -notmatch '^-' } | Select-Object -Unique
            foreach ($name in $names) {
                foreach ($folder in @($cwd) + $script:folders) {
                    try { $path = [IO.Path]::Combine($folder, (WindowsPath $name)) } catch { continue }
                    $label = "the file '$name' named in the gh command"
                    if (-not [IO.File]::Exists($path) -or $parts.Contains($label)) { continue }
                    $length = (New-Object IO.FileInfo $path).Length
                    if ($length -gt 5MB) { Block "the file '$name' in the gh command is over 5 MB, so it is not read here." }
                    Budget $length "the file '$name'"
                    $parts[$label] = Decoded ([IO.File]::ReadAllBytes($path))
                }
            }
        }
        $parts['author or committer'] = Ident
        $parts['the index'] = Added @('diff', '--cached')
        # Against the index, not HEAD: what is staged is in the line above, and a big staged file is read once
        $parts['the working tree'] = Added @('diff')
        # In a merge conflict, git diff leaves out the lines a file takes from the other side
        if (GitText ls-files -u) { $parts['the working tree against HEAD'] = Added @('diff', 'HEAD') }

        $untracked = (GitText ls-files -z --others --exclude-standard) -split "`0" | Where-Object { $_ }
        foreach ($name in $untracked) {
            # A normal Windows path drops a trailing dot or space, so x.md. can stand for x.md. Git refuses
            # such names by default; with that check off it might publish another file's content for them.
            if ($name -match '[. ](/|$)') {
                Block "the new file '$name' has a name that ends with a dot or a space, so it may stand for another file. Rename it, then commit or push again."
            }
            # A \\?\ path opens exactly the file git lists, also a hidden one or one with a long path
            $full = ($script:root.TrimEnd('\', '/') + '\' + $name).Replace('/', '\')
            $path = if ($full.StartsWith('\\')) { '\\?\UNC\' + $full.Substring(2) } else { '\\?\' + $full }
            # Git publishes a symlink as the path of its target, so that path is scanned, not the target's content
            if (([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) -and
                ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)) {
                $link = Get-Item -LiteralPath $full -Force -ErrorAction Stop
                if ($link.LinkType -eq 'SymbolicLink') {
                    $target = @($link.Target) -join "`n"
                    $parts["new file $name"] = $name + "`n" + $target + "`n" + $target.Replace('\', '/')
                    continue
                }
            }
            # A folder (a nested repository), a file that is gone, or a name decoded wrongly
            if (-not [IO.File]::Exists($path)) {
                Block "cannot read the new file '$name', so its content is not checked. Remove it or add it to .gitignore, then commit or push again."
            }
            $length = (New-Object IO.FileInfo $path).Length
            if ($length -gt 5MB) {
                Block "the new file '$name' is over 5 MB, so it is not read here. Stage it with git add first, then commit: staged files are checked through the diff."
            }
            Budget $length "the new file '$name'"
            # Binary formats are read as text and rarely match
            $parts["new file $name"] = $name + "`n" + (Decoded ([IO.File]::ReadAllBytes($path)))
        }

        # Local commits and tags that are not on any remote yet, and a detached HEAD
        $range = @('--branches', '--tags')
        $null = & $script:gitExe -C $script:root rev-parse --verify --quiet HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { $range += 'HEAD' }
        Outgoing ($range + @('--not', '--remotes'))
        $parts['tag messages'] = GitScan for-each-ref refs/tags '--format=%(contents)'
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
                $message = $script:latin1.GetString([IO.File]::ReadAllBytes([IO.Path]::Combine($script:root, $HookArg)))
                $message = [regex]::Split($message, '(?m)^\S -{24} >8 -{24}\r?$')[0]
                $parts['the commit message'] = Decoded $script:latin1.GetBytes($message)
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
                    $parts['tag messages to push'] = ($tags | ForEach-Object { GitScan cat-file tag $_ }) -join "`n"
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
            if ([regex]::IsMatch([string]$parts[$label], $pattern, 'IgnoreCase', (Remaining))) { $hits.Add("'$term' in $label") }
        }
    }
    if ($hits.Count -gt 0) {
        Block ("found internal terms: " + ($hits -join '; ') + ". Remove them, then commit or push again.")
    }
    exit 0
}
catch {
    if ($_.Exception.GetBaseException() -is [Text.RegularExpressions.RegexMatchTimeoutException]) { TimeUp }
    Block "check failed: $($_.Exception.Message)"
}
