# Signature/field checks are read only and do not execute any original game methods.
# Use powershell.exe -NoProfile -File tests/guard-target-smoke.ps1 after a Release build.
# -InstallPatches is optional: Unity ECall dependencies can reject external CLR JIT,
# so this mode cannot establish whether installation succeeds inside the game.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath,
    [switch]$InstallPatches,
    [switch]$LoadOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this smoke test with Windows PowerShell (powershell.exe), whose .NET Framework matches the net46 mod.'
}
$guardManaged = Join-Path $GameDir 'LibraryOfRuina_Data\Managed'
$guardModPath = (Resolve-Path -LiteralPath $ModAssembly).ProviderPath
$guardModDir = Split-Path -Parent $guardModPath
$guardDependencyDir = Join-Path $guardModDir '..\Dependencies'
$guardResolveRoots = [string[]]@($guardModDir, $guardDependencyDir, $guardManaged)
$guardHarmonyPath = @(foreach ($root in $guardResolveRoots) {
    $candidate = Join-Path $root '0Harmony.dll'
    if (Test-Path -LiteralPath $candidate) { (Resolve-Path -LiteralPath $candidate).ProviderPath }
}) | Select-Object -First 1
if (-not ('RuinaGuardSmokeLoader' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class RuinaGuardSmokeLoader
{
    private static string[] roots;
    public static void Register(string[] directories)
    {
        roots = directories;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var root in roots)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    public static object CreateFields(Type type, string[] names, object[] values)
    {
        object boxed = Activator.CreateInstance(type);
        for (int i = 0; i < names.Length; i++)
        {
            type.GetField(names[i], BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).SetValue(boxed, values[i]);
        }
        return boxed;
    }
    public static FieldInfo FindField(Type type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }
}
"@
}
[RuinaGuardSmokeLoader]::Register($guardResolveRoots)
$GuardSmokeGameAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $guardManaged 'Assembly-CSharp.dll'))
$GuardSmokeModAssembly = [Reflection.Assembly]::LoadFrom($guardModPath)
$GuardSmokeBindingFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
if ($LoadOnly) { return }

