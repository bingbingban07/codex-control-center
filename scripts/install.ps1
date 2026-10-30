[CmdletBinding()]
param(
 [string]$CodexHome=$(if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}),
 [string]$StateHome=(Join-Path $env:LOCALAPPDATA 'CodexControlCenter'),
 [string]$ApplicationRoot=(Split-Path -Parent $PSScriptRoot),
 [switch]$SkipPluginRegistration,[switch]$NoShortcuts
)
$ErrorActionPreference='Stop'
$app=[IO.Path]::GetFullPath($ApplicationRoot)
$python=(Get-Command python -ErrorAction Stop).Source
& $python -c "import sys; assert sys.version_info >= (3,11), 'Python 3.11+ required'"
if($LASTEXITCODE -ne 0){throw 'Python 3.11 or later is required.'}
$node=Get-Command node -ErrorAction SilentlyContinue
$argsList=@('-X','utf8',(Join-Path $PSScriptRoot 'initialize-defaults.py'),'--codex-home',$CodexHome,'--state-home',$StateHome,'--app-root',$app)
if($node){$argsList+=@('--node',$node.Source)}
& $python @argsList
if($LASTEXITCODE -ne 0){throw 'Default initialization failed; no existing settings were overwritten.'}
$runtimeInstaller=Join-Path $app 'adapters\router-observability\install-router-runtime.ps1'
if(-not(Test-Path -LiteralPath $runtimeInstaller)){$runtimeInstaller=Join-Path $PSScriptRoot 'install-router-runtime.ps1'}
& $runtimeInstaller -RouterHome (Join-Path $CodexHome 'model-router-sandbox') -CodexHome $CodexHome
if(-not $SkipPluginRegistration){
 & (Join-Path $PSScriptRoot 'setup-enhancement-plugin.ps1') -CodexHome $CodexHome -WorkspaceRoot $app -StateHome $StateHome
 if($LASTEXITCODE -ne 0){throw 'Plugin registration failed.'}
}
if(-not $NoShortcuts){
 $exe=Join-Path $app 'CodexControlCenter.App.exe'
 if(-not(Test-Path -LiteralPath $exe)){throw 'Run the installer from the extracted portable application directory.'}
 $shell=New-Object -ComObject WScript.Shell
 $desktop=[Environment]::GetFolderPath('DesktopDirectory')
 $console=$shell.CreateShortcut((Join-Path $desktop 'Codex 管理台.lnk'));$console.TargetPath=$exe;$console.WorkingDirectory=$app;$console.IconLocation=(Join-Path $app 'assets\codex-control-center.ico')+',0';$console.Save()
 # A dedicated managed entry does not replace the official Codex shortcut.
 $launch=$shell.CreateShortcut((Join-Path $desktop 'Codex 增强版.lnk'));$launch.TargetPath=(Get-Command powershell.exe).Source
 $entry=Join-Path $app 'scripts\open-codex.ps1'
 $launch.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$entry+'" -ApplicationRoot "'+$app+'"'
 $launch.WorkingDirectory=$app;$launch.IconLocation=(Join-Path $app 'assets\codex-control-center.ico')+',0';$launch.Save()
}
Write-Output 'Installed with native sign-in, empty providers/models and disabled agent policy. Open Codex Control Center to configure enhancements.'
