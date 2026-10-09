# Read-only game metadata and pure compiled input identity checks. No Unity,
# native reception/model constructors, Steam, inventory, tasks, or saves execute.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$prepOutput=$OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
Add-Type -Path $CecilPath
$prepCecil=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $guardManaged 'Assembly-CSharp.dll'))
$prepUiCecil=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $guardManaged 'UnityEngine.UI.dll'))
$prepModCecil=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($guardModPath)
$prepChecks=New-Object 'System.Collections.Generic.List[string]'
$prepFlags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
function Assert-Prep([bool]$Condition,[string]$Name) { if(!$Condition){throw "Preparation contract failed: $Name"};$prepChecks.Add($Name) }
function Get-PrepType([string]$Name) {
    $type=$GuardSmokeGameAssembly.GetType($Name,$false)
    if(!$type){foreach($a in [AppDomain]::CurrentDomain.GetAssemblies()){$type=$a.GetType($Name,$false);if($type){break}}}
    if(!$type){$type=[Reflection.Assembly]::LoadFrom((Join-Path $guardManaged 'UnityEngine.UI.dll')).GetType($Name,$false)}
    if(!$type-and$Name.StartsWith('UnityEngine.')){foreach($file in Get-ChildItem -LiteralPath $guardManaged -Filter 'UnityEngine.*Module.dll'){$type=[Reflection.Assembly]::LoadFrom($file.FullName).GetType($Name,$false);if($type){break}}}
    if(!$type){throw "Missing real preparation type: $Name"};return $type
}
function Get-PrepCecilType([string]$Name) {
    foreach($asm in @($prepCecil,$prepUiCecil,$prepModCecil)){
        foreach($t in $asm.MainModule.Types){if($t.FullName-eq$Name){return $t};foreach($n in $t.NestedTypes){if($n.FullName-eq$Name){return $n}}}
    }
    throw "Missing Cecil type: $Name"
}
function Get-PrepMethod([string]$Type,[string]$Name,[string[]]$Parameters=@()) {
    $types=[type[]]@(foreach($a in $Parameters){$p=[type]::GetType($a,$false);if(!$p){$p=Get-PrepType $a};$p})
    $method=(Get-PrepType $Type).GetMethod($Name,$prepFlags,$null,$types,$null)
    Assert-Prep ($null-ne$method) "Real native method: $Type.$Name($($Parameters-join', '))"
    return $method
}
function Get-PrepField([string]$Type,[string]$Name,[string]$Shape='') {
    $field=[RuinaGuardSmokeLoader]::FindField((Get-PrepType $Type),$Name)
    Assert-Prep ($null-ne$field) "Real UI field: $Type.$Name"
    if($Shape){Assert-Prep ($field.FieldType.FullName-eq$Shape) "Field shape: $Type.$Name -> $Shape"}
    return $field
}
$prepTargets=New-Object 'System.Collections.Generic.List[object]'
function Target([string]$Type,[string]$Method,[string[]]$Parameters,[string]$Prefix,[string]$Postfix='') {$prepTargets.Add([pscustomobject]@{Type=$Type;Method=$Method;Parameters=$Parameters;Prefix=$Prefix;Postfix=$Postfix})}
Target 'UI.UIController' 'PrepareBattle' @('StageClassInfo','System.Collections.Generic.List`1[[DropBookXmlInfo, Assembly-CSharp]]') 'InvitationPrefix' 'PreparedPostfix'
foreach($name in @('SendInvitation','ConfirmSendInvitation')){Target 'UI.UIInvitationRightMainPanel' $name @() 'InvitationPrefix'}
Target 'UI.UIController' 'BackBattlePrepare' @() 'BackPrefix' 'BackPostfix'
Target 'UI.UIBattleSettingPanel' 'OnUIPhaseEnter' @('UI.UIPhase') 'EnteredPrefix' 'EnteredPostfix'
foreach($row in @(@('OnOpen','OpenedPrefix'),@('OnClose','ClosedPrefix'),@('SetToggles','TogglesPrefix'),@('OnClickWaveButton','CenterPrefix'),@('OnClickFloorButton','CenterPrefix'),@('OnCancel','CancelPrefix'),@('OnClickBackButton','CancelPrefix'),@('SelectCurrentFloor','GuestUnsafePrefix'),@('UpdateEditPanel','GuestUnsafePrefix'),@('IsRunningTutorial','TutorialPrefix'))){Target 'UI.UIBattleSettingPanel' $row[0] @() $row[1]}
Target 'UI.UIBattleSettingPanel' 'SetNextSephirah' @('UI.UISephirahButton') 'FloorPrefix' 'FloorPostfix'
Target 'UI.UIBattleSettingPanel' 'SelectedToggles' @('UI.UICharacterSlot') 'RosterClickPrefix'
Target 'UI.UIBattleSettingPanel' 'OnClickOpenEditPage' @('UI.UIBattleSettingEditTap') 'EditPrefix'
Target 'UI.UIBattleSettingLibrarianInfoPanel' 'SetData' @('UnitDataModel') 'ProfilePrefix'
Target 'UI.UIBattleSettingLibrarianInfoPanel' 'OnPointerClickBattlePageSlot' @('UnityEngine.EventSystems.BaseEventData') 'CardEditPrefix'
Target 'UI.UIBattleSettingLibrarianInfoPanel' 'OnPointerClickEquipPage' @('UnityEngine.EventSystems.BaseEventData') 'CoreEditPrefix'
foreach($name in @('OnUpdatePhase','SetEnemyWaveInBattleSetting')){Target 'UI.UIEnemyCharacterListPanel' $name @() 'EnemiesPrefix'}
Target 'UI.UIEnemyCharacterListPanel' 'ChangeEnemyWave' @('System.Int32') 'WavePrefix'
foreach($name in @('OnUpdatePhase','SetLibrarianCharacterListPanel_Battle')){Target 'UI.UILibrarianCharacterListPanel' $name @() 'LibrariansPrefix'}
Target 'UI.UILibrarianCharacterListPanel' 'OnSelect' @('UI.UICharacterSlot') 'SelectedPrefix'
Target 'UI.UIEnemyCharacterListPanel' 'OnSelect' @('UI.UICharacterSlot') 'EnemySelectedPrefix'
foreach($type in @('UI.UICustomSelectable','UnityEngine.EventSystems.EventTrigger','UnityEngine.UI.Selectable')){Target $type 'OnPointerDown' @('UnityEngine.EventSystems.PointerEventData') 'PressPrefix'}
foreach($type in @('UI.UICustomSelectable','UnityEngine.EventSystems.EventTrigger','UnityEngine.UI.Button')){Target $type 'OnSubmit' @('UnityEngine.EventSystems.BaseEventData') 'PressPrefix'}
Target 'UI.UISlot' 'OnPointerDown' @('UnityEngine.EventSystems.BaseEventData') 'PressPrefix'
$bridge=$GuardSmokeModAssembly.GetType('RuinaCoop.NativePreparation',$true)
foreach($spec in $prepTargets){
    $original=Get-PrepMethod $spec.Type $spec.Method $spec.Parameters
    foreach($hookName in @($spec.Prefix,$spec.Postfix)|Where-Object {$_}){
        $hook=$bridge.GetMethod($hookName,$prepFlags)
        Assert-Prep ($null-ne$hook-and$hook.IsStatic) "Static hook: $hookName"
        Assert-Prep ($hook.ReturnType-eq$(if($hookName-eq$spec.Postfix){[void]}else{[bool]})) "Hook return: $hookName"
        foreach($p in $hook.GetParameters()){
            $actual=$p.ParameterType;if($actual.IsByRef){$actual=$actual.GetElementType()}
            if($p.Name-eq'__instance'){Assert-Prep ($actual.IsAssignableFrom($original.DeclaringType)) "Instance injection: $hookName"}
            elseif($p.Name-eq'__result'){Assert-Prep ($p.ParameterType.IsByRef-and$actual-eq$original.ReturnType) "Result injection: $hookName"}
            elseif($p.Name-match'^__(\d+)$'){Assert-Prep ($actual.IsAssignableFrom($original.GetParameters()[[int]$Matches[1]].ParameterType)) "Argument injection: $hookName"}
            else{throw "Unsupported preparation injection: $($p.Name)"}
        }
    }
}
foreach($row in @(
    @('UI.UIController','Panels'),@('UI.UIController','_uiData'),@('UI.UIController','_uiPhaseStack'),
    @('UI.UIDataState','stage','StageClassInfo'),@('UI.UIDataState','unit','UnitDataModel'),@('UI.UIDataState','sephirah','SephirahType'),
    @('UI.UIBattleSettingPanel','_sephirah','SephirahType'),@('UI.UIBattleSettingPanel','currentSephirahButton','UI.UISephirahButton'),@('UI.UIBattleSettingPanel','infoRightPanel','UI.UIBattleSettingLibrarianInfoPanel'),@('UI.UIBattleSettingPanel','infoLeftPanel','UI.UIBattleSettingLibrarianInfoPanel'),
    @('UI.UIBattleSettingPanel','txt_enemyNametext'),@('UI.UIBattleSettingPanel','txt_AvailableUnitNumberText'),@('UI.UIBattleSettingPanel','txt_FloorText'),@('UI.UIBattleSettingPanel','txt_WaveButtonText'),
    @('UI.UIBattleSettingPanel','SephirahButtons'),@('UI.UIBattleSettingPanel','waveList'),@('UI.UIBattleSettingPanel','_editPanel'),@('UI.UIBattleSettingPanel','currentAvailbleUnitslots'),
    @('UI.UISephirahButton','sephirahType','SephirahType'),@('UI.UISephirahButton','selectable','UI.UICustomSelectable'),
    @('UI.UICharacterListPanel','CharacterList','UI.UICharacterList'),@('UI.UICharacterList','slotList'),@('UI.UICharacterList','isSelectableList','System.Boolean'),@('UI.UICharacterList','currentSelectedSlot','UI.UICharacterSlot'),
    @('UI.UICharacterSlot','unitData','UnitDataModel'),@('UI.UICharacterSlot','_unitBattleData','UnitBattleDataModel'),@('UI.UICharacterSlot','isEmptySlot','System.Boolean'),@('UI.UICharacterSlot','isToggleActive','System.Boolean'),@('UI.UICharacterSlot','_isToggleSelected','System.Boolean'),@('UI.UICharacterSlot','toggleRoot','UnityEngine.GameObject'),@('UI.UICharacterSlot','portraitImage'),
    @('UI.UIEnemyCharacterListPanel','currentWave','System.Int32'),@('UI.UIEnemyCharacterListPanel','currentEnemyStageinfo','StageClassInfo'),@('UI.UIEnemyCharacterListPanel','StageEnemyListObject','UnityEngine.GameObject'),
    @('UI.UILibrarianCharacterListPanel','ob_tutorialhighlightedFrame','UnityEngine.GameObject'),
    @('UI.UIBattleSettingLibrarianInfoPanel','unitdata','UnitDataModel'),@('UI.UIBattleSettingLibrarianInfoPanel','txt_BookName'),@('UI.UIBattleSettingLibrarianInfoPanel','StatsInfo','UI.UICharacterStatInfoPanel'),
    @('UI.UIBattleSettingLibrarianInfoPanel','passiveSlotsPanel','UI.UISetInfoSlotListSc'),@('UI.UIBattleSettingLibrarianInfoPanel','equipedCardListPanel','UI.UIEquipCardList'),
    @('UI.UIBattleSettingLibrarianInfoPanel','toggle_ReleaseToggle'),@('UI.UIBattleSettingLibrarianInfoPanel','img_Unknown'),@('UI.UIBattleSettingLibrarianInfoPanel','portrait'),@('UI.UIBattleSettingLibrarianInfoPanel','cg'),@('UI.UIBattleSettingLibrarianInfoPanel','img_BookIcon','UnityEngine.UI.Image'),@('UI.UIBattleSettingLibrarianInfoPanel','img_BookIconGlow','UnityEngine.UI.Image'),
    @('UI.UIBattleSettingLibrarianInfoPanel','isBattlePageLock','System.Boolean'),@('UI.UIBattleSettingLibrarianInfoPanel','isEquipPageLock','System.Boolean'),@('UI.UIBattleSettingLibrarianInfoPanel','BattlePageSelectable','UI.UICustomSelectable'),@('UI.UIBattleSettingLibrarianInfoPanel','equipPageSelectable','UI.UICustomSelectable'),
    @('UI.UICharacterRenderer','currentDataList'),@('UI.UICharacterRenderer','characterList')
)){$shape=if($row.Count-gt2){$row[2]}else{''};Get-PrepField $row[0] $row[1] $shape|Out-Null}
foreach($name in @('emotionText','speedDiceText','speedDiceNumText','playpointText','resistSlash','resistPentrate','resistHit','resistBreakSlash','resistBreakPentrate','resistBreakHit','hpText','breakText')){Get-PrepField 'UI.UICharacterStatInfoPanel' $name|Out-Null}
foreach($spec in @(
    @('UI.UICharacterSlot','SetSlot',@('UnitDataModel','UnityEngine.Color','System.Boolean')),@('UI.UICharacterSlot','SetToggle',@('System.Boolean')),
    @('UI.UICharacterSlot','SetYesToggleState',@()),@('UI.UICharacterSlot','SetNoToggleState',@()),@('UI.UICharacterSlot','SetUnknownSlot',@()),@('UI.UICharacterSlot','SetLockSlot',@('System.Boolean')),@('UI.UICharacterSlot','SetSelected',@('System.Boolean')),
    @('UI.UISephirahButton','SetButtonState',@('UI.UISephirahButton+ButtonState')),@('UI.UISephirahButton','SetButtonHighlightedState',@('UI.UISephirahButton+UISephirahButtonType')),
    @('UI.UIBattleSettingPanel','FindSephirahButton',@('SephirahType')),@('UI.UIBattleSettingPanel','SetAvailibleText',@()),@('UI.UIBattleSettingPanel','SetLibrarianProfileData',@('UnitDataModel')),@('UI.UIBattleSettingPanel','SetButtonState',@('System.Boolean')),
    @('UI.UIBattleSettingLibrarianInfoPanel','StopProcess',@()),@('UI.UIBattleSettingLibrarianInfoPanel','SetUnKnownData',@()),@('UI.UICharacterStatInfoPanel','SetData',@('UnitDataModel')),
    @('UI.UISetInfoSlotListSc','SetStatsDataInEquipBook',@('BookModel')),@('UI.UIEquipCardList','SetData',@('System.Collections.Generic.List`1[[DiceCardItemModel, Assembly-CSharp]]')),
    @('UI.UICharacterRenderer','SetCharacter',@('UnitDataModel','System.Int32','System.Boolean','System.Boolean')),@('UI.UICharacterRenderer','DestroyCharacters',@()),@('UI.UICharacterRenderer','GetRenderTextureByIndexAndSize',@('System.Int32')),
    @('UI.UIColorManager','GetSephirahColor',@('SephirahType')),@('UI.UIPanel','RevealAnim',@())
)){Get-PrepMethod $spec[0] $spec[1] $spec[2]|Out-Null}
Assert-Prep ((Get-PrepType 'UI.UIColorManager').GetProperty('EnemyUIColor',$prepFlags)-ne$null) 'Enemy coloring property exists'
foreach($spec in @(@('UI.UIPanel','IsActivated',[bool]),@('UnityEngine.UI.RawImage','texture',(Get-PrepType 'UnityEngine.Texture')),@('UnityEngine.CanvasGroup','alpha',[single]))){$p=(Get-PrepType $spec[0]).GetProperty($spec[1],$prepFlags);Assert-Prep ($p-and$p.CanRead-and$p.CanWrite-and$p.PropertyType-eq$spec[2]) "Restorable native property: $($spec[0]).$($spec[1])"}
foreach($name in @('bookIcon','bookIconGlow')){Assert-Prep ((Get-PrepType 'BookModel').GetProperty($name,$prepFlags).PropertyType-eq(Get-PrepType 'UnityEngine.Sprite')) "Static core-page display property: $name"}
Assert-Prep ((Get-PrepType 'UnityEngine.UI.Image').GetProperty('sprite',$prepFlags).CanWrite) 'Original book icon image accepts a sprite'
function Calls($Method,[string]$Name){return ,@($Method.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt')-and$_.Operand.Name-eq$Name})}
$capture=(Get-PrepCecilType 'RuinaCoop.NativePreparation').Methods|Where-Object Name -eq 'Capture'
Assert-Prep ((Calls $capture 'get_IsHost').Count-eq1) 'Capture verifies host identity'
Assert-Prep ((Calls $capture 'GetUnitBattleDataList').Count-eq1) 'Capture reads complete native roster'
Assert-Prep ((Calls $capture 'GetUnitAddedBattleDataList').Count-eq0) 'Capture never auto-selects through added-list getter'
Assert-Prep ((Calls $capture 'GetAvailableFloorList').Count-eq1) 'Capture uses native challenge-floor limits'
Assert-Prep ((Calls $capture 'CaptureDisplay').Count-eq1) 'Enemy capture uses actual host display metadata'
foreach($name in @('Render','RenderLibrarians','RenderEnemies','RenderProfile','OpenedPrefix','ClosedPrefix','EnteredPrefix')){
    $method=(Get-PrepCecilType 'RuinaCoop.NativePreparation').Methods|Where-Object Name -eq $name
    foreach($unsafe in @('PrepareBattle','InitStageByInvitation','Init','ApplyPassiveSuccession','SavePlayData','OnSendInvitation','UnlockAchievement','GetUnitAddedBattleDataList')){Assert-Prep ((Calls $method $unsafe).Count-eq0) "Guest $name does not call $unsafe"}
}
$nativeEnter=(Get-PrepCecilType 'UI.UIBattleSettingPanel').Methods|Where-Object Name -eq 'OnUIPhaseEnter'
Assert-Prep ((Calls $nativeEnter 'SetNextSephirah').Count-gt0-and(Calls $nativeEnter 'get_PlayHistory').Count-gt0) 'Original phase entry has local model/tutorial dependencies and is replaced'
$nativeConfirm=(Get-PrepCecilType 'UI.UIInvitationRightMainPanel').Methods|Where-Object Name -eq 'ConfirmSendInvitation'
$calls=@($nativeConfirm.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt')})
Assert-Prep (($calls|Where-Object {$_.Operand.Name-eq'OnSendInvitation'})[0].Offset-lt($calls|Where-Object {$_.Operand.Name-eq'PrepareBattle'})[0].Offset) 'Invitation must be blocked before quest start'
$close=(Get-PrepCecilType 'RuinaCoop.NativePreparation').Methods|Where-Object Name -eq 'Close'
Assert-Prep ((Calls $close 'PrepareBattle').Count-eq0-and(Calls $close 'get_GuestProjectionGuard').Count-eq0) 'Close restores UI without reception initialization'
$closed=(Get-PrepCecilType 'RuinaCoop.NativePreparation').Methods|Where-Object Name -eq 'ClosedPrefix'
Assert-Prep ((Calls $closed 'get_GuestProjectionGuard').Count-eq1) 'Guest OnClose remains isolated during cleanup'
$return=(Get-PrepCecilType 'RuinaCoop.NativePreparation').Methods|Where-Object Name -eq 'ReturnFromEditor'
Assert-Prep ((Calls $return 'Close').Count-eq1-and(Calls $return 'PrepareBattle').Count-eq0) 'Editor return closes detached view and never reinitializes reception'
$nativeBridge=Get-PrepCecilType 'RuinaCoop.NativePreparation'
$tick=$nativeBridge.Methods|Where-Object Name -eq 'Tick'
$fail=$nativeBridge.Methods|Where-Object Name -eq 'Fail'
$resume=$nativeBridge.Methods|Where-Object Name -eq 'Resume'
$restoreHost=$nativeBridge.Methods|Where-Object Name -eq 'RestoreHostContext'
$entered=$nativeBridge.Methods|Where-Object Name -eq 'EnteredPrefix'
$enteredAfter=$nativeBridge.Methods|Where-Object Name -eq 'EnteredPostfix'
Assert-Prep ((Calls $capture 'IsFailedContext').Count-eq1) 'Failed host display publishes CaptureFailed through the next capture'
Assert-Prep ((Calls $tick 'IsFailedContext')[0].Offset-lt(Calls $tick 'OpenGuest')[0].Offset) 'Same failed room/context is checked before guest page opens'
Assert-Prep ((Calls $tick 'IsFailedContext')[0].Offset-lt(Calls $tick 'CaptureRenderer')[0].Offset) 'Same failed room/context is checked before host renderer captures'
Assert-Prep ((Calls $fail 'RecordFailure')[0].Offset-lt(Calls $fail 'Close')[0].Offset) 'Failure is cached before page cleanup'
Assert-Prep ((Calls $fail 'Close')[0].Offset-lt(Calls $fail 'RefreshPreparation')[0].Offset) 'Host failure publication occurs after page cleanup'
Assert-Prep ((Calls $close 'ClearFailure').Count-eq0) 'Cleanup preserves failure cache and cannot trigger a retry loop'
Assert-Prep ((Calls $resume 'ClearFailure')[0].Offset-lt(Calls $resume 'RefreshPreparation')[0].Offset) 'Explicit host retry clears failure before capture refresh'
Assert-Prep ((Calls $resume 'ClearFailure')[0].Offset-lt(Calls $resume 'Tick')[0].Offset) 'Explicit retry clears failure before reopening the page'
foreach($name in @('GetStageModel','GetCurrentStageFloorModel','GetUnitBattleDataList','PickHostUnit')){Assert-Prep ((Calls $restoreHost $name).Count-eq1) "Host UI restoration reads actual context: $name"}
foreach($name in @('PrepareBattle','InitStageByInvitation','Init','GetUnitAddedBattleDataList','SetCurrentStage','SetCurrentSephirah')){Assert-Prep ((Calls $restoreHost $name).Count-eq0) "Host UI restoration cannot mutate native context: $name"}
$restoreFields=@($restoreHost.Body.Instructions|Where-Object {$_.OpCode.Name-eq'ldstr'}|ForEach-Object {$_.Operand})
foreach($name in @('stage','sephirah','unit','_sephirah','currentSephirahButton')){Assert-Prep ($name-in$restoreFields) "Host restores actual UI selection field: $name"}
Assert-Prep ((Calls $entered 'RestoreHostContext').Count-eq1-and(Calls $enteredAfter 'RestoreHostContext').Count-eq1) 'Native phase entry restores authoritative host fields before and after drawing'
Assert-Prep ((Calls $return 'RestoreHostContext')[0].Offset-lt(Calls $return 'Call')[0].Offset) 'Editor return restores actual host context before calling phase transition'
Assert-Prep ((Calls $tick 'RestoreHostContext')[0].Offset-lt(Calls $tick 'RenderEnemies')[0].Offset) 'Host dirty redraw restores actual context before rendering'
$lockSlot=(Get-PrepCecilType 'UI.UICharacterSlot').Methods|Where-Object Name -eq 'SetLockSlot'
Assert-Prep ((Calls $lockSlot 'SetToggle').Count-eq1-and(Calls $lockSlot 'SetToggle')[0].Previous.OpCode.Name-eq'ldc.i4.0') 'Native lock refresh always hides participation toggle'
$renderRoster=$nativeBridge.Methods|Where-Object Name -eq 'RenderLibrarians'
$rosterStrings=@($renderRoster.Body.Instructions|Where-Object {$_.OpCode.Name-eq'ldstr'})
Assert-Prep (($rosterStrings|Where-Object Operand -eq 'SetLockSlot')[0].Offset-lt($rosterStrings|Where-Object Operand -eq 'SetToggle')[0].Offset) 'Guest restores visible participation toggle after native lock refresh'
# These helpers execute only DTO/reference/field operations; original game constructors are never invoked.
$snapshotType=$GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot',$true)
$prepType=$GuardSmokeModAssembly.GetType('RuinaCoop.PreparationSnapshot',$true)
$a=[Activator]::CreateInstance($snapshotType,$true);$b=[Activator]::CreateInstance($snapshotType,$true)
foreach($snapshot in @($a,$b)){$p=$snapshotType.GetField('Preparation',$prepFlags).GetValue($snapshot);$prepType.GetField('Available',$prepFlags).SetValue($p,$true);$prepType.GetField('Phase',$prepFlags).SetValue($p,[Enum]::ToObject($prepType.GetField('Phase',$prepFlags).FieldType,1));$prepType.GetField('ContextId',$prepFlags).SetValue($p,[uint64]1)}
$valid=$bridge.GetMethod('IsCurrentInput',$prepFlags)
Assert-Prep ([bool]$valid.Invoke($null,@($a,[uint64]3,$a,[uint64]3))) 'Current displayed snapshot/generation accepts input'
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]2,$a,[uint64]3))) 'Same snapshot after redraw/role cycle rejects old press'
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]3,$b,[uint64]3))) 'Same-valued replacement snapshot rejects old input'
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]3,$null,[uint64]4))) 'Close then late release cannot revive a context'
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]3,$a,[uint64]5))) 'Close/reopen same snapshot revokes old input'
$p=$snapshotType.GetField('Preparation',$prepFlags).GetValue($a);$prepType.GetField('Available',$prepFlags).SetValue($p,$false)
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]3,$a,[uint64]3))) 'Unavailable preparation refuses input'
$prepType.GetField('Available',$prepFlags).SetValue($p,$true);$prepType.GetField('Phase',$prepFlags).SetValue($p,[Enum]::ToObject($prepType.GetField('Phase',$prepFlags).FieldType,2))
Assert-Prep (-not[bool]$valid.Invoke($null,@($a,[uint64]3,$a,[uint64]3))) 'Frozen StartPending preparation refuses input'
$recordFailure=$bridge.GetMethod('RecordFailure',$prepFlags);$clearFailure=$bridge.GetMethod('ClearFailure',$prepFlags);$isFailed=$bridge.GetMethod('IsFailedContext',$prepFlags)
$clearFailure.Invoke($null,@())|Out-Null
Assert-Prep (-not[bool]$isFailed.Invoke($null,@([uint64]1,[uint64]7))) 'Fresh context has no cached display failure'
$recordFailure.Invoke($null,@([uint64]1,[uint64]7))|Out-Null
foreach($attempt in 1..3){Assert-Prep ([bool]$isFailed.Invoke($null,@([uint64]1,[uint64]7))) "Repeated failed context remains suppressed: attempt $attempt"}
Assert-Prep (-not[bool]$isFailed.Invoke($null,@([uint64]2,[uint64]7))) 'Failure cannot suppress a new room with the same context number'
Assert-Prep (-not[bool]$isFailed.Invoke($null,@([uint64]1,[uint64]8))) 'Failure cannot suppress a new reception in the same room'
Assert-Prep (-not[bool]$isFailed.Invoke($null,@([uint64]1,[uint64]0))) 'Uninitialized context is never treated as a failed reception'
$clearFailure.Invoke($null,@())|Out-Null
Assert-Prep (-not[bool]$isFailed.Invoke($null,@([uint64]1,[uint64]7))) 'Explicit retry clears cached failure'
$prepType.GetField('Phase',$prepFlags).SetValue($p,[Enum]::ToObject($prepType.GetField('Phase',$prepFlags).FieldType,1))
$prepType.GetField('StageId',$prepFlags).SetValue($p,[int]1);$prepType.GetField('FloorId',$prepFlags).SetValue($p,[byte]1)
$prepType.GetField('MaxUnits',$prepFlags).SetValue($p,[byte]3);$prepType.GetField('CurrentWaveIndex',$prepFlags).SetValue($p,[byte]0)
foreach($name in @('Floors','Participants','Waves','Controllers')){$list=$prepType.GetField($name,$prepFlags).GetValue($p);$list.Add([Activator]::CreateInstance($list.GetType().GetGenericArguments()[0],$true))}
$unavailable=$bridge.GetMethod('Unavailable',$prepFlags);$failureReason=[Enum]::Parse($unavailable.GetParameters()[1].ParameterType,'CaptureFailed')
$unavailable.Invoke($null,@($p,$failureReason))|Out-Null
Assert-Prep (-not[bool]$prepType.GetField('Available',$prepFlags).GetValue($p)-and$prepType.GetField('Reason',$prepFlags).GetValue($p)-eq$failureReason) 'Host display failure publishes unavailable CaptureFailed'
Assert-Prep ($prepType.GetField('ContextId',$prepFlags).GetValue($p)-eq[uint64]1-and$prepType.GetField('StageId',$prepFlags).GetValue($p)-eq1) 'Failure keeps valid native context identity for explicit retry'
Assert-Prep ($prepType.GetField('MaxUnits',$prepFlags).GetValue($p)-eq0-and$prepType.GetField('CurrentWaveIndex',$prepFlags).GetValue($p)-eq255) 'Failure revokes current challenge roster/wave metadata'
foreach($name in @('Floors','Participants','Waves','Controllers')){Assert-Prep ($prepType.GetField($name,$prepFlags).GetValue($p).Count-eq0) "Failure clears authority/presentation data: $name"}
$addedGetter=(Get-PrepCecilType 'UnitBattleDataModel').Methods|Where-Object Name -eq 'get_IsAddedBattle'
Assert-Prep ((Calls $addedGetter 'GetUnitAddedBattleDataList').Count-eq0-and@($addedGetter.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt','stfld','stsfld')}).Count-eq0) 'Actual participation getter is a pure field read for compiled roster recovery tests'
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
public static class RuinaPreparationRecoveryChecks
{
    public static string[] Run(Type bridge, Type unitType, Type battleType)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        MethodInfo pick = bridge.GetMethod("PickHostUnit", flags);
        FieldInfo unit = battleType.GetField("unitData", flags), selected = battleType.GetField("_isAddedBattle", flags);
        object first = FormatterServices.GetUninitializedObject(unitType), second = FormatterServices.GetUninitializedObject(unitType), oldFloor = FormatterServices.GetUninitializedObject(unitType);
        object row0 = FormatterServices.GetUninitializedObject(battleType), row1 = FormatterServices.GetUninitializedObject(battleType);
        unit.SetValue(row0, first); unit.SetValue(row1, second); selected.SetValue(row1, true);
        IList roster = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(battleType)); roster.Add(null); roster.Add(row0); roster.Add(row1);
        List<string> checks = new List<string>();
        Check(Object.ReferenceEquals(pick.Invoke(null, new object[] { roster, first }), first), "Current unit remains selected when it belongs to actual floor", checks);
        Check(Object.ReferenceEquals(pick.Invoke(null, new object[] { roster, oldFloor }), second), "Unit restored from old floor is replaced by actual participating unit", checks);
        Check(Object.ReferenceEquals(pick.Invoke(null, new object[] { roster, null }), second), "No selected unit chooses actual participating row and skips null row", checks);
        selected.SetValue(row1, false);
        Check(Object.ReferenceEquals(pick.Invoke(null, new object[] { roster, oldFloor }), first), "Empty participation selection uses first actual nonnull unit", checks);
        roster.Clear();
        Check(pick.Invoke(null, new object[] { roster, oldFloor }) == null, "Empty actual roster clears stale selected unit", checks);
        Check(pick.Invoke(null, new object[] { null, oldFloor }) == null, "Missing actual roster cannot keep stale selected unit", checks);
        return checks.ToArray();
    }
    private static void Check(bool valid, string name, List<string> checks)
    { if (!valid) throw new InvalidOperationException(name); checks.Add(name); }
}
'@
foreach($check in [RuinaPreparationRecoveryChecks]::Run($bridge,(Get-PrepType 'UnitDataModel'),(Get-PrepType 'UnitBattleDataModel'))){Assert-Prep $true $check}
$result=[ordered]@{GameAssemblySha256=(Get-FileHash (Join-Path $guardManaged 'Assembly-CSharp.dll') -Algorithm SHA256).Hash;ModAssemblySha256=(Get-FileHash $guardModPath -Algorithm SHA256).Hash;CheckCount=$prepChecks.Count;TargetCount=$prepTargets.Count;Execution='Read-only metadata/IL plus pure compiled DTO identity/failure/roster recovery; no Unity/Steam/game constructors.';Checks=$prepChecks.ToArray()}
if($prepOutput){$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $prepOutput -Encoding UTF8}
"PASS: $($prepChecks.Count) native preparation contracts; $($prepTargets.Count) patch targets."
$prepCecil.Dispose();$prepUiCecil.Dispose();$prepModCecil.Dispose()
