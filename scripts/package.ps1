param([string]$Configuration='Release',[switch]$SkipBuild,[string]$ReleaseDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(-not $ReleaseDirectory){$project=Join-Path $projectRoot 'src\CodexControlCenter.App\CodexControlCenter.App.csproj';$version=([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Select-Object -First 1;$ReleaseDirectory=Join-Path $projectRoot ('release\v'+$version+'\CodexControlCenter')}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -Publish -OutputDirectory $ReleaseDirectory}
$release=(Resolve-Path -LiteralPath $ReleaseDirectory).Path
if((Split-Path -Leaf $release) -ne 'CodexControlCenter'){throw 'Release directory must be named CodexControlCenter for the portable package'}
$zipPath=Join-Path $projectRoot 'CodexControlCenter-Windows-x64.zip'
$temporary=Join-Path $projectRoot ('work\package-'+[Guid]::NewGuid().ToString('N')+'.zip')
[IO.Directory]::CreateDirectory((Split-Path -Parent $temporary))|Out-Null
try{
  [IO.Compression.ZipFile]::CreateFromDirectory($release,$temporary,[IO.Compression.CompressionLevel]::Optimal,$true)
  $archive=[IO.Compression.ZipFile]::OpenRead($temporary)
  try{
    foreach($wallpaperName in @('CodexControlCenter/helpers/wallpaper/CodexControlCenter.WallpaperHost.exe','CodexControlCenter/assets/wallpapers/静夜-深蓝.jpg','CodexControlCenter/启动Codex壁纸.cmd')){
      if(-not $archive.GetEntry($wallpaperName)){throw ('Package missing '+$wallpaperName)}
    }
    foreach($name in @('CodexControlCenter/CodexControlCenter.App.exe','CodexControlCenter/MainWindow.xbf','CodexControlCenter/App.xbf','CodexControlCenter/assets/codex-control-center.ico','CodexControlCenter/adapters/subagent-policy/policy_hook.py','CodexControlCenter/adapters/router-observability/hot-router.mjs','CodexControlCenter/adapters/router-observability/provider-credentials.ps1','CodexControlCenter/adapters/router-observability/install-router-runtime.ps1')){
      if(-not $archive.GetEntry($name)){throw ('Package missing '+$name)}
    }
  }finally{$archive.Dispose()}
  if(Test-Path -LiteralPath $zipPath){$archiveDirectory=Join-Path $projectRoot 'work\previous-packages';[IO.Directory]::CreateDirectory($archiveDirectory)|Out-Null;Copy-Item -LiteralPath $zipPath -Destination (Join-Path $archiveDirectory ('CodexControlCenter-'+(Get-Date -Format yyyyMMdd-HHmmss)+'.zip'))}
  Move-Item -LiteralPath $temporary -Destination $zipPath -Force
  Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Path,Hash
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
