[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$RouterHome,

  [string]$ConfigPath = '',

  [string]$CodexHome = '',

  [string]$NodePath = '',

  [switch]$UseObserver,

  [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-RouterHealth {
  param([string]$HostValue, [int]$PortValue)
  $request = $null
  $response = $null
  try {
    $uri = 'http://' + $HostValue + ':' + $PortValue + '/health'
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($uri)
    $request.Method = 'GET'
    $request.Proxy = $null
    $request.Timeout = 2000
    $request.ReadWriteTimeout = 2000
    $response = $request.GetResponse()
    return ([int]$response.StatusCode -eq 200)
  } catch {
    return $false
  } finally {
    if ($response) { $response.Dispose() }
  }
}

function Initialize-ProtectedData {
  try {
    Add-Type -AssemblyName 'System.Security' -ErrorAction Stop
  } catch {
    try {
      Add-Type -AssemblyName 'System.Security.Cryptography.ProtectedData' -ErrorAction Stop
    } catch {
      return $false
    }
  }
  return $true
}

function Get-DpapiCredential {
  param([string]$Path)
  $protected = $null
  $clear = $null
  try {
    if (-not [IO.File]::Exists($Path)) { return $null }
    $base64 = [IO.File]::ReadAllText($Path).Trim()
    $protected = [Convert]::FromBase64String($base64)
    $clear = [Security.Cryptography.ProtectedData]::Unprotect(
      $protected,
      $null,
      [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    $value = [Text.Encoding]::UTF8.GetString($clear)
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]') { return $null }
    return $value
  } catch {
    return $null
  } finally {
    if ($protected) { [Array]::Clear($protected, 0, $protected.Length) }
    if ($clear) { [Array]::Clear($clear, 0, $clear.Length) }
  }
}

function Get-SecureStringCredential {
  param([string]$Path)
  $secure = $null
  $ptr = [IntPtr]::Zero
  try {
    if (-not [IO.File]::Exists($Path)) { return $null }
    $secure = [IO.File]::ReadAllText($Path).Trim() | ConvertTo-SecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '[\r\n]') { return $null }
    return $value
  } catch {
    return $null
  } finally {
    if ($ptr -ne [IntPtr]::Zero) {
      [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
    if ($secure) { $secure.Dispose() }
  }
}

function Add-QuotedArgument {
  param([Collections.Generic.List[string]]$Target, [string]$Value)
  $Target.Add('"' + $Value.Replace('"', '\"') + '"')
}

$resolvedRouterHome = [IO.Path]::GetFullPath($RouterHome)
if (-not [IO.Directory]::Exists($resolvedRouterHome)) {
  throw 'RouterHome does not exist.'
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
  $ConfigPath = Join-Path $resolvedRouterHome 'router.json'
}
$resolvedConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not [IO.File]::Exists($resolvedConfigPath)) {
  throw 'Router configuration does not exist.'
}
if ([string]::IsNullOrWhiteSpace($CodexHome)) {
  $CodexHome = Join-Path $env:USERPROFILE '.codex'
}
$resolvedCodexHome = [IO.Path]::GetFullPath($CodexHome)

$runtimePath = Join-Path $resolvedRouterHome 'runtime.json'
if ([string]::IsNullOrWhiteSpace($NodePath)) {
  if (-not [IO.File]::Exists($runtimePath)) { throw 'runtime.json does not exist.' }
  $runtime = [IO.File]::ReadAllText($runtimePath) | ConvertFrom-Json
  $NodePath = [string]$runtime.node
}
$resolvedNodePath = [IO.Path]::GetFullPath($NodePath)
if (-not [IO.File]::Exists($resolvedNodePath)) {
  throw 'The configured Node executable does not exist.'
}

$config = [IO.File]::ReadAllText($resolvedConfigPath) | ConvertFrom-Json
$routerHost = '127.0.0.1'
$routerPort = 18748
if ($config.host) {
  $routerHost = [string]$config.host
} elseif ($config.router -and $config.router.host) {
  $routerHost = [string]$config.router.host
}
if ($config.port) {
  $routerPort = [int]$config.port
} elseif ($config.router -and $config.router.port) {
  $routerPort = [int]$config.router.port
}
if ($routerHost -ne '127.0.0.1') {
  throw 'Router host must be the loopback address 127.0.0.1.'
}

if (Test-RouterHealth -HostValue $routerHost -PortValue $routerPort) {
  if ($ValidateOnly) {
    [pscustomobject]@{
      configuration_ready = $true
      healthy = $true
      node_exists = $true
      provider_count = @($config.providers.PSObject.Properties).Count
    } | ConvertTo-Json -Compress
    return
  }
  exit 0
}

if (-not (Initialize-ProtectedData)) {
  throw 'DPAPI support is unavailable.'
}

$providerProperties = @()
if ($config.providers) {
  $providerProperties = @($config.providers.PSObject.Properties)
}
$userEnvironment = @{}
$processEnvironment = @{}
foreach ($property in $providerProperties) {
  $configuredKeyEnvironment = [string]$property.Value.key_env
  if ($configuredKeyEnvironment -match '^[A-Za-z_][A-Za-z0-9_]*$') {
    $userEnvironment[$configuredKeyEnvironment] = [Environment]::GetEnvironmentVariable($configuredKeyEnvironment, 'User')
    $processEnvironment[$configuredKeyEnvironment] = [Environment]::GetEnvironmentVariable($configuredKeyEnvironment, 'Process')
  }
}

$legacyEnvironmentName = 'CODEX_114514_API_KEY'
$userEnvironment[$legacyEnvironmentName] = [Environment]::GetEnvironmentVariable($legacyEnvironmentName, 'User')
$processEnvironment[$legacyEnvironmentName] = [Environment]::GetEnvironmentVariable($legacyEnvironmentName, 'Process')

$secretDirectory = Join-Path $resolvedCodexHome 'secrets'
$secureCredentialCount = 0
$fallbackCredentialCount = 0
foreach ($property in $providerProperties) {
  $providerId = [string]$property.Name
  $authMode = [string]$property.Value.auth_mode
  if ($authMode -eq 'chatgpt') { continue }

  $keyEnvironment = [string]$property.Value.key_env
  if ([string]::IsNullOrWhiteSpace($keyEnvironment)) {
    $keyEnvironment = 'CODEX_CC_' + ($providerId.ToUpperInvariant() -replace '[^A-Z0-9_]', '_')
  }
  if ($keyEnvironment -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
    continue
  }

  $credential = $null
  $providerSecretPath = Join-Path $secretDirectory ('control-center-' + $providerId + '.dpapi')
  $credential = Get-DpapiCredential -Path $providerSecretPath
  if ($credential) {
    $secureCredentialCount++
  } else {
    if ($userEnvironment.ContainsKey($keyEnvironment)) { $credential = $userEnvironment[$keyEnvironment] }
    if (-not $credential -and $processEnvironment.ContainsKey($keyEnvironment)) {
      $credential = $processEnvironment[$keyEnvironment]
    }

    $useFirstLegacy = $providerId -eq 'a' -or $keyEnvironment -eq 'CODEX_ROUTER_KEY_A'
    $useSecondLegacy = $providerId -eq 'b' -or $keyEnvironment -eq 'CODEX_ROUTER_KEY_B'
    $useMimoLegacy = $providerId -eq 'mimo' -or $keyEnvironment -eq 'CODEX_ROUTER_KEY_MIMO'
    if (-not $credential -and $useFirstLegacy) {
      $credential = $userEnvironment[$legacyEnvironmentName]
      if (-not $credential) { $credential = $processEnvironment[$legacyEnvironmentName] }
    }
    if (-not $credential -and $useSecondLegacy) {
      $credential = Get-SecureStringCredential -Path (Join-Path $resolvedRouterHome 'key-b.dpapi')
    }
    if (-not $credential -and $useMimoLegacy) {
      $credential = Get-SecureStringCredential -Path (Join-Path $resolvedRouterHome 'key-mimo.dpapi')
    }
  }

  if ($credential -and $credential -notmatch '[\r\n]') {
    [Environment]::SetEnvironmentVariable($keyEnvironment, $credential, 'Process')
  }
  $credential = $null
}

if ($ValidateOnly) {
  [pscustomobject]@{
    configuration_ready = $true
    healthy = $false
    node_exists = $true
    provider_count = $providerProperties.Count
    secure_credential_count = $secureCredentialCount
    fallback_credential_count = $fallbackCredentialCount
    observer_enabled = [bool]$UseObserver
  } | ConvertTo-Json -Compress
  return
}

$mutex = [Threading.Mutex]::new($false, 'Local\CodexControlCenterManagedRouterV1')
if (-not $mutex.WaitOne(0)) {
  exit 0
}

try {
  $routerModulePath = Join-Path $resolvedRouterHome 'router.mjs'
  $observerPath = Join-Path $PSScriptRoot 'hot-router.mjs'
  if (-not [IO.File]::Exists($routerModulePath)) {
    throw 'router.mjs does not exist.'
  }

  $argumentList = [Collections.Generic.List[string]]::new()
  if ($UseObserver) {
    if (-not [IO.File]::Exists($observerPath)) {
      throw 'hot-router.mjs does not exist.'
    }
    Add-QuotedArgument -Target $argumentList -Value $observerPath
    Add-QuotedArgument -Target $argumentList -Value '--engine'
    Add-QuotedArgument -Target $argumentList -Value $routerModulePath
    Add-QuotedArgument -Target $argumentList -Value '--config'
    Add-QuotedArgument -Target $argumentList -Value $resolvedConfigPath
    Add-QuotedArgument -Target $argumentList -Value '--codex-home'
    Add-QuotedArgument -Target $argumentList -Value $resolvedCodexHome
  } else {
    Add-QuotedArgument -Target $argumentList -Value $routerModulePath
    Add-QuotedArgument -Target $argumentList -Value '--config'
    Add-QuotedArgument -Target $argumentList -Value $resolvedConfigPath
  }

  $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
  $stdoutPath = Join-Path $resolvedRouterHome ('router-managed-' + $stamp + '.stdout.log')
  $stderrPath = Join-Path $resolvedRouterHome ('router-managed-' + $stamp + '.stderr.log')
  $child = Start-Process `
    -FilePath $resolvedNodePath `
    -ArgumentList $argumentList.ToArray() `
    -WorkingDirectory $resolvedRouterHome `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -PassThru
  [IO.File]::WriteAllText((Join-Path $resolvedRouterHome 'router-observability.pid'), [string]$child.Id)
} catch {
  [IO.File]::WriteAllText(
    (Join-Path $resolvedRouterHome 'router-observability-error.log'),
    ((Get-Date).ToUniversalTime().ToString('o') + ' ' + $_.Exception.Message.Replace([Environment]::NewLine, ' ').Substring(0, [Math]::Min(500, $_.Exception.Message.Length)) + [Environment]::NewLine)
  )
  throw
} finally {
  $mutex.ReleaseMutex()
  $mutex.Dispose()
}



