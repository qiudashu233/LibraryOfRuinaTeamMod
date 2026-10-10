# Static Cecil audit. Does not load Unity, execute game methods, create native
# models, start the game, or read/write the user's saves.
param(
    [string]$GameDir='D:/game/steamapps/common/Library Of Ruina',
    [string]$CecilPath=(Join-Path $PSScriptRoot '../src/RuinaCoop/bin/Release/net46/Mono.Cecil.dll'),
    [string]$ModAssembly=(Join-Path $PSScriptRoot '../src/RuinaCoop/bin/Release/net46/RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Resolve-Path -LiteralPath $CecilPath).ProviderPath
$battleGamePath=Join-Path $GameDir 'LibraryOfRuina_Data/Managed/Assembly-CSharp.dll'
$battleAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($battleGamePath)
$battleMod=$null
$battleChecks=New-Object 'System.Collections.Generic.List[string]'
$battleTargets=New-Object 'System.Collections.Generic.List[string]'
function Assert-Battle([bool]$condition,[string]$name) {
    if(-not $condition){throw "Native battle audit failed: $name"}
    $battleChecks.Add($name)
}
function Battle-Type([string]$name) {
    $parts=$name.Split('/')
    $type=$battleAssembly.MainModule.Types | Where-Object FullName -eq $parts[0]
    for($i=1;$i -lt $parts.Count -and $null -ne $type;$i++){$type=$type.NestedTypes | Where-Object Name -eq $parts[$i]}
    Assert-Battle ($null -ne $type) "Native type $name exists"
    return $type
}
function Battle-Method([string]$type,[string]$name,[string]$return,[string[]]$parameters=@()) {
    $matches=@((Battle-Type $type).Methods | Where-Object {
        $method=$_
        if($method.Name -ne $name -or $method.ReturnType.FullName -ne $return -or $method.Parameters.Count -ne $parameters.Count){return $false}
        for($i=0;$i -lt $parameters.Count;$i++){if($method.Parameters[$i].ParameterType.FullName -ne $parameters[$i]){return $false}}
        return $true
    })
    Assert-Battle ($matches.Count -eq 1) "$type.$name($($parameters -join ',')) returns $return"
    $battleTargets.Add($matches[0].FullName)
    return $matches[0]
}
function Battle-Calls($method,[string]$type,[string]$name) {
    return ,@($method.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt','newobj') -and $_.Operand.DeclaringType.FullName -eq $type -and $_.Operand.Name -eq $name })
}
function Battle-NoDirectPersistence($method) {
    $matches=@($method.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @('call','callvirt','newobj') -and
        ($_.Operand.DeclaringType.FullName -like 'GameSave.*' -or $_.Operand.DeclaringType.FullName -like 'System.IO.*' -or
         $_.Operand.Name -match 'SavePlayData|SaveLatestData|OnStageStart|OnWaveStart|OnStageClear|UnlockAchievement|RemoveBook|GameOver')
    })
    Assert-Battle ($matches.Count -eq 0) "$($method.FullName) has no direct persistence/reward/quest call (not a transitive guarantee)"
}
try {
    $targets=@(
        @('UI.UIBattleSettingPanel','OnClickBattleStart','System.Void',@()),
        @('UI.UIController','OnClickGameStart','System.Void',@()),
        @('GlobalGameManager','LoadBattleScene','System.Void',@()),
        @('GameSceneManager','ActivateBattleScene','System.Void',@()),
        @('GameSceneManager','ActivateUIController','System.Void',@('System.Boolean')),
        @('BattleSceneRoot','StartBattle','System.Void',@()),
        @('BattleSceneRoot','ExitStage','System.Void',@()),
        @('BattleSceneRoot','EndBattle','System.Void',@('System.Boolean')),
        @('StageController','StartBattle','System.Void',@()),
        @('StageController','OnUpdate','System.Void',@('System.Single')),
        @('StageController','OnFixedUpdate','System.Void',@('System.Single')),
        @('StageController','OnFixedUpdateLate','System.Void',@('System.Single')),
        @('StageController','RoundStartPhase_UI','System.Void',@()),
        @('StageController','RoundStartPhase_System','System.Void',@()),
        @('StageController','SortUnitPhase','System.Void',@()),
        @('StageController','DrawCardPhase','System.Void',@()),
        @('StageController','ApplyEnemyCardPhase','System.Void',@()),
        @('StageController','StopSpeedDiceRoll','System.Void',@()),
        @('StageController','CheckInput','System.Void',@('System.Boolean')),
        @('StageController','CompleteApplyingLibrarianCardPhase','System.Void',@('System.Boolean')),
        @('StageController','SetAutoCardForPlayer','System.Void',@()),
        @('StageController','SetUnequipCardAll','System.Void',@()),
        @('StageController','EndBattle','System.Void',@()),
        @('StageController','CloseBattleScene','System.Void',@()),
        @('StageController','BattleEndForcelyNotRound','System.Void',@()),
        @('StageController','ClearBattle','System.Void',@()),
        @('StageController','ClearResources','System.Void',@()),
        @('StageController','EndBattlePhase','System.Void',@('System.Single')),
        @('StageController','EndBattlePhaseAfter','System.Void',@('System.Single')),
        @('StageController','RoundEndPhase','System.Void',@('System.Single')),
        @('StageController','EndBattlePhase_invitation','System.Void',@()),
        @('StageController','EndBattlePhase_creature','System.Void',@()),
        @('StageController','GameOver','System.Void',@('System.Boolean','System.Boolean')),
        @('StageController','get_UsedBooks','System.Collections.Generic.List`1<LorId>',@()),
        @('UI.UIBgScreenChangeAnim','StartBg','System.Void',@('UI.UIScreenChangeType')),
        @('BattlePlayingCardSlotDetail','AddCard','System.Void',@('BattleDiceCardModel','BattleUnitModel','System.Int32','System.Boolean')),
        @('BattlePlayingCardSlotDetail','DestroyCard','System.Void',@('System.Int32')),
        @('BattlePlayingCardSlotDetail','DestroyCardWithoutCurrentAction','System.Void',@('System.Int32')),
        @('BattlePlayingCardSlotDetail','DestroyCardByIdx','System.Void',@('System.Int32')),
        @('BattlePlayingCardSlotDetail','DestroyCardAll','System.Void',@()),
        @('MapManager','ResetMap','System.Void',@()),
        @('PassiveAbilityBase','OnEndBattlePhase','System.Void',@()),
        @('BattleUnitBuf','OnEndBattlePhase','System.Void',@()),
        @('BattleObjectManager','Clear','System.Void',@()),
        @('StageClassInfo','GetPrevBattleStory','StageStoryInfo',@())
    )
    $methods=@{}
    foreach($target in $targets){$method=Battle-Method $target[0] $target[1] $target[2] $target[3];$methods[$target[0]+'.'+$target[1]]=$method}
    $load=$methods['GlobalGameManager.LoadBattleScene']
    Assert-Battle ((Battle-Calls $load 'GameSceneManager' 'ActivateBattleScene').Count -eq 1) 'LoadBattleScene activates native battle scene'
    Assert-Battle ((Battle-Calls $load 'BattleSceneRoot' 'StartBattle').Count -eq 1) 'LoadBattleScene starts native root'
    $rootRoutine=Battle-Method 'BattleSceneRoot/<StartBattleRoutine>d__14' 'MoveNext' 'System.Boolean'
    Assert-Battle ((Battle-Calls $rootRoutine 'StageController' 'StartBattle').Count -eq 1) 'Native root coroutine starts StageController before first yield'
    $start=$methods['StageController.StartBattle']
    Assert-Battle ((Battle-Calls $start 'LibraryQuestManager' 'OnWaveStart').Count -eq 1) 'Native StartBattle has quest side effects and is forbidden on guest'
    Assert-Battle ((Battle-Calls $start 'BattleAllyCardDetail' 'DrawCards').Count -eq 1) 'Native StartBattle draws starting hand and is forbidden on guest'
    $sort=$methods['StageController.SortUnitPhase']
    Assert-Battle ((Battle-Calls $sort 'StageController' 'set_phase').Count -eq 1) 'Native SortUnitPhase changes phase'
    $sortDelegate=Battle-Method 'StageController/<>c' '<SortUnitPhase>b__138_0' 'System.Void' @('BattleUnitModel')
    Assert-Battle ((Battle-Calls $sortDelegate 'BattleUnitModel' 'RollSpeedDice').Count -eq 1) 'Actual speed dice are rolled in SortUnitPhase callback'
    $stop=$methods['StageController.StopSpeedDiceRoll']
    $stopPhase=@($stop.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'set_phase' })
    Assert-Battle ($stopPhase.Count -eq 1 -and $stopPhase[0].Previous.OpCode.Name -eq 'ldc.i4.3') 'StopSpeedDiceRoll advances to phase 3'
    $draw=$methods['StageController.DrawCardPhase']
    Assert-Battle ((Battle-Calls $draw 'BattleAllyCardDetail' 'DrawCards').Count -eq 1) 'Draw phase invokes authoritative card draw'
    $drawPhase=@($draw.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'set_phase' })
    Assert-Battle ($drawPhase.Count -eq 1 -and $drawPhase[0].Previous.OpCode.Name -eq 'ldc.i4.4') 'Draw phase advances to phase 4'
    $enemy=$methods['StageController.ApplyEnemyCardPhase']
    Assert-Battle ((Battle-Calls $enemy 'StageController' 'ApplyEnemyCardAuto').Count -eq 1) 'Enemy phase invokes native AI'
    $enemyPhase=@($enemy.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'set_phase' })
    Assert-Battle ($enemyPhase.Count -eq 1 -and $enemyPhase[0].Previous.OpCode.Name -eq 'ldc.i4.5') 'Enemy phase advances to first player input boundary 5'
    foreach($name in @('EndBattle','CloseBattleScene')) {
        $method=$methods['StageController.'+$name]
        $phase=@($method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'set_phase' })[0]
        Assert-Battle ($phase.Previous.OpCode.Name -eq 'ldc.i4.s' -and [int]$phase.Previous.Operand -eq 28) "$name enters result phase 28 and cannot be a verification exit"
    }
    Assert-Battle ((Battle-Calls $methods['StageController.ClearBattle'] 'StageController' 'ProcessAchievements').Count -eq 1) 'ClearBattle submits achievements and cannot be a verification exit'
    foreach($key in @('BattleSceneRoot.ExitStage','BattleSceneRoot.EndBattle','StageController.ClearResources','MapManager.ResetMap','GameSceneManager.ActivateUIController')) { Battle-NoDirectPersistence $methods[$key] }
    foreach($key in @('PassiveAbilityBase.OnEndBattlePhase','BattleUnitBuf.OnEndBattlePhase')) {
        Assert-Battle ($methods[$key].Body.Instructions.Count -eq 1 -and $methods[$key].Body.Instructions[0].OpCode.Name -eq 'ret') "$key audited base implementation is empty; derived overrides require rejection"
    }
    $clear=$methods['BattleObjectManager.Clear']
    Assert-Battle ((Battle-Calls $clear 'BookModel' 'SetOriginalResists').Count -eq 1) 'Clear resets transient book resistance'
    foreach($pair in @(@('<Clear>b__13_0','PassiveAbilityBase'),@('<Clear>b__13_1','BattleEmotionCardModel'),@('<Clear>b__13_2','BattleUnitBuf'))) {
        $callback=Battle-Method 'BattleObjectManager/<>c' $pair[0] 'System.Void' @($pair[1])
        Assert-Battle ((Battle-Calls $callback $pair[1] 'OnEndBattlePhase').Count -eq 1) "Clear dispatches $($pair[1]).OnEndBattlePhase; runtime type validation is required"
    }
    foreach($pair in @(@('StageController','_stageModel'),@('StageController','_state'),@('StageController','_phase'),@('StageController','_bCalledRoundStart_system'),@('StageController','_librarianTeam'),@('StageController','_enemyTeam'),@('BattleSceneRoot','_battleStarted'),@('BattleSceneRoot','mapList'),@('BattleSceneRoot','_addedMapList'))) {
        Assert-Battle (@((Battle-Type $pair[0]).Fields | Where-Object Name -eq $pair[1]).Count -eq 1) "$($pair[0]).$($pair[1]) exists"
    }
    $battleMod=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $ModAssembly).ProviderPath)
    $bridge=$battleMod.MainModule.Types | Where-Object FullName -eq 'RuinaCoop.NativeBattleBridge'
    Assert-Battle ($null -ne $bridge) 'Compiled native battle bridge exists'
    $bridgeMethods=@{}
    foreach($name in @('Install','AllowNativeStart','StartHost','GetInvitationBooks','StageStartPrefix','SceneStartPrefix','UserUpdatePrefix','FixedPrefix','InitialPhasePrefix','InitialPhasePostfix','InitializationFinalizer','CaptureBoundary','FreezeFailure','InputPrefix','NeverAdvancePrefix','EndVerification','get_Protected','get_Initializing')) {
        $method=@($bridge.Methods | Where-Object Name -eq $name)
        Assert-Battle ($method.Count -eq 1) "Compiled bridge $name exists"
        $bridgeMethods[$name]=$method[0]
    }
    $bridgeStart=$bridgeMethods['StartHost']
    Assert-Battle ((Battle-Calls $bridgeStart 'StageClassInfo' 'GetPrevBattleStory').Count -eq 1) 'Direct host load rejects unaudited previous-battle story'
    Assert-Battle ((Battle-Calls $bridgeStart 'GlobalGameManager' 'LoadBattleScene').Count -eq 1) 'Bridge has one explicit host scene load'
    Assert-Battle ((Battle-Calls $bridgeStart 'RuinaCoop.RelaySession' 'get_IsHost').Count -eq 1) 'Bridge checks host authority before loading'
    Assert-Battle ((Battle-Calls $bridgeStart 'RuinaCoop.RelaySession' 'get_BattleCommitted').Count -eq 1) 'Bridge requires network commit before loading'
    $ownedWrites=@(foreach($method in $bridge.Methods){if($method.HasBody){$method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_ownedScene' }}})
    Assert-Battle ($ownedWrites.Count -eq 1 -and $ownedWrites[0].Previous.OpCode.Name -eq 'ldc.i4.1') 'Native scene latch can only be set; room teardown cannot clear it'
    $protectedFields=@($bridgeMethods['get_Protected'].Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq '_ownedScene' })
    Assert-Battle ($protectedFields.Count -eq 1) 'Protection includes native ownership independently of active Relay session'
    foreach($name in @('UserUpdatePrefix','NeverAdvancePrefix')) {
        Assert-Battle ((Battle-Calls $bridgeMethods[$name] 'RuinaCoop.NativeBattleBridge' 'get_Protected').Count -eq 1) "$name blocks independently of active room once native scene is latched"
    }
    Assert-Battle ((Battle-Calls $bridgeMethods['FixedPrefix'] 'RuinaCoop.NativeBattleBridge' 'get_Initializing').Count -eq 1) 'Fixed progress requires active authorized initialization'
    Assert-Battle ((Battle-Calls $bridgeMethods['InputPrefix'] 'RuinaCoop.NativeBattleBridge' 'get_Initializing').Count -eq 1) 'Card mutations require internal live initialization rather than UI input'
    Assert-Battle ((Battle-Calls $bridgeMethods['AllowNativeStart'] 'RuinaCoop.RelaySession' 'RequestBattleStart').Count -eq 1) 'Unauthorized host native starts request the network barrier'
    $postStrings=@($bridgeMethods['InitialPhasePostfix'].Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
    Assert-Battle ($postStrings -contains 'RoundStartPhase_System' -and $postStrings -contains '_bCalledRoundStart_system' -and $postStrings -contains 'StopSpeedDiceRoll') 'Initialization explicitly advances completed speed-roll preparation without user input'
    Assert-Battle ($postStrings -contains 'ApplyEnemyCardPhase') 'Initial snapshot is captured after authoritative enemy choices'
    $capture=$bridgeMethods['CaptureBoundary']
    $captureCall=(Battle-Calls $capture 'RuinaCoop.RelaySession' 'OnNativeBattleInitialBoundary')[0]
    $boundaryWrite=@($capture.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq '_boundary' })[0]
    Assert-Battle ($boundaryWrite.Offset -lt $captureCall.Offset -and $boundaryWrite.Previous.OpCode.Name -eq 'ldc.i4.1') 'Bridge pauses before invoking state capture or any networking handler'
    $end=$bridgeMethods['EndVerification']
    Assert-Battle ((Battle-Calls $end 'RuinaCoop.RelaySession' 'FailBattle').Count -eq 1) 'Ending a committed native verification reports the restart requirement'
    $unsafeBridgeCalls=@(foreach($method in $bridge.Methods){if($method.HasBody){$method.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @('call','callvirt','newobj') -and
        ($_.Operand.Name -in @('EndBattle','CloseBattleScene','ClearBattle','ClearResources','GameOver','SavePlayData','SaveLatestData') -or
         $_.Operand.DeclaringType.FullName -eq 'RuinaCoop.NativeUi' -and $_.Operand.Name -eq 'Set')
    }}})
    Assert-Battle ($unsafeBridgeCalls.Count -eq 0) 'Reduced bridge contains no native teardown, result execution, save call or native state reflection writes'
    $result=[ordered]@{Passed=$true;CheckCount=$battleChecks.Count;TargetCount=$battleTargets.Count;GameSha256=(Get-FileHash -LiteralPath $battleGamePath -Algorithm SHA256).Hash;ModSha256=(Get-FileHash -LiteralPath $ModAssembly -Algorithm SHA256).Hash;Checks=@($battleChecks);Targets=@($battleTargets);Scope='Static native signatures, direct IL and compiled bridge guards only. No native methods invoked; runtime and two-end testing still required.'}
    if($OutputPath){$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8}
    Write-Output "Battle native target audit passed: $($battleChecks.Count) checks, $($battleTargets.Count) targets."
} finally {if($null -ne $battleMod){$battleMod.Dispose()};$battleAssembly.Dispose()}
