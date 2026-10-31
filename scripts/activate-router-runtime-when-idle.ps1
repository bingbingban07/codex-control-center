param([Parameter(Mandatory=$true)][string]$RouterRoot,[Parameter(Mandatory=$true)][int]$ExpectedPid,
 [ValidateRange(100,5000)][int]$IdleConfirmMilliseconds=500,
 [ValidateSet('credential_status','custom_tool_compat','tool_definition_guard')][string]$RequiredFeature='credential_status',
 [ValidateRange(2,100)][int]$RequiredGuardRevision=6,
 [string]$ReportPath='')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$activationReportPath=if($ReportPath){[IO.Path]::GetFullPath($ReportPath)}else{Join-Path $workspace 'verification\v0.5.0-release\runtime-activation.json'}
$physicalRoot=[IO.Path]::GetFullPath($RouterRoot)
$mutex=[Threading.Mutex]::new($false,'Local\CodexControlCenterIdleRuntimeActivationV1')
if(-not $mutex.WaitOne(0)){exit 0}
function Report([string]$state,$count,$ready){
 $value=[ordered]@{state=$state;helper_pid=$PID;expected_router_pid=$ExpectedPid;active_requests=$count;credential_isolation_active=($ready -and $RequiredFeature -eq 'credential_status');required_feature=$RequiredFeature;feature_active=$ready;timestamp_utc=[DateTime]::UtcNow.ToString('o')}
 [IO.Directory]::CreateDirectory((Split-Path -Parent $activationReportPath))|Out-Null
 $temporary=$activationReportPath+'.tmp'
 [IO.File]::WriteAllText($temporary,($value|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
 [IO.File]::Move($temporary,$activationReportPath,$true)
}
function FeatureReady($health){
 if(-not $health -or $health.runtime.reload_error){return $false}
 if($RequiredFeature -eq 'custom_tool_compat'){return $health.features.custom_tool_compat -eq $true}
 if($RequiredFeature -eq 'tool_definition_guard'){return $health.features.tool_definition_guard -eq $true -and $health.features.tool_definition_guard_revision -ge $RequiredGuardRevision}
 return [bool]$health.runtime.credential_status
}
try{
 $original=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$ExpectedPid)
 if(-not $original -or $original.Name -ne 'node.exe' -or -not $original.CommandLine.Contains((Join-Path $physicalRoot 'router.mjs')) -or -not $original.CommandLine.Contains('router.json')){throw 'Process identity mismatch'}
 $idleSamples=0
 $deadline=[DateTime]::UtcNow.AddMinutes(45)
 while([DateTime]::UtcNow -lt $deadline){
  $health=$null
  try{$health=Invoke-RestMethod -Uri 'http://127.0.0.1:18748/health' -TimeoutSec 3}catch{}
  if(FeatureReady $health){Report 'active' $health.runtime.active_requests $true;exit 0}
  $current=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$ExpectedPid)
  if(-not $current -or $current.CreationDate -ne $original.CreationDate -or $current.CommandLine -ne $original.CommandLine){
   Report 'waiting_for_existing_supervisor' $null $false;Start-Sleep -Seconds 3;continue
  }
  if($health -and $health.runtime.PSObject.Properties.Name -contains 'active_requests' -and $health.runtime.active_requests -eq 0 -and -not $health.runtime.reload_error){$idleSamples++}else{$idleSamples=0}
  if($idleSamples -ge 3){
   $check=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$ExpectedPid)
   $latest=Invoke-RestMethod -Uri 'http://127.0.0.1:18748/health' -TimeoutSec 3
   if($check.CreationDate -eq $original.CreationDate -and $check.CommandLine -eq $original.CommandLine -and $latest.runtime.active_requests -eq 0){
    Report 'switching_idle_router' 0 $false
    Stop-Process -Id $ExpectedPid -ErrorAction Stop
    for($i=0;$i -lt 30;$i++){
     Start-Sleep -Seconds 1
     try{$newHealth=Invoke-RestMethod -Uri 'http://127.0.0.1:18748/health' -TimeoutSec 2;if(FeatureReady $newHealth){Report 'active' $newHealth.runtime.active_requests $true;exit 0}}catch{}
    }
    Report 'restart_not_confirmed' $null $false;exit 2
   }
  }
  Report 'waiting_for_active_requests' $(if($health){$health.runtime.active_requests}else{$null}) $false
  # Once idle, confirm promptly; a fixed five-second interval required a ten-second
  # global gap and could starve activation while otherwise completed requests alternated.
  # All three samples and the final identity/request-count recheck remain required.
  if($idleSamples -gt 0){Start-Sleep -Milliseconds $IdleConfirmMilliseconds}else{Start-Sleep -Seconds 5}
 }
 Report 'idle_wait_timed_out' $null $false;exit 2
}catch{Report 'activation_failed' $null $false;exit 2}
finally{$mutex.ReleaseMutex();$mutex.Dispose()}
