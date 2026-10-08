# Read-only real-DLL contracts and pure compiled lifecycle state. Never invokes
# Unity UI, Steam transport, game model constructors, or original equip methods.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$equipmentOutputPath = $OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$equipmentFlags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$equipmentChecks = New-Object 'System.Collections.Generic.List[string]'
function Assert-Equipment([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $equipmentChecks.Add($Name)
}
function Resolve-EquipmentType([string]$Name) {
    if ($Name -match '^List<(.+)>$') {
        return [System.Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType((Resolve-EquipmentType $Matches[1]))
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
    if ($null -eq $type) { throw "Missing equipment type: $Name" }
    return $type
}
if (-not ('RuinaNativeIl' -as [type])) {
    # Reuse the standalone IL reader, without running the native-deck tests.
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native-target-smoke.ps1') -Raw
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
    $reader = $ast.FindAll({ param($node)
        ($node -is [Management.Automation.Language.StringConstantExpressionAst] -or
         $node -is [Management.Automation.Language.ExpandableStringExpressionAst]) -and
        $node.Value.Contains('class RuinaNativeIl')
    }, $true)[0].Value
    Add-Type -TypeDefinition $reader
}
$equipmentEditor = $GuardSmokeModAssembly.GetType('RuinaCoop.NativeEquipmentEditor', $true)
$equipmentTargets = New-Object 'System.Collections.Generic.List[object]'
function Add-EquipmentTarget([string]$Type, [string]$Method, [string[]]$Parameters, [string]$Prefix, [string]$Postfix = '') {
    $equipmentTargets.Add([pscustomobject]@{ Type = $Type; Method = $Method; Args = $Parameters; Prefix = $Prefix; Postfix = $Postfix })
}
Add-EquipmentTarget 'UI.UIEquipPageInventoryPanel' 'OnOpen' @() 'PanelOpenPrefix'
Add-EquipmentTarget 'UI.UIEquipPageInventoryPanel' 'OnUpdatePhase' @() 'PanelUpdatePrefix'
Add-EquipmentTarget 'UI.UIEquipPageInventoryPanel' 'OnClose' @() 'PanelClosePrefix'
Add-EquipmentTarget 'UI.UIEquipPageInventoryLeftPanel' 'UpdateEquipPageList' @('System.Boolean') 'LeftListPrefix'
Add-EquipmentTarget 'UI.UIEquipPageScrollList' 'FilterBookModels' @('List<BookModel>') 'FilterPrefix'
Add-EquipmentTarget 'UI.UIEquipPagePreviewPanel' 'SetData' @('BookModel') '' 'PreviewPostfix'
Add-EquipmentTarget 'UI.UIEquipPagePreviewPanel' 'SetPassiveBookInfoPanel' @() 'PassivePanelPrefix'
Add-EquipmentTarget 'UI.UIOriginEquipPageSlot' 'SetActiveSlot' @('System.Boolean') 'SlotActivePrefix'
Add-EquipmentTarget 'UI.UICustomSelectable' 'OnPointerDown' @('UnityEngine.EventSystems.PointerEventData') 'PressPrefix'
Add-EquipmentTarget 'UI.UICustomSelectable' 'OnSubmit' @('UnityEngine.EventSystems.BaseEventData') 'PressPrefix'
foreach ($type in @('UI.UIInvenEquipPageSlot', 'UI.UIInvenLeftEquipPageSlot')) {
    Add-EquipmentTarget $type 'SetData' @('BookModel') 'SlotDataPrefix'
    Add-EquipmentTarget $type 'SetOperatingPanel' @() 'OperatingPrefix'
    Add-EquipmentTarget $type 'SetActiveOperatinPanel' @('System.Boolean') 'OperatingVisibilityPrefix'
    Add-EquipmentTarget $type 'OnPointerClick' @('UnityEngine.EventSystems.BaseEventData') 'SlotClickPrefix'
    foreach ($method in @('OnClickPassiveSuccessionButton', 'OnClickRelaseButton', 'OnClickBookMarkButton')) {
        Add-EquipmentTarget $type $method @() 'UnsafeSlotPrefix'
    }
}
Add-EquipmentTarget 'UI.UIMainTutorialManager' 'StartEquipPageOpenTutorial' @() 'TutorialPrefix'
Add-EquipmentTarget 'UI.UIMainTutorialManager' 'StartEquipPageChangeTutorial' @() 'TutorialPrefix'
Add-EquipmentTarget 'UI.UIMainTutorialManager' 'StartEquipPageClickTutorial' @('UnityEngine.RectTransform', 'UnityEngine.RectTransform') 'TutorialPrefix'
foreach ($spec in $equipmentTargets) {
    $type = Resolve-EquipmentType $spec.Type
    $parameters = [type[]]@(foreach ($name in $spec.Args) { Resolve-EquipmentType $name })
    $original = $type.GetMethod($spec.Method, $equipmentFlags, $null, $parameters, $null)
    Assert-Equipment ($null -ne $original) "Real patch target: $($spec.Type).$($spec.Method)"
    foreach ($hookName in @($spec.Prefix, $spec.Postfix)) {
        if ([string]::IsNullOrEmpty($hookName)) { continue }
        $hook = $equipmentEditor.GetMethod($hookName, $equipmentFlags)
        Assert-Equipment ($null -ne $hook -and $hook.IsStatic) "Static patch callback: $hookName"
        Assert-Equipment ($hook.ReturnType -eq $(if ($hookName -eq $spec.Prefix) { [bool] } else { [void] })) "Patch return contract: $hookName"
        $originalParameters = $original.GetParameters()
        foreach ($parameter in $hook.GetParameters()) {
            $injected = $parameter.ParameterType
            if ($injected.IsByRef) { $injected = $injected.GetElementType() }
            if ($parameter.Name -eq '__instance') {
                Assert-Equipment (-not $original.IsStatic -and $injected.IsAssignableFrom($original.DeclaringType)) "Instance injection: $hookName"
            } elseif ($parameter.Name -eq '__result') {
                Assert-Equipment ($parameter.ParameterType.IsByRef -and $original.ReturnType -eq $injected) "Result injection: $hookName"
            } elseif ($parameter.Name -match '^__(\d+)$') {
                $index = [int]$Matches[1]
                Assert-Equipment ($index -lt $originalParameters.Length -and $originalParameters[$index].ParameterType -eq $injected) "Argument $index injection: $hookName"
            } else { throw "Unverified equipment injection: $hookName.$($parameter.Name)" }
        }
    }
}

# Include every member read or written by this adapter's reflection calls.
foreach ($spec in @(
    @('UI.UIController', 'CurrentUIPhase', 'UI.UIPhase'),
    @('UI.UIEquipPageInventoryPanel', '_equipPageScrollListPanel', 'UI.UIEquipPageScrollList'),
    @('UI.UIEquipPageInventoryPanel', '_equipLeftPanel', 'UI.UIEquipPageInventoryLeftPanel'),
    @('UI.UIEquipPageInventoryPanel', '_librarianInfo', 'UI.UILibrarianInfoInCardPhase'),
    @('UI.UIEquipPageInventoryPanel', '_equipPagePreviewPanel', 'UI.UIEquipPagePreviewPanel'),
    @('UI.UIEquipPageInventoryPanel', 'currentOverSlot', 'UI.UIOriginEquipPageSlot'),
    @('UI.UIEquipPageInventoryPanel', '_currentSelectedSlot', 'UI.UIOriginEquipPageSlot'),
    @('UI.UIEquipPageInventoryPanel', 'isSaveCheck', 'System.Boolean'),
    @('UI.UIEquipPageInventoryPanel', 'isPreviewVisible', 'System.Boolean'),
    @('UI.UIEquipPageInventoryLeftPanel', 'equipPageList', 'UI.UIInvenLeftEquipPageList'),
    @('UI.UIEquipPageInventoryLeftPanel', 'button_BookMark', 'UnityEngine.UI.Button'),
    @('UI.UIEquipPageInventoryLeftPanel', 'button_EquipedBook', 'UnityEngine.UI.Button'),
    @('UI.UIEquipPageInventoryLeftPanel', 'currentShowState', 'UI.UIBookInvenLeftPanelShowState'),
    @('UI.UIEquipPageScrollList', 'bookSortFilter', 'UI.UIBookListSortPanel'),
    @('UI.UIEquipPageScrollList', 'GradeFilter', 'UI.UIStoryGradeFilter'),
    @('UI.UIOriginEquipPageSlot', '_bookDataModel', 'BookModel'),
    @('UI.UIOriginEquipPageSlot', 'isEmptyBook', 'System.Boolean'),
    @('UI.UIOriginEquipPageSlot', 'BookName', 'TMPro.TextMeshProUGUI'),
    @('UI.UIOriginEquipPageSlot', 'Icon', 'UnityEngine.UI.Image'),
    @('UI.UIOriginEquipPageSlot', 'IconGlow', 'UnityEngine.UI.Image'),
    @('UI.UIOriginEquipPageSlot', 'currenSlotState', 'UI.UIEquipPageSlotState'),
    @('UI.UIOriginEquipPageSlot', 'cg', 'UnityEngine.CanvasGroup'),
    @('UI.UIOriginEquipPageSlot', 'selectable', 'UI.UICustomSelectable'),
    @('UI.UICustomGraphicObject', 'selectable', 'UI.UICustomSelectable'),
    @('UI.UICustomSelectable', 'interactable', 'System.Boolean'),
    @('UI.UIInvenEquipPageSlot', 'isBlock', 'System.Boolean'),
    @('UI.UIInvenEquipPageSlot', 'ob_blockFrame', 'UnityEngine.GameObject'),
    @('UI.UIInvenEquipPageSlot', 'ob_equipRoot', 'UnityEngine.GameObject'),
    @('UI.UIInvenEquipPageSlot', 'ob_OperatingPanel', 'UnityEngine.GameObject'),
    @('UI.UIInvenEquipPageSlot', 'cg_operatingPanel', 'UnityEngine.CanvasGroup'),
    @('UI.UIInvenEquipPageSlot', 'txt_equipButton', 'TMPro.TextMeshProUGUI'),
    @('UI.UIEquipPagePreviewPanel', 'bookDataModel', 'BookModel'),
    @('UI.UIEquipPagePreviewPanel', 'cg_receivedPassiveBookListPanel', 'UnityEngine.CanvasGroup'),
    @('UI.UIEquipPagePreviewPanel', 'cg_givePassiveBookPanel', 'UnityEngine.CanvasGroup'),
    @('UI.UIEquipPagePreviewPanel', 'StatsInfo', 'UI.UICharacterStatInfoPanel'),
    @('UI.UIEquipPagePreviewPanel', 'passiveSlotsPanel', 'UI.UISetInfoSlotListSc'),
    @('UI.UIEquipPagePreviewPanel', 'equipedCardListPanel', 'UI.UIEquipCardList'),
    @('UI.UICustomGraphicObject', 'interactable', 'System.Boolean'),
    @('UnityEngine.Component', 'gameObject', 'UnityEngine.GameObject'),
    @('UnityEngine.GameObject', 'activeSelf', 'System.Boolean'),
    @('UnityEngine.UI.Image', 'sprite', 'UnityEngine.Sprite'),
    @('TMPro.TextMeshProUGUI', 'text', 'System.String'),
    @('UnityEngine.CanvasGroup', 'alpha', 'System.Single'),
    @('UnityEngine.CanvasGroup', 'interactable', 'System.Boolean'),
    @('UnityEngine.CanvasGroup', 'blocksRaycasts', 'System.Boolean')
)) {
    $type = Resolve-EquipmentType $spec[0]
    $field = [RuinaGuardSmokeLoader]::FindField($type, $spec[1])
    $property = if ($null -eq $field) { $type.GetProperty($spec[1], $equipmentFlags) } else { $null }
    $actual = if ($null -ne $field) { $field.FieldType } elseif ($null -ne $property) { $property.PropertyType } else { $null }
    Assert-Equipment ($actual -eq (Resolve-EquipmentType $spec[2])) "Reflected UI member: $($spec[0]).$($spec[1])"
    if ($null -ne $property -and $spec[1] -in @('text', 'sprite', 'interactable', 'blocksRaycasts', 'alpha')) {
        Assert-Equipment ($property.CanWrite) "Writable UI property: $($spec[0]).$($spec[1])"
    }
}
foreach ($typeName in @('UI.UIInvenEquipPageSlot', 'UI.UIInvenLeftEquipPageSlot')) {
    foreach ($name in @('button_BookMark', 'button_Equip', 'button_PassiveSuccession', 'button_ReleaseButton', 'button_EmptyDeck')) {
        $field = [RuinaGuardSmokeLoader]::FindField((Resolve-EquipmentType $typeName), $name)
        Assert-Equipment ($null -ne $field -and $field.FieldType -eq (Resolve-EquipmentType 'UI.UICustomGraphicObject')) "Inventory operation button: $typeName.$name"
    }
}
foreach ($name in @('hpText', 'breakText', 'emotionText', 'speedDiceText', 'speedDiceNumText', 'playpointText',
    'resistSlash', 'resistPentrate', 'resistHit', 'resistBreakSlash', 'resistBreakPentrate', 'resistBreakHit')) {
    $field = [RuinaGuardSmokeLoader]::FindField((Resolve-EquipmentType 'UI.UICharacterStatInfoPanel'), $name)
    Assert-Equipment ($null -ne $field -and $field.FieldType -eq (Resolve-EquipmentType 'TMPro.TextMeshProUGUI')) "Authoritative preview stat label: $name"
}
foreach ($spec in @(
    @('UI.UIController', 'CallUIPhase', @('UI.UIPhase'), 'System.Void'),
    @('UI.UIPanel', 'RevealAnim', @(), 'System.Void'),
    @('UI.UIPanel', 'SetActiveCg', @('System.Boolean'), 'System.Void'),
    @('UI.UIEquipPageInventoryPanel', 'HidePreviewPanel', @(), 'System.Void'),
    @('UI.UIEquipPageInventoryPanel', 'OnClickSlot', @('UI.UIOriginEquipPageSlot'), 'System.Void'),
    @('UI.UIEquipPageScrollList', 'SetData', @('List<BookModel>', 'UnitDataModel', 'System.Boolean'), 'System.Void'),
    @('UI.UIEquipPageScrollList', 'OpenInit', @(), 'System.Void'),
    @('UI.UIInvenLeftEquipPageList', 'SetBooksData', @('List<BookModel>', 'UnitDataModel', 'System.Boolean'), 'System.Void'),
    @('UI.UILibrarianInfoInCardPhase', 'SetData', @('UnitDataModel'), 'System.Void'),
    @('UI.UIOriginEquipPageSlot', 'SetColorFrame', @('UI.UIEquipPageSlotState'), 'System.Void'),
    @('UI.UIStoryGradeFilter', 'GetStoryGradeFilter', @(), 'List<Grade>'),
    @('BookXmlList', 'GetData', @('LorId', 'System.Boolean'), 'BookXmlInfo'),
    @('UnitDataModel', 'EquipBookForUI', @('BookModel', 'System.Boolean', 'System.Boolean'), 'System.Boolean')
)) {
    $type = Resolve-EquipmentType $spec[0]
    $parameters = [type[]]@(foreach ($name in $spec[2]) { Resolve-EquipmentType $name })
    $method = $type.GetMethod($spec[1], $equipmentFlags, $null, $parameters, $null)
    Assert-Equipment ($null -ne $method -and $method.ReturnType -eq (Resolve-EquipmentType $spec[3])) "Real call signature: $($spec[0]).$($spec[1])"
}

# Transaction/Capture use existing private lists so GetFloor cannot create a floor.
foreach ($spec in @(
    @('LibraryModel', '_floorList', 'List<LibraryFloorModel>'),
    @('LibraryFloorModel', '_unitDataList', 'List<UnitDataModel>'),
    @('UnitDataModel', '_bookItem', 'BookModel'),
    @('UnitDataModel', '_CustomBookItem', 'BookModel'),
    @('BookModel', '_deck', 'DeckModel'),
    @('BookModel', '_deckList', 'List<DeckModel>'),
    @('BookModel', '_activatedAllPassives', 'List<PassiveModel>'),
    @('BookInventoryModel', '_bookList', 'List<BookModel>'),
    @('InventoryModel', '_cardList', 'List<DiceCardItemModel>'),
    @('DeckModel', '_deck', 'List<LOR_DiceSystem.DiceCardXmlInfo>')
)) {
    $field = [RuinaGuardSmokeLoader]::FindField((Resolve-EquipmentType $spec[0]), $spec[1])
    Assert-Equipment ($null -ne $field -and $field.FieldType -eq (Resolve-EquipmentType $spec[2])) "Live transaction/capture field: $($spec[0]).$($spec[1])"
}
foreach ($model in @('BookModel', 'PassiveModel')) {
    $type = Resolve-EquipmentType $model
    $origin = [RuinaGuardSmokeLoader]::FindField($type, 'originData')
    $reserved = [RuinaGuardSmokeLoader]::FindField($type, 'reservedData')
    Assert-Equipment ($null -ne $origin -and $null -ne $reserved -and $origin.FieldType -eq $reserved.FieldType) "Origin/reserved save contract: $model"
    $data = $origin.FieldType
    $fields = if ($model -eq 'BookModel') {
        @(@('equipedPassiveBookInstanceId', 'System.Int32'), @('equipedBookIdListInPassive', 'List<System.Int32>'))
    } else {
        @(@('currentpassive', 'PassiveXmlInfo'), @('receivepassivebookId', 'System.Int32'), @('givePassiveBookId', 'System.Int32'))
    }
    foreach ($spec in $fields) {
        $field = [RuinaGuardSmokeLoader]::FindField($data, $spec[0])
        Assert-Equipment ($null -ne $field -and $field.FieldType -eq (Resolve-EquipmentType $spec[1])) "Committed passive/draft field: $model.$($spec[0])"
    }
}

# The compiled sender must reject stale displays before Steam, Time or allocation.
$session = $GuardSmokeModAssembly.GetType('RuinaCoop.RelaySession', $true)
$snapshot = $GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot', $true)
$request = $session.GetMethod('RequestCorePageEdit', $equipmentFlags, $null, [type[]]@($snapshot, [byte], [uint64]), $null)
$validate = $session.GetMethod('ValidateDisplayedDeckRequest', $equipmentFlags)
Assert-Equipment ($null -ne $request -and $request.ReturnType -eq [bool]) 'Expected-display core sender signature'
$requestIl = [RuinaNativeIl]::Read($request)
$firstCall = @($requestIl | Where-Object { $_.Opcode -eq 'call' -or $_.Opcode -eq 'callvirt' })[0]
Assert-Equipment ($firstCall.Operand -eq $validate) 'Core sender validates displayed snapshot before transport or allocation'
$callIndex = [Array]::IndexOf($requestIl, $firstCall)
Assert-Equipment ($requestIl[$callIndex + 1].Opcode -match '^brtrue' -and
    $requestIl[$callIndex + 2].Opcode -eq 'ldc.i4.0' -and $requestIl[$callIndex + 3].Opcode -eq 'ret') 'Core sender returns immediately for a stale display'

# Lifecycle generation is the production helper, not a duplicated test model.
$generation = $equipmentEditor.GetField('_generation', $equipmentFlags)
$advance = $equipmentEditor.GetMethod('AdvanceGeneration', $equipmentFlags)
$generation.SetValue($null, [uint64]10)
$advance.Invoke($null, @()) | Out-Null
Assert-Equipment ($generation.GetValue($null) -eq 11) 'Rendered-button generation advances in production helper'
$generation.SetValue($null, [uint64]::MaxValue)
$advance.Invoke($null, @()) | Out-Null
Assert-Equipment ($generation.GetValue($null) -eq 1) 'Generation wrap never creates the invalid zero token'
foreach ($methodName in @('TryOpen', 'Refresh', 'Close')) {
    $method = $equipmentEditor.GetMethod($methodName, $equipmentFlags)
    $calls = @([RuinaNativeIl]::Read($method) | Where-Object { $_.Operand -eq $advance })
    Assert-Equipment ($calls.Count -ge 1) "Rendered callbacks retired on $methodName"
}
$route = $equipmentEditor.GetMethod('TryHandleCorePageClick', $equipmentFlags)
$routeCalls = @([RuinaNativeIl]::Read($route) | Where-Object { $_.Opcode -eq 'call' -or $_.Opcode -eq 'callvirt' })
Assert-Equipment (@($routeCalls | Where-Object { $_.Operand -eq $request }).Count -eq 1) 'Inventory click has one core-request send path'
Assert-Equipment (@($routeCalls | Where-Object { $_.Operand -is [Reflection.MethodBase] -and $_.Operand.Name -match '^EquipBook' }).Count -eq 0) 'Inventory click never directly equips a mirror or real book'
$guard = $GuardSmokeModAssembly.GetType('RuinaCoop.DeckGuard', $true).GetMethod('UiEquipBookPrefix', $equipmentFlags)
$guardCalls = @([RuinaNativeIl]::Read($guard) | Where-Object { $_.Opcode -eq 'call' -or $_.Opcode -eq 'callvirt' })
Assert-Equipment ($guardCalls[0].Operand -eq $route) 'Existing vanilla equip guard routes mirror clicks before refusing mutation'
foreach ($name in @('PanelOpenPrefix', 'PanelUpdatePrefix', 'PanelClosePrefix', 'LeftListPrefix', 'FilterPrefix',
    'SlotDataPrefix', 'SlotActivePrefix', 'OperatingPrefix', 'OperatingVisibilityPrefix', 'SlotClickPrefix', 'PressPrefix', 'PassivePanelPrefix', 'PreviewPostfix')) {
    $method = $equipmentEditor.GetMethod($name, $equipmentFlags)
    $forbidden = @([RuinaNativeIl]::Read($method) | Where-Object {
        $_.Operand -is [Reflection.MethodBase] -and ($_.Operand.DeclaringType.Name -in @('BookInventoryModel', 'LibraryModel', 'SaveManager'))
    })
    Assert-Equipment ($forbidden.Count -eq 0) "Native mirror callback has no real inventory/tutorial/save entry: $name"
}
$result = [pscustomobject]@{ Passed = $true; Checks = $equipmentChecks.Count; PatchTargets = $equipmentTargets.Count; CheckNames = $equipmentChecks.ToArray() }
if (-not [string]::IsNullOrWhiteSpace($equipmentOutputPath)) {
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $equipmentOutputPath -Encoding UTF8
}
Write-Output "equipment-target-smoke: $($equipmentChecks.Count) checks passed; $($equipmentTargets.Count) native patch targets verified."
