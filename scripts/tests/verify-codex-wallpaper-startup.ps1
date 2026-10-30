$ErrorActionPreference='Stop'
$script:entry=Join-Path (Split-Path -Parent $PSScriptRoot) 'start-wallpaper-before-codex.ps1'
$tokens=$null;$syntaxErrors=$null
$null=[Management.Automation.Language.Parser]::ParseFile($script:entry,[ref]$tokens,[ref]$syntaxErrors)
if($syntaxErrors.Count){throw 'Wallpaper preparation syntax invalid'}
$global:wallpaperStartupTestCalls=[Collections.Generic.List[object]]::new()
$global:wallpaperStartupTestScenario='warm';$global:wallpaperStartupTestQueries=0
function Get-Process {
    param($Name,$ErrorAction)
    $global:wallpaperStartupTestQueries++
    if($global:wallpaperStartupTestScenario -eq 'warm' -or ($global:wallpaperStartupTestScenario -eq 'cold' -and $global:wallpaperStartupTestQueries -gt 1)){
        [pscustomobject]@{Path='C:\Program Files\WindowsApps\OpenAI.Codex_26.1027.3384.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe';MainWindowHandle=1}
    }
}
function Test-Path {param($LiteralPath,$PathType) return -not $LiteralPath.EndsWith('enhancement-mode\state.json')}
function Get-Content {param($LiteralPath,[switch]$Raw) return $(if($global:wallpaperStartupTestScenario -eq 'disabled'){'{"enabled":false}'}else{'{"enabled":true}'})}
function Start-Process {param($FilePath,$WindowStyle) $global:wallpaperStartupTestCalls.Add(@{path=$FilePath;style=$WindowStyle})}
function Start-Sleep {param($Milliseconds) [Threading.Thread]::Sleep(10)}
& $script:entry -WorkspaceRoot 'D:\fixture'
if($global:wallpaperStartupTestCalls.Count){throw 'Warm launch started another instance'}
$global:wallpaperStartupTestScenario='cold';$global:wallpaperStartupTestQueries=0
& $script:entry -WorkspaceRoot 'D:\fixture'
if($global:wallpaperStartupTestCalls.Count -ne 1 -or $global:wallpaperStartupTestCalls[0].style -ne 'Hidden'){throw 'Cold launch did not dispatch companion exactly once'}
$global:wallpaperStartupTestCalls.Clear();$global:wallpaperStartupTestScenario='disabled';$global:wallpaperStartupTestQueries=0
& $script:entry -WorkspaceRoot 'D:\fixture'
if($global:wallpaperStartupTestCalls.Count){throw 'Disabled wallpaper was started'}
$global:wallpaperStartupTestScenario='failure';$global:wallpaperStartupTestQueries=0;$watch=[Diagnostics.Stopwatch]::StartNew()
& $script:entry -WorkspaceRoot 'D:\fixture' -WaitSeconds 1
if($watch.Elapsed.TotalSeconds -gt 3 -or $global:wallpaperStartupTestCalls.Count -ne 1){throw 'Failed companion blocked normal app fallback'}
$launcher='C:\CodexControlCenter\router\Open-Codex.ps1'
$null=[Management.Automation.Language.Parser]::ParseFile($launcher,[ref]$tokens,[ref]$syntaxErrors)
if($syntaxErrors.Count){throw 'Desktop launcher syntax invalid'}
$text=[IO.File]::ReadAllText($launcher)
if(-not $text.Contains('if (-not $taskUsesNativeLogin)') -or -not $text.Contains('shell:AppsFolder\') -or $text.Contains("Start-Process -FilePath (Join-Path `$taskPackage.InstallLocation 'app\ChatGPT.exe')")){throw 'Native login or official activation fallback changed'}
[pscustomobject]@{success=$true;warmLaunchDoesNotStartCodex=$true;coldLaunchDispatchesOnce=$true;disabledWallpaperSkips=$true;failedCompanionBounded=$true;officialActivationFallback=$true;nativeLoginGuardPreserved=$true;realAppStarted=$false;mouseOrKeyboardControl=$false}|ConvertTo-Json
