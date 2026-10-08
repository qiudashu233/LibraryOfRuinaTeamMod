param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$nativeRequestedOutputPath = $OutputPath
# Only load assemblies and allocate DTO/session state. No Steam sockets or game constructors.
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$nativeFlags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$nativeSessionType = $GuardSmokeModAssembly.GetType('RuinaCoop.RelaySession', $true)
$nativeSnapshotType = $GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot', $true)
$nativeActionType = $GuardSmokeModAssembly.GetType('RuinaCoop.DeckAction', $true)
$nativeReplyType = $GuardSmokeModAssembly.GetType('RuinaCoop.DeckReply', $true)
$nativeResultType = $GuardSmokeModAssembly.GetType('RuinaCoop.DeckResultCode', $true)
$nativeRequest = $nativeSessionType.GetMethod('RequestDeckEdit', $nativeFlags, $null,
    [type[]]@($nativeSnapshotType, [byte], [int], $nativeActionType), $null)
if ($null -eq $nativeRequest -or $nativeRequest.ReturnType -ne [bool]) { throw 'Missing expected-display RequestDeckEdit overload.' }
$nativeComplete = $nativeSessionType.GetMethod('CompleteDeckReply', $nativeFlags)
$nativeValidate = $nativeSessionType.GetMethod('ValidateDisplayedDeckRequest', $nativeFlags, $null,
    [type[]]@($nativeSnapshotType), $null)
