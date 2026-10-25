param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
[xml]$projectFile = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'PeripheralBattery.csproj')
$releaseVersion = [string]$projectFile.Project.PropertyGroup.Version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw 'Invalid project release version' }
$releaseName = "PeripheralBattery-v$releaseVersion-win-x64"
$publishFolder = Join-Path $projectRoot "dist\$releaseName"
dotnet publish (Join-Path $projectRoot 'PeripheralBattery.csproj') -c Release -r win-x64 --self-contained true -o $publishFolder -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (-not $SkipTests) {
    & (Join-Path $publishFolder 'PeripheralBattery.exe') --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed' }
    & (Join-Path $publishFolder 'PeripheralBattery.exe') --tray-self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Windows tray registration self-test failed' }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishFolder -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.en.md') -Destination $publishFolder -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $publishFolder -Force
$docsFolder = Join-Path $publishFolder 'docs'
New-Item -ItemType Directory -Force -Path $docsFolder | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -Filter '*.md' -File | Copy-Item -Destination $docsFolder -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\licenses') -Destination $docsFolder -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\images') -Destination $docsFolder -Recurse -Force
$archive = Join-Path $projectRoot "dist\$releaseName.zip"
Compress-Archive -LiteralPath $publishFolder -DestinationPath $archive -Force
Write-Output $archive
