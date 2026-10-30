[CmdletBinding()]
param([string]$WorkspaceRoot=(Split-Path -Parent $PSScriptRoot),[string]$CodexHome=(Join-Path $env:USERPROFILE '.codex'))
$ErrorActionPreference='Stop'
$taskMigrationStatePath=Join-Path $CodexHome 'control-center\native-session-migration\state.json'
if(-not (Test-Path -LiteralPath $taskMigrationStatePath -PathType Leaf)){return}
$taskMigrationState=Get-Content -LiteralPath $taskMigrationStatePath -Raw | ConvertFrom-Json
if(-not $taskMigrationState.reloadRequired){return}
# A warm launch must not imply that cached in-memory thread providers were replaced.
$taskExistingClients=@(Get-Process ChatGPT,Codex -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -match '\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$' } catch { $false }
})
if($taskExistingClients.Count -gt 0){return}
$taskMigrationScript=Join-Path $WorkspaceRoot 'adapters\native-sessions\session_migration.py'
$taskMigrationPython=(Get-Command python -ErrorAction Stop).Source
& $taskMigrationPython -X utf8 $taskMigrationScript --codex-home $CodexHome --finish-pending
if($LASTEXITCODE -ne 0){throw 'Native chat metadata synchronization could not complete. The original launcher remains available.'}
