# Read-only real-DLL contracts and pure compiled lifecycle state. Never invokes
# Unity UI, Steam transport, game model constructors, or original equip methods.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$passiveOutputPath = $OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$passiveFlags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$passiveChecks = New-Object 'System.Collections.Generic.List[string]'
function Assert-Passive([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $passiveChecks.Add($Name)
}
function Resolve-PassiveType([string]$Name) {
    if ($Name -match '^List<(.+)>$') {
        return [System.Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType((Resolve-PassiveType $Matches[1]))
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
    if ($null -eq $type) { throw "Missing passive type: $Name" }
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
$passiveEditor = $GuardSmokeModAssembly.GetType('RuinaCoop.NativePassiveEditor', $false)
$passiveTargets = New-Object 'System.Collections.Generic.List[object]'
function Add-PassiveTarget([string]$Type, [string]$Method, [string[]]$Parameters, [string]$Hook, [bool]$Postfix=$false) {
    $passiveTargets.Add([pscustomobject]@{ Type=$Type; Method=$Method; Args=$Parameters; Hook=$Hook; Postfix=$Postfix })
}
$popup = Resolve-PassiveType 'UI.UIPassiveSuccessionPopup'
$applyEvent = $popup.GetMethod('SetData', $passiveFlags).GetParameters()[1].ParameterType.FullName
Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' 'SetData' @('UnitDataModel',$applyEvent) 'SetDataPrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' 'SetDataOnly' @('BookModel',$applyEvent) 'SetDataOnlyPrefix'
foreach ($row in @(@('Open','OpenPrefix'),@('Close','ClosePrefix'),@('InitReservedData','InitPrefix'),@('OnClickReleaseAllEquipedBookButton','ResetPrefix'),@('OnClickApplyButton','ApplyPrefix'),@('OnClickCancelButton','CancelPrefix'),@('OnCancel','CancelPrefix'),@('CloseDefault','UnsafePrefix'))) {
    Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' $row[0] @() $row[1]
}
foreach ($row in @(@('EquipBook','AttachPrefix'),@('UnEquipBook','DetachPrefix'),@('UnEquipBookOtherBook','OtherBookPrefix'))) {
    Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' $row[0] @('BookModel') $row[1]
}
Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' 'ChangePassive' @('UI.UIPassiveSuccessionCenterPassiveSlot') 'ChangePrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' 'ReleasePassive' @('PassiveModel') 'ReleasePrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' 'ReleasePassiveReverse' @('UI.UIPassiveSuccessionSlot') 'ReleaseReversePrefix'
foreach ($name in @('<OnClickApplyButton>b__42_0','<OnClickCancelButton>b__43_0')) { Add-PassiveTarget 'UI.UIPassiveSuccessionPopup' $name @('System.Boolean') 'LateCallbackPrefix' }
Add-PassiveTarget 'UI.UIPassiveSuccessionBookSlot' '<OnPointerClick>b__41_0' @('System.Boolean') 'SourceLatePrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionBookSlot' 'SetDisabledByTheBluePrimary' @() 'DisabledPrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionBookListPanel' 'SetPreviewData' @('UI.UIPassiveSuccessionBookSlot') 'PreviewPrefix'
Add-PassiveTarget 'BookModel' 'GetEquipedBookList' @('System.Boolean') 'EquippedQueryPrefix'
Add-PassiveTarget 'BookModel' 'GetGiveBookModel' @() 'ReceiverQueryPrefix'
Add-PassiveTarget 'BookModel' 'GetMaxPassiveCost' @() 'BudgetPrefix'
Add-PassiveTarget 'BookModel' 'CanToGivePassiveBook' @('System.Boolean') 'CanGiveBookPrefix'
foreach ($type in @('UI.UICustomSelectable','UnityEngine.EventSystems.EventTrigger')) {
    Add-PassiveTarget $type 'OnPointerDown' @('UnityEngine.EventSystems.PointerEventData') 'PressPrefix'
    Add-PassiveTarget $type 'OnSubmit' @('UnityEngine.EventSystems.BaseEventData') 'PressPrefix'
}
Add-PassiveTarget 'UnityEngine.UI.Selectable' 'OnPointerDown' @('UnityEngine.EventSystems.PointerEventData') 'PressPrefix'
Add-PassiveTarget 'UnityEngine.UI.Button' 'OnSubmit' @('UnityEngine.EventSystems.BaseEventData') 'PressPrefix'
Add-PassiveTarget 'UI.UIPassiveSuccessionCenterEquipBookSlot' 'SetData' @('BookModel') 'SlotPostfix' $true
Add-PassiveTarget 'UI.UIPassiveSuccessionCenterEquipBookSlot' 'OnClickReleaseButton' @() 'SlotClickPrefix'
foreach ($type in @('UI.UIPassiveSuccessionBookSlot','UI.UIPassiveSuccessionCenterPassiveSlot','UI.UIPassiveSuccessionSlot','UIPassiveSuccessionEquipBookSlot')) {
    Add-PassiveTarget $type 'OnPointerClick' @('UnityEngine.EventSystems.BaseEventData') 'SlotClickPrefix'
    $method = if ($type -eq 'UI.UIPassiveSuccessionSlot') { 'SetDataModel' } else { 'SetData' }
    $model = if ($type -eq 'UI.UIPassiveSuccessionBookSlot' -or $type -eq 'UIPassiveSuccessionEquipBookSlot') { 'BookModel' } else { 'PassiveModel' }
    Add-PassiveTarget $type $method @($model) 'SlotPostfix' $true
}
foreach ($type in @('UI.UIPassiveSuccessionBookSlot','UI.UIPassiveSuccessionCenterPassiveSlot')) { Add-PassiveTarget $type 'OnXEvent' @() 'XPrefix' }
foreach ($spec in $passiveTargets) {
    $type = Resolve-PassiveType $spec.Type
    $parameters = [type[]]@(foreach ($name in $spec.Args) { Resolve-PassiveType $name })
    $original = $type.GetMethod($spec.Method,$passiveFlags,$null,$parameters,$null)
    Assert-Passive ($null -ne $original) "Real patch target: $($spec.Type).$($spec.Method)"
    if ($null -eq $passiveEditor) { continue }
    $hook = $passiveEditor.GetMethod($spec.Hook,$passiveFlags)
    Assert-Passive ($null -ne $hook -and $hook.IsStatic) "Static callback: $($spec.Hook)"
    Assert-Passive ($hook.ReturnType -eq $(if($spec.Postfix){[void]}else{[bool]})) "Callback return: $($spec.Hook)"
    foreach ($parameter in $hook.GetParameters()) {
        $injected=$parameter.ParameterType
        if ($injected.IsByRef) { $injected=$injected.GetElementType() }
        if ($parameter.Name -eq '__instance') { Assert-Passive ($injected.IsAssignableFrom($original.DeclaringType)) "Instance injection: $($spec.Hook)" }
        elseif ($parameter.Name -eq '__result') { Assert-Passive ($parameter.ParameterType.IsByRef -and $injected -eq $original.ReturnType) "Result injection: $($spec.Hook)" }
        elseif ($parameter.Name -match '^__(\d+)$') { Assert-Passive ($injected.IsAssignableFrom($original.GetParameters()[[int]$Matches[1]].ParameterType)) "Argument injection: $($spec.Hook)" }
        else { throw "Unsupported hook parameter $($parameter.Name)" }
    }
}
function Assert-PassiveField([string]$Type,[string]$Name,[string]$FieldType='') {
    $typeObject=Resolve-PassiveType $Type
    $field=$typeObject.GetField($Name,$passiveFlags)
    Assert-Passive ($null -ne $field) "Real UI field: $Type.$Name"
    if($FieldType){ Assert-Passive ($field.FieldType -eq (Resolve-PassiveType $FieldType)) "Field type: $Type.$Name" }
    return $field.FieldType
}
function Assert-PassiveMethod([string]$Type,[string]$Name,[string[]]$Parameters,[string]$Return='System.Void') {
    $typeObject=Resolve-PassiveType $Type;$argTypes=[type[]]@(foreach($a in $Parameters){Resolve-PassiveType $a})
    $method=$typeObject.GetMethod($Name,$passiveFlags,$null,$argTypes,$null)
    Assert-Passive ($null -ne $method -and $method.ReturnType -eq (Resolve-PassiveType $Return)) "Real UI method: $Type.$Name"
    return $method
}
foreach($name in @('_currentUnit','_currentBookModel','_applyEvent','ob_Profile','currentBook','equipBookList','equipPassiveList','centerBookListPanel','rightBookListPanel','anim','cg')) { Assert-PassiveField 'UI.UIPassiveSuccessionPopup' $name | Out-Null }
foreach($name in @('isDisabled','currentbookmodel','selectable','ob_blockFrame')) { Assert-PassiveField 'UI.UIPassiveSuccessionBookSlot' $name | Out-Null }
Assert-PassiveField 'UI.UIPassiveSuccessionBookSlot' 'panel' 'UI.UIPassiveSuccessionBookListPanel' | Out-Null
Assert-PassiveField 'UI.UIPassiveSuccessionBookListPanel' 'panel' 'UI.UIPassiveSuccessionPopup' | Out-Null
foreach($name in @('_id','cost','rare','isNegative','isHide','isLock','CanGivePassive','CanReceivePassive','InnerTypeId','param')) { Assert-PassiveField 'PassiveXmlInfo' $name | Out-Null }
foreach($name in @('passivemodel','selectable')) {
    Assert-PassiveField 'UI.UIPassiveSuccessionCenterPassiveSlot' $name | Out-Null
    Assert-PassiveField 'UI.UIPassiveSuccessionSlot' $name | Out-Null
}
Assert-PassiveField 'UIPassiveSuccessionEquipBookSlot' 'bookmodel' 'BookModel' | Out-Null
Assert-PassiveField 'UI.UIPassiveSuccessionCenterEquipBookSlot' 'button_UnEquipButton' 'UnityEngine.UI.Button' | Out-Null
Assert-PassiveField 'UI.UIPassiveSuccessionCenterEquipBookSlot' '_currentbookmodel' 'BookModel' | Out-Null
Assert-PassiveField 'UI.UIPassiveSuccessionBookListPanel' 'ob_OtherEquipInfo' 'UnityEngine.GameObject' | Out-Null
Assert-PassiveField 'UI.UIPassiveSuccessionBookListPanel' 'previewPanel' 'UI.UIPassiveSuccessionPreviewBookPanel' | Out-Null
$bookOrigin=Assert-PassiveField 'BookModel' 'originData'
$bookReserved=Assert-PassiveField 'BookModel' 'reservedData'
foreach($field in @('equipedPassiveBookInstanceId','equipedBookIdListInPassive')) { Assert-PassiveField $bookOrigin.FullName $field | Out-Null }
$passiveOrigin=Assert-PassiveField 'PassiveModel' 'originData'
Assert-PassiveField 'PassiveModel' 'reservedData' $passiveOrigin.FullName | Out-Null
foreach($field in @('currentpassive','receivepassivebookId','givePassiveBookId')) { Assert-PassiveField $passiveOrigin.FullName $field | Out-Null }
Assert-PassiveMethod 'UI.UICharacterBookSlot' 'SetData' @('BookModel') | Out-Null
Assert-PassiveMethod 'UIPassiveSuccessionEquipBookList' 'SetData' @('List<BookModel>') | Out-Null
Assert-PassiveMethod 'UI.UIPassiveSuccessionList' 'SetEquipModelData' @('List<PassiveModel>') | Out-Null
Assert-PassiveMethod 'UI.UIPassiveSuccessionCenterPanel' 'SetBooksData' @('List<BookModel>') | Out-Null
Assert-PassiveMethod 'UI.UIPassiveSuccessionBookListPanel' 'SetPassiveBooksData' @('List<BookModel>') | Out-Null
Assert-PassiveMethod 'UI.UIPassiveSuccessionPreviewBookPanel' 'SetData' @('BookModel') | Out-Null
Assert-PassiveMethod 'UI.UIPassiveSuccessionPopup' 'SetCostData' @() | Out-Null
Assert-PassiveMethod 'BookModel' 'InitReservedDataForPassiveSuccession' @() | Out-Null
Assert-PassiveMethod 'PassiveModel' 'InitReservedData' @() | Out-Null
Assert-PassiveMethod 'PassiveXmlList' 'GetData' @('LorId') 'PassiveXmlInfo' | Out-Null
Assert-PassiveMethod 'UI.UIAlarmPopup' 'SetAlarmText' @('UI.UIAlarmType','UI.UIAlarmButtonType','UI.ConfirmEvent','System.String','System.String') | Out-Null
foreach ($type in @('BookModel','PassiveModel')) {
    $name=if($type -eq 'BookModel'){'InitReservedDataForPassiveSuccession'}else{'InitReservedData'}
    $method=(Resolve-PassiveType $type).GetMethod($name,$passiveFlags)
    $calls=@([RuinaNativeIl]::Read($method)|Where-Object{$_.Operand -is [Reflection.MethodBase]}|ForEach-Object{$_.Operand})
    Assert-Passive (@($calls|Where-Object{$_.DeclaringType.Name -match 'Inventory|LibraryModel|SaveManager|Achievement'}).Count -eq 0) "Detached initialization has no global inventory/save query: $type"
}
foreach($type in @('UI.UIPassiveSuccessionPopup','UI.UIPassiveSuccessionBookListPanel','UI.UIPopup')) {
    $close=(Resolve-PassiveType $type).GetMethod('Close',$passiveFlags)
    $calls=@([RuinaNativeIl]::Read($close)|Where-Object{$_.Operand -is [Reflection.MethodBase]}|ForEach-Object{$_.Operand})
    Assert-Passive (@($calls|Where-Object{$_.DeclaringType.Name -match 'Inventory|LibraryModel|SaveManager|Achievement|BookModel'}).Count -eq 0) "Closing old popup only updates UI: $type"
}
if($null -ne $passiveEditor) {
    foreach($name in @('BuildModels','ClonePassiveXml','SyncReserved','Render','Close','Confirm')) {
        $calls=@([RuinaNativeIl]::Read($passiveEditor.GetMethod($name,$passiveFlags))|Where-Object{$_.Operand -is [Reflection.MethodBase]}|ForEach-Object{$_.Operand})
        Assert-Passive (@($calls|Where-Object{$_.DeclaringType.Name -match 'BookInventory|LibraryModel|SaveManager|Achievement'}).Count -eq 0) "Editor never directly accesses save/inventory: $name"
    }
    $confirm=$passiveEditor.GetNestedType('Confirmation',$passiveFlags).GetMethod('Invoke',$passiveFlags)
    $calls=@([RuinaNativeIl]::Read($confirm)|Where-Object{$_.Operand -is [Reflection.MethodBase]}|ForEach-Object{$_.Operand})
    Assert-Passive (@($calls|Where-Object{$_.Name -eq 'RequestPassiveEdit'}).Count -eq 1) 'Only own confirmed callback submits passive request'
    Assert-Passive (@($calls|Where-Object{$_.Name -eq 'IsCurrent'}).Count -eq 1) 'Confirmation requires captured owner and generation'
    Assert-Passive (@([RuinaNativeIl]::Read($passiveEditor.GetMethod('Close',$passiveFlags))|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.Name -eq 'IsAlive'}).Count -ge 2) 'Close avoids destroyed Unity UI property calls'
    $closeIl=[RuinaNativeIl]::Read($passiveEditor.GetMethod('Close',$passiveFlags))
    $retire=@($closeIl|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.Name -eq 'RetireDraft'})[0]
    $firstUi=@($closeIl|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.Name -eq 'IsAlive'})[0]
    Assert-Passive ($retire.Offset -lt $firstUi.Offset) 'Close retires event owner before touching native UI'
    $openIl=[RuinaNativeIl]::Read($passiveEditor.GetMethod('TryOpen',$passiveFlags))
    $factory=@($openIl|Where-Object{$_.Opcode -eq 'newobj' -and $_.Operand -is [Reflection.ConstructorInfo] -and $_.Operand.DeclaringType.FullName -eq 'RuinaCoop.NativeDeckModels'})
    Assert-Passive ($factory.Count -eq 1 -and $factory[0].Operand.GetParameters().Count -eq 0) 'Popup owns a separate parameterless mirror factory'
    Assert-Passive (@([RuinaNativeIl]::Read($passiveEditor.GetMethod('BuildModels',$passiveFlags))|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.DeclaringType.FullName -eq 'RuinaCoop.NativeDeckEditor' -and $_.Operand.Name -eq 'CreateDetachedBook'}).Count -eq 0) 'Popup books never accumulate in parent factory'
    Assert-Passive (@($closeIl|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.DeclaringType.FullName -eq 'RuinaCoop.NativeDeckModels' -and $_.Operand.Name -eq 'Dispose'}).Count -eq 1) 'Popup factory strong references are disposed on close'
    $factoryType=$GuardSmokeModAssembly.GetType('RuinaCoop.NativeDeckModels',$true)
    $independentFactory=[Activator]::CreateInstance($factoryType,$true)
    Assert-Passive ($factoryType.GetField('_books',$passiveFlags).GetValue($independentFactory).Count -eq 0 -and
        $factoryType.GetField('Units',$passiveFlags).GetValue($independentFactory).Count -eq 0) 'Actual independent constructor creates no game book or parent roster'
    $factoryType.GetMethod('Dispose',$passiveFlags).Invoke($independentFactory,@()) | Out-Null
    Assert-Passive ($factoryType.GetField('_disposed',$passiveFlags).GetValue($independentFactory)) 'Actual independent factory is disposable without native model calls'
}
if($null -ne $passiveEditor) {
    function New-PassiveDto([string]$Name) { return [Activator]::CreateInstance($GuardSmokeModAssembly.GetType(('RuinaCoop.'+$Name),$true),$true) }
    function Set-PassiveValue($Object,[string]$Name,$Value) { $Object.GetType().GetField($Name,$passiveFlags).SetValue($Object,$Value) }
    function Get-PassiveValue($Object,[string]$Name) { return ,($Object.GetType().GetField($Name,$passiveFlags).GetValue($Object)) }
    function New-PassiveSnapshot {
        $snapshot=New-PassiveDto 'ProgressSnapshot'; Set-PassiveValue $snapshot 'SelectedStageId' ([int]1); Set-PassiveValue $snapshot 'SelectedFloorId' ([byte]1)
        Set-PassiveValue $snapshot 'PassivesAvailable' $true; Set-PassiveValue $snapshot 'Sequence' ([uint32]7)
        $deck=New-PassiveDto 'ProgressSnapshot+UnitDeckEntry'; Set-PassiveValue $deck 'BookToken' ([uint64]10); Set-PassiveValue $deck 'UnitIdentity' ([uint64]100)
        Set-PassiveValue $deck 'BookId' ([int]1); Set-PassiveValue $deck 'BookInstanceId' ([int]11)
        (Get-PassiveValue $snapshot 'UnitDecks').Add($deck); (Get-PassiveValue $snapshot 'ClaimOwners').Add([uint64]20)
        $receiver=New-PassiveDto 'ProgressSnapshot+PassiveBookEntry'; Set-PassiveValue $receiver 'BookToken' ([uint64]10)
        $flags=Resolve-PassiveType 'RuinaCoop.PassiveBookFlags'; if($null -eq $flags){throw 'Missing enum'}
        Set-PassiveValue $receiver 'Flags' ([Enum]::ToObject($flags,1)); Set-PassiveValue $receiver 'MaxCost' ([int]6)
        $slot=New-PassiveDto 'ProgressSnapshot+PassiveSlotEntry'; Set-PassiveValue $slot 'OriginId' ([int]1); Set-PassiveValue $slot 'CurrentId' ([int]1)
        (Get-PassiveValue $receiver 'Slots').Add($slot); (Get-PassiveValue $snapshot 'PassiveBooks').Add($receiver)
        return $snapshot
    }
    $failureStatus = $passiveEditor.GetMethod('OpenFailureStatus',$passiveFlags)
    Assert-Passive (@($openIl|Where-Object{$_.Operand -is [Reflection.MethodBase] -and $_.Operand.Name -eq 'OpenFailureStatus'}).Count -eq 1) 'Actual open failure uses the specific inventory diagnostic'
    $statusSnapshot=New-PassiveSnapshot
    Set-PassiveValue $statusSnapshot 'PassivesAvailable' $false
    $reasonType=$GuardSmokeModAssembly.GetType('RuinaCoop.PassivesReason',$true)
    foreach($diagnostic in @(@(3,'传输上限'),@(4,'采集失败'),@(6,'核心书库'),@(1,'等待房主'))) {
        Set-PassiveValue $statusSnapshot 'PassivesReason' ([Enum]::ToObject($reasonType,[int]$diagnostic[0]))
        $statusText=[string]$failureStatus.Invoke($null,@($statusSnapshot,[byte]0))
        Assert-Passive ($statusText.Contains($diagnostic[1]) -and -not $statusText.Contains('认领')) "Inventory failure is distinguished from claim ownership: $($diagnostic[0])"
    }
    Set-PassiveValue $statusSnapshot 'PassivesAvailable' $true
    $statusReceiver=(Get-PassiveValue $statusSnapshot 'PassiveBooks')[0]
    Set-PassiveValue $statusReceiver 'Flags' ([Enum]::ToObject(($GuardSmokeModAssembly.GetType('RuinaCoop.PassiveBookFlags',$true)),4))
    Assert-Passive (([string]$failureStatus.Invoke($null,@($statusSnapshot,[byte]0))).Contains('结构')) 'Unsupported receiver has its own failure reason'
    (Get-PassiveValue $statusSnapshot 'PassiveBooks').Clear()
    Assert-Passive (([string]$failureStatus.Invoke($null,@($statusSnapshot,[byte]0))).Contains('缺少')) 'Missing receiver metadata has its own failure reason'
    Assert-Passive (([string]$failureStatus.Invoke($null,@($null,[byte]0))).Contains('等待房主')) 'Missing snapshot reports synchronization wait'
    $draftType=$GuardSmokeModAssembly.GetType('RuinaCoop.NativePassiveDraft',$true)
    $snapshot=New-PassiveSnapshot; $create=[object[]]@([uint64]77,$snapshot,[byte]0,[uint64]20,$null)
    Assert-Passive ($draftType.GetMethod('TryCreate',$passiveFlags).Invoke($null,$create)) 'Compiled actual draft can bind DTO without game constructors'
    $oldDraft=$create[4]; $event=$draftType.GetMethod('CaptureEvent',$passiveFlags).Invoke($oldDraft,@())
    $confirmationType=$passiveEditor.GetNestedType('Confirmation',$passiveFlags)
    $lateApply=[Activator]::CreateInstance($confirmationType,$true)
    Set-PassiveValue $lateApply 'Owner' $oldDraft; Set-PassiveValue $lateApply 'Event' $event; Set-PassiveValue $lateApply 'Apply' $true
    $passiveEditor.GetField('_draft',$passiveFlags).SetValue($null,$oldDraft)
    $passiveEditor.GetMethod('RetireDraft',$passiveFlags).Invoke($null,@()) | Out-Null
    Assert-Passive (-not $draftType.GetProperty('Valid',$passiveFlags).GetValue($oldDraft,$null)) 'Production close retirement invalidates captured draft'
    $confirmationType.GetMethod('Invoke',$passiveFlags).Invoke($lateApply,@($true)) | Out-Null
    Assert-Passive ($null -eq $passiveEditor.GetField('_draft',$passiveFlags).GetValue($null)) 'Late confirmed apply after close cannot reopen or send'
    $create=[object[]]@([uint64]77,$snapshot,[byte]0,[uint64]20,$null); $draftType.GetMethod('TryCreate',$passiveFlags).Invoke($null,$create) | Out-Null
    $newDraft=$create[4]; $passiveEditor.GetField('_draft',$passiveFlags).SetValue($null,$newDraft)
    $confirmationType.GetMethod('Invoke',$passiveFlags).Invoke($lateApply,@($true)) | Out-Null
    Assert-Passive ([object]::ReferenceEquals($newDraft,$passiveEditor.GetField('_draft',$passiveFlags).GetValue($null))) 'Old apply cannot submit for reopened role with same snapshot'
    Set-PassiveValue $lateApply 'Apply' $false
    $confirmationType.GetMethod('Invoke',$passiveFlags).Invoke($lateApply,@($true)) | Out-Null
    Assert-Passive ($draftType.GetProperty('Valid',$passiveFlags).GetValue($newDraft,$null)) 'Old cancel cannot close new popup binding'
    $slotTagType=$passiveEditor.GetNestedType('SlotTag',$passiveFlags)
    $row=New-Object object; $shown=[Activator]::CreateInstance($slotTagType,$true); $pressed=[Activator]::CreateInstance($slotTagType,$true)
    $fresh=$draftType.GetMethod('CaptureEvent',$passiveFlags).Invoke($newDraft,@())
    Set-PassiveValue $shown 'Event' $fresh; Set-PassiveValue $shown 'OwnerSlot' $row
    Set-PassiveValue $pressed 'Event' $event; Set-PassiveValue $pressed 'OwnerSlot' $row
    $slotMap=$passiveEditor.GetField('Slots',$passiveFlags).GetValue($null); $pressMap=$passiveEditor.GetField('Presses',$passiveFlags).GetValue($null)
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Reflection;
public static class RuinaPassiveClickProbe {
    public static bool Rejects(MethodInfo hook, IDictionary slots, IDictionary presses, object shown, object pressed) {
        var row = new object();
        slots.Add(row, shown); presses.Add(row, pressed);
        try { return !(bool)hook.Invoke(null, new object[] { row }) && !presses.Contains(row); }
        finally { slots.Remove(row); presses.Remove(row); }
    }
}
'@
    Assert-Passive ([RuinaPassiveClickProbe]::Rejects($passiveEditor.GetMethod('SlotClickPrefix',$passiveFlags),$slotMap,$pressMap,$shown,$pressed)) 'Reused row rejects and consumes old press before any session or UI call'
    $later=New-PassiveSnapshot; Set-PassiveValue $later 'Sequence' ([uint32]8)
    Assert-Passive ($draftType.GetMethod('TryUpdate',$passiveFlags).Invoke($newDraft,@([uint64]77,$later))) 'Same-context snapshot refresh keeps draft valid'
    Assert-Passive ([RuinaPassiveClickProbe]::Rejects($passiveEditor.GetMethod('SlotClickPrefix',$passiveFlags),$slotMap,$pressMap,$shown,$shown)) 'Same row reference cannot revive a press from older displayed generation'
    $slotMap.Clear(); $pressMap.Clear()
    $tagType=$passiveEditor.GetNestedType('BookTag',$passiveFlags); $tag=[Activator]::CreateInstance($tagType,$true)
    Set-PassiveValue $tag 'Owner' $oldDraft; Set-PassiveValue $tag 'Token' ([uint64]10); Set-PassiveValue $tag 'MaxCost' ([int]12)
    $book=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'BookModel'))
    $weak=$passiveEditor.GetField('MarkedBooks',$passiveFlags).GetValue($null); $weak.GetType().GetMethod('Add').Invoke($weak,@($book,$tag)) | Out-Null
    $query=[object[]]@($book,$null)
    Assert-Passive (-not $passiveEditor.GetMethod('EquippedQueryPrefix',$passiveFlags).Invoke($null,$query) -and $query[1].Count -eq 0) 'Retired mirror source query returns isolated empty list'
    $query=[object[]]@($book,$null)
    Assert-Passive (-not $passiveEditor.GetMethod('ReceiverQueryPrefix',$passiveFlags).Invoke($null,$query) -and $null -eq $query[1]) 'Retired mirror receiver query never falls through to inventory'
    $query=[object[]]@($book,[int]0)
    Assert-Passive (-not $passiveEditor.GetMethod('BudgetPrefix',$passiveFlags).Invoke($null,$query) -and $query[1] -eq 12) 'Retired mirror budget never reads local chapter'
    $passiveEditor.GetMethod('RetireDraft',$passiveFlags).Invoke($null,@()) | Out-Null
    $template=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'PassiveXmlInfo'))
    Set-PassiveValue $template 'cost' ([int]9); Set-PassiveValue $template 'rare' ([Enum]::ToObject((Resolve-PassiveType 'Rarity'),3))
    Set-PassiveValue $template 'isNegative' $true
    $parameters=New-Object 'System.Collections.Generic.List[int]'; $parameters.Add(3); Set-PassiveValue $template 'param' $parameters
    $slot=New-PassiveDto 'ProgressSnapshot+PassiveSlotEntry'; Set-PassiveValue $slot 'OriginId' ([int]10); Set-PassiveValue $slot 'CurrentId' ([int]10)
    Set-PassiveValue $slot 'Cost' ([int]4); Set-PassiveValue $slot 'CurrentCost' ([int]0); Set-PassiveValue $slot 'OriginRarity' ([byte]2)
    Set-PassiveValue $slot 'CurrentRarity' ([byte]0); Set-PassiveValue $slot 'CurrentNegative' $false
    Set-PassiveValue $slot 'Flags' ([Enum]::ToObject((Resolve-PassiveType 'RuinaCoop.PassiveSlotFlags'),5))
    $clone=$passiveEditor.GetMethod('ClonePassiveXml',$passiveFlags)
    $nativeXml=$clone.Invoke($null,@($template,$slot,$false)); $currentXml=$clone.Invoke($null,@($template,$slot,$true))
    Assert-Passive ((Get-PassiveValue $nativeXml 'cost') -eq 4 -and [int](Get-PassiveValue $nativeXml 'rare') -eq 2 -and (Get-PassiveValue $nativeXml 'isNegative')) 'Native XML clone uses host origin cost rarity and negative flag'
    Assert-Passive ((Get-PassiveValue $currentXml 'cost') -eq 0 -and [int](Get-PassiveValue $currentXml 'rare') -eq 0 -and -not (Get-PassiveValue $currentXml 'isNegative')) 'Given source current XML keeps actual zero cost common rarity and cleared negative state'
    Assert-Passive ((Get-PassiveValue $template 'cost') -eq 9 -and [int](Get-PassiveValue $template 'rare') -eq 3 -and (Get-PassiveValue $template 'isNegative')) 'Host metadata overrides never mutate shared XML template'
    (Get-PassiveValue $nativeXml 'param').Add(4)
    Assert-Passive ($parameters.Count -eq 1 -and -not [object]::ReferenceEquals((Get-PassiveValue $nativeXml 'param'),$parameters)) 'XML parameter lists are detached from static data'
    $popupObject=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'UI.UIPassiveSuccessionPopup'))
    $popupMarks=$passiveEditor.GetField('MarkedPopups',$passiveFlags).GetValue($null)
    $popupMarks.GetType().GetMethod('Add').Invoke($popupMarks,@($popupObject,(New-Object object))) | Out-Null
    foreach($method in @('OpenPrefix','InitPrefix','LateCallbackPrefix')) {
        Assert-Passive (-not $passiveEditor.GetMethod($method,$passiveFlags).Invoke($null,@($popupObject))) "Retired popup rejects old original callback: $method"
    }
    $realUnit=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'UnitDataModel'))
    Assert-Passive ($passiveEditor.GetMethod('SetDataPrefix',$passiveFlags).Invoke($null,@($popupObject,$realUnit))) 'Fresh offline real-unit entry restores vanilla popup'
    Assert-Passive ($passiveEditor.GetMethod('OpenPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'Fresh offline entry clears retired popup isolation'
    Assert-Passive ($passiveEditor.GetMethod('LateCallbackPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'New offline original confirmation can run normally'
    $popupMarks.GetType().GetMethod('Add').Invoke($popupMarks,@($popupObject,(New-Object object))) | Out-Null
    $realBook=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'BookModel'))
    Assert-Passive ($passiveEditor.GetMethod('SetDataOnlyPrefix',$passiveFlags).Invoke($null,@($popupObject,$realBook))) 'Fresh offline real-book entry restores vanilla popup'
    Assert-Passive ($passiveEditor.GetMethod('OpenPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'Both real entry paths clear popup isolation'
    $query=[object[]]@($popupObject,$book,$true)
    Assert-Passive (-not $passiveEditor.GetMethod('AttachPrefix',$passiveFlags).Invoke($null,$query) -and -not $query[2]) 'Fresh offline context still refuses a retired mirror book attach'
    Assert-Passive (-not $passiveEditor.GetMethod('DetachPrefix',$passiveFlags).Invoke($null,@($popupObject,$book))) 'Fresh offline context still refuses a retired mirror book detach'
    Assert-Passive (-not $passiveEditor.GetMethod('OtherBookPrefix',$passiveFlags).Invoke($null,@($popupObject,$book))) 'Retired mirror book cannot enter other-receiver unlink path'
    $retiredPassive=[Runtime.Serialization.FormatterServices]::GetUninitializedObject((Resolve-PassiveType 'PassiveModel'))
    $passiveMarks=$passiveEditor.GetField('MarkedPassives',$passiveFlags).GetValue($null)
    $passiveMarks.GetType().GetMethod('Add').Invoke($passiveMarks,@($retiredPassive,(New-Object object))) | Out-Null
    Assert-Passive (-not $passiveEditor.GetMethod('ReleasePrefix',$passiveFlags).Invoke($null,@($popupObject,$retiredPassive))) 'Retired private passive cannot mutate an offline receiver'
    Assert-Passive ($passiveEditor.GetMethod('DetachPrefix',$passiveFlags).Invoke($null,@($popupObject,$realBook))) 'Fresh offline real-book mutation remains vanilla'
    $guardType=$GuardSmokeModAssembly.GetType('RuinaCoop.DeckGuard',$true)
    $relayType=$GuardSmokeModAssembly.GetType('RuinaCoop.RelaySession',$true)
    $activeRoom=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($relayType)
    $guardType.GetProperty('Session',$passiveFlags).SetValue($null,$activeRoom,$null)
    try {
        Assert-Passive (-not $passiveEditor.GetMethod('SetDataPrefix',$passiveFlags).Invoke($null,@($popupObject,$realUnit))) 'Active room rejects real-unit bypass without native binding'
        Assert-Passive (-not $passiveEditor.GetMethod('SetDataOnlyPrefix',$passiveFlags).Invoke($null,@($popupObject,$realBook))) 'Active room rejects real-book bypass without native binding'
        Assert-Passive (-not $passiveEditor.GetMethod('InitPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'Active room shields global reserved initialization'
        Assert-Passive (-not $passiveEditor.GetMethod('LateCallbackPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'Active room shields global original apply and cancel'
        Assert-Passive (-not $passiveEditor.GetMethod('CancelPrefix',$passiveFlags).Invoke($null,@($popupObject))) 'Room entered over a retired popup can cancel without global reset'
    } finally { $guardType.GetProperty('Session',$passiveFlags).SetValue($null,$null,$null) }
}
if($passiveOutputPath){ $passiveChecks | Set-Content -LiteralPath $passiveOutputPath -Encoding UTF8 }
Write-Output "PASS: $($passiveChecks.Count) passive real-DLL checks; $($passiveTargets.Count) patch targets."
if($null -eq $passiveEditor){ Write-Output 'Compiled editor missing in last Release DLL; hook/IL checks deferred until build.' }
