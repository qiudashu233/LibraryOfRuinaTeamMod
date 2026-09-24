param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\RuinaCoop\RuinaCoop.csproj'
$outputDir = Join-Path $repositoryRoot 'dist\RuinaCoop'
$assemblyDir = Join-Path $outputDir 'Assemblies'
$dependencyDir = Join-Path $outputDir 'Dependencies'

dotnet build $project --configuration $Configuration "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

New-Item -ItemType Directory -Path $assemblyDir, $dependencyDir -Force | Out-Null
Get-ChildItem -LiteralPath $assemblyDir -Filter '*.dll' -File | Remove-Item -Force
Get-ChildItem -LiteralPath $dependencyDir -Filter '*.dll' -File | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'mod\StageModInfo.xml') -Destination $outputDir -Force

$buildDir = Join-Path $repositoryRoot ("src\RuinaCoop\bin\$Configuration\net46")
Copy-Item -LiteralPath (Join-Path $buildDir 'RuinaCoop.dll') -Destination $assemblyDir -Force
Get-ChildItem -LiteralPath $buildDir -Filter '*.dll' -File |
    Where-Object { $_.Name -ne 'RuinaCoop.dll' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dependencyDir -Force }

Write-Output "Package ready: $outputDir"