function Get-GuardType([string]$Name) {
    $type = [type]::GetType($Name, $false)
    if ($null -eq $type) { $type = $GuardSmokeGameAssembly.GetType($Name, $false) }
    if ($null -eq $type) { throw "Missing real game type: $Name" }
    return $type
}
$guardType = $GuardSmokeModAssembly.GetType('RuinaCoop.DeckGuard', $true)
$guardTargets = New-Object 'System.Collections.Generic.List[object]'
function Add-GuardTarget([string]$TypeName, [string]$MethodName, [string[]]$Parameters, [string]$Prefix) {
    $guardTargets.Add([pscustomobject]@{
        TypeName = $TypeName; MethodName = $MethodName; Parameters = $Parameters; Prefix = $Prefix
    })
}
Add-GuardTarget 'LibraryModel' 'LoadFromSaveData' @('GameSave.SaveData') 'LibraryLoadPrefix'
Add-GuardTarget 'UnitDataModel' 'AddCardFromInventory' @('LorId') 'UnitAddPrefix'
Add-GuardTarget 'UnitDataModel' 'AddCardInDeckFromInventory' @('LOR_DiceSystem.DiceCardXmlInfo') 'UnitAddPrefix'
foreach ($name in @('MoveCardToInventory', 'RemoveCardInDeck')) {
    $parameter = if ($name -eq 'MoveCardToInventory') { 'LorId' } else { 'LOR_DiceSystem.DiceCardXmlInfo' }
    Add-GuardTarget 'UnitDataModel' $name @($parameter) 'UnitBoolPrefix'
}
foreach ($name in @('EquipBook', 'EquipBookForUI')) {
    Add-GuardTarget 'UnitDataModel' $name @('BookModel', 'System.Boolean', 'System.Boolean') 'UnitBoolPrefix'
}
Add-GuardTarget 'UnitDataModel' 'EquipCustomCoreBook' @('BookModel') 'UnitVoidPrefix'
foreach ($name in @('EmptyDeckToInventory', 'EmptyDeckToInventoryAll', 'ReEquipDeck', 'CreateDeckByDeckInfo')) {
    Add-GuardTarget 'UnitDataModel' $name @() 'UnitVoidPrefix'
}
Add-GuardTarget 'BookModel' 'AddCardFromInventoryToCurrentDeck' @('LorId') 'BookAddPrefix'
Add-GuardTarget 'BookModel' 'MoveCardFromCurrentDeckToInventory' @('LorId') 'BookBoolPrefix'
Add-GuardTarget 'BookModel' 'ChangeDeck' @('System.Int32') 'BookVoidPrefix'
foreach ($name in @('EmptyDeckToInventory', 'EmptyDeckToInventoryAll', 'CreateDeckByDeckInfo', 'ApplyPassiveSuccession')) {
    Add-GuardTarget 'BookModel' $name @() 'BookVoidPrefix'
}
Add-GuardTarget 'DeckModel' 'AddCardFromInventory' @('LorId') 'DeckAddPrefix'
Add-GuardTarget 'DeckModel' 'MoveCardToInventory' @('LorId') 'DeckBoolPrefix'
foreach ($name in @('SetDeck', 'AddCardForLoading')) {
    Add-GuardTarget 'DeckModel' $name @('LorId') 'DeckVoidPrefix'
}
Add-GuardTarget 'DeckModel' 'LoadFromSaveData' @('GameSave.SaveData') 'DeckVoidPrefix'
foreach ($name in @('EmptyDeckToInventory', 'RemoveAllErrorCard')) {
    Add-GuardTarget 'DeckModel' $name @() 'DeckVoidPrefix'
}
foreach ($name in @('OnClickCardSlotByDeck', 'RemoveCardSlot')) {
    Add-GuardTarget 'UI.UIEquipDeckCardList' $name @('UI.UIOriginCardSlot') 'UiUnitFieldPrefix'
}
foreach ($name in @('OnClickCardSlotByInven', 'InsertCardSlot')) {
    Add-GuardTarget 'UI.UIEquipDeckCardList' $name @('UI.UIInvenCardSlot') 'UiUnitFieldPrefix'
}
Add-GuardTarget 'UI.UIEquipDeckCardList' 'OnChangeDeckTab' @() 'UiUnitFieldPrefix'
Add-GuardTarget 'UI.UILibrarianEquipDeckPanel' 'OnClickClearDeckButton' @() 'UiUnitFieldPrefix'
Add-GuardTarget 'UI.UIDeckCardList' 'SetDeckCheck' @('DeckModel', 'UnitDataModel') 'UiDeckApplyPrefix'
Add-GuardTarget 'UI.UIDeckInfoPopup' 'OnclickApplyDeckButton' @() 'UiCurrentUnitPrefix'
Add-GuardTarget 'UI.UICardEquipInfoPanel' 'DeleteCardFrom' @('UnitDataModel') 'UiDeleteUnitPrefix'
Add-GuardTarget 'UI.UICardEquipInfoPanel' 'DeleteCardFrom' @('BookModel') 'UiDeleteBookPrefix'
$guardEquipSlots = @('UI.UIInvenEquipPageSlot', 'UI.UIInvenLeftEquipPageSlot',
    'UI.UISettingInvenEquipPageSlot', 'UI.UISettingInvenEquipPageLeftSlot')
foreach ($typeName in $guardEquipSlots) {
    Add-GuardTarget $typeName 'OnClickEquipButton' @() 'UiEquipBookPrefix'
    Add-GuardTarget $typeName 'OnClickEmptyDeckButton' @() 'UiBookFieldPrefix'
}
foreach ($typeName in @('UI.UILibrarianInfoInCardPhase', 'UI.UIBattleSettingLibrarianInfoPanel')) {
    Add-GuardTarget $typeName 'OnClickReleaseToggle' @() 'UiCurrentUnitPrefix'
}
Add-GuardTarget 'GameSave.SaveManager' 'SavePlayData' @('System.Int32', 'System.Boolean') 'SaveBoolPrefix'
Add-GuardTarget 'GameSave.SaveManager' 'SaveLatestData' @('LatestDataModel') 'SaveVoidPrefix'
Add-GuardTarget 'PlatformManager' 'SavePlayData' @('System.Int32', 'GameSave.SaveData') 'SaveVoidPrefix'
Add-GuardTarget 'PlatformCore_default' 'SavePlayData' @('System.Int32', 'GameSave.SaveData', 'System.Action`1[System.Boolean]') 'SaveCorePrefix'
Add-GuardTarget 'GlobalGameManager' 'LoadBattleScene' @() 'GuestBattlePrefix'
Add-GuardTarget 'UI.UIBattleSettingPanel' 'OnClickBattleStart' @() 'GuestBattlePrefix'

