# Static Cecil audit only. No Unity loading, game/model constructors, singleton
# invocation, original game execution, save reads/writes, or game process start.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Resolve-Path -LiteralPath $CecilPath).ProviderPath
$prepGamePath = Join-Path $GameDir 'LibraryOfRuina_Data\Managed\Assembly-CSharp.dll'
$prepGame = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($prepGamePath)
$prepMod = $null
$prepChecks = New-Object 'System.Collections.Generic.List[string]'
function Assert-Flow([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "Preparation flow audit failed: $Name" }
    $prepChecks.Add($Name)
}
function Flow-Type([string]$Name) {
    $parts = $Name.Split('/')
    $type = $prepGame.MainModule.Types | Where-Object FullName -eq $parts[0]
    for ($i = 1; $i -lt $parts.Length -and $null -ne $type; $i++) { $type = $type.NestedTypes | Where-Object Name -eq $parts[$i] }
    Assert-Flow ($null -ne $type) "Actual type $Name exists"
    return $type
}
function Flow-Method([string]$Type, [string]$Name, [string]$Return, [string[]]$Parameters = @()) {
    $matches = @((Flow-Type $Type).Methods | Where-Object {
        $m = $_
        if ($m.Name -ne $Name -or $m.ReturnType.FullName -ne $Return -or $m.Parameters.Count -ne $Parameters.Count) { return $false }
        for ($i = 0; $i -lt $Parameters.Count; $i++) { if ($m.Parameters[$i].ParameterType.FullName -ne $Parameters[$i]) { return $false } }
        return $true
    })
    Assert-Flow ($matches.Count -eq 1) "Actual method $Type.$Name($($Parameters -join ', ')) returns $Return"
    return $matches[0]
}
function Flow-Calls($Method, [string]$Name) {
    return ,@($Method.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') -and $_.Operand.Name -eq $Name })
}
function Flow-Fields($Method, [string]$Opcode, [string]$Name) {
    return ,@($Method.Body.Instructions | Where-Object { $_.OpCode.Name -eq $Opcode -and $_.Operand.Name -eq $Name })
}
function Flow-NoPersistentCalls($Method, [string]$Name) {
    $unsafe = @($Method.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @('call','callvirt','newobj') -and
        ($_.Operand.Name -match 'SavePlayData|SaveLatestData|UnlockAchievement|OnStageStart|OnSendInvitation|RemoveBook|PrepareBattle|InitStageByInvitation' -or
         $_.Operand.DeclaringType.FullName -like 'System.IO.*')
    })
    Assert-Flow ($unsafe.Count -eq 0) "$Name contains no direct save/quest/achievement/invitation/start call"
}
try {
    $targets = @(
        @('UI.UIController','PrepareBattle','System.Void',@('StageClassInfo','System.Collections.Generic.List`1<DropBookXmlInfo>')),
        @('UI.UIController','OnClickGameStart','System.Void',@()),
        @('UI.UIController','BackBattlePrepare','System.Void',@()),
        @('UI.UIController/<>c','<BackBattlePrepare>b__148_1','System.Void',@('System.Boolean')),
        @('UI.UIBgScreenChangeAnim','StartBg','System.Void',@('UI.UIScreenChangeType')),
        @('UI.UIController','CallUIPhase','System.Void',@('UI.UIPhase')),
        @('UI.UIController','OnUIPhaseTransition','System.Void',@('UI.UIPhase','UI.UIPhase')),
        @('UI.UIBattleSettingPanel','OnClickBattleStart','System.Void',@()),
        @('GlobalGameManager','LoadBattleScene','System.Void',@()),
        @('StageController','InitCommon','System.Void',@('StageClassInfo','System.Boolean')),
        @('StageController','InitStageByInvitation','System.Void',@('StageClassInfo','System.Collections.Generic.List`1<LorId>')),
        @('StageModel','Init','System.Void',@('StageClassInfo','LibraryModel','System.Boolean')),
        @('StageModel','GetAvailableFloorList','System.Collections.Generic.List`1<StageLibraryFloorModel>',@()),
        @('StageModel','<GetAvailableFloorList>b__31_0','System.Boolean',@('StageLibraryFloorModel')),
        @('StageModel','IsUsedSephirah','System.Boolean',@('SephirahType')),
        @('StageLibraryFloorModel','Init','System.Void',@('StageModel','LibraryFloorModel','System.Boolean')),
        @('StageLibraryFloorModel','GetUnitBattleDataList','System.Collections.Generic.List`1<UnitBattleDataModel>',@()),
        @('StageLibraryFloorModel','GetUnitAddedBattleDataList','System.Collections.Generic.List`1<UnitBattleDataModel>',@()),
        @('StageLibraryFloorModel','IsUnavailable','System.Boolean',@()),
        @('StageWaveModel','Init','System.Void',@('StageModel','StageWaveInfo')),
        @('StageWaveModel','get_AvailableUnitNumber','System.Int32',@()),
        @('UnitBattleDataModel','CreateUnitBattleDataByEnemyUnitId','UnitBattleDataModel',@('StageModel','LorId')),
        @('UnitBattleDataModel','get_IsAddedBattle','System.Boolean',@()),
        @('UnitDataModel','IsLockUnit','System.Boolean',@()),
        @('StageClassInfo','get_currentState','UI.StoryState',@()),
        @('StageClassInfo','IsNormalInvitation','System.Boolean',@()),
        @('UI.UIBattleSettingPanel','OnUIPhaseEnter','System.Void',@('UI.UIPhase')),
        @('UI.UIBattleSettingPanel','OnUIPhaseExit','System.Void',@('UI.UIPhase')),
        @('UI.UIBattleSettingPanel','OnClose','System.Void',@()),
        @('UI.UIBattleSettingPanel','SetNextSephirah','System.Void',@('UI.UISephirahButton')),
        @('UI.UIEnemyCharacterListPanel','SetEnemyWave','System.Void',@('System.Int32')),
        @('UI.UIEnemyCharacterListPanel','ChangeEnemyWave','System.Void',@('System.Int32')),
        @('UI.UIEnemyCharacterListPanel','SetEnemyWaveInBattleSetting','System.Void',@()),
        @('UI.UIEnemyCharacterListPanel','Activate','System.Void',@('System.Boolean')),
        @('UI.UIEnemyCharacterListPanel','OnUpdatePhase','System.Void',@()),
        @('UI.UIEnemyCharacterListPanel','SetEnemyCharacterListPanel','System.Void',@('System.Collections.Generic.List`1<UnitDataModel>','System.Boolean')),
        @('UI.UICharacterListPanel','SetCharacterRenderer','System.Void',@('System.Collections.Generic.List`1<UnitBattleDataModel>','System.Boolean')),
        @('UI.UILibrarianCharacterListPanel','SetLibrarianCharacterListPanel_Battle','System.Void',@()),
        @('UI.UICharacterRenderer','SetCharacter','System.Void',@('UnitDataModel','System.Int32','System.Boolean','System.Boolean')),
        @('UI.UICharacterRenderer','DestroyCharacters','System.Void',@()),
        @('UI.UICharacterRenderer','GetRenderTextureByIndexAndSize','UnityEngine.Texture',@('System.Int32')),
        @('UI.UICharacterSlot','SetSlot','System.Void',@('UnitDataModel','UnityEngine.Color','System.Boolean')),
        @('UI.UICharacterSlot','SetUnknownSlot','System.Void',@()),
        @('UI.UICharacterSlot','SetBattleCharacter','System.Void',@('UnitBattleDataModel')),
        @('UI.UICharacterSlot','SetYesToggleState','System.Void',@()),
        @('UI.UICharacterSlot','SetNoToggleState','System.Void',@()),
        @('UI.UICharacterSlot','OnClickToggle','System.Void',@()),
        @('UI.UIBattleSettingLibrarianInfoPanel','SetData','System.Void',@('UnitDataModel')),
        @('UI.UIBattleSettingLibrarianInfoPanel','OnPointerClickBattlePageSlot','System.Void',@('UnityEngine.EventSystems.BaseEventData')),
        @('UI.UIFeedButton','OnPhaseEnter','System.Void',@('UI.UIPhase')),
        @('UI.UIFeedButton','OnPhaseExit','System.Void',@('UI.UIPhase')),
        @('UI.UICardPanel','OnUIPhaseEnter','System.Void',@('UI.UIPhase')),
        @('UI.UICardPanel','OnUIPhaseExit','System.Void',@('UI.UIPhase')),
        @('UI.UIMainPanel','OnUIPhaseEnter','System.Void',@('UI.UIPhase')),
        @('UI.UITitleRearPanel','OnUIPhaseEnter','System.Void',@('UI.UIPhase')),
        @('UI.UITitleRearPanel','OnPhaseTransition','System.Void',@('UI.UIPhase','UI.UIPhase')),
        @('UI.UITitlePanel','OnPhaseTransition','System.Void',@('UI.UIPhase','UI.UIPhase')),
        @('UI.UIFilterPanel','OnPhaseTransition','System.Void',@('UI.UIPhase','UI.UIPhase')),
        @('UI.UICharacterSlot/<FillProcess>d__66','MoveNext','System.Boolean',@()),
        @('UI.UIBattleSettingLibrarianInfoPanel/<RevealProcess>d__31','MoveNext','System.Boolean',@())
    )
    $methods = @{}
    foreach ($row in $targets) { $methods[$row[0]+'.'+$row[1]] = Flow-Method $row[0] $row[1] $row[2] $row[3] }
    $prepare = $methods['UI.UIController.PrepareBattle']
    Assert-Flow ((Flow-Calls $prepare 'UnlockAchievement').Count -gt 0) 'PrepareBattle unlocks an achievement; guest cannot use it for navigation'
    Assert-Flow ((Flow-Calls $prepare 'InitStageByInvitation').Count -eq 1) 'PrepareBattle initializes the real host reception'
    Assert-Flow ((Flow-Calls $prepare 'GetClearCount').Count -gt 0 -and (Flow-Calls $prepare 'GetStartStory').Count -gt 0) 'PrepareBattle chooses its story using local clear history'
    Assert-Flow ((Flow-Calls $prepare 'SavePlayData').Count -eq 0) 'PrepareBattle has no direct SavePlayData call; its navigation callbacks require separate review'
    $normalInvitation = $methods['StageClassInfo.IsNormalInvitation']
    foreach ($field in @('invitationInfo','combine','isStageFixedNormal')) {
        Assert-Flow ((Flow-Fields $normalInvitation 'ldfld' $field).Count -eq 1) "Vanilla general-invitation classification reads $field"
    }
    Assert-Flow ((Flow-Fields $normalInvitation 'ldfld' 'stageType').Count -eq 0 -and
        @($normalInvitation.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.i4.2' }).Count -eq 1) 'IsNormalInvitation classifies BookValue recipes, not every fixed mainline reception'
    Assert-Flow (((Flow-Type 'StageCombineType').Fields | Where-Object Name -eq BookValue).Constant -eq 2 -and
        ((Flow-Type 'StageType').Fields | Where-Object Name -eq Invitation).Constant -eq 0) 'General-invitation recipe and invitation stage kind use different enums'
    Assert-Flow ((Flow-Calls $methods['StageController.InitCommon'] 'OnStageStart').Count -eq 1) 'InitCommon starts a real library quest'
    Assert-Flow ((Flow-Calls $methods['StageController.InitStageByInvitation'] 'RemoveBook').Count -eq 0) 'Invitation initialization does not directly consume dropped books'
    Assert-Flow ((Flow-Calls $methods['StageModel.Init'] 'GetOpenedFloorList').Count -eq 1) 'Stage initialization uses the real library floors'
    $special = @($methods['StageModel.Init'].Body.Instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and $_.Operand.Name -eq 'Init' -and $_.Operand.DeclaringType.FullName -eq 'SpecialCardListModel' })
    Assert-Flow ($special.Count -eq 1) 'Stage initialization resets the global special card model'
    Assert-Flow ((Flow-Calls $methods['StageWaveModel.Init'] 'CreateUnitBattleDataByEnemyUnitId').Count -gt 0) 'Wave initialization creates actual battle enemy units'
    Assert-Flow ((Flow-Calls $methods['UnitBattleDataModel.CreateUnitBattleDataByEnemyUnitId'] 'SystemRange').Count -gt 0) 'Actual enemy construction consumes random selection'
    $available = $methods['StageModel.GetAvailableFloorList']
    Assert-Flow (@($available.Body.Instructions | Where-Object { $_.OpCode.Name -in @('stfld','stsfld') }).Count -eq 0) 'Available-floor getter does not write model fields'
    foreach ($field in @('floorOnlyList','exceptFloorList','_usedFloorList')) { Assert-Flow ((Flow-Fields $available 'ldfld' $field).Count -gt 0) "Available-floor getter reads actual $field restrictions" }
    Assert-Flow ((Flow-Calls $available 'IsUnavailable').Count -eq 1) 'Available-floor getter excludes defeated floors'
    foreach ($name in @('StageModel.GetAvailableFloorList','StageModel.<GetAvailableFloorList>b__31_0','StageModel.IsUsedSephirah','StageLibraryFloorModel.IsUnavailable')) { Flow-NoPersistentCalls $methods[$name] $name }
    foreach ($row in @(@('StageLibraryFloorModel.IsUnavailable','_defeated'),@('StageLibraryFloorModel.GetUnitBattleDataList','_unitList'),@('StageWaveModel.get_AvailableUnitNumber','_availableUnitNumber'))) {
        $m = $methods[$row[0]]; Assert-Flow ($m.Body.Instructions.Count -eq 3 -and $m.Body.Instructions[1].Operand.Name -eq $row[1]) "$($row[0]) is a direct existing-field read"
    }
    $participating = $methods['UnitBattleDataModel.get_IsAddedBattle']
    Assert-Flow ((Flow-Fields $participating 'ldfld' 'isDead').Count -eq 1 -and (Flow-Fields $participating 'ldfld' '_isAddedBattle').Count -eq 1 -and
        @($participating.Body.Instructions | Where-Object { $_.OpCode.Name -in @('stfld','stsfld','call','callvirt','newobj') }).Count -eq 0) 'IsAddedBattle is read-only and returns false for dead units before reading the stored selection'
    $added = $methods['StageLibraryFloorModel.GetUnitAddedBattleDataList']
    Assert-Flow ((Flow-Calls $added 'set_IsAddedBattle').Count -eq 1 -and (Flow-Fields $added 'stfld' 'addedunitList').Count -gt 0) 'Added-roster getter writes state and silently auto-selects a librarian'
    Assert-Flow ((Flow-Calls $methods['StageLibraryFloorModel.Init'] 'GetUnitAddedBattleDataList').Count -eq 1) 'Host native floor initialization applies its own default selection'
    foreach ($name in @('SetYesToggleState','SetNoToggleState')) { Assert-Flow ((Flow-Calls $methods['UI.UICharacterSlot.'+$name] 'set_IsAddedBattle').Count -eq 1) "$name writes the attached battle unit; detached guest slots must have no battle binding" }
    $enter = $methods['UI.UIBattleSettingPanel.OnUIPhaseEnter']
    Assert-Flow ((Flow-Calls $enter 'SetNextSephirah').Count -eq 1 -and (Flow-Calls $enter 'IsBinahLockedInLibrary').Count -gt 0) 'Original preparation enter changes the floor and uses local locks'
    Assert-Flow ((Flow-Calls $enter 'StartBattleSettingStartTutorial').Count -gt 0) 'Original preparation enter starts local tutorials'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingPanel.SetNextSephirah'] 'SetCurrentSephirah').Count -eq 2) 'Native floor selection changes both stage and UI current floor'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingPanel.OnUIPhaseExit'] 'StopProcess').Count -eq 2) 'Preparation exit stops both profile animations'
    Flow-NoPersistentCalls $methods['UI.UIBattleSettingPanel.OnUIPhaseExit'] 'Preparation phase exit'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingPanel.OnClose'] 'DestoryWaveTutorialInit').Count -eq 1) 'Original preparation close touches local tutorial state'
    $preview = $methods['UI.UIEnemyCharacterListPanel.SetEnemyWave']
    Assert-Flow ((Flow-Calls $preview 'SystemRange').Count -ge 1) 'Static enemy preview can re-randomize a core page'
    Assert-Flow ((Flow-Calls $preview 'GetChapter').Count -eq 1 -and (Flow-Calls $preview 'get_currentState').Count -gt 0 -and (Flow-Calls $preview 'InitNotClearEnemyList').Count -gt 0) 'Static future-wave visibility checks host chapter and clear state and uses unknown placeholders'
    Assert-Flow ((Flow-Calls $methods['UI.UIEnemyCharacterListPanel.ChangeEnemyWave'] 'SetEnemyWave').Count -eq 1) 'Manual wave selection uses the static visibility/randomization path'
    $activateEnemy = $methods['UI.UIEnemyCharacterListPanel.Activate']
    foreach ($property in @('set_alpha','set_interactable','set_blocksRaycasts')) {
        Assert-Flow ((Flow-Calls $activateEnemy $property).Count -eq 1) "Original enemy Activate writes $property"
    }
    $enemyUpdate = $methods['UI.UIEnemyCharacterListPanel.OnUpdatePhase']
    $enemyActivateCalls = Flow-Calls $enemyUpdate 'Activate'
    Assert-Flow ($enemyActivateCalls.Count -eq 2 -and $enemyActivateCalls[0].Previous.OpCode.Name -eq 'ldc.i4.1' -and
        $enemyActivateCalls[1].Previous.OpCode.Name -eq 'ldc.i4.0') 'Original enemy phase update explicitly enables preparation canvas and disables invitation canvas'
    Assert-Flow ((Flow-Calls $methods['UI.UIEnemyCharacterListPanel.SetEnemyWaveInBattleSetting'] 'GetCurrentWaveModel').Count -eq 1) 'Automatic preparation preview reads the initialized current wave'
    $nativeLibrarians = $methods['UI.UILibrarianCharacterListPanel.SetLibrarianCharacterListPanel_Battle']
    $nativeLibrarianRender = Flow-Calls $nativeLibrarians 'SetCharacterRenderer'
    Assert-Flow ($nativeLibrarianRender.Count -eq 1 -and $nativeLibrarianRender[0].Previous.OpCode.Name -eq 'ldc.i4.0') 'Actual native librarians use the false renderer bank'
    $nativeEnemyRender = Flow-Calls $methods['UI.UIEnemyCharacterListPanel.SetEnemyWaveInBattleSetting'] 'SetCharacterRenderer'
    Assert-Flow ($nativeEnemyRender.Count -eq 1 -and $nativeEnemyRender[0].Previous.OpCode.Name -eq 'ldc.i4.1') 'Actual native current-wave enemies use the true renderer bank'
    $nativeBanks = $methods['UI.UICharacterListPanel.SetCharacterRenderer']
    $bankFive = @($nativeBanks.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.i4.5' })
    Assert-Flow ($bankFive.Count -eq 1 -and $bankFive[0].Previous.OpCode.Name -eq 'endfinally' -and
        $bankFive[0].Next.OpCode.Name -like 'stloc*') 'Actual false renderer bank starts at slot five after the true-bank loop'
    Assert-Flow ((Flow-Calls $nativeBanks 'SetCharacter').Count -eq 2) 'Actual renderer banks have separate SetCharacter loops'
    $nativeCharacter = $methods['UI.UICharacterRenderer.SetCharacter']
    Assert-Flow ((Flow-Fields $nativeCharacter 'stfld' 'textureIndex').Count -eq 1 -and
        (Flow-Fields $nativeCharacter 'stfld' 'textureIndex')[0].Previous.OpCode.Name -eq 'ldarg.2') 'SetCharacter assigns the caller slot to the unit texture index'
    Assert-Flow ((Flow-Fields $nativeCharacter 'stfld' 'unitModel').Count -ge 1 -and
        (Flow-Calls $nativeCharacter 'ReleaseSdObject').Count -ge 1) 'SetCharacter replaces the shared slot model and releases its prior appearance'
    $nativeDestroy = $methods['UI.UICharacterRenderer.DestroyCharacters']
    Assert-Flow ((Flow-Fields $nativeDestroy 'stfld' 'unitAppearance').Count -eq 1 -and
        (Flow-Fields $nativeDestroy 'stfld' 'unitModel').Count -eq 0 -and
        (Flow-Fields $nativeDestroy 'stfld' 'resName').Count -eq 0) 'DestroyCharacters clears appearances but leaves shared slot model/resource metadata'
    Assert-Flow ((Flow-Calls $nativeDestroy 'set_enabled').Count -eq 1 -and
        (Flow-Calls $nativeDestroy 'ReleaseSdObject').Count -eq 1) 'DestroyCharacters also disables cameras and releases renderer resources'
    $nativeTexture = $methods['UI.UICharacterRenderer.GetRenderTextureByIndexAndSize']
    Assert-Flow ((Flow-Fields $nativeTexture 'ldfld' 'cameraList').Count -eq 1 -and
        (Flow-Calls $nativeTexture 'get_targetTexture').Count -eq 1) 'Displayed portraits read the shared camera texture by slot index'
    $battleSlot = $methods['UI.UICharacterSlot.SetBattleCharacter']
    Assert-Flow ((Flow-Calls $battleSlot 'get_currentState').Count -eq 1 -and (Flow-Fields $battleSlot 'ldfld' 'isUnknownBattleSetting').Count -eq 1) 'Current-wave unknown decision combines host clear state with the real enemy unknown flag'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingLibrarianInfoPanel.SetData'] 'get_currentState').Count -gt 0) 'Original profile depends on local stage visibility; guest replaces the whole method'
    Assert-Flow ((Flow-Calls $methods['UI.UIEnemyCharacterListPanel.SetEnemyCharacterListPanel'] 'InitEnemyList').Count -eq 1 -and (Flow-Calls $methods['UI.UIEnemyCharacterListPanel.SetEnemyCharacterListPanel'] 'GetCurrentWaveModel').Count -eq 0) 'Small list display accepts explicit units without local stage lookup'
    Flow-NoPersistentCalls $methods['UI.UICharacterSlot.SetUnknownSlot'] 'Unknown placeholder rendering'
    Assert-Flow ((Flow-Calls $methods['UnitDataModel.IsLockUnit'] 'IsBinahLockedInLibrary').Count -eq 1 -and (Flow-Calls $methods['UnitDataModel.IsLockUnit'] 'IsBinahLockedInStage').Count -eq 1) 'Original unit lock getter uses local progress; detached mirror lock override is required'
    Assert-Flow ((Flow-Calls $methods['UI.UICharacterSlot.SetSlot'] 'IsLockUnit').Count -gt 0) 'Small native unit slot still calls the lock getter'
    $phase = $methods['UI.UIController.CallUIPhase']
    $phaseCalls = @($phase.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and $_.Operand.DeclaringType.FullName -eq 'UI.OnUIPhaseEnter' -and $_.Operand.Name -eq 'Invoke' })
    Assert-Flow ($phaseCalls.Count -eq 1 -and $phase.Body.Instructions[$phase.Body.Instructions.IndexOf($phaseCalls[0])-1].OpCode.Name -eq 'ldarg.1') 'Phase-enter delegate receives the new requested phase'
    $acceptedPhaseWrite = Flow-Fields $phase 'stfld' '_currentUIPhase'
    $phaseClose = Flow-Calls $phase 'OnClose'
    $phaseOpen = Flow-Calls $phase 'OnOpen'
    $phaseUpdate = Flow-Calls $phase 'OnUpdatePhase'
    Assert-Flow ($acceptedPhaseWrite.Count -eq 1 -and $acceptedPhaseWrite[0].Previous.OpCode.Name -eq 'ldarg.1' -and
        $phaseClose.Count -eq 1 -and $acceptedPhaseWrite[0].Offset -lt $phaseClose[0].Offset) 'Native accepted phase is recorded before any panel OnClose callback'
    $transitionGate = Flow-Fields $phase 'ldfld' '_transition'
    Assert-Flow ($transitionGate.Count -eq 1 -and $transitionGate[0].Next.OpCode.Name -like 'brfalse*' -and
        $transitionGate[0].Next.Next.OpCode.Name -eq 'ret' -and
        $transitionGate[0].Next.Next.Offset -lt $acceptedPhaseWrite[0].Offset) 'Active native transition rejects navigation before changing phase or closing a panel'
    $phaseVeto = @($phase.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and
        $_.Operand.DeclaringType.FullName -eq 'System.Func`1<System.Boolean>' -and $_.Operand.Name -eq 'Invoke' })
    Assert-Flow ($phaseVeto.Count -eq 1 -and $phaseVeto[0].Previous.OpCode.Name -eq 'ldfld' -and
        $phaseVeto[0].Previous.Operand.Name -eq 'onCallUIPhase' -and $phaseVeto[0].Next.OpCode.Name -like 'brtrue*' -and
        $phaseVeto[0].Next.Next.OpCode.Name -eq 'ret' -and
        $phaseVeto[0].Next.Next.Offset -lt $acceptedPhaseWrite[0].Offset) 'Native navigation veto returns before phase write and cannot invoke the lifecycle close bridge'
    Assert-Flow ($phaseOpen.Count -eq 1 -and $phaseUpdate.Count -eq 1 -and
        $phaseClose[0].Offset -lt $phaseOpen[0].Offset -and $phaseOpen[0].Offset -lt $phaseUpdate[0].Offset) 'Native phase routing closes old panels before opening and updating new panels'
    $closeLoop = $phaseClose[0].Next
    Assert-Flow ($closeLoop.OpCode.Name -like 'ldloca*' -and $closeLoop.Next.Operand.Name -eq 'MoveNext' -and
        $closeLoop.Next.Next.OpCode.Name -like 'brtrue*' -and $closeLoop.Next.Next.Operand.Offset -lt $phaseClose[0].Offset -and
        $closeLoop.Next.Next.Next.OpCode.Name -like 'leave*' -and $closeLoop.Next.Next.Next.Operand.Offset -lt $phaseOpen[0].Offset) 'The complete native OnClose loop finishes before the first new-page OnOpen'
    Flow-NoPersistentCalls $phase 'CallUIPhase direct implementation'
    foreach ($name in @('UI.UIController.OnUIPhaseTransition','UI.UIFeedButton.OnPhaseEnter','UI.UIFeedButton.OnPhaseExit','UI.UICardPanel.OnUIPhaseEnter','UI.UICardPanel.OnUIPhaseExit','UI.UIMainPanel.OnUIPhaseEnter','UI.UITitleRearPanel.OnUIPhaseEnter','UI.UITitleRearPanel.OnPhaseTransition','UI.UITitlePanel.OnPhaseTransition','UI.UIFilterPanel.OnPhaseTransition')) {
        Flow-NoPersistentCalls $methods[$name] $name
        $writes = @($methods[$name].Body.Instructions | Where-Object { $_.OpCode.Name -in @('stfld','stsfld') -and $_.Operand.DeclaringType.FullName -match '^(PlayHistoryModel|LibraryModel|InventoryModel|BookInventoryModel|GameSave\.)' })
        Assert-Flow ($writes.Count -eq 0) "$name has no direct progress/inventory/save-model field write"
    }
    foreach ($name in @('UI.UICharacterSlot/<FillProcess>d__66.MoveNext','UI.UIBattleSettingLibrarianInfoPanel/<RevealProcess>d__31.MoveNext')) {
        $queries = @($methods[$name].Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') -and $_.Operand.DeclaringType.FullName -match '^(StageController|StageModel|LibraryModel|UnitDataModel|UnitBattleDataModel|GameSave\.)' })
        Assert-Flow ($queries.Count -eq 0) "$name animates captured values without delayed game/progress lookups"
    }
    Assert-Flow ((Flow-Calls $methods['UI.UICharacterSlot.OnClickToggle'] 'SelectedToggles').Count -eq 1) 'Toggle click forwards the original slot identity'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingLibrarianInfoPanel.OnPointerClickBattlePageSlot'] 'GetEndContentState').Count -eq 1) 'Original edit pointer checks local end-content progress before its route'
    Assert-Flow (((Flow-Type 'UI.UIPhase').Fields | Where-Object Name -eq BattleSetting).Constant -eq 5) 'Actual preparation phase is enum value 5'
    Assert-Flow (((Flow-Type 'UI.UIScreenChangeType').Fields | Where-Object Name -eq BackInvitation).Constant -eq 4) 'Actual confirmed invitation-return animation is enum value four'
    $back = $methods['UI.UIController.BackBattlePrepare']
    $backAnimations = Flow-Calls $back 'StartBg'
    Assert-Flow (@($backAnimations | Where-Object { $_.Previous.OpCode.Name -eq 'ldc.i4.4' }).Count -eq 1 -and
        (Flow-Calls $back 'SetAlarmText').Count -eq 2) 'BackBattlePrepare has both immediate exit and deferred confirmation paths'
    $confirmedBack = $methods['UI.UIController/<>c.<BackBattlePrepare>b__148_1']
    Assert-Flow ($confirmedBack.Body.Instructions[0].OpCode.Name -eq 'ldarg.1' -and
        $confirmedBack.Body.Instructions[1].OpCode.Name -like 'brfalse*' -and
        (Flow-Calls $confirmedBack 'StartBg').Count -eq 1 -and
        (Flow-Calls $confirmedBack 'StartBg')[0].Previous.OpCode.Name -eq 'ldc.i4.4') 'Cancelled return confirmation never begins the invitation-return animation'
    Assert-Flow ((Flow-Fields $methods['UI.UIBgScreenChangeAnim.StartBg'] 'stfld' 'changeType').Count -eq 1) 'StartBg records the requested confirmed screen transition before asynchronous animation'
    Assert-Flow ((Flow-Calls $methods['UI.UIController.OnClickGameStart'] 'StartBg').Count -eq 1) 'OnClickGameStart is a zero-argument animation entry'
    Assert-Flow ((Flow-Calls $methods['UI.UIBattleSettingPanel.OnClickBattleStart'] 'StartLightSpread').Count -eq 1) 'Start button has a separate special-final-stage animation branch'
    $prepMod = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $ModAssembly).ProviderPath)
    $preparation = $prepMod.MainModule.Types | Where-Object FullName -eq 'RuinaCoop.NativePreparation'
    Assert-Flow ($null -ne $preparation) 'Compiled native preparation bridge exists'
    $capture = $preparation.Methods | Where-Object Name -eq Capture
    Assert-Flow ((Flow-Calls $capture 'IsSupportedInvitation').Count -eq 1 -and
        (Flow-Calls $capture 'IsNormalInvitation').Count -eq 0) 'Capture supports fixed mainline invitations without the general-invitation achievement filter'
    $support = $preparation.Methods | Where-Object Name -eq IsSupportedInvitation
    Assert-Flow ($null -ne $support -and $support.ReturnType.FullName -eq 'System.Boolean' -and
        $support.Parameters.Count -eq 2 -and $support.Parameters[0].ParameterType.FullName -eq 'StageClassInfo' -and
        $support.Parameters[1].ParameterType.FullName -eq 'System.Boolean') 'Compiled invitation support accepts explicit stage metadata and end-content state'
    Flow-NoPersistentCalls $support 'Invitation support classification'
    Assert-Flow ((Flow-Fields $support 'ldfld' 'stageType').Count -eq 1 -and
        (Flow-Calls $support 'IsBasic').Count -eq 1 -and (Flow-Calls $support 'IsNormalInvitation').Count -eq 0) 'Support retains native invitation identity/type guards without the recipe filter'
    $defaultRoster = $preparation.Methods | Where-Object Name -eq InitializeDefaultRoster
    Assert-Flow ((Flow-Calls $defaultRoster 'Any').Count -eq 0 -and
        (Flow-Calls $defaultRoster 'get_AvailableUnitNumber').Count -eq 1 -and
        (Flow-Calls $defaultRoster 'set_IsAddedBattle').Count -eq 1) 'Explicit initial host selection fills the native limit despite the native added-list default'
    Assert-Flow ((Flow-Calls $defaultRoster 'Add').Count -eq 1 -and
        (Flow-Calls $defaultRoster 'Add')[0].Offset -lt (Flow-Calls $defaultRoster 'set_IsAddedBattle')[0].Offset) 'Only the first explicit preparation visit per floor initializes participation'
    Assert-Flow ((Flow-Calls $capture 'InitializeDefaultRoster').Count -eq 0) 'Repeated snapshot capture cannot silently reselect librarians'
    Assert-Flow ((Flow-Calls $capture 'ObserveRoom').Count -eq 1 -and (Flow-Calls $capture 'Matches').Count -eq 1 -and
        (Flow-Calls $capture 'Adopt').Count -eq 0) 'Capture cannot implicitly re-adopt a retained StageModel after a confirmed same-room exit'
    $endPreparation = $preparation.Methods | Where-Object Name -eq EndHostPreparation
    Assert-Flow ($null -ne $endPreparation -and (Flow-Calls $endPreparation 'End').Count -eq 1 -and
        (Flow-Calls $endPreparation 'Close').Count -eq 1 -and (Flow-Calls $endPreparation 'RefreshPreparation').Count -eq 1) 'Confirmed host exit retires lifecycle, closes projection and publishes the new state'
    Assert-Flow ((Flow-Calls $endPreparation 'End')[0].Offset -lt (Flow-Calls $endPreparation 'Close')[0].Offset -and
        (Flow-Calls $endPreparation 'Close')[0].Offset -lt (Flow-Calls $endPreparation 'RefreshPreparation')[0].Offset) 'Exit publication occurs after lifecycle revocation and view cleanup'
    Flow-NoPersistentCalls $endPreparation 'Cooperative reception exit'
    $preparationInstall = $preparation.Methods | Where-Object Name -eq Install
    $preparationInstallStrings = @($preparationInstall.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
    Assert-Flow ('CallUIPhase' -notin $preparationInstallStrings -and 'PreparationPhasePrefix' -notin $preparationInstallStrings -and
        @($preparation.Methods | Where-Object Name -eq PreparationPhasePrefix).Count -eq 0) 'Neither phase overload is prefixed; rejected native requests cannot retire preparation'
    $acceptedPhase = $preparation.Methods | Where-Object Name -eq EndForAcceptedPhase
    $acceptedPhaseCallers = @($preparation.Methods | Where-Object { $_.HasBody -and (Flow-Calls $_ 'EndForAcceptedPhase').Count -gt 0 })
    Assert-Flow ($acceptedPhaseCallers.Count -eq 1 -and $acceptedPhaseCallers[0].Name -eq 'ClosedPrefix' -and
        (Flow-Calls $acceptedPhaseCallers[0] 'EndForAcceptedPhase').Count -eq 1) 'Only actual preparation OnClose can invoke accepted-phase lifecycle fallback'
    Assert-Flow (@($acceptedPhase.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'CurrentUIPhase' }).Count -eq 1 -and
        (Flow-Calls $acceptedPhase 'ShouldEndForPhase').Count -eq 1 -and (Flow-Calls $acceptedPhase 'AllowsPhase').Count -eq 1 -and
        (Flow-Calls $acceptedPhase 'EndHostPreparation').Count -eq 1) 'Accepted-phase fallback reads the real phase and preserves active card/core editing'
    Flow-NoPersistentCalls $acceptedPhase 'Accepted native phase lifecycle fallback'
    Assert-Flow ('StartBg' -in $preparationInstallStrings -and 'ScreenChangePrefix' -in $preparationInstallStrings -and
        'BackPostfix' -notin $preparationInstallStrings) 'Lifecycle listens to actual screen exit and never clears on the return-confirmation popup'
    $preparing = $preparation.Methods | Where-Object Name -eq PreparingPrefix
    Assert-Flow ((Flow-Calls $preparing 'EndHostPreparation').Count -eq 1 -and
        (Flow-Calls $preparing 'InvitationPrefix')[0].Offset -lt (Flow-Calls $preparing 'EndHostPreparation')[0].Offset) 'Starting a new host invitation retires previous projection before native stage construction'
    $renderEnemies = $preparation.Methods | Where-Object Name -eq RenderEnemies
    $renderProfile = $preparation.Methods | Where-Object Name -eq RenderProfile
    foreach ($renderMethod in @($renderEnemies, $renderProfile)) {
        Assert-Flow ((Flow-Calls $renderMethod 'RendererSlot').Count -eq 1 -and
            (Flow-Calls $renderMethod 'SetEnemyWave').Count -eq 0 -and (Flow-Calls $renderMethod 'SystemRange').Count -eq 0) "$($renderMethod.Name) uses disjoint role slots and the authoritative resolved enemy DTO"
    }
    $tick = $preparation.Methods | Where-Object Name -eq Tick
    Assert-Flow ((Flow-Calls $tick 'CaptureRenderer').Count -eq 0 -and (Flow-Calls $tick 'RenderEnemies').Count -eq 1) 'Host keeps deterministic shared enemy previews without retaining old whole-renderer restoration state'
    $guard = $prepMod.MainModule.Types | Where-Object FullName -eq 'RuinaCoop.DeckGuard'
    Assert-Flow ($null -ne $guard) 'Compiled DeckGuard exists'
    $install = $guard.Methods | Where-Object Name -eq Install
    foreach ($name in @('LoadBattleScene','OnClickBattleStart','OnClickGameStart')) {
        $strings = @($install.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $name })
        Assert-Flow ($strings.Count -eq 1) "Compiled install contains one $name target"
        $found = $false
        for ($i = $install.Body.Instructions.IndexOf($strings[0])+1; $i -lt $install.Body.Instructions.Count; $i++) {
            $instruction = $install.Body.Instructions[$i]
            if ($instruction.OpCode.Name -eq 'ldstr' -and $instruction.Operand -eq 'GuestBattlePrefix') { $found = $true }
            if ($instruction.OpCode.Name -eq 'call' -and $instruction.Operand.Name -eq 'Patch') { break }
        }
        Assert-Flow $found "$name registers the shared battle guard"
    }
    $prefix = $guard.Methods | Where-Object Name -eq GuestBattlePrefix
    Assert-Flow ($prefix.ReturnType.FullName -eq 'System.Boolean' -and $prefix.Parameters.Count -eq 0) 'Compiled battle guard has a Boolean zero-argument Harmony shape'
    Assert-Flow ((Flow-Calls $prefix 'get_IsActive').Count -eq 1) 'Compiled battle guard applies to every active room, including the host'
    $result = [ordered]@{Success=$true;CheckCount=$prepChecks.Count;MethodCount=$targets.Count;GameDllSha256=(Get-FileHash -LiteralPath $prepGamePath -Algorithm SHA256).Hash;Execution='Cecil metadata/IL only; no game models, singleton calls, saves or game process.';Checks=$prepChecks.ToArray()}
    if ($OutputPath) { $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    "PASS: $($prepChecks.Count) preparation flow/side-effect checks; $($targets.Count) actual method signatures."
} finally { if ($null -ne $prepMod) { $prepMod.Dispose() }; $prepGame.Dispose() }
