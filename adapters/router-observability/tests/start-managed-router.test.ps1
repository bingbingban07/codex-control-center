$ErrorActionPreference = 'Stop'

$adapterRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $adapterRoot 'start-managed-router.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('router-managed-test-' + [Guid]::NewGuid().ToString('N'))
$routerHome = Join-Path $fixtureRoot 'router'
$codexHome = Join-Path $fixtureRoot 'codex'
$secretDirectory = Join-Path $codexHome 'secrets'
[void][IO.Directory]::CreateDirectory($routerHome)
[void][IO.Directory]::CreateDirectory($secretDirectory)

try {
  Add-Type -AssemblyName 'System.Security'
  $syntheticCredential = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
  try {
    $clearBytes = [Text.Encoding]::UTF8.GetBytes($syntheticCredential)
    $protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
      $clearBytes,
      $null,
      [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    [IO.File]::WriteAllText(
      (Join-Path $secretDirectory 'control-center-alpha.dpapi'),
      [Convert]::ToBase64String($protectedBytes)
    )
  } finally {
    if ($clearBytes) { [Array]::Clear($clearBytes, 0, $clearBytes.Length) }
    if ($protectedBytes) { [Array]::Clear($protectedBytes, 0, $protectedBytes.Length) }
  }

  $nodePath = (Get-Command node).Source
  @{
    node = $nodePath
    python = ''
  } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $routerHome 'runtime.json')

  @{
    host = '127.0.0.1'
    port = 49317
    providers = @{
      alpha = @{
        base_url = 'http://127.0.0.1:49318/v1'
        key_env = 'CODEX_CC_ALPHA'
      }
    }
    routes = @{
      test = @{ provider = 'alpha'; model = 'test-model' }
    }
  } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $routerHome 'router.json')
  Set-Content -Encoding utf8 (Join-Path $routerHome 'router.mjs') 'export async function createRouter() { throw new Error("not used"); }'

  $output = & pwsh.exe -NoProfile -NonInteractive -File $scriptPath `
    -RouterHome $routerHome `
    -ConfigPath (Join-Path $routerHome 'router.json') `
    -CodexHome $codexHome `
    -ValidateOnly 2>&1
  if ($LASTEXITCODE -ne 0) {
    throw "ValidateOnly failed: $output"
  }

  $text = [string]$output
  if ($text -match [regex]::Escape($syntheticCredential)) {
    throw 'ValidateOnly exposed a credential.'
  }
  $result = $text | ConvertFrom-Json
  if ($result.secure_credential_count -ne 1) {
    throw "Expected one secure credential, received $($result.secure_credential_count)."
  }
  if ($result.provider_count -ne 1 -or -not $result.node_exists) {
    throw 'ValidateOnly returned an unexpected readiness result.'
  }

  $configPath = Join-Path $routerHome 'router.json'
  $configData = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
  $configData.host = '0.0.0.0'
  $configData | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $configPath

  $badOutput = & pwsh.exe -NoProfile -NonInteractive -File $scriptPath `
    -RouterHome $routerHome `
    -ConfigPath $configPath `
    -CodexHome $codexHome `
    -ValidateOnly 2>&1
  if ($LASTEXITCODE -eq 0 -or ([string]$badOutput -notmatch 'loopback')) {
    throw 'Non-loopback router host was not rejected.'
  }
  if ([string]$badOutput -match [regex]::Escape($syntheticCredential)) {
    throw 'Loopback validation exposed a credential.'
  }

  Write-Output 'start-managed-router validation passed'
} finally {
  $syntheticCredential = $null
  if ([IO.Directory]::Exists($fixtureRoot)) {
    [IO.Directory]::Delete($fixtureRoot, $true)
  }
}



