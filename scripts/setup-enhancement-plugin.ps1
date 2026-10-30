[CmdletBinding()]
param([string]$CodexHome=(Join-Path $env:USERPROFILE '.codex'),[string]$WorkspaceRoot=(Split-Path -Parent $PSScriptRoot),[string]$StateHome=(Join-Path $env:LOCALAPPDATA 'CodexControlCenter'),[switch]$GenerateOnly)
$ErrorActionPreference='Stop'
$pythonExe=(Get-Command python -ErrorAction Stop).Source
$source=Join-Path $WorkspaceRoot 'plugins'
$market=Join-Path $StateHome 'plugin-source'
New-Item -ItemType Directory -Path $market -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $source '.agents') -Destination $market -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source 'codex-enhancements') -Destination $market -Recurse -Force
$pluginRoot=Join-Path $market 'codex-enhancements'
$adapter=Join-Path $WorkspaceRoot 'adapters\enhancement-mode\enhancement_mode.py'
if(-not (Test-Path -LiteralPath $adapter -PathType Leaf)){throw 'Missing mode controller'}
$scripts=Join-Path $pluginRoot 'scripts';New-Item -ItemType Directory -Path $scripts -Force|Out-Null
Copy-Item -LiteralPath $adapter -Destination (Join-Path $scripts 'enhancement_mode.py') -Force
$mcp=@{mcpServers=@{enhancements=@{command=$pythonExe;args=@('-X','utf8',(Join-Path $scripts 'enhancement_mode.py'),'serve','--codex-home',$CodexHome,'--workspace',$WorkspaceRoot,'--state-home',$StateHome)}}}
[IO.File]::WriteAllText((Join-Path $pluginRoot '.mcp.json'),($mcp|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$hookCommand='"'+$pythonExe+'" -X utf8 "'+$adapter+'" hook --codex-home "'+$CodexHome+'" --workspace "'+$WorkspaceRoot+'" --state-home "'+$StateHome+'"'
$hooks=@{hooks=@{SessionStart=@(@{hooks=@(@{type='command';command=$hookCommand;commandWindows=$hookCommand;timeout=15;statusMessage='Codex 增强包 · 同步增强模式'})})}}
$hooksPath=Join-Path $pluginRoot 'hooks';New-Item -ItemType Directory -Path $hooksPath -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $hooksPath 'hooks.json'),($hooks|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
if($GenerateOnly){Write-Output 'Generated local manifests only.';return}
$codexExe=(Get-Command codex -ErrorAction Stop).Source
$previousCodexHome=[Environment]::GetEnvironmentVariable('CODEX_HOME','Process')
try{
 [Environment]::SetEnvironmentVariable('CODEX_HOME',$CodexHome,'Process')
 & $codexExe plugin marketplace add $market --json
 if($LASTEXITCODE -ne 0){throw 'Local marketplace registration failed'}
 & $codexExe plugin add 'codex-enhancements@codex-control-center' --json
 if($LASTEXITCODE -ne 0){throw 'Native plugin installation failed'}
}finally{[Environment]::SetEnvironmentVariable('CODEX_HOME',$previousCodexHome,'Process')}
