[CmdletBinding()]
param([string]$WorkspaceRoot=(Split-Path -Parent $PSScriptRoot),[ValidateRange(1,20)][int]$WaitSeconds=10)
$ErrorActionPreference='Stop'
$modeHome=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}
$modeFile=Join-Path $modeHome 'control-center\enhancement-mode\state.json'
if(Test-Path -LiteralPath $modeFile){
 $modeState=Get-Content -LiteralPath $modeFile -Raw|ConvertFrom-Json
 if($modeState.mode -ne 'enhanced'){return}
}
function Get-VisibleCodex {
    @(Get-Process ChatGPT,Codex -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -match '\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$' -and $_.MainWindowHandle -ne 0 } catch { $false }
    })
}
# Warm launches keep the existing visible app. The new host never restarts it.
$running=@(Get-Process ChatGPT,Codex -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -match '\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$' } catch { $false }
})
if($running.Count -gt 0){return}
$statePath=Join-Path $env:LOCALAPPDATA 'CodexControlCenter\wallpaper\wallpaper.json'
if(-not (Test-Path -LiteralPath $statePath -PathType Leaf)){return}
$settings=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if(-not $settings.enabled){return}
$launcher=Join-Path $WorkspaceRoot '启动Codex壁纸.cmd'
if(-not (Test-Path -LiteralPath $launcher -PathType Leaf)){return}
Start-Process -FilePath $launcher -WindowStyle Hidden | Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds($WaitSeconds)
while([DateTime]::UtcNow -lt $deadline){
    if((Get-VisibleCodex).Count -gt 0){return}
    Start-Sleep -Milliseconds 150
}
# Return to the ordinary animation/system launcher even when wallpaper startup failed.
# Never hide or kill a Codex process, and never block the official startup indefinitely.
