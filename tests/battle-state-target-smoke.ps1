# Read-only game metadata, embedded vanilla XML and compiled adapter audit.
# Does not initialize Unity, a stage, Steam, inventory, tasks, or a save model.
param(
    [string]$GameDir='D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly=(Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$CecilPath=(Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
Add-Type -Path $CecilPath
$managed=Join-Path $GameDir 'LibraryOfRuina_Data\Managed'
$game=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Assembly-CSharp.dll'))
$mod=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($ModAssembly)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Assert-State([bool]$value,[string]$label){if(!$value){throw "Battle state contract failed: $label"};$checks.Add($label)}
function GameType([string]$name){$t=$game.MainModule.GetType($name);if(!$t){throw "Missing game type $name"};return $t}
function ModType([string]$name){$t=$mod.MainModule.GetType('RuinaCoop.'+$name);if(!$t){throw "Missing mod type $name"};return $t}
function Field([string]$type,[string]$name,[string]$shape=''){
    $f=(GameType $type).Fields|Where-Object Name -eq $name
    Assert-State ($null-ne$f) "Real field $type.$name"
    if($shape){Assert-State ($f.FieldType.FullName-eq$shape) "Field type $type.$name = $shape"}
}
function Method([string]$type,[string]$name,[int]$argc=-1){
    $m=@((GameType $type).Methods|Where-Object {$_.Name-eq$name-and($argc-lt 0-or$_.Parameters.Count-eq$argc)})
    Assert-State ($m.Count-eq1) "Unique native method $type.$name arity $argc";return $m[0]
}
function IL($method){return (($method.Body.Instructions|ForEach-Object ToString)-join"`n")}
try {
    Field 'BattleUnitModel' '<hp>k__BackingField' 'System.Single'; Field 'UnitBattleDataModel' 'hp' 'System.Single'
    foreach($name in @('_id','index','faction','speedDiceResult','currentDiceAction','turnState','breakDetail','cardSlotDetail','bufListDetail','allyCardDetail','savedCardDetail','personalEgoDetail','passiveDetail','_connectedBufs','targetSetter')){Field 'BattleUnitModel' $name}
    foreach($name in @('breakGauge','breakLife','nextTurnBreak','blockRecoverBreakByEvaision')){Field 'BattleUnitBreakDetail' $name}
    foreach($name in @('_cardInDeck','_cardInHand','_cardInUse','_cardInDiscarded','_cardInReserved')){Field 'BattleAllyCardDetail' $name 'System.Collections.Generic.List`1<BattleDiceCardModel>'}
    foreach($name in @('_maxHand','_maxDrawHand')){Field 'BattleAllyCardDetail' $name 'System.Int32'}
    foreach($name in @('_playPoint','_reservedPlayPoint','_losePlayPoint','_nextRoundPlayPoint','_startingPlayPoint','_defaultRecoverPoint','_recoverPoint','cardAry','cardQueue','keepCard')){Field 'BattlePlayingCardSlotDetail' $name}
    foreach($name in @('_emotionCoins','totalEmotionCoins','_emotionCoinsForEgoCooltime','_forcelyLevelUpCount','_mentalState','_statBonus','skillPoint')){Field 'BattleUnitEmotionDetail' $name}
    foreach($name in @('hpRate','breakRate','dmgAdder','breakAdder','hpAdder','breakGageAdder','guardTakenBreakRate')){Field 'StatBonus' $name 'System.Int32'}
    foreach($name in @('_emotionLevel','_prevEmotionLevel','emotionLevelMax','_emotionTotalCoinNumber','emotionTotalBonus','_emotionCoinNumber','_currentLevelNeedEmotionMaxCoin','skillPoint','egoSelectionPoint','currentSelectEmotionLevel','_emotionCoinNumberForEgoCooltime','_currentLevelNeedEmotionMaxCointForEgo')){Field 'EmotionBattleTeamModel' $name 'System.Int32'}
    foreach($name in @('_stageDataStorage','_burnKillCount','_matanKillCount','_heartKillCount','danggoUsed','enemyTeamLevel5','playerTeamLevel5','enterBinah')){Field 'StageModel' $name}
    foreach($name in @('_selectedList','_selectedEgoList','addedunitList')){Field 'StageLibraryFloorModel' $name};Field 'StageWaveModel' '_selectedList'
    foreach($name in @('min','faces','value')){Field 'LOR_DiceSystem.Dice' $name 'System.Int32'}
    foreach($name in @('breaked','isControlable')){Field 'LOR_DiceSystem.SpeedDice' $name 'System.Boolean'}
    foreach($name in @('owner','target','card','earlyTarget','earlyTargetOrder','targetSlotOrder','speedDiceResultValue','emotionMultiplier','isFirstAction','ignorePower','cardBehaviorQueue','subTargets','_excludedIndies','currentBehavior','currentBehaviorUI','cardAbility')){Field 'BattlePlayingCardDataInUnitModel' $name}
    foreach($name in @('_originalXmlData','_script','_costAdder','_costZero','_curCost','_priorityAdder','exhaust','temporary','costSpended','isCopiedCard','maxCooltimeValue','currentCooltimeValue')){Field 'BattleDiceCardModel' $name}
    foreach($name in @('GetActivatedBufList','GetReadyBufList','GetReadyReadyBufList')){$null=Method 'BattleUnitBufListDetail' $name 0}
    foreach($name in @('get_PassiveList','get_ReadyPassiveList')){$null=Method 'BattleUnitPassiveDetail' $name 0}
    foreach($spec in @(@('StageController','GetStageModel'),@('StageController','GetCurrentStageFloorModel'),@('StageController','GetCurrentWaveModel'),@('BattleUnitBreakDetail','GetDefaultBreakGauge'),@('BattlePersonalEgoCardDetail','GetCardAll'),@('StageLibraryFloorModel','GetUnitBattleDataList'),@('StageModel','GetCurrentMapInfo'))){$null=Method $spec[0] $spec[1] 0}
    foreach($name in @('GetCost','GetOriginCost','GetPriorityAdder','GetBufList','GetID')){$null=Method 'BattleDiceCardModel' $name 0}
    $ctor=IL (Method 'BattlePlayingCardSlotDetail' '.ctor' 1)
    Assert-State ($ctor-match'newobj.*BattleKeepedCardDataInUnitModel::.ctor'-and$ctor-match'BattleKeepedCardDataInUnitModel::Reset') 'Native keepCard is an allocated empty container, not null'
    Assert-State ($ctor-match'ldc.i4.m1[\s\S]*_nextRoundPlayPoint') 'Next-round energy uses the valid -1 sentinel'
    $use=IL (Method 'BattleAllyCardDetail' 'UseCard' 1)
    Assert-State ($use-match'_cardInUse[\s\S]*::Add'-and$use-match'_cardInReserved[\s\S]*::Add'-and$use-match'_cardInHand[\s\S]*::Remove') 'Used and Reserved share one instance while Hand removes it'
    $allDeck=IL (Method 'BattleAllyCardDetail' 'GetAllDeck' 0)
    Assert-State ($allDeck-notmatch'_cardInReserved') 'GetAllDeck omits Reserved, so five fields must be captured directly'
    $added=IL (Method 'StageLibraryFloorModel' 'GetUnitAddedBattleDataList' 0)
    Assert-State ($added-match'addedunitList[\s\S]*::Clear'-and$added-match'FindAll') 'addedunitList is the selected roster, not a summoned-unit queue'
    $create=IL (Method 'StageController' 'CreateLibrarianUnit' 1)
    Assert-State ($create-match'GetUnitAddedBattleDataList'-and$create-match'CreateLibrarianUnit\(SephirahType,UnitBattleDataModel,System.Int32\)') 'Selected librarians receive fresh native ordinal indices'
    $idGen=IL (Method 'BattleObjectManager' 'CreateDefaultUnit' 1)
    Assert-State ($idGen-match'_idGen'-and$idGen-match'newobj.*BattleUnitModel::.ctor\(System.Int32\)') 'Native unit IDs share one global generator across factions'
    $intentCtor=IL (Method 'BattlePlayingCardDataInUnitModel' '.ctor' 0)
    Assert-State ($intentCtor-match'newobj.*Queue`1<BattleDiceBehavior>'-and$intentCtor-notmatch'CreateDiceCardBehaviorList') 'Intent constructor allocates an empty behaviour queue'
    $add=IL (Method 'BattlePlayingCardSlotDetail' 'AddCard' 4)
    Assert-State ($add-match'earlyTarget'-and$add-match'earlyTargetOrder'-and$add-notmatch'CreateDiceCardBehaviorList') 'AddCard records early target without generating resolution dice'
    $auto=IL (Method 'BattleAllyCardDetail' 'PlayTurnAutoForEnemy' 2)
    Assert-State ($auto-match'Random::Range\(System.Int32,System.Int32\)'-and$auto-match'ChangeTargetSlot'-and$auto-match'AddCard') 'Native enemy AI selects a target die before AddCard'
    $phase=IL (Method 'StageController' 'ApplyEnemyCardPhase' 0)
    Assert-State ($phase-match'ldc.i4.5[\s\S]*StageController::set_phase') 'AI completion enters phase 5, librarian input'
    $start=IL (Method 'StageController' 'StartBattle' 0)
    Assert-State ($start-match'ldc.i4.1[\s\S]*StageController::_roundTurn') 'First authoritative round begins at one'
    $init=IL (Method 'StageController' 'InitCommon' 2)
    Assert-State ($init-match'ldc.i4.1\s*\r?\n[^\n]*StageController::SetCurrentWave') 'Native invitation initializes current wave to one'
    $wave=IL (Method 'StageModel' 'GetWave' 1)
    Assert-State ($wave-match'ldarg.1[\s\S]*ldc.i4.1[\s\S]*sub') 'Native GetWave subtracts one before reading the wave list'

    # Preparation receives the game's display deck, not the XML insertion order.
    # Prove the real call chain and comparator without initializing any models.
    $previewDeck=IL (Method 'UnitDataModel' 'GetDeckCardModelAll' 0)
    Assert-State ($previewDeck-match'call.*UnitDataModel::GetDeckAll\(\)'-and$previewDeck-match'DiceCardItemModel::.ctor\(LOR_DiceSystem.DiceCardXmlInfo\)') 'Native enemy preview preserves the GetDeckAll result order'
    $unitDeck=IL (Method 'UnitDataModel' 'GetDeckAll' 0)
    Assert-State ($unitDeck-match'BookModel::GetCardListFromCurrentDeck\(\)[\s\S]*SortUtil::CardInfoCompByCost[\s\S]*List`1<LOR_DiceSystem.DiceCardXmlInfo>::Sort') 'GetDeckAll sorts the current book deck with the real cost comparator'
    $bookDeck=IL (Method 'BookModel' 'GetCardListFromCurrentDeck' 0)
    Assert-State ($bookDeck-match'DeckModel::GetAllCardList\(\)[\s\S]*SortUtil::CardInfoCompByCost[\s\S]*List`1<LOR_DiceSystem.DiceCardXmlInfo>::Sort') 'BookModel also sorts its copied current deck before returning it'
    $costComparator=IL (Method 'SortUtil' 'CardInfoCompByCost' 2)
    Assert-State (([regex]::Matches($costComparator,'DiceCardSpec::Cost')).Count-eq2-and([regex]::Matches($costComparator,'LorId::id')).Count-eq2) 'Native card comparator reads both costs and both numeric IDs'
    Assert-State ($costComparator-match'DiceCardSpec::Cost[\s\S]*DiceCardSpec::Cost[\s\S]*sub[\s\S]*stloc\.0[\s\S]*ldloc\.0[\s\S]*brfalse[^\n]*\n[^\n]*ldloc\.0\s*\n[^\n]*ret[\s\S]*DiceCardXmlInfo::get_id\(\)[\s\S]*LorId::id[\s\S]*DiceCardXmlInfo::get_id\(\)[\s\S]*LorId::id[\s\S]*sub\s*\n[^\n]*ret') 'Native card comparator orders cost first and uses ID only for equal costs'

    $adapter=ModType 'NativeBattleStateAdapter';$capture=$adapter.Methods|Where-Object Name -eq 'Capture';$captureIL=IL $capture
    Assert-State ($captureIL-match'get_IsHost') 'Native Capture explicitly requires host authority'
    Assert-State ($captureIL-match'get_CurrentWave\(\)[\s\S]*ldc.i4.1[\s\S]*sub.ovf') 'Adapter explicitly normalizes one-based native wave to zero-based wire index'
    $manifestMethod=$adapter.Methods|Where-Object Name -eq 'ValidateManifest';$manifestIL=IL $manifestMethod
    $enemyComparison=[regex]::Match($manifestIL,'(?s)ldfld System.Collections.Generic.List`1<System.Int32> RuinaCoop.BattleEnemyConfiguration::Cards(?<comparison>.*?)call System.Boolean System.Linq.Enumerable::SequenceEqual<System.Int32>\(')
    Assert-State ($enemyComparison.Success-and$enemyComparison.Groups['comparison'].Value-match'Enumerable::OrderBy<System.Int32,System.Int32>') 'Compiled enemy whitelist compares an ordered copy rather than XML insertion order'
    Assert-State ($enemyComparison.Groups['comparison'].Value-notmatch'Enumerable::Distinct|::ToHashSet') 'Enemy whitelist preserves the number of physical copies of each card'
    Assert-State ($manifestIL-notmatch'List`1<[^>]+>::(Sort|Clear|Add|AddRange|Remove|RemoveAll|RemoveAt|Reverse|set_Item)\('-and$manifestIL-notmatch'stfld .*Battle(Enemy|Actor)Configuration::Cards') 'Manifest validation never sorts or changes the original card lists in place'
    # The compiled initializer must contain the exact six-copy multiplicity.
    # Keep each byte array intact rather than letting PowerShell enumerate bytes.
    $whitelistArrays=@($manifestMethod.Body.Instructions|Where-Object {$_.OpCode.Code-eq[Mono.Cecil.Cil.Code]::Ldtoken-and$_.Operand-is[Mono.Cecil.FieldReference]})
    Assert-State ($whitelistArrays.Count-eq1) 'Enemy whitelist has one explicit compiled expected-card initializer'
    $whitelistBytes=$whitelistArrays[0].Operand.Resolve().InitialValue
    Assert-State ($whitelistBytes.Length-eq24) 'Enemy whitelist expects exactly six 32-bit card IDs'
    $whitelistIds=@(for($i=0;$i-lt6;$i++){[BitConverter]::ToInt32($whitelistBytes,$i*4)})
    Assert-State (($whitelistIds-join',')-eq'1,1,2,2,3,3') 'Compiled whitelist keeps exactly two copies each of IDs 1,2,3'
    $adapterIL=($adapter.Methods|Where-Object HasBody|ForEach-Object {IL $_})-join"`n"
    foreach($method in @('GetActivatedBufList','GetReadyBufList','GetReadyReadyBufList','get_PassiveList','get_ReadyPassiveList')){Assert-State ($adapterIL-match[regex]::Escape($method)) "Adapter reads actual effect queue $method"}
    foreach($name in @('_cardInDeck','_cardInHand','_cardInUse','_cardInDiscarded','_cardInReserved','_stageDataStorage','_excludedIndies','_statBonus')){Assert-State ($adapterIL.Contains('"'+$name+'"')) "Compiled adapter retains explicit read $name"}
    Assert-State ($adapterIL-notmatch'NativeUi::Set\(') 'State adapter never writes native fields'
    Assert-State ($adapterIL-notmatch'newobj.*(BattleUnitModel|UnitDataModel|BookModel|StageModel|BattleDiceCardModel)::.ctor') 'Adapter never creates gameplay or inventory models'
    Assert-State ($adapterIL-match'GetType'-and$adapterIL-match'StatBonus'-and$adapterIL-match'EnemyTeamStageManager') 'Derived gameplay scripts and state containers are guarded'
    Assert-State (((ModType 'BattleActorState').Fields|Where-Object Name -eq 'Hp').FieldType.FullName-eq'System.Single') 'Wire HP preserves IEEE-754 single values'
    $presentation=(ModType 'BattlePresentation').Methods|Where-Object HasBody|ForEach-Object {IL $_}
    Assert-State (($presentation-join"`n")-notmatch'(StageController::|BattleSceneRoot::|newobj.*(BattleUnitModel|UnitDataModel|BookModel|StageModel)::.ctor)') 'Guest presentation uses DTOs without native battle construction'

    # TextAssets contain uncompressed UTF-8 XML inside resources.assets. Read the
    # current file directly; no ignored extracted artifact is a test dependency.
    $resourcePath=Join-Path $GameDir 'LibraryOfRuina_Data\resources.assets'
    $resource=[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($resourcePath))
    function XmlEntry([string]$tag,[string]$key,[int]$id){
        if($tag-eq'Stage'){
            $roots=[regex]::Matches($resource,'(?s)<StageXmlRoot>.*?</StageXmlRoot>');$nodes=@(foreach($root in $roots){([xml]$root.Value).SelectNodes('/StageXmlRoot/Stage[@id="'+$id+'"]')})
            Assert-State ($nodes.Count-eq1) "Unique embedded $tag $id";return [xml]$nodes[0].OuterXml
        }
        $matches=[regex]::Matches($resource,'(?s)<'+$tag+' '+$key+'="'+$id+'">.*?</'+$tag+'>')
        Assert-State ($matches.Count-eq1) "Unique embedded $tag $id";return [xml]$matches[0].Value
    }
    $stage=XmlEntry 'Stage' 'id' 3
    Assert-State (@($stage.Stage.Wave).Count-eq1) 'Stage 3 is a single wave'
    Assert-State (($stage.Stage.Wave.Unit-join',')-eq'1003,1004') 'Stage 3 has exact ordinary enemy IDs 1003,1004'
    Assert-State (!$stage.Stage.Wave.ManagerScript-and!$stage.Stage.Wave.AggroScript) 'Stage 3 XML has no manager or aggro script'
    Assert-State (@($stage.Stage.Story|Where-Object Condition -eq 'PrevBattle').Count-eq0) 'Stage 3 has no previous-battle scene story'
    foreach($id in @((1..20|Where-Object {$_-ne6-and$_-ne8})+@(200001,200002,200003,101003,101004))){
        $book=XmlEntry 'Book' 'ID' $id
        Assert-State (!$book.Book.EquipEffect.Passive) "Book $id has no native passive"
        Assert-State (!$book.Book.EquipEffect.OnlyCard-and!$book.Book.EquipEffect.CardList) "Book $id has no special card mechanism"
    }
    foreach($id in @(1003,1004)){$enemy=XmlEntry 'Enemy' 'ID' $id;Assert-State (!$enemy.Enemy.AiScript) "Enemy $id has no AI script";Assert-State ($enemy.Enemy.DeckId-eq'101003') "Enemy $id shares vanilla deck 101003"}
    $deck=XmlEntry 'Deck' 'ID' 101003
    Assert-State (($deck.Deck.Card-join',')-eq'1,2,3,1,2,3') 'Enemy deck is exactly six independent basic card copies'
    $basicCosts=@{};$expectedCosts=@(0,1,1,2,3)
    foreach($id in 1..5){
        $card=XmlEntry 'Card' 'ID' $id
        $basicCosts[$id]=[int]$card.Card.Spec.Cost
        Assert-State ($basicCosts[$id]-eq$expectedCosts[$id-1]) "Basic card $id has native XML cost $($expectedCosts[$id-1])"
        Assert-State ([string]::IsNullOrEmpty([string]$card.Card.Script)) "Basic card $id has no self script"
        foreach($die in $card.Card.BehaviourList.Behaviour){Assert-State ([string]::IsNullOrEmpty($die.Script)-and[string]::IsNullOrEmpty($die.ActionScript)) "Basic card $id die has no dice/action script"}
    }
    $previewIds=@($deck.Deck.Card|ForEach-Object {[int]$_}|Sort-Object @{Expression={$basicCosts[$_]}},@{Expression={$_}})
    Assert-State (($previewIds-join',')-eq'1,1,2,2,3,3') 'Real six-card enemy preview is cost/ID sorted 1,1,2,2,3,3'
    Assert-State (($previewIds-join',')-ne($deck.Deck.Card-join',')) 'Enemy preparation order differs from its XML insertion order'
    $resource=$null
    $report=[ordered]@{status='passed';checks=$checks.Count;gameAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -Algorithm SHA256).Hash;resourceAssetsSha256=(Get-FileHash -LiteralPath $resourcePath -Algorithm SHA256).Hash;modAssemblySha256=(Get-FileHash -LiteralPath $ModAssembly -Algorithm SHA256).Hash;supportedStage=3;supportedEnemies=@(1003,1004);supportedCards=@(1,2,3,4,5);notes='Read-only metadata/XML checks. Does not execute Unity or Steam; actual dual-end first-round validation remains required.';details=@($checks)}
    $json=$report|ConvertTo-Json -Depth 6
    if($OutputPath){$dir=Split-Path -Parent $OutputPath;if($dir){New-Item -ItemType Directory -Path $dir -Force|Out-Null};$json|Set-Content -LiteralPath $OutputPath -Encoding UTF8}
    Write-Output "Battle state target audit passed: $($checks.Count) checks. No game code or saves executed."
}finally{$game.Dispose();$mod.Dispose()}
