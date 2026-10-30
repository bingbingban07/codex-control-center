[CmdletBinding()]
param([string]$CodexHome=$(if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}),[string]$ApplicationRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath (Join-Path $CodexHome 'models_cache.json'))){throw 'Open Codex, sign in and load its model list before enabling unified routing.'}
$node=Get-Command node -ErrorAction Stop
$router=Join-Path $CodexHome 'model-router-sandbox'
& python -X utf8 (Join-Path $PSScriptRoot 'initialize-defaults.py') --codex-home $CodexHome --state-home (Join-Path $env:LOCALAPPDATA 'CodexControlCenter') --app-root $ApplicationRoot --node $node.Source
if($LASTEXITCODE -ne 0){throw 'Routing initialization failed.'}
& python -X utf8 (Join-Path $ApplicationRoot 'adapters\enhancement-mode\configure_unified_models.py') --codex-home $CodexHome --workspace $ApplicationRoot --apply
if($LASTEXITCODE -ne 0){throw 'Unified model metadata setup failed.'}
$localToken=[IO.File]::ReadAllText((Join-Path $router 'router.token')).Trim()
[Environment]::SetEnvironmentVariable('CODEX_CONTROL_CENTER_ROUTER_TOKEN',$localToken,'User')
[Environment]::SetEnvironmentVariable('CODEX_CONTROL_CENTER_ROUTER_TOKEN',$localToken,'Process')
$localToken=$null
& (Join-Path $ApplicationRoot 'adapters\router-observability\start-managed-router.ps1') -RouterHome $router -CodexHome $CodexHome
& python -X utf8 (Join-Path $ApplicationRoot 'adapters\enhancement-mode\enhancement_mode.py') enhanced --codex-home $CodexHome --workspace $ApplicationRoot
Write-Output 'Unified routing is configured. Review your settings and normally reopen Codex.'