function Assert-GuardPrefix([Reflection.MethodInfo]$Original, [Reflection.MethodInfo]$Prefix) {
    if ($Prefix.ReturnType -ne [bool] -and $Prefix.ReturnType -ne [void]) {
        throw "Invalid prefix return type: $($Prefix.Name) -> $($Prefix.ReturnType)"
    }
    $arguments = $Original.GetParameters()
    foreach ($parameter in $Prefix.GetParameters()) {
        $name = $parameter.Name
        $parameterType = $parameter.ParameterType
        if ($parameterType.IsByRef) { $parameterType = $parameterType.GetElementType() }
        if ($name -eq '__result') {
            if ($Original.ReturnType -eq [void] -or $Original.ReturnType -ne $parameterType) {
                throw "Result mismatch: $Original uses $($Original.ReturnType), $($Prefix.Name) uses $parameterType"
            }
        }
        elseif ($name -eq '__instance') {
            if ($Original.IsStatic -or -not $parameterType.IsAssignableFrom($Original.DeclaringType)) {
                throw "Instance mismatch: $Original / $($Prefix.Name)"
            }
        }
        elseif ($name -eq '__originalMethod') {
            if (-not $parameterType.IsAssignableFrom([Reflection.MethodInfo])) {
                throw "Original-method injection mismatch: $($Prefix.Name)"
            }
        }
        elseif ($name -eq '__args') {
            if ($parameterType -ne [object[]]) {
                throw "Argument-array injection mismatch: $($Prefix.Name)"
            }
        }
        elseif ($name -eq '__state') {
            if ($parameterType -ne [bool] -or -not $parameter.IsOut) {
                throw 'Library load prefix must provide an out bool __state.'
            }
        }
        elseif ($name -match '^__(\d+)$') {
            $index = [int]$Matches[1]
            if ($index -ge $arguments.Length -or $arguments[$index].ParameterType -ne $parameterType) {
                throw "Ordinal injection mismatch: $Original / $($Prefix.Name) $name"
            }
        }
        else { throw "Unexpected injection: $($Prefix.Name) $name" }
    }
}
$guardResults = New-Object 'System.Collections.Generic.List[object]'
$guardMethods = New-Object 'System.Collections.Generic.List[System.Reflection.MethodBase]'
foreach ($target in $guardTargets) {
    $type = Get-GuardType $target.TypeName
    $parameterTypes = [type[]]@(foreach ($name in $target.Parameters) { Get-GuardType $name })
    $original = $type.GetMethod($target.MethodName, $GuardSmokeBindingFlags, $null, $parameterTypes, $null)
    $prefix = $guardType.GetMethod($target.Prefix, $GuardSmokeBindingFlags)
    if ($null -eq $original -or $null -eq $prefix) { throw "Missing target/prefix: $($target.TypeName).$($target.MethodName) / $($target.Prefix)" }
    Assert-GuardPrefix $original $prefix
    $guardMethods.Add($original)
    $guardResults.Add([pscustomobject]@{
        Target = $original.ToString(); DeclaringType = $original.DeclaringType.FullName
        ReturnType = $original.ReturnType.FullName; Prefix = $prefix.ToString()
    })
}
$loadFinalizer = $guardType.GetMethod('LibraryLoadFinalizer', $GuardSmokeBindingFlags)
if ($loadFinalizer.ReturnType -ne [Exception] -or
    $loadFinalizer.GetParameters()[0].Name -ne '__exception' -or
    $loadFinalizer.GetParameters()[0].ParameterType -ne [Exception] -or
    $loadFinalizer.GetParameters()[1].Name -ne '__state' -or
    $loadFinalizer.GetParameters()[1].ParameterType -ne [bool]) {
    throw 'Library load finalizer must preserve Exception and consume the bool load state.'
}
$guardFieldSpecs = @(
    @('UI.UIEquipDeckCardList', 'currentunit', 'UnitDataModel'),
    @('UI.UILibrarianEquipDeckPanel', '_unitdata', 'UnitDataModel'),
    @('BookModel', '_deck', 'DeckModel')
)
foreach ($name in $guardEquipSlots) { $guardFieldSpecs += ,@($name, '_bookDataModel', 'BookModel') }
$guardFields = @(foreach ($spec in $guardFieldSpecs) {
    $type = Get-GuardType $spec[0]
    $field = [RuinaGuardSmokeLoader]::FindField($type, $spec[1])
    if ($null -eq $field -or $field.FieldType -ne (Get-GuardType $spec[2])) { throw "Missing or incompatible UI field: $($spec[0]).$($spec[1])" }
    # Check the exact AccessTools helper used by production, including private base fields.
    $harmonyAssembly = [Reflection.Assembly]::LoadFrom($guardHarmonyPath)
    $accessTools = $harmonyAssembly.GetType('HarmonyLib.AccessTools', $true)
    $accessField = $accessTools.GetMethod('Field', $GuardSmokeBindingFlags, $null, [type[]]@([type], [string]), $null)
    $actualField = $accessField.Invoke($null, [object[]]@($type, $spec[1]))
    if ($null -eq $actualField -or $actualField.MetadataToken -ne $field.MetadataToken -or $actualField.DeclaringType -ne $field.DeclaringType) { throw "AccessTools cannot resolve inherited field: $($spec[0]).$($spec[1])" }
    [pscustomobject]@{ Type = $spec[0]; Field = $field.Name; DeclaredOn = $field.DeclaringType.FullName; FieldType = $field.FieldType.FullName }
})
$currentUnit = (Get-GuardType 'UI.UIController').GetProperty('CurrentUnit', $GuardSmokeBindingFlags)
if ($null -eq $currentUnit -or $currentUnit.PropertyType -ne (Get-GuardType 'UnitDataModel')) { throw 'UIController.CurrentUnit contract changed.' }
$guardPatchCount = 0
if ($InstallPatches) {
    $harmonyAssembly = [Reflection.Assembly]::LoadFrom($guardHarmonyPath)
    $harmonyType = $harmonyAssembly.GetType('HarmonyLib.Harmony', $true)
    $patchOwner = 'ruinacoop.guard-target-smoke.' + [Guid]::NewGuid().ToString('N')
    $harmony = [Activator]::CreateInstance($harmonyType, [object[]]@($patchOwner))
    try {
        # Install patches only. No original game methods or saved model constructors execute.
        $guardType.GetMethod('Install', $GuardSmokeBindingFlags).Invoke($null, [object[]]@($harmony)) | Out-Null
        $patchedMethods = @($harmonyType.GetMethod('GetPatchedMethods', $GuardSmokeBindingFlags).Invoke($harmony, @()))
        $guardPatchCount = $patchedMethods.Count
        if ($guardPatchCount -ne $guardMethods.Count) { throw "Guard target manifest differs from actual installation: manifest $($guardMethods.Count), patched $guardPatchCount" }
        foreach ($original in $guardMethods) {
            if (-not ($patchedMethods -contains $original)) { throw "Guard target omitted from actual installation: $original" }
        }
    }
    catch {
        Write-Output $_.Exception.ToString()
        throw
    }
    finally {
        try { $harmonyType.GetMethod('UnpatchSelf', $GuardSmokeBindingFlags).Invoke($harmony, @()) | Out-Null }
        catch { Write-Warning ("Isolated process cleanup failed: " + $_.Exception.ToString()) }
    }
}
$report = [pscustomobject]@{
    Passed = $true; GameAssembly = $GuardSmokeGameAssembly.Location; ModAssembly = $guardModPath
    TargetCount = $guardMethods.Count; InstalledPatchCount = $guardPatchCount
    PatchInstallationRequested = [bool]$InstallPatches
    ModSha256 = (Get-FileHash -LiteralPath $guardModPath -Algorithm SHA256).Hash
    Targets = $guardResults.ToArray(); Fields = $guardFields
}
if ($OutputPath) { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
$installStatus = if ($InstallPatches) { "installed $guardPatchCount patches in this isolated process" } else { 'actual patch installation skipped' }
Write-Output "PASS: $($guardMethods.Count) target/prefix signatures against the real DLL, $($guardFields.Count) inherited fields, library-load finalizer; $installStatus."