if ($null -eq $nativeValidate -or $nativeValidate.ReturnType -ne [bool]) { throw 'Missing pure displayed-request guard.' }
$nativeChecks = New-Object 'System.Collections.Generic.List[string]'
if (-not ('RuinaNativeStateObserver' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
public static class RuinaNativeStateObserver
{
    public static int Count;
    public static Action Handler = () => Count++;
}
public sealed class RuinaNativeInstruction
{
    public int Offset;
    public string Opcode;
    public object Operand;
}
public static class RuinaNativeIl
{
    private static readonly System.Collections.Generic.Dictionary<short, System.Reflection.Emit.OpCode> Codes = BuildCodes();
    private static System.Collections.Generic.Dictionary<short, System.Reflection.Emit.OpCode> BuildCodes()
    {
        var result = new System.Collections.Generic.Dictionary<short, System.Reflection.Emit.OpCode>();
        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.FieldType == typeof(System.Reflection.Emit.OpCode))
            {
                var code = (System.Reflection.Emit.OpCode)field.GetValue(null);
                result[code.Value] = code;
            }
        return result;
    }
    public static RuinaNativeInstruction[] Read(System.Reflection.MethodBase method)
    {
        var result = new System.Collections.Generic.List<RuinaNativeInstruction>();
        var body = method.GetMethodBody();
        if (body == null) return result.ToArray();
        var bytes = body.GetILAsByteArray();
        for (int offset = 0; offset < bytes.Length;)
        {
            int start = offset;
            short value = bytes[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | bytes[offset++]));
            var code = Codes[value];
            object operand = null;
            int size = 0;
            switch (code.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineI: operand = (sbyte)bytes[offset]; size = 1; break;
                case System.Reflection.Emit.OperandType.ShortInlineVar: operand = bytes[offset]; size = 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: operand = BitConverter.ToUInt16(bytes, offset); size = 2; break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget: operand = offset + 1 + (sbyte)bytes[offset]; size = 1; break;
                case System.Reflection.Emit.OperandType.InlineBrTarget: operand = offset + 4 + BitConverter.ToInt32(bytes, offset); size = 4; break;
                case System.Reflection.Emit.OperandType.InlineI: operand = BitConverter.ToInt32(bytes, offset); size = 4; break;
                case System.Reflection.Emit.OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, offset); size = 4; break;
                case System.Reflection.Emit.OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, offset); size = 8; break;
                case System.Reflection.Emit.OperandType.InlineR: operand = BitConverter.ToDouble(bytes, offset); size = 8; break;
                case System.Reflection.Emit.OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(bytes, offset); break;
                case System.Reflection.Emit.OperandType.InlineString: operand = method.Module.ResolveString(BitConverter.ToInt32(bytes, offset)); size = 4; break;
                case System.Reflection.Emit.OperandType.InlineField:
                case System.Reflection.Emit.OperandType.InlineMethod:
                case System.Reflection.Emit.OperandType.InlineType:
                case System.Reflection.Emit.OperandType.InlineTok:
                    operand = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset),
                        method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    size = 4; break;
                case System.Reflection.Emit.OperandType.InlineSig: size = 4; break;
                default: throw new InvalidOperationException("Unexpected IL operand: " + code.OperandType);
            }
            offset += size;
            result.Add(new RuinaNativeInstruction { Offset = start, Opcode = code.Name, Operand = operand });
        }
        return result.ToArray();
    }
}
"@
}
function Set-NativeField([object]$Object, [string]$Name, [object]$Value) {
    $field = $Object.GetType().GetField($Name, $nativeFlags)
    if ($null -eq $field) { throw "Missing compiled native editor state: $Name" }
    $field.SetValue($Object, $Value)
}
function Get-NativeField([object]$Object, [string]$Name) {
    return $Object.GetType().GetField($Name, $nativeFlags).GetValue($Object)
}
function New-NativeSnapshot([uint32]$Revision = 11) {
    $snapshot = [Activator]::CreateInstance($nativeSnapshotType, $true)
    Set-NativeField $snapshot 'SelectedStageId' ([int]1)
    Set-NativeField $snapshot 'SelectedFloorId' ([byte]1)
    Set-NativeField $snapshot 'ClaimRevision' ([uint32]3)
    Set-NativeField $snapshot 'DeckRevision' $Revision
    return $snapshot
}
function New-NativeSession {
    $session = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($nativeSessionType)
    Set-NativeField $session '_guestAuthenticated' $true
    Set-NativeField $session '<LatestSnapshot>k__BackingField' (New-NativeSnapshot)
    Set-NativeField $session 'DeckStateChanged' ([RuinaNativeStateObserver]::Handler)
    [RuinaNativeStateObserver]::Count = 0
    return $session
}
function Assert-Native([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $nativeChecks.Add($Name)
}
function Invoke-NativeRequest([object]$Session, [object]$Snapshot) {
    # JIT of the full sender method resolves Unity Time's ECall even for an early
    # return. Execute its production guard, then inspect the caller IL below.
    return $nativeValidate.Invoke($Session, [object[]]@($Snapshot))
}
function New-NativeReply([uint32]$RequestId, [uint32]$Revision) {
    return [RuinaGuardSmokeLoader]::CreateFields($nativeReplyType,
        [string[]]@('RequestId', 'DeckRevision', 'Result'),
        [object[]]@($RequestId, $Revision, [Enum]::Parse($nativeResultType, 'Accepted')))
}

$nativeRequestIl = [RuinaNativeIl]::Read($nativeRequest)
$firstRequestCall = @($nativeRequestIl | Where-Object { $_.Opcode -eq 'call' -or $_.Opcode -eq 'callvirt' })[0]
Assert-Native ($firstRequestCall.Operand -eq $nativeValidate) 'Compiled sender calls displayed-request guard before transport, time, or request allocation'
$guardCallIndex = [Array]::IndexOf($nativeRequestIl, $firstRequestCall)
Assert-Native ($nativeRequestIl[$guardCallIndex + 1].Opcode -match '^brtrue' -and
    $nativeRequestIl[$guardCallIndex + 2].Opcode -eq 'ldc.i4.0' -and
    $nativeRequestIl[$guardCallIndex + 3].Opcode -eq 'ret') 'Guard rejection returns false before sender proceeds'

$session = New-NativeSession
$oldDisplay = New-NativeSnapshot
Assert-Native (-not (Invoke-NativeRequest $session $oldDisplay)) 'Different displayed DTO with equal versions cannot submit'
Assert-Native ((Get-NativeField $session '_nextDeckRequest') -eq 0 -and
    (Get-NativeField $session '_pendingDeckRequest') -eq 0) 'Stale display rejection occurs before request allocation or transport'
Assert-Native ($nativeSessionType.GetProperty('DeckStatus', $nativeFlags).GetValue($session, $null) -eq
    'The displayed deck changed; refresh before editing.') 'Stale display reports refresh requirement'
Assert-Native ([RuinaNativeStateObserver]::Count -eq 1) 'Stale display rejection notifies native controls'
$session = New-NativeSession
Assert-Native (-not (Invoke-NativeRequest $session $null) -and
    (Get-NativeField $session '_nextDeckRequest') -eq 0) 'Missing display cannot use current snapshot implicitly'
$session = New-NativeSession
Set-NativeField $session '_pendingDeckRequest' ([uint32]7)
Assert-Native (-not (Invoke-NativeRequest $session (Get-NativeField $session '<LatestSnapshot>k__BackingField')) -and
    (Get-NativeField $session '_nextDeckRequest') -eq 0 -and
    (Get-NativeField $session '_pendingDeckRequest') -eq 7) 'Outstanding request prevents new native request allocation'
$session = New-NativeSession
Set-NativeField $session '_stopped' $true
Assert-Native (-not (Invoke-NativeRequest $session (Get-NativeField $session '<LatestSnapshot>k__BackingField')) -and
    (Get-NativeField $session '_nextDeckRequest') -eq 0) 'Stopped editor session rejects displayed request before transport'

$session = New-NativeSession
Set-NativeField $session '_pendingDeckRequest' ([uint32]7)
$reply = New-NativeReply 7 12
$nativeComplete.Invoke($session, [object[]]@($reply)) | Out-Null
Assert-Native ((Get-NativeField $session '_pendingDeckRequest') -eq 7 -and
    [RuinaNativeStateObserver]::Count -eq 1) 'ACK ahead of snapshot preserves pending and informs native editor'
Set-NativeField $session '<LatestSnapshot>k__BackingField' (New-NativeSnapshot 12)
$nativeComplete.Invoke($session, [object[]]@((Get-NativeField $session '_deferredDeckReply'))) | Out-Null
Assert-Native ((Get-NativeField $session '_pendingDeckRequest') -eq 0 -and
    [RuinaNativeStateObserver]::Count -eq 2) 'Matching snapshot completion emits separate control-unlock notification'
$nativeComplete.Invoke($session, [object[]]@($reply)) | Out-Null
Assert-Native ([RuinaNativeStateObserver]::Count -eq 2) 'Duplicate ACK cannot re-notify or revive closed controls'
$session = New-NativeSession
Set-NativeField $session '_pendingDeckRequest' ([uint32]7)
Set-NativeField $session '<LatestSnapshot>k__BackingField' (New-NativeSnapshot 12)
$nativeComplete.Invoke($session, [object[]]@(New-NativeReply 7 12)) | Out-Null
Assert-Native ((Get-NativeField $session '_pendingDeckRequest') -eq 0 -and
    [RuinaNativeStateObserver]::Count -eq 1) 'Snapshot-first ACK completion emits unlock notification'
$session = New-NativeSession
Set-NativeField $session '_pendingDeckRequest' ([uint32]7)
Set-NativeField $session '_stopped' $true
$nativeComplete.Invoke($session, [object[]]@(New-NativeReply 7 11)) | Out-Null
Assert-Native ((Get-NativeField $session '_pendingDeckRequest') -eq 7 -and
    [RuinaNativeStateObserver]::Count -eq 0) 'Late ACK after session close does not notify native editor'

# Every native patch must match a real game method and valid Harmony injection types.
$nativeEditorType = $GuardSmokeModAssembly.GetType('RuinaCoop.NativeDeckEditor', $true)
$nativeTargets = New-Object 'System.Collections.Generic.List[object]'
function Add-NativeTarget([string]$TypeName, [string]$MethodName, [string[]]$Parameters,
    [string]$Prefix, [string]$Postfix = '') {
    $nativeTargets.Add([pscustomobject]@{
        TypeName = $TypeName; MethodName = $MethodName; Parameters = $Parameters
        Prefix = $Prefix; Postfix = $Postfix
    })
}
function Resolve-NativeType([string]$Name) {
    if ($Name -match '^(List|Stack)<(.+)>$') {
        $genericName = $Matches[1]; $argumentName = $Matches[2]
        $definition = if ($genericName -eq 'List') { [System.Collections.Generic.List[object]] } else { [System.Collections.Generic.Stack[object]] }
        return $definition.GetGenericTypeDefinition().MakeGenericType((Resolve-NativeType $argumentName))
    }
    if ($Name.EndsWith('[]')) {
        return (Resolve-NativeType $Name.Substring(0, $Name.Length - 2)).MakeArrayType()
    }
    $type = [type]::GetType($Name, $false)
    if ($null -eq $type) { $type = $GuardSmokeGameAssembly.GetType($Name, $false) }
    if ($null -eq $type) {
        foreach ($assembly in [AppDomain]::CurrentDomain.GetAssemblies()) {
            $type = $assembly.GetType($Name, $false)
            if ($null -ne $type) { break }
        }
    }
    if ($null -eq $type -and $Name.StartsWith('UnityEngine.EventSystems.')) {
        $type = [Reflection.Assembly]::LoadFrom((Join-Path $guardManaged 'UnityEngine.UI.dll')).GetType($Name, $false)
    }
    if ($null -eq $type) { throw "Missing native type: $Name" }
    return $type
}
Add-NativeTarget 'UI.UICardPanel' 'OnUpdatePhase' @() 'CardPanelPrefix'
Add-NativeTarget 'UI.UICardPanel' 'OnOpen' @() '' 'CardOpenedPostfix'
Add-NativeTarget 'UI.UILibrarianCharacterListPanel' 'OnUpdatePhase' @() 'RosterPrefix'
foreach ($name in @('OnSetSephirah', 'SetLibrarianCharacterListPanel_Default')) {
    Add-NativeTarget 'UI.UILibrarianCharacterListPanel' $name @('SephirahType') 'RosterPrefix'
}
Add-NativeTarget 'UI.UIController' 'SetSelectedUnit' @('UnitDataModel') 'SelectedUnitPrefix'
Add-NativeTarget 'UI.UIController' 'SetCurrentSephirah' @('SephirahType') 'SephirahPrefix'
Add-NativeTarget 'UI.UIController' 'CallUIPhase' @('UI.UIPhase') 'PhasePrefix'
Add-NativeTarget 'UI.UIController' 'CallUIPhase' @('System.Int32') 'PhasePrefix'
Add-NativeTarget 'UI.UIController' 'CallUIPhase_RevelAnim' @('UI.UIPhase') 'PhasePrefix'
Add-NativeTarget 'UI.UIController' 'CallUIPhase_FullTransitionAnim' @('UI.UIPhase', 'System.Action') 'PhasePrefix'
Add-NativeTarget 'UI.UIInvenCardListScroll' 'SetData' @('List<DiceCardItemModel>', 'UnitDataModel') 'InventoryPrefix'
Add-NativeTarget 'UI.UIInvenCardSlot' 'SetSlotState' @() 'StockStatePrefix'
Add-NativeTarget 'UI.UIStoryGradeFilter' 'Activate' @() 'GradeFilterPrefix'
foreach ($name in @('SetData', 'SetDeckButton')) {
    Add-NativeTarget 'UI.UILibrarianEquipDeckPanel' $name @() '' 'DeckPanelPostfix'
}
Add-NativeTarget 'UI.UILibrarianEquipDeckPanel' 'IsLockBattleBluePrimary' @() 'FalseInNativePrefix'
Add-NativeTarget 'UI.UILibrarianInfoInCardPhase' 'SetData' @('UnitDataModel') '' 'ProfilePostfix'
Add-NativeTarget 'UI.UILibrarianInfoInCardPhase' 'CheckDisabledBluePrimary' @() 'UnsafePrefix'
foreach ($name in @('IsLockUnit', 'isLockUnitForBluePrimary')) {
    Add-NativeTarget 'UnitDataModel' $name @() 'MirrorUnlockedPrefix'
}
Add-NativeTarget 'BookModel' 'TryGainUniquePassive' @() 'MirrorPassivePrefix'
Add-NativeTarget 'UI.UIMainTutorialManager' 'StartBattlePageTutorial' @() 'UnsafePrefix'
foreach ($name in @('OnClickSaveDeckButton', 'OnClickOpenDeckListButton', 'OnClickClearDeckButton')) {
    Add-NativeTarget 'UI.UILibrarianEquipDeckPanel' $name @() 'UnsafePrefix'
}
Add-NativeTarget 'UI.UIInvenCardSlot' 'OnClickCardEquipInfoButton' @() 'UnsafePrefix'
Add-NativeTarget 'UI.UICardEquipInfoPanel' 'OpenCardEquipInfo' @('DiceCardItemModel', 'System.Boolean') 'UnsafePrefix'
Add-NativeTarget 'UI.UILibrarianInfoInCardPhase' 'OnClickReleaseToggle' @() 'UnsafePrefix'
Add-NativeTarget 'UI.UILibrarianInfoInCardPhase' 'OnPointerClickPassiveSlot' @('UnityEngine.EventSystems.BaseEventData') 'PassiveClickPrefix'
Add-NativeTarget 'UI.UILibrarianInfoInCardPhase' 'OnPointerClickEquipPage' @('UnityEngine.EventSystems.BaseEventData') 'CorePageClickPrefix'
foreach ($spec in $nativeTargets) {
    $type = Resolve-NativeType $spec.TypeName
    $parameters = [type[]]@(foreach ($name in $spec.Parameters) { Resolve-NativeType $name })
    $original = $type.GetMethod($spec.MethodName, $nativeFlags, $null, $parameters, $null)
    Assert-Native ($null -ne $original) ("Native game patch target: $($spec.TypeName).$($spec.MethodName)")
    foreach ($hookName in @($spec.Prefix, $spec.Postfix)) {
        if ([string]::IsNullOrEmpty($hookName)) { continue }
        $hook = $nativeEditorType.GetMethod($hookName, $nativeFlags)
        Assert-Native ($null -ne $hook -and $hook.IsStatic) ("Native patch callback: $hookName")
        $originalParameters = $original.GetParameters()
        foreach ($parameter in $hook.GetParameters()) {
            $injectionType = $parameter.ParameterType
            if ($injectionType.IsByRef) { $injectionType = $injectionType.GetElementType() }
            if ($parameter.Name -eq '__instance') {
                Assert-Native (-not $original.IsStatic -and $injectionType.IsAssignableFrom($original.DeclaringType)) ("Instance injection: $hookName")
            }
            elseif ($parameter.Name -eq '__args') {
                Assert-Native ($injectionType -eq [object[]]) ("Argument-array injection: $hookName")
            }
            elseif ($parameter.Name -eq '__result') {
                Assert-Native ($original.ReturnType -ne [void] -and $injectionType -eq $original.ReturnType) ("Result injection: $hookName")
            }
            elseif ($parameter.Name -match '^__(\d+)$') {
                $index = [int]$Matches[1]
                Assert-Native ($index -lt $originalParameters.Length -and
                    $injectionType -eq $originalParameters[$index].ParameterType) ("Argument $index injection: $hookName")
            }
            else { throw "Unverified native Harmony parameter: $hookName.$($parameter.Name)" }
        }
    }
}
# Verify the actual private game fields used by the detached display factory.
foreach ($spec in @(
    @('UnitDataModel', '_bookItem', 'BookModel'),
    @('UnitDataModel', '_CustomBookItem', 'BookModel'),
    @('UnitDataModel', '_name', 'System.String'),
    @('UnitDataModel', '_tempName', 'System.String'),
    @('BookModel', '_deck', 'DeckModel'),
    @('BookModel', '_activatedAllPassives', 'List<PassiveModel>'),
    @('BookModel', '_characterSkin', 'System.String'),
    @('UnitCustomizingData', '_bUseCustomData', 'System.Boolean'),
    @('UnitCustomizingData', '_height', 'System.Int32')
)) {
    $type = $GuardSmokeGameAssembly.GetType($spec[0], $true)
    $field = [RuinaGuardSmokeLoader]::FindField($type, $spec[1])
    Assert-Native ($null -ne $field -and $field.FieldType -eq (Resolve-NativeType $spec[2])) ("Detached display factory field contract: $($spec[0]).$($spec[1])")
}

# These are every reflected model/UI member used by the native view, including
# hidden panels retained during close. No singleton getter or UI method executes.
foreach ($spec in @(
    @('UI.UIController', 'Panels', 'UI.UIPanel[]'),
    @('UI.UIController', '_uiData', 'UI.UIDataState'),
    @('UI.UIController', '_uiPhaseStack', 'Stack<UI.UIPhase>'),
    @('UI.UIController', 'CurrentUIPhase', 'UI.UIPhase'),
    @('UI.UIDataState', 'unit', 'UnitDataModel'),
    @('UI.UIDataState', 'sephirah', 'SephirahType'),
    @('UI.UICardPanel', '_equipInfoDeckPanel', 'UI.UILibrarianEquipDeckPanel'),
    @('UI.UICardPanel', '_invenCardList', 'UI.UIInvenCardListScroll'),
    @('UI.UICardPanel', 'librarianInfoPanel', 'UI.UILibrarianInfoInCardPhase'),
    @('UI.UICardPanel', 'CardEquipInfoPanel', 'UI.UICardEquipInfoPanel'),
    @('UI.UILibrarianEquipDeckPanel', '_equipDeckPanel', 'UI.UIEquipDeckCardList'),
    @('UI.UILibrarianEquipDeckPanel', '_unitdata', 'UnitDataModel'),
    @('UI.UILibrarianEquipDeckPanel', 'button_SaveDeckButton', 'UI.UICustomGraphicObject'),
    @('UI.UILibrarianEquipDeckPanel', 'button_OpenDeckListButton', 'UI.UICustomGraphicObject'),
    @('UI.UILibrarianEquipDeckPanel', 'button_EmptyDeckButton', 'UI.UICustomGraphicObject'),
    @('UI.UILibrarianEquipDeckPanel', 'button_CloseDeckButton', 'UI.UICustomGraphicObject'),
    @('UI.UIEquipDeckCardList', 'currentunit', 'UnitDataModel'),
    @('UI.UIEquipDeckCardList', 'changed', 'System.Boolean'),
    @('UI.UIInvenCardListScroll', '_unitdata', 'UnitDataModel'),
    @('UI.UIInvenCardListScroll', '_originCardList', 'List<DiceCardItemModel>'),
    @('UI.UIInvenCardListScroll', '_currentCardListForFilter', 'List<DiceCardItemModel>'),
    @('UI.UIInvenCardListScroll', 'curRow', 'System.Int32'),
    @('UI.UIInvenCardListScroll', 'scrollBar', 'UICustomScrollBar'),
    @('UI.UIInvenCardListScroll', 'slotHeight', 'System.Single'),
    @('UICustomScrollBar', 'scrollWindow', 'UnityEngine.RectTransform'),
    @('UnityEngine.RectTransform', 'anchoredPosition', 'UnityEngine.Vector2'),
    @('UI.UIStoryGradeFilter', 'canvasGroup', 'UnityEngine.CanvasGroup'),
    @('UI.UIStoryGradeFilter', 'gradeSlots', 'List<UI.UIStoryGradeFilterSlot>'),
    @('UnityEngine.CanvasGroup', 'alpha', 'System.Single'),
    @('UnityEngine.CanvasGroup', 'interactable', 'System.Boolean'),
    @('UnityEngine.CanvasGroup', 'blocksRaycasts', 'System.Boolean'),
    @('UI.UILibrarianCharacterListPanel', 'CharacterList', 'UI.UICharacterList'),
    @('UI.UILibrarianCharacterListPanel', 'SephirahSelectionButtons', 'List<UI.UISephirahSelectionButton>'),
    @('UI.UILibrarianCharacterListPanel', 'ob_tutorialhighlightedFrame', 'UnityEngine.GameObject'),
    @('UI.UICharacterList', 'slotList', 'List<UI.UICharacterSlot>'),
    @('UI.UICharacterList', 'isSelectableList', 'System.Boolean'),
    @('UI.UICharacterList', 'currentSelectedSlot', 'UI.UICharacterSlot'),
    @('UI.UICharacterSlot', 'toggleRoot', 'UnityEngine.GameObject'),
    @('UI.UICharacterSlot', 'portraitImage', 'UnityEngine.UI.RawImage'),
    @('UI.UICharacterRenderer', 'characterList', 'List<UI.UICharacter>'),
    @('UI.UICharacterRenderer', 'currentDataList', 'List<UnitDataModel>'),
    @('UI.UICharacter', 'unitModel', 'UnitDataModel'),
    @('UI.UICharacter', 'resName', 'System.String'),
    @('UI.UILibrarianInfoInCardPhase', 'unitdata', 'UnitDataModel'),
    @('UI.UILibrarianInfoInCardPhase', 'portrait', 'UnityEngine.UI.RawImage'),
    @('UI.UILibrarianInfoInCardPhase', 'StatsInfo', 'UI.UICharacterStatInfoPanel'),
    @('UI.UILibrarianInfoInCardPhase', 'passiveSlotsPanel', 'UI.UISetInfoSlotListSc'),
    @('UI.UILibrarianInfoInCardPhase', 'toggle_ReleaseToggle', 'UnityEngine.UI.Toggle'),
    @('UI.UILibrarianInfoInCardPhase', 'toggle_ReleaseToggle_Controller', 'UnityEngine.UI.Toggle'),
    @('UI.UILibrarianInfoInCardPhase', 'equipPageSelectable', 'UI.UICustomSelectable'),
    @('UI.UILibrarianInfoInCardPhase', 'PassiveListSelectable', 'UI.UICustomSelectable'),
    @('UI.UILibrarianInfoInCardPhase', 'isDisabledPassiveSuccession', 'System.Boolean'),
    @('UI.UIInvenCardSlot', 'CardModel', 'DiceCardItemModel'),
    @('UI.UIOriginCardSlot', 'CardModel', 'DiceCardItemModel'),
    @('UI.UIInvenCardSlot', 'slotState', 'UIINVENCARD_STATE'),
    @('UI.UIInvenCardSlot', 'deckLimitRoot', 'UnityEngine.GameObject'),
    @('UI.UIInvenCardSlot', 'txt_deckLimit', 'TMPro.TextMeshProUGUI'),
    @('LibraryModel', 'PlayHistory', 'PlayHistoryModel'),
    @('PlayHistoryModel', 'tutorial_EnterBattlePagePanel', 'System.Int32'),
    @('PlayHistoryModel', 'tutorial_SelectLibrarianSlot', 'System.Int32'),
    @('UnityEngine.Component', 'gameObject', 'UnityEngine.GameObject'),
    @('UnityEngine.GameObject', 'activeSelf', 'System.Boolean'),
    @('UnityEngine.GameObject', 'activeInHierarchy', 'System.Boolean'),
    @('TMPro.TextMeshProUGUI', 'text', 'System.String')
)) {
    $type = Resolve-NativeType $spec[0]
    $field = [RuinaGuardSmokeLoader]::FindField($type, $spec[1])
    $property = $null
    if ($null -eq $field) { $property = $type.GetProperty($spec[1], $nativeFlags) }
    $actualType = if ($null -ne $field) { $field.FieldType } elseif ($null -ne $property) { $property.PropertyType } else { $null }
    Assert-Native ($actualType -eq (Resolve-NativeType $spec[2])) ("Native UI member contract: $($spec[0]).$($spec[1])")
}
foreach ($spec in @(
    @('UI.UIDataState', 'unit'), @('UI.UIDataState', 'sephirah'),
    @('UI.UICharacterList', 'isSelectableList'), @('UI.UICharacterList', 'currentSelectedSlot'),
    @('UI.UIInvenCardListScroll', '_unitdata'), @('UI.UIInvenCardListScroll', 'curRow'),
    @('UI.UIEquipDeckCardList', 'currentunit'), @('UI.UIEquipDeckCardList', 'changed'),
    @('UI.UILibrarianEquipDeckPanel', '_unitdata'), @('UI.UILibrarianInfoInCardPhase', 'unitdata'),
    @('UI.UILibrarianInfoInCardPhase', 'isDisabledPassiveSuccession'),
    @('UI.UIInvenCardSlot', 'slotState'), @('UI.UICharacter', 'unitModel'), @('UI.UICharacter', 'resName')
)) {
    $field = [RuinaGuardSmokeLoader]::FindField((Resolve-NativeType $spec[0]), $spec[1])
    Assert-Native ($null -ne $field -and -not $field.IsInitOnly -and -not $field.IsLiteral) ("Native UI write contract: $($spec[0]).$($spec[1])")
}
$textProperty = (Resolve-NativeType 'TMPro.TextMeshProUGUI').GetProperty('text', $nativeFlags)
Assert-Native ($null -ne $textProperty -and $textProperty.CanWrite) 'Native card-state and stat text setters exist'
foreach ($spec in @(
    @('UnityEngine.CanvasGroup', 'alpha'),
    @('UnityEngine.CanvasGroup', 'interactable'),
    @('UnityEngine.CanvasGroup', 'blocksRaycasts'),
    @('UnityEngine.RectTransform', 'anchoredPosition')
)) {
    $property = (Resolve-NativeType $spec[0]).GetProperty($spec[1], $nativeFlags)
    Assert-Native ($null -ne $property -and $property.CanWrite) ("Native UI property write contract: $($spec[0]).$($spec[1])")
}
foreach ($name in @('hpText', 'breakText', 'emotionText', 'speedDiceText', 'speedDiceNumText', 'playpointText',
    'resistSlash', 'resistPentrate', 'resistHit', 'resistBreakSlash', 'resistBreakPentrate', 'resistBreakHit')) {
    $field = [RuinaGuardSmokeLoader]::FindField((Resolve-NativeType 'UI.UICharacterStatInfoPanel'), $name)
    Assert-Native ($null -ne $field -and $field.FieldType -eq (Resolve-NativeType 'TMPro.TextMeshProUGUI')) ("Native stat label contract: $name")
}
foreach ($spec in @(
    @('UI.UIInvenCardListScroll', 'GetMaxRow', @(), 'System.Int32'),
    @('UI.UIInvenCardListScroll', 'ApplyFilterAll', @(), 'System.Void'),
    @('UI.UICharacterRenderer', 'SetCharacter', @('UnitDataModel', 'System.Int32', 'System.Boolean', 'System.Boolean'), 'System.Void'),
    @('UI.UICharacterRenderer', 'DestroyCharacters', @(), 'System.Void'),
    @('UI.UICharacterRenderer', 'GetRenderTextureByIndexAndSize', @('System.Int32'), 'UnityEngine.Texture'),
    @('UI.UICharacterSlot', 'SetSlot', @('UnitDataModel', 'UnityEngine.Color', 'System.Boolean'), 'System.Void'),
    @('UI.UICharacterSlot', 'SetSelected', @('System.Boolean'), 'System.Void'),
    @('UI.UILibrarianCharacterListPanel', 'SetColor', @('UnityEngine.Color'), 'System.Void'),
    @('UI.UIColorManager', 'GetSephirahColor', @('SephirahType'), 'UnityEngine.Color'),
    @('UI.UICardEquipInfoPanel', 'CloseCardEquipInfo', @(), 'System.Void'),
    @('UI.UIInvenCardSlot', 'SetGrayScale', @('System.Boolean'), 'System.Void'),
    @('UI.UIInvenCardSlot', 'RefreshNumbersData', @(), 'System.Void'),
    @('Stack<UI.UIPhase>', 'Clear', @(), 'System.Void'),
    @('Stack<UI.UIPhase>', 'Push', @('UI.UIPhase'), 'System.Void')
)) {
    $type = Resolve-NativeType $spec[0]
    $parameters = [type[]]@(foreach ($name in $spec[2]) { Resolve-NativeType $name })
    $method = $type.GetMethod($spec[1], $nativeFlags, $null, $parameters, $null)
    Assert-Native ($null -ne $method -and $method.ReturnType -eq (Resolve-NativeType $spec[3])) ("Native UI call contract: $($spec[0]).$($spec[1])")
}
foreach ($name in @('UI.UIController', 'UI.UICharacterRenderer', 'UI.UIColorManager')) {
    $type = Resolve-NativeType $name
    $singletonType = $type
    $singleton = $null
    while ($null -ne $singletonType -and $null -eq $singleton) {
        foreach ($memberName in @('Instance', 'Manager', 'instance')) {
            $singleton = $singletonType.GetProperty($memberName, [Reflection.BindingFlags]'Static,Public,NonPublic,DeclaredOnly')
            if ($null -eq $singleton) { $singleton = $singletonType.GetField($memberName, [Reflection.BindingFlags]'Static,Public,NonPublic,DeclaredOnly') }
            if ($null -ne $singleton) { break }
        }
        $singletonType = $singletonType.BaseType
    }
    $actualType = if ($singleton -is [Reflection.PropertyInfo]) { $singleton.PropertyType } elseif ($singleton -is [Reflection.FieldInfo]) { $singleton.FieldType } else { $null }
    Assert-Native ($null -ne $actualType -and $type.IsAssignableFrom($actualType)) ("Native singleton reflection contract: $name")
}
$slotStateType = Resolve-NativeType 'UIINVENCARD_STATE'
Assert-Native ([int][Enum]::Parse((Resolve-NativeType 'UI.UIPhase'), 'Librarian_CardList') -eq 10) 'Native editor phase constant routes to the vanilla librarian combat-page view'
$slotStateNames = @('None', 'LimitedDeck', 'LimitedFloor', 'NumberZero', 'OnlyPage', 'RangeCard', 'MeleeCard')
for ($i = 0; $i -lt $slotStateNames.Length; $i++) {
    Assert-Native ([int][Enum]::Parse($slotStateType, $slotStateNames[$i]) -eq $i) ("Native card-state value contract: $($slotStateNames[$i])=$i")
}
$currentUnit = $GuardSmokeGameAssembly.GetType('UI.UIController', $true).GetProperty('CurrentUnit', $nativeFlags)
Assert-Native ($null -ne $currentUnit -and $currentUnit.CanRead -and -not $currentUnit.CanWrite -and
    $currentUnit.PropertyType.FullName -eq 'UnitDataModel') 'CurrentUnit remains getter-only and requires explicit UI selection routing'
foreach ($spec in @(
    @('UnitDataModel', @('System.Int32', 'SephirahType', 'System.Boolean')),
    @('BookModel', @('BookXmlInfo')),
    @('DiceCardItemModel', @('LOR_DiceSystem.DiceCardXmlInfo')),
    @('PassiveModel', @('LorId', 'System.Int32', 'System.Int32'))
)) {
    $type = $GuardSmokeGameAssembly.GetType($spec[0], $true)
    $parameters = [type[]]@(foreach ($name in $spec[1]) {
        $parameterType = [type]::GetType($name, $false)
        if ($null -eq $parameterType) { $parameterType = $GuardSmokeGameAssembly.GetType($name, $true) }
        $parameterType
    })
    Assert-Native ($null -ne $type.GetConstructor($nativeFlags, $null, $parameters, $null)) ("Detached display factory constructor contract: $($spec[0])")
}

# Execute the actual passive prefix with uninitialized, detached BookModels.
# Registration, disposal and the prefix are managed-only; the vanilla passive
# population method and all original game constructors remain unexecuted.
$nativeModelsType = $GuardSmokeModAssembly.GetType('RuinaCoop.NativeDeckModels', $true)
$nativeBookType = Resolve-NativeType 'BookModel'
$nativePassivePrefix = $nativeEditorType.GetMethod('MirrorPassivePrefix', $nativeFlags)
$ordinaryBook = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($nativeBookType)
$mirrorBook = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($nativeBookType)
$passiveArguments = [object[]]@($ordinaryBook, $true)
Assert-Native ([bool]$nativePassivePrefix.Invoke($null, $passiveArguments) -and [bool]$passiveArguments[1]) 'Ordinary books retain the vanilla unique-passive path'
$mirrorBooks = $nativeModelsType.GetField('MirrorBooks', $nativeFlags).GetValue($null)
$mirrorMark = $nativeModelsType.GetField('MirrorMark', $nativeFlags).GetValue($null)
$mirrorBooks.GetType().GetMethod('Add').Invoke($mirrorBooks, [object[]]@($mirrorBook, $mirrorMark)) | Out-Null
$passiveArguments = [object[]]@($mirrorBook, $true)
Assert-Native (-not [bool]$nativePassivePrefix.Invoke($null, $passiveArguments) -and -not [bool]$passiveArguments[1]) 'Mirrored empty passive lists cannot be populated from static key-page XML'
$retiredModels = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($nativeModelsType)
foreach ($field in $nativeModelsType.GetFields([Reflection.BindingFlags]'Instance,Public,NonPublic')) {
    if ($field.FieldType.IsGenericType -and $field.FieldType.GetGenericTypeDefinition() -eq [System.Collections.Generic.List[object]].GetGenericTypeDefinition()) {
        $field.SetValue($retiredModels, [Activator]::CreateInstance($field.FieldType))
    }
}
$nativeModelsType.GetField('_books', $nativeFlags).GetValue($retiredModels).Add($mirrorBook)
$nativeModelsType.GetMethod('Dispose', $nativeFlags).Invoke($retiredModels, @()) | Out-Null
$passiveArguments = [object[]]@($mirrorBook, $true)
Assert-Native (-not [bool]$nativePassivePrefix.Invoke($null, $passiveArguments) -and -not [bool]$passiveArguments[1]) 'Retired mirrored books remain guarded for late UI callbacks'
$constructionDepth = $nativeModelsType.GetField('_constructionDepth', $nativeFlags)
$previousDepth = $constructionDepth.GetValue($null)
try {
    $constructionDepth.SetValue($null, [int]1)
    $passiveArguments = [object[]]@($ordinaryBook, $true)
    Assert-Native (-not [bool]$nativePassivePrefix.Invoke($null, $passiveArguments) -and -not [bool]$passiveArguments[1]) 'Mirror construction blocks unique-passive population before registration'
}
finally { $constructionDepth.SetValue($null, $previousDepth) }

# Verify the supported game UI consumes the overridden live list rather than a
# second book cache; do not execute UI or the original XML-population method.
$nativePassiveGetter = $nativeBookType.GetMethod('GetPassiveInfoList', $nativeFlags, $null, [type[]]@([bool]), $null)
Assert-Native ($null -ne $nativePassiveGetter -and $nativePassiveGetter.ReturnType -eq (Resolve-NativeType 'List<BookPassiveInfo>')) 'Vanilla passive-list getter has the expected managed contract'
$nativePassiveGetterIl = [RuinaNativeIl]::Read($nativePassiveGetter)
$bookFieldsRead = @($nativePassiveGetterIl | Where-Object {
    $_.Operand -is [Reflection.FieldInfo] -and $_.Operand.DeclaringType -eq $nativeBookType
})
Assert-Native ($bookFieldsRead.Length -eq 1 -and $bookFieldsRead[0].Operand.Name -eq '_activatedAllPassives') 'Vanilla passive-list getter reads the mirrored live passive list without another book cache'
$boolArgumentLoads = @($nativePassiveGetterIl | Where-Object {
    $_.Opcode -eq 'ldarg.1' -or (($_.Opcode -eq 'ldarg' -or $_.Opcode -eq 'ldarg.s') -and $_.Operand -eq 1)
})
Assert-Native ($boolArgumentLoads.Length -eq 0) 'Passive-list bool parameter does not choose a local alternate cache'
$nativePassiveUi = (Resolve-NativeType 'UI.UISetInfoSlotListSc').GetMethod('SetStatsDataInEquipBook', $nativeFlags,
    $null, [type[]]@($nativeBookType), $null)
$nativePassiveUiIl = [RuinaNativeIl]::Read($nativePassiveUi)
$nativePassiveUiCalls = @($nativePassiveUiIl | Where-Object { $_.Operand -eq $nativePassiveGetter })
Assert-Native ($nativePassiveUiCalls.Length -gt 0) 'Vanilla passive UI consumes the inspected live getter'
foreach ($call in $nativePassiveUiCalls) {
    $index = [Array]::IndexOf($nativePassiveUiIl, $call)
    Assert-Native ($index -gt 0 -and $nativePassiveUiIl[$index - 1].Opcode -eq 'ldc.i4.0') 'Vanilla passive UI requests the authoritative active list with false'
}

# Appearance comparison uses only host DTOs, so normal authoritative deck,
# inventory and claim updates do not force the expensive native renderer path.
$nativeDeckType = $nativeSnapshotType.GetNestedType('UnitDeckEntry', $nativeFlags)
$nativeStockType = $nativeSnapshotType.GetNestedType('CardStockEntry', $nativeFlags)
$nativeAppearance = $nativeEditorType.GetMethod('SameAppearance', $nativeFlags, $null,
    [type[]]@($nativeSnapshotType, $nativeSnapshotType), $null)
Assert-Native ($null -ne $nativeAppearance -and $nativeAppearance.ReturnType -eq [bool]) 'Compiled pure appearance-comparison contract exists'
function New-NativeAppearanceSnapshot {
    $snapshot = New-NativeSnapshot
    $deck = [Activator]::CreateInstance($nativeDeckType, $true)
    Set-NativeField $deck 'BookId' ([int]501)
    Set-NativeField $deck 'BookInstanceId' ([int]601)
    Set-NativeField $deck 'UnitIdentity' ([uint64]401)
    $display = Get-NativeField $deck 'Display'
    Set-NativeField $display 'AppearanceAvailable' $true
    Set-NativeField $display 'DefaultBookId' ([int]1)
    Set-NativeField $display 'CharacterSkin' 'Original'
    $nativeSnapshotType.GetField('UnitDecks', $nativeFlags).GetValue($snapshot).Add($deck)
    return $snapshot
}
$appearanceBefore = New-NativeAppearanceSnapshot
$appearanceAfter = New-NativeAppearanceSnapshot
$afterDeck = $nativeSnapshotType.GetField('UnitDecks', $nativeFlags).GetValue($appearanceAfter)[0]
$afterDeck.GetType().GetField('Cards', $nativeFlags).GetValue($afterDeck).Add([int]100)
Set-NativeField $appearanceAfter 'DeckRevision' ([uint32]12)
Set-NativeField $appearanceAfter 'ClaimRevision' ([uint32]4)
$nativeSnapshotType.GetField('ClaimOwners', $nativeFlags).GetValue($appearanceAfter).Add([uint64]222)
$stock = [Activator]::CreateInstance($nativeStockType, $true)
Set-NativeField $stock 'Id' ([int]100)
Set-NativeField $stock 'Count' ([int]2)
$nativeSnapshotType.GetField('CardStock', $nativeFlags).GetValue($appearanceAfter).Add($stock)
Assert-Native ([bool]$nativeAppearance.Invoke($null, [object[]]@($appearanceBefore, $appearanceAfter))) 'Deck, inventory and claim changes retain the current native portrait'
$afterDisplay = Get-NativeField $afterDeck 'Display'
Set-NativeField $afterDisplay 'MaxHp' ([int]100)
$afterDisplay.GetType().GetField('PassiveIds', $nativeFlags).GetValue($afterDisplay).Add([int]101)
Assert-Native ([bool]$nativeAppearance.Invoke($null, [object[]]@($appearanceBefore, $appearanceAfter))) 'Authoritative stat and passive updates refresh without rebuilding appearance'
Set-NativeField $afterDisplay 'CharacterSkin' 'Changed'
Assert-Native (-not [bool]$nativeAppearance.Invoke($null, [object[]]@($appearanceBefore, $appearanceAfter))) 'Host skin changes require a new native portrait'
Set-NativeField $afterDisplay 'CharacterSkin' 'Original'
Set-NativeField $afterDisplay 'AppearanceAvailable' $false
Assert-Native (-not [bool]$nativeAppearance.Invoke($null, [object[]]@($appearanceBefore, $appearanceAfter))) 'Loss of supported host appearance invalidates the existing portrait'
Set-NativeField $afterDisplay 'AppearanceAvailable' $true
Set-NativeField $afterDeck 'BookId' ([int]502)
Assert-Native (-not [bool]$nativeAppearance.Invoke($null, [object[]]@($appearanceBefore, $appearanceAfter))) 'Host key-page changes require native appearance reevaluation'
$report = [pscustomobject]@{
    Passed = $true; ModAssembly = $GuardSmokeModAssembly.Location
    ModSha256 = (Get-FileHash -LiteralPath $GuardSmokeModAssembly.Location -Algorithm SHA256).Hash
    GameSha256 = (Get-FileHash -LiteralPath $GuardSmokeGameAssembly.Location -Algorithm SHA256).Hash
    RequestMethod = $nativeRequest.ToString(); ExecutedRequestGuard = $nativeValidate.ToString()
    SenderVerifiedByIlOnly = $true; AckMethod = $nativeComplete.ToString()
    NativePatchTargetCount = $nativeTargets.Count
    CheckCount = $nativeChecks.Count; Checks = $nativeChecks.ToArray()
}
if ($nativeRequestedOutputPath) { $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $nativeRequestedOutputPath -Encoding UTF8 }
Write-Output "PASS: $($nativeChecks.Count) native display-guard, ACK-notification, real UI target/member checks; sender ordering verified by IL only; no Steam or Unity native operations."
