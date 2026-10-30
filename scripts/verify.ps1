param([switch]$SkipNative)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try{
  python -X utf8 scripts/audit-public-release.py
  if($LASTEXITCODE -ne 0){throw 'Public source audit failed'}
  python -X utf8 -m unittest discover -s scripts/tests -p test_fresh_install.py -v
  if($LASTEXITCODE -ne 0){throw 'Fresh install defaults tests failed'}
  dotnet run --project 'tests\CodexControlCenter.Tests' -c Release
  if($LASTEXITCODE -ne 0){throw 'Configuration tests failed'}
  dotnet run --project 'adapters\router-observability\tests\RuntimeService.Tests.csproj' -c Release
  if($LASTEXITCODE -ne 0){throw 'Runtime tests failed'}
  python -m unittest discover -s 'adapters\image-provider' -p 'test_*.py' -v
  if($LASTEXITCODE -ne 0){throw 'Image tests failed'}
  python -m unittest discover -s 'adapters\subagent-policy\tests' -p 'test_policy_hook.py' -v
  if($LASTEXITCODE -ne 0){throw 'Subagent policy tests failed'}
  python -X utf8 -m unittest discover -s 'adapters\enhancement-mode' -p 'test_*.py' -v
  if($LASTEXITCODE -ne 0){throw 'Enhancement mode switch tests failed'}
  $nodeCommand=Get-Command node -ErrorAction SilentlyContinue
  $nodePath=if($nodeCommand){$nodeCommand.Source}else{$null}
  if(-not $nodePath){
    $runtimePath=Join-Path $env:USERPROFILE '.codex\model-router-sandbox\runtime.json'
    if(Test-Path -LiteralPath $runtimePath){$nodePath=(Get-Content -LiteralPath $runtimePath -Raw|ConvertFrom-Json).node}
  }
  if(-not $nodePath -or -not (Test-Path -LiteralPath $nodePath)){throw 'Node.js was not found'}
  & $nodePath --test 'adapters\router-observability\tests\observer.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Observer tests failed'}
  & $nodePath --test 'adapters\router-observability\tests\hot-router.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Router hot-reload tests failed'}
  & $nodePath --test 'adapters\router-observability\tests\custom-tool-compat.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Custom tool compatibility tests failed'}
  & $nodePath --test 'adapters\router-observability\tests\tool-definition-guard.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Tool definition guard tests failed'}
  & $nodePath --test 'adapters\router-observability\tests\installed-runtime.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Installed runtime tests failed'}
  & $nodePath --test 'adapters\router-observability\tests\credential-isolation.test.mjs' 'adapters\router-observability\tests\credential-helper.test.mjs' 'adapters\router-observability\tests\real-engine-metadata.test.mjs'
  if($LASTEXITCODE -ne 0){throw 'Provider isolation or request metadata tests failed'}
  & (Get-Command pwsh).Source -NoProfile -File 'adapters\router-observability\tests\start-managed-router.test.ps1'
  if($LASTEXITCODE -ne 0){throw 'Launcher tests failed'}
  if(-not $SkipNative){
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish
    $out=Join-Path $projectRoot ('verification\native-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    $state=Join-Path $projectRoot ('work\native-state-'+[Guid]::NewGuid().ToString('N'))
    $project=Join-Path $projectRoot 'src\CodexControlCenter.App\CodexControlCenter.App.csproj'
    $version=([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
    $app=Join-Path $projectRoot ('release\v'+$version+'\CodexControlCenter\CodexControlCenter.App.exe')
    $proc=Start-Process -FilePath $app -ArgumentList @('--smoke',('"'+$out+'"'),'--state-home',('"'+$state+'"')) -PassThru -WindowStyle Hidden
    if(-not $proc.WaitForExit(60000)){throw 'Native self-check timed out'}
    $proc.Refresh()
    if($proc.ExitCode -ne 0){throw ('Native self-check exited with code '+$proc.ExitCode)}
    $resultPath=Join-Path $out 'smoke-result.json'
    if(-not (Test-Path -LiteralPath $resultPath)){throw ('Native self-check produced no result: '+$out)}
    $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if(-not $result.success){throw $result.error}
  }
  Write-Output 'Verification passed'
}finally{Pop-Location}
