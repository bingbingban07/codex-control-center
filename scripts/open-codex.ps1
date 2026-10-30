[CmdletBinding()]
param([string]$ApplicationRoot=(Split-Path -Parent $PSScriptRoot),[string]$CodexHome=$(if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}))
$ErrorActionPreference='Stop'
function Open-OfficialCodex {
 $package=Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop|Select-Object -First 1
 if(-not $package){Start-Process 'codex:///';return}
 [xml]$manifest=Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -Raw
 $entry=@($manifest.Package.Applications.Application)|Where-Object {$_.Executable -match '(ChatGPT|Codex)\.exe$'}|Select-Object -First 1
 if(-not $entry){throw 'Codex application entry was not found.'}
 Start-Process ('shell:AppsFolder\'+$package.PackageFamilyName+'!'+$entry.Id)
}
try{
 $controller=Join-Path $ApplicationRoot 'adapters\enhancement-mode\enhancement_mode.py'
 $status=(& python -X utf8 $controller reconcile --codex-home $CodexHome --workspace $ApplicationRoot|ConvertFrom-Json)
 if(-not $status.success){throw 'Enhancement mode reconciliation failed.'}
 if($status.mode -eq 'enhanced'){
  $routing=Join-Path $CodexHome 'control-center\unified-routing.json'
  if(Test-Path -LiteralPath $routing){$profile=Get-Content -LiteralPath $routing -Raw|ConvertFrom-Json;if($profile.enabled){& (Join-Path $ApplicationRoot 'adapters\router-observability\start-managed-router.ps1') -RouterHome (Join-Path $CodexHome 'model-router-sandbox') -CodexHome $CodexHome}}
  $wallpaper=Join-Path $env:LOCALAPPDATA 'CodexControlCenter\wallpaper\wallpaper.json'
  if((Test-Path -LiteralPath $wallpaper) -and (Get-Content -LiteralPath $wallpaper -Raw|ConvertFrom-Json).enabled){
   $helper=Join-Path $ApplicationRoot 'helpers\wallpaper\CodexControlCenter.WallpaperHost.exe'
   if(Test-Path -LiteralPath $helper){Start-Process -FilePath $helper -ArgumentList '--launch' -WindowStyle Hidden;return}
  }
 }
}catch{Write-Warning 'Enhancement startup was unavailable; opening Codex through its official entry.'}
Open-OfficialCodex
