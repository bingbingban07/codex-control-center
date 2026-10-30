param([string]$Configuration='Release',[switch]$Publish,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$project=Join-Path $projectRoot 'src\CodexControlCenter.App\CodexControlCenter.App.csproj'
if($Publish){
  if(-not $OutputDirectory){$version=([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Select-Object -First 1;$OutputDirectory=Join-Path $projectRoot ('release\v'+$version+'\CodexControlCenter')}
  dotnet publish $project -c $Configuration -p:Platform=x64 -o $OutputDirectory --nologo
}else{
  dotnet build $project -c $Configuration -p:Platform=x64 --nologo
}
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$wallpaperProject=Join-Path $projectRoot 'src\CodexControlCenter.WallpaperHost\CodexControlCenter.WallpaperHost.csproj'
$applicationOutput=if($Publish){$OutputDirectory}else{Join-Path $projectRoot "src\CodexControlCenter.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64"}
dotnet publish $wallpaperProject -c $Configuration -p:Platform=x64 -o (Join-Path $applicationOutput 'helpers\wallpaper') --nologo
if($LASTEXITCODE -ne 0){throw 'Wallpaper companion build failed'}
