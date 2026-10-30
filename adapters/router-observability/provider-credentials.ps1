param(
  [Parameter(Mandatory=$true)][string]$ConfigPath,
  [Parameter(Mandatory=$true)][string]$CodexHome,
  [switch]$FingerprintOnly
)
# Private pipe protocol. This helper is only spawned by hot-router.mjs; never log its stdout.
#
# Every provider is reported independently so one unusable source never removes the credentials
# of the healthy ones. Two shapes are produced:
#   full load   -> {"credentials":{env:name},"unavailable":[{id,reason}],"env_fingerprint":sha256,
#                   env:name (legacy flat readers)}
#   fingerprint -> {"env_fingerprint":sha256}
# The fingerprint only covers environment-sourced credentials (no encrypted file exists for that
# provider). Encrypted files are covered by the caller's file stamp, so the fingerprint stays
# identical between both modes and can be compared safely.
$ErrorActionPreference='Stop'

function Get-Sha256Hex([string]$text){
  $sha=[Security.Cryptography.SHA256]::Create()
  try{
    return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','').ToLowerInvariant()
  }finally{ $sha.Dispose() }
}

try {
  Add-Type -AssemblyName System.Security
  $config=[IO.File]::ReadAllText($ConfigPath)|ConvertFrom-Json
  $values=@{}
  $unavailable=New-Object System.Collections.ArrayList
  $environmentEntries=New-Object System.Collections.ArrayList
  $providers=@()
  if($config.providers){$providers=@($config.providers.PSObject.Properties)}
  foreach($property in $providers){
    $id=[string]$property.Name
    $provider=$property.Value
    if($provider.auth_mode -eq 'chatgpt'){continue}
    if($id -notmatch '^[A-Za-z0-9_-]{1,64}$'){[void]$unavailable.Add(@{id=$id;reason='invalid_provider_reference'});continue}
    $keyEnvironment=[string]$provider.key_env
    if($keyEnvironment -notmatch '^[A-Za-z_][A-Za-z0-9_]*$'){[void]$unavailable.Add(@{id=$id;reason='missing_key_env'});continue}
    $path=Join-Path $CodexHome ('secrets\control-center-'+$id+'.dpapi')
    $value=$null
    if([IO.File]::Exists($path)){
      # Encrypted source: a broken payload disables this provider only and never falls back to an
      # environment value that could belong to a different key.
      if($FingerprintOnly){continue}
      $encrypted=$null;$clear=$null
      try{
        $encrypted=[Convert]::FromBase64String([IO.File]::ReadAllText($path).Trim())
        $clear=[Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $value=[Text.Encoding]::UTF8.GetString($clear)
      }catch{
        $value=$null
        [void]$unavailable.Add(@{id=$id;reason='credential_unreadable'})
      }finally{
        if($encrypted){[Array]::Clear($encrypted,0,$encrypted.Length)}
        if($clear){[Array]::Clear($clear,0,$clear.Length)}
      }
      if($value -and $value -match '[\r\n]'){$value=$null;[void]$unavailable.Add(@{id=$id;reason='credential_invalid'})}
    }else{
      # Environment source: only the configured key name is read, never the whole environment.
      $environmentValue=[Environment]::GetEnvironmentVariable($keyEnvironment,'User')
      if(-not $environmentValue){$environmentValue=[Environment]::GetEnvironmentVariable($keyEnvironment,'Process')}
      if(-not $environmentValue -and ($id -eq 'a' -or $keyEnvironment -eq 'CODEX_ROUTER_KEY_A')){
        $environmentValue=[Environment]::GetEnvironmentVariable('CODEX_114514_API_KEY','User')
        if(-not $environmentValue){$environmentValue=[Environment]::GetEnvironmentVariable('CODEX_114514_API_KEY','Process')}
      }
      [void]$environmentEntries.Add(@{id=$id;value=$environmentValue})
      $value=$environmentValue
      if(-not $value -and -not $FingerprintOnly){
        $legacy=if($id -eq 'b'){'key-b.dpapi'}elseif($id -eq 'mimo'){'key-mimo.dpapi'}else{$null}
        if($legacy){
          $legacyPath=Join-Path (Split-Path -Parent $ConfigPath) $legacy
          if([IO.File]::Exists($legacyPath)){
            $secure=[IO.File]::ReadAllText($legacyPath).Trim()|ConvertTo-SecureString
            $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            try{$value=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)}
            finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr);$secure.Dispose()}
          }
        }
      }
      if([string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]'){
        $value=$null
        [void]$unavailable.Add(@{id=$id;reason='credential_unavailable'})
      }
    }
    if($value){$values[$keyEnvironment]=$value}
    $value=$null
  }
  $fingerprintParts=@()
  foreach($entry in ($environmentEntries|Sort-Object id)){
    $fingerprintParts+=("{0}={1}" -f $entry.id,(Get-Sha256Hex([string]$entry.value)))
  }
  $fingerprint=Get-Sha256Hex(($fingerprintParts -join "`n"))
  if($FingerprintOnly){
    [Console]::Out.Write((@{env_fingerprint=$fingerprint}|ConvertTo-Json -Compress))
  }else{
    $payload=@{credentials=$values;unavailable=@($unavailable);env_fingerprint=$fingerprint}
    # A router process started before the structured protocol may reload this helper from disk.
    # Keep its flat credential names until that process can restart safely. New readers use only
    # the nested map; fingerprint-only output must never include either credential representation.
    foreach($name in $values.Keys){if(-not $payload.ContainsKey($name)){$payload[$name]=$values[$name]}}
    [Console]::Out.Write(($payload|ConvertTo-Json -Compress))
  }
}catch{
  [Console]::Error.Write('Provider credential loading failed. Check the configured encrypted credential or known environment reference.')
  exit 2
}
