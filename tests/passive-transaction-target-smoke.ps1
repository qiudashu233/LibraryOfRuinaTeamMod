# Read-only Cecil metadata/IL contract. Does not load Unity, invoke game
# constructors/singletons, or call any original game method or save API.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Resolve-Path -LiteralPath $CecilPath).ProviderPath
$transactionAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'LibraryOfRuina_Data\Managed\Assembly-CSharp.dll'))
$transactionChecks = New-Object 'System.Collections.Generic.List[string]'
function Assert-Tx([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw "Passive transaction contract failed: $Name" }
    $transactionChecks.Add($Name)
}
function Get-TxType([string]$Name) {
    $parts = $Name.Split('/')
    $type = $transactionAssembly.MainModule.Types | Where-Object FullName -eq $parts[0]
    if ($parts.Count -gt 1) { $type = $type.NestedTypes | Where-Object Name -eq $parts[1] }
    Assert-Tx ($null -ne $type) "Type $Name exists"
    return $type
}
function Get-TxField([string]$TypeName,[string]$Name,[string]$Type) {
    $field = (Get-TxType $TypeName).Fields | Where-Object Name -eq $Name
    Assert-Tx ($null -ne $field) "Field $TypeName.$Name exists"
    Assert-Tx ($field.FieldType.FullName -eq $Type) "Field $TypeName.$Name has $Type shape"
    return $field
}
function Get-TxMethod([string]$TypeName,[string]$Name,[string]$Return,[string[]]$Params=@()) {
    $matches = @((Get-TxType $TypeName).Methods | Where-Object {
        $candidate=$_
        if ($candidate.Name -ne $Name -or $candidate.ReturnType.FullName -ne $Return -or $candidate.Parameters.Count -ne $Params.Count) { return $false }
        for ($i=0;$i -lt $Params.Count;$i++) { if ($candidate.Parameters[$i].ParameterType.FullName -ne $Params[$i]) { return $false } }
        return $true
    })
    Assert-Tx ($matches.Count -eq 1) "Method $TypeName.$Name($($Params -join ', ')) -> $Return matches"
    return $matches[0]
}
function Tx-Calls($Method,[string]$Name) {
    return ,@($Method.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') -and $_.Operand.Name -eq $Name })
}
function Tx-Fields($Method,[string]$Opcode,[string]$Name) {
    return ,@($Method.Body.Instructions | Where-Object { $_.OpCode.Name -eq $Opcode -and $_.Operand.Name -eq $Name })
}
try {
    $fieldTargets=@(
        @('LibraryModel','_floorList','System.Collections.Generic.List`1<LibraryFloorModel>'),
        @('LibraryFloorModel','_unitDataList','System.Collections.Generic.List`1<UnitDataModel>'),
        @('UnitDataModel','_bookItem','BookModel'), @('UnitDataModel','_CustomBookItem','BookModel'),
        @('BookModel','_deck','DeckModel'), @('BookModel','_deckList','System.Collections.Generic.List`1<DeckModel>'),
        @('BookModel','_activatedAllPassives','System.Collections.Generic.List`1<PassiveModel>'),
        @('BookModel','originData','BookModel/BookEquipedBookSavedData'), @('BookModel','reservedData','BookModel/BookEquipedBookSavedData'),
        @('BookModel','owner','UnitDataModel'), @('BookModel','instanceId','System.Int32'),
        @('BookModel/BookEquipedBookSavedData','equipedBookIdListInPassive','System.Collections.Generic.List`1<System.Int32>'),
        @('BookModel/BookEquipedBookSavedData','equipedPassiveBookInstanceId','System.Int32'),
        @('PassiveModel','originpassive','PassiveXmlInfo'), @('PassiveModel','_bookInstanceId','System.Int32'),
        @('PassiveModel','originData','PassiveModel/PassiveModelSavedData'), @('PassiveModel','reservedData','PassiveModel/PassiveModelSavedData'),
        @('PassiveModel/PassiveModelSavedData','currentpassive','PassiveXmlInfo'),
        @('PassiveModel/PassiveModelSavedData','givePassiveBookId','System.Int32'),
        @('PassiveModel/PassiveModelSavedData','receivepassivebookId','System.Int32'),
        @('InventoryModel','_cardList','System.Collections.Generic.List`1<DiceCardItemModel>'),
        @('DiceCardItemModel','_xmlData','LOR_DiceSystem.DiceCardXmlInfo'), @('DiceCardItemModel','num','System.Int32'),
        @('LOR_DiceSystem.DiceCardXmlInfo','optionList','System.Collections.Generic.List`1<LOR_DiceSystem.CardOption>'),
        @('PassiveXmlInfo','cost','System.Int32'), @('PassiveXmlInfo','InnerTypeId','System.Int32'),
        @('PassiveXmlInfo','isNegative','System.Boolean'), @('PassiveXmlInfo','rare','Rarity'),
        @('PassiveXmlInfo','isLock','System.Boolean'), @('PassiveXmlInfo','isHide','System.Boolean'),
        @('PassiveXmlInfo','CanGivePassive','System.Boolean'), @('PassiveXmlInfo','CanReceivePassive','System.Boolean')
    )
    foreach ($target in $fieldTargets) { $null = Get-TxField $target[0] $target[1] $target[2] }
    $methodTargets=@(
        @('BookModel','InitReservedDataForPassiveSuccession','System.Void',@()),
        @('BookModel','GetPassiveModelList','System.Collections.Generic.List`1<PassiveModel>',@()),
        @('BookModel','UnEquipGivePassiveBook','System.Void',@('BookModel','System.Boolean')),
        @('BookModel','EquipGivePassiveBook','System.Boolean',@('BookModel')),
        @('BookModel','ChangePassive','System.Void',@('PassiveModel','PassiveModel')),
        @('BookModel','CanSuccessionPassive','System.Boolean',@('PassiveModel','GivePassiveState&')),
        @('BookModel','CanSuccessionPassiveByCost','System.Boolean',@('PassiveModel','PassiveModel','System.Boolean')),
        @('BookModel','GetCurrentPassiveCost','System.Int32',@('System.Boolean')),
        @('BookModel','GetMaxPassiveCost','System.Int32',@()),
        @('BookModel','ApplyPassiveSuccession','System.Void',@()),
        @('BookModel','IsEmptyDeckAll','System.Boolean',@()),
        @('BookModel','EmptyDeckToInventoryAll','System.Void',@()),
        @('BookModel','GetDeckAll_nocopy','System.Collections.Generic.List`1<DeckModel>',@()),
        @('PassiveModel','InitReservedData','System.Void',@()),
        @('PassiveModel','ReleaseSuccesionReceivePassive','System.Void',@('System.Boolean')),
        @('PassiveModel','ReleaseSuccesionGivePassive','System.Void',@('System.Boolean')),
        @('PassiveModel','get_CanToGivePassive','System.Boolean',@()),
        @('PassiveModel','get_BookInstanceId','System.Int32',@()),
        @('PassiveModel','ApplyReserved','System.Void',@()),
        @('PassiveModel','SuccessionPassiveForReserved','System.Void',@('PassiveModel')),
        @('PassiveModel','SetGiveBookId','System.Void',@('System.Int32')),
        @('PassiveModel','DeepCopy','PassiveXmlInfo',@('PassiveXmlInfo')),
        @('PassiveModel/PassiveModelSavedData','DeepCopy','System.Void',@('PassiveModel/PassiveModelSavedData')),
        @('InventoryModel','GetCardListOrigin','System.Collections.Generic.List`1<DiceCardItemModel>',@()),
        @('InventoryModel','AddCard','System.Void',@('LorId','System.Int32')),
        @('DiceCardItemModel','get_ClassInfo','LOR_DiceSystem.DiceCardXmlInfo',@()),
        @('DeckModel','GetCardList_nocopy','System.Collections.Generic.List`1<LOR_DiceSystem.DiceCardXmlInfo>',@()),
        @('DeckModel','EmptyDeckToInventory','System.Void',@()),
        @('DeckModel','MoveCardToInventory','System.Boolean',@('LorId')),
        @('UnitDataModel','IsChangeItemLock','System.Boolean',@())
    )
    $methods=@{}
    foreach ($target in $methodTargets) { $methods[$target[0]+'.'+$target[1]]=Get-TxMethod $target[0] $target[1] $target[2] $target[3] }
    foreach ($name in @('BookModel.InitReservedDataForPassiveSuccession','BookModel.EquipGivePassiveBook','BookModel.UnEquipGivePassiveBook',
        'BookModel.ChangePassive','BookModel.ApplyPassiveSuccession','BookModel.EmptyDeckToInventoryAll','DeckModel.EmptyDeckToInventory',
        'DeckModel.MoveCardToInventory','PassiveModel.InitReservedData','PassiveModel.SuccessionPassiveForReserved',
        'PassiveModel.SetGiveBookId','PassiveModel.ReleaseSuccesionReceivePassive','PassiveModel.ReleaseSuccesionGivePassive','PassiveModel.ApplyReserved')) {
        $method=$methods[$name]
        $unsafeCalls=@($method.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') -and
            ($_.Operand.Name -match 'SavePlayData|SaveLatestData|UnlockAchievement|ReleaseAllEquipedPassiveBooks|GetBookList_equip|InitReservedDataForAll' -or
             $_.Operand.DeclaringType.FullName -like 'System.IO.*') })
        Assert-Tx ($unsafeCalls.Count -eq 0) "$name has no save/achievement/global inventory reconciliation call"
    }
    $apply=$methods['BookModel.ApplyPassiveSuccession']
    Assert-Tx ((Tx-Calls $apply 'ApplyReserved').Count -eq 1) 'Book apply commits each local passive buffer'
    Assert-Tx ((Tx-Calls $apply 'EmptyDeckToInventoryAll').Count -eq 1 -and (Tx-Calls $apply 'IsEmptyDeckAll').Count -eq 1) 'Donor commit uses native conditional combat-page return'
    Assert-Tx ((Tx-Fields $apply 'stfld' 'equipedPassiveBookInstanceId').Count -eq 1) 'Book apply commits its recipient link'
    $empty=$methods['BookModel.IsEmptyDeckAll']
    Assert-Tx ($empty.Body.Instructions[1].Operand.Name -eq 'IsMultiDeck' -and $empty.Body.Instructions[2].OpCode.Name -like 'brfalse*') 'Only multiple-deck IsEmptyDeckAll scans stored decks'
    $last=@($empty.Body.Instructions | Select-Object -Last 4)
    Assert-Tx ($last[0].OpCode.Name -eq 'ldarg.0' -and $last[1].Operand.Name -eq '_deck' -and $last[2].Operand.Name -eq 'IsDeckEmpty') 'Ordinary IsEmptyDeckAll tests only current deck'
    Assert-Tx ((Tx-Calls $methods['BookModel.EmptyDeckToInventoryAll'] 'EmptyDeckToInventory').Count -eq 2) 'Native return visits stored decks then current deck (possibly same object)'
    Assert-Tx ((Tx-Calls $methods['DeckModel.EmptyDeckToInventory'] 'MoveCardToInventory').Count -eq 1) 'Deck return removes each original card id one at a time'
    Assert-Tx ((Tx-Calls $methods['DeckModel.MoveCardToInventory'] 'AddCard').Count -eq 1) 'Each removed card contributes one inventory AddCard call'
    Assert-Tx ((Tx-Calls $methods['BookModel.ChangePassive'] 'SuccessionPassiveForReserved').Count -eq 1) 'ChangePassive applies only the selected target/source slots'
    Assert-Tx ((Tx-Calls $methods['PassiveModel.SuccessionPassiveForReserved'] 'SetGiveBookId').Count -eq 1) 'Selection records source slot give relation'
    Assert-Tx ((Tx-Fields $methods['PassiveModel.get_CanToGivePassive'] 'ldfld' 'reservedData').Count -eq 1) 'CanToGivePassive is a reserved-buffer property, not an origin getter'
    Assert-Tx ((Tx-Fields $methods['PassiveModel.get_BookInstanceId'] 'ldfld' '_bookInstanceId').Count -eq 1) 'Passive slot identity uses its owning book instance'
    Assert-Tx ((Tx-Calls $methods['BookModel.CanSuccessionPassive'] 'op_Equality').Count -eq 1 -and
        (Tx-Fields $methods['BookModel.CanSuccessionPassive'] 'ldfld' 'InnerTypeId').Count -eq 3) 'Native compatibility checks both LorId and nonnegative inner-type equality'
    Assert-Tx ((Tx-Calls $methods['BookModel.CanSuccessionPassiveByCost'] 'GetPossibleRemainCost').Count -eq 1) 'Native slot-cost validation calculates remaining receiver budget'
    Assert-Tx ((Tx-Fields $methods['BookModel.GetCurrentPassiveCost'] 'ldfld' 'givePassiveBookId').Count -eq 1 -and
        (Tx-Fields $methods['BookModel.GetCurrentPassiveCost'] 'ldfld' 'receivepassivebookId').Count -eq 1) 'Passive cost charges nonnative relation slots only'
    $copy=$methods['PassiveModel.DeepCopy']
    $writes=@($copy.Body.Instructions | Where-Object OpCode -ne $null | Where-Object { $_.OpCode.Name -eq 'stfld' } | ForEach-Object { $_.Operand.Name })
    Assert-Tx (($writes -join ',') -eq '_id,workshopID,isNegative,param,rare,cost') 'Source temporary XML DeepCopy copies identity/stats only, not give/receive/lock/inner metadata'
    Assert-Tx ((Tx-Calls $methods['PassiveModel/PassiveModelSavedData.DeepCopy'] 'GetData').Count -eq 1) 'Saved passive copy resolves canonical XML by id'
    $getList=$methods['BookModel.GetPassiveModelList']
    $last=@($getList.Body.Instructions | Select-Object -Last 2)
    Assert-Tx ($last[0].OpCode.Name -eq 'ldfld' -and $last[0].Operand.Name -eq '_activatedAllPassives') 'Native passive list returns actual fixed slot order'
    $item=$methods['DiceCardItemModel.get_ClassInfo']
    Assert-Tx ((Tx-Fields $item 'ldfld' '_xmlData').Count -eq 1) 'Inventory ClassInfo getter reads row XML without side effects'
    $add=$methods['InventoryModel.AddCard'];$il=$add.Body.Instructions
    $find=(Tx-Calls $add 'Find')[0];$findIndex=$il.IndexOf($find)
    Assert-Tx ($findIndex -ge 0 -and $il[$findIndex+3].OpCode.Name -like 'brtrue*') 'AddCard branches to existing-row increment before XML/options lookup'
    Assert-Tx ((Tx-Calls $add 'Add').Count -eq 1 -and (Tx-Calls $add 'Sort').Count -eq 1) 'New ordinary inventory row is appended and then inventory sorted'
    $options=@($il | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'optionList' })
    Assert-Tx ($options.Count -eq 2 -and $il[$il.IndexOf($options[0])+1].OpCode.Name -eq 'ldc.i4.5' -and
        $il[$il.IndexOf($options[1])+1].OpCode.Name -eq 'ldc.i4.0') 'No new row is inserted for NoInventory(5) or Basic(0) pages'
    $enum=Get-TxType 'LOR_DiceSystem.CardOption'
    Assert-Tx (($enum.Fields | Where-Object Name -eq Basic).Constant -eq 0 -and ($enum.Fields | Where-Object Name -eq NoInventory).Constant -eq 5) 'Actual Basic/NoInventory option enum values match the adapter'
    Assert-Tx (@($il | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'num' }).Count -eq 2) 'AddCard initializes a new quantity or increments the existing row'
    $originList=$methods['InventoryModel.GetCardListOrigin']
    Assert-Tx ($originList.Body.Instructions.Count -eq 3 -and $originList.Body.Instructions[1].Operand.Name -eq '_cardList') 'GetCardListOrigin preserves the actual inventory list reference'
    $emptyAll=$methods['BookModel.EmptyDeckToInventoryAll']
    Assert-Tx ((Tx-Fields $emptyAll 'ldfld' '_deckList').Count -eq 1 -and (Tx-Fields $emptyAll 'ldfld' '_deck').Count -eq 1) 'Book return also empties a current deck absent from the stored list; repeated references are empty on later calls'
    foreach ($name in @('BookModel/BookEquipedBookSavedData','PassiveModel/PassiveModelSavedData')) {
        $type=Get-TxType $name
        $other=@($type.Fields | Where-Object { -not $_.IsStatic -and $_.FieldType.FullName -notin @('System.Int32','PassiveXmlInfo','System.Collections.Generic.List`1<System.Int32>') })
        Assert-Tx ($other.Count -eq 0) "$name rollback covers all actual instance-field shapes"
    }
    $result=[pscustomobject]@{Success=$true;CheckCount=$transactionChecks.Count;FieldCount=$fieldTargets.Count;MethodCount=$methodTargets.Count;
        Execution='Cecil metadata/IL only; no constructors, game singletons, Unity, saves or original method execution.';Checks=$transactionChecks.ToArray()}
    if ($OutputPath) { $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "PASS: $($transactionChecks.Count) actual game passive transaction metadata/IL checks ($($fieldTargets.Count) fields, $($methodTargets.Count) method signatures)."
} finally { $transactionAssembly.Dispose() }
