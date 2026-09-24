param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repositoryRoot 'dist\RuinaCoop'
$modsDirectory = Join-Path $GameDir 'LibraryOfRuina_Data\Mods'
$target = Join-Path $modsDirectory 'RuinaCoop'
$backupRoot = Join-Path $repositoryRoot 'artifacts\install-backups'

if (-not (Test-Path -LiteralPath (Join-Path $package 'Assemblies\RuinaCoop.dll'))) {
    throw 'Build the package with scripts/pack.ps1 first.'
}
if (-not (Test-Path -LiteralPath $modsDirectory -PathType Container)) {
    throw "Game Mods directory is missing: $modsDirectory"
}
if (Get-Process LibraryOfRuina -ErrorAction SilentlyContinue) {
    throw 'Close Library of Ruina before installing the Mod.'
}

$modsFull = [IO.Path]::GetFullPath($modsDirectory).TrimEnd('\')
$targetFull = [IO.Path]::GetFullPath($target)
$expectedTarget = Join-Path $modsFull 'RuinaCoop'
if (-not [string]::Equals($targetFull, $expectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unexpected install target.'
}
$modsItem = Get-Item -LiteralPath $modsFull -Force
if (($modsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'A linked Mods directory needs manual inspection before installation.'
}

if (Test-Path -LiteralPath $targetFull) {
    $targetItem = Get-Item -LiteralPath $targetFull -Force
    if (($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A linked RuinaCoop directory needs manual inspection before installation.'
    }
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $backup = Join-Path $backupRoot ('RuinaCoop-' + $stamp)
    Copy-Item -LiteralPath $targetFull -Destination $backup -Recurse -Force
    Write-Output "Previous package backed up to: $backup"
    Remove-Item -LiteralPath $targetFull -Recurse -Force
}

Copy-Item -LiteralPath $package -Destination $targetFull -Recurse -Force
Write-Output "Installed: $targetFull"