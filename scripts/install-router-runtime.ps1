[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$RouterHome,
 [Parameter(Mandatory=$true)][string]$CodexHome,
 [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
$mutex=[Threading.Mutex]::new($false,'Local\CodexControlCenterRouterInstallV1')
if(-not $mutex.WaitOne(0)){throw 'Another router installation is in progress.'}
try {
$source=if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'hot-router.mjs')){$PSScriptRoot}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'adapters\router-observability'}
$root=[IO.Path]::GetFullPath($RouterHome)
$codex=[IO.Path]::GetFullPath($CodexHome)
if(-not [IO.Directory]::Exists($root) -or -not [IO.File]::Exists((Join-Path $root 'router.json'))){throw 'Router directory is invalid.'}
$engine=Join-Path $root 'router-control-center-engine.mjs'
$entry=Join-Path $root 'router.mjs'
$text=[IO.File]::ReadAllText($entry)
$marker='CODEX_CONTROL_CENTER_HOT_RELOAD_V1'
if($text.Contains($marker)){
 if(-not [IO.File]::Exists($engine)){throw 'Installed router engine is missing.'}
}elseif($text -notmatch 'export async function createRouter\(' -or $text -notmatch 'export async function startRouter\('){
 throw 'Router module does not expose the supported engine interface.'
}
foreach($name in @('hot-router.mjs','provider-credentials.ps1','observer.mjs','custom-tool-compat.mjs','tool-definition-guard.mjs')){
 if(-not [IO.File]::Exists((Join-Path $source $name))){throw 'Runtime asset is missing.'}
}
if($ValidateOnly){[pscustomobject]@{ready=$true;already_installed=$text.Contains($marker);running_processes_changed=$false}|ConvertTo-Json -Compress;return}
$backup=Join-Path $root ('control-center-runtime-backups\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($backup)|Out-Null
[IO.File]::Copy($entry,(Join-Path $backup 'router.mjs'),$false)
$runtime=Join-Path $root 'control-center-runtime'
[IO.Directory]::CreateDirectory($runtime)|Out-Null
# Runtime dependencies are copied before atomically publishing the entry-point shim.
foreach($name in @('hot-router.mjs','provider-credentials.ps1','observer.mjs','custom-tool-compat.mjs','tool-definition-guard.mjs')){
 $destination=Join-Path $runtime $name
 if([IO.File]::Exists($destination)){[IO.File]::Copy($destination,(Join-Path $backup $name),$false)}
 $temporary=$destination+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 [IO.File]::Copy((Join-Path $source $name),$temporary,$false)
 [IO.File]::Move($temporary,$destination,$true)
}
if(-not $text.Contains($marker)){[IO.File]::Copy($entry,$engine,$false)}
$codexJson=ConvertTo-Json -InputObject $codex -Compress
$shim=@"
// $marker
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { main } from './control-center-runtime/hot-router.mjs';
export * from './router-control-center-engine.mjs';
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.length !== 2 || args[0] !== '--config') {
    process.stderr.write('Invalid router arguments.\n'); process.exitCode = 1;
  } else {
    main([...args, '--engine', join(dirname(fileURLToPath(import.meta.url)), 'router-control-center-engine.mjs'), '--codex-home', $codexJson])
      .catch(() => { process.stderr.write('Router activation failed; check management console configuration status.\n'); process.exitCode = 1; });
  }
}
"@
$temporary=$entry+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
[IO.File]::WriteAllText($temporary,$shim,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporary,$entry,$true)
[pscustomobject]@{installed=$true;backup=$backup;running_processes_changed=$false;activation='Next router process start; existing requests remain untouched.'}|ConvertTo-Json -Compress
} finally {
 $mutex.ReleaseMutex()
 $mutex.Dispose()
}
