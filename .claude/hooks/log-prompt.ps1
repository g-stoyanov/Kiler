# UserPromptSubmit hook: appends every submitted prompt to course/prompts.jsonl.
# Input (stdin): the hook JSON from Claude Code, with session_id and prompt.
$ErrorActionPreference = 'Stop'

[Console]::InputEncoding = [Text.Encoding]::UTF8
$hook = [Console]::In.ReadToEnd() | ConvertFrom-Json

$entry = [ordered]@{
    ts      = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
    session = $hook.session_id
    prompt  = $hook.prompt
}

# The script lives in <project>\.claude\hooks
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$log = Join-Path $project 'course\prompts.jsonl'

$utf8NoBom = New-Object Text.UTF8Encoding $false
[IO.File]::AppendAllText($log, ($entry | ConvertTo-Json -Compress) + "`n", $utf8NoBom)
