# Read-only game metadata/IL contract. Cecil does not construct game objects or
# invoke Unity/Steam singletons. Run after building the mod to locate bundled Cecil.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Resolve-Path -LiteralPath $CecilPath).ProviderPath
$equipmentAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly(
    (Join-Path $GameDir 'LibraryOfRuina_Data\Managed\Assembly-CSharp.dll'))
$equipmentChecks = New-Object 'System.Collections.Generic.List[string]'

function Assert-Equipment([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Equipment transaction contract failed: $Message" }
    $equipmentChecks.Add($Message)
}
function Get-EquipmentType([string]$Name) {
    $type = $equipmentAssembly.MainModule.Types | Where-Object FullName -eq $Name
    if ($null -eq $type -and $Name.Contains('/')) {
        $parts = $Name.Split('/')
        $outer = $equipmentAssembly.MainModule.Types | Where-Object FullName -eq $parts[0]
        $type = $outer.NestedTypes | Where-Object Name -eq $parts[1]
    }
    Assert-Equipment ($null -ne $type) "Type $Name exists"
    return $type
}
function Assert-EquipmentField([string]$TypeName, [string]$Name, [string]$FieldType) {
    $type = Get-EquipmentType $TypeName
    $field = $type.Fields | Where-Object Name -eq $Name
    Assert-Equipment ($null -ne $field) "Field $TypeName.$Name exists"
    Assert-Equipment ($field.FieldType.FullName -eq $FieldType) "Field $TypeName.$Name is $FieldType"
    Assert-Equipment (-not $field.IsStatic -and -not $field.IsInitOnly -and -not $field.IsLiteral) "Field $TypeName.$Name is a mutable instance field"
    return $field
}
function Get-EquipmentMethod([string]$TypeName, [string]$Name, [string]$ReturnType, [string[]]$Parameters = @()) {
    $type = Get-EquipmentType $TypeName
    $methods = @($type.Methods | Where-Object { $_.Name -eq $Name -and $_.Parameters.Count -eq $Parameters.Count })
    $match = @($methods | Where-Object {
        $candidate = $_
        $same = $candidate.ReturnType.FullName -eq $ReturnType
        for ($i = 0; $i -lt $Parameters.Count; $i++) {
            if ($candidate.Parameters[$i].ParameterType.FullName -ne $Parameters[$i]) { $same = $false }
        }
        $same
    })
    Assert-Equipment ($match.Count -eq 1) "Method $TypeName.$Name signature is ($($Parameters -join ', ')) -> $ReturnType"
    return $match[0]
}

try {
    $equipmentFields = @(
        @('UnitDataModel', '_bookItem', 'BookModel'),
        @('UnitDataModel', '_CustomBookItem', 'BookModel'),
        @('UnitDataModel', '_defaultBook', 'BookModel'),
        @('UnitDataModel', 'appearanceType', 'Gender'),
        @('BookModel', '_deck', 'DeckModel'),
        @('BookModel', '_deckList', 'System.Collections.Generic.List`1<DeckModel>'),
        @('BookModel', '_activatedAllPassives', 'System.Collections.Generic.List`1<PassiveModel>'),
        @('BookModel', 'originData', 'BookModel/BookEquipedBookSavedData'),
        @('BookModel', 'reservedData', 'BookModel/BookEquipedBookSavedData'),
        @('BookModel', 'owner', 'UnitDataModel'),
        @('BookModel', 'basicBookOwner', 'UnitDataModel'),
        @('BookModel', 'instanceId', 'System.Int32'),
        @('DeckModel', '_deck', 'System.Collections.Generic.List`1<LOR_DiceSystem.DiceCardXmlInfo>'),
        @('InventoryModel', '_cardList', 'System.Collections.Generic.List`1<DiceCardItemModel>'),
        @('BookInventoryModel', '_bookList', 'System.Collections.Generic.List`1<BookModel>'),
        @('LibraryModel', '_floorList', 'System.Collections.Generic.List`1<LibraryFloorModel>'),
        @('LibraryFloorModel', '_unitDataList', 'System.Collections.Generic.List`1<UnitDataModel>'),
        @('PassiveModel', 'originpassive', 'PassiveXmlInfo'),
        @('PassiveModel', 'originData', 'PassiveModel/PassiveModelSavedData'),
        @('PassiveModel', 'reservedData', 'PassiveModel/PassiveModelSavedData'),
        @('PassiveModel', '_bookInstanceId', 'System.Int32'),
        @('BookModel/BookEquipedBookSavedData', 'equipedBookIdListInPassive', 'System.Collections.Generic.List`1<System.Int32>'),
        @('BookModel/BookEquipedBookSavedData', 'equipedPassiveBookInstanceId', 'System.Int32'),
        @('PassiveModel/PassiveModelSavedData', 'currentpassive', 'PassiveXmlInfo'),
        @('PassiveModel/PassiveModelSavedData', 'receivepassivebookId', 'System.Int32'),
        @('PassiveModel/PassiveModelSavedData', 'givePassiveBookId', 'System.Int32'),
        @('DiceCardItemModel', 'num', 'System.Int32'),
        @('BookXmlInfo', 'isError', 'System.Boolean'),
        @('BookXmlInfo', 'canNotEquip', 'System.Boolean'),
        @('BookXmlInfo', 'optionList', 'System.Collections.Generic.List`1<BookOption>'),
        @('PlayHistoryModel', 'Start_TheBlueReverberationPrimaryBattle', 'System.Int32')
        @('PassiveXmlInfo', 'isNegative', 'System.Boolean'),
        @('PassiveXmlInfo', 'rare', 'Rarity')
    )
    foreach ($target in $equipmentFields) { $null = Assert-EquipmentField $target[0] $target[1] $target[2] }

    # ObjectState copies all instance fields. Fail if an update adds a collection
    # or readonly field whose contents this local rollback does not understand.
    foreach ($shape in @(@('PassiveModel', 4), @('PassiveModel/PassiveModelSavedData', 3), @('BookModel/BookEquipedBookSavedData', 2))) {
        $type = Get-EquipmentType $shape[0]
        $fields = @($type.Fields | Where-Object { -not $_.IsStatic })
        Assert-Equipment ($fields.Count -eq $shape[1]) "ObjectState $($shape[0]) contains the verified instance-field shape"
        foreach ($field in $fields) {
            Assert-Equipment (-not $field.IsInitOnly -and $field.FieldType.FullName -in @(
                'System.Int32', 'PassiveXmlInfo', 'PassiveModel/PassiveModelSavedData', 'System.Collections.Generic.List`1<System.Int32>')) "ObjectState $($shape[0]).$($field.Name) has a supported mutable scalar/XML/saved-data/list type"
        }
    }

    $equipmentMethods = @(
        @('UnitDataModel', 'EquipBookForUI', 'System.Boolean', @('BookModel', 'System.Boolean', 'System.Boolean')),
        @('UnitDataModel', 'EquipBook', 'System.Boolean', @('BookModel', 'System.Boolean', 'System.Boolean')),
        @('UnitDataModel', 'IsChangeItemLock', 'System.Boolean', @()),
        @('UnitDataModel', 'get_OwnerSephirah', 'SephirahType', @()),
        @('UnitDataModel', 'get_defaultBook', 'BookModel', @()),
        @('UnitDataModel', 'get_bookItem', 'BookModel', @()),
        @('BookModel', 'get_BookId', 'LorId', @()),
        @('BookModel', 'get_ClassInfo', 'BookXmlInfo', @()),
        @('BookModel', 'IsFixedDeck', 'System.Boolean', @()),
        @('BookModel', 'IsDeckLocked', 'System.Boolean', @()),
        @('BookModel', 'IsLockByBluePrimary', 'System.Boolean', @()),
        @('BookModel', 'IsMultiDeck', 'System.Boolean', @()),
        @('BookModel', 'CanEquipBookByGivePassive', 'System.Boolean', @()),
        @('BookModel', 'SetBasicBookOwner', 'System.Void', @('UnitDataModel')),
        @('BookModel', 'GetDeckAll_nocopy', 'System.Collections.Generic.List`1<DeckModel>', @()),
        @('DeckModel', 'GetCardList_nocopy', 'System.Collections.Generic.List`1<LOR_DiceSystem.DiceCardXmlInfo>', @()),
        @('BookInventoryModel', 'GetBookList_equip', 'System.Collections.Generic.List`1<BookModel>', @()),
        @('BookInventoryModel', 'GetBlackSilenceBook', 'BookModel', @()),
        @('LibraryModel', 'get_PlayHistory', 'PlayHistoryModel', @()),
        @('LibraryModel', 'IsClearTheBlueReverberationPrimary', 'System.Boolean', @('SephirahType')),
        @('LibraryFloorModel', 'get_Sephirah', 'SephirahType', @()),
        @('LorId', 'IsBasic', 'System.Boolean', @()),
        @('LorId', 'op_Inequality', 'System.Boolean', @('LorId', 'LorId')),
        @('PassiveXmlInfo', 'get_id', 'LorId', @()),
        @('LOR_DiceSystem.DiceCardXmlInfo', 'get_id', 'LorId', @())
    )
    foreach ($target in $equipmentMethods) { $null = Get-EquipmentMethod $target[0] $target[1] $target[2] $target[3] }
    $canEquip = Get-EquipmentMethod 'BookModel' 'CanEquipBookByGivePassive' 'System.Boolean'
    $canEquipFields = @($canEquip.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' })
    Assert-Equipment ($canEquipFields.Count -eq 2 -and $canEquipFields[0].Operand.Name -eq 'originData' -and
        $canEquipFields[1].Operand.Name -eq 'equipedPassiveBookInstanceId') 'CanEquipBookByGivePassive reads only committed book donor state, never passive reserved buffers'
    Assert-Equipment (@($canEquip.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.i4.m1' }).Count -eq 1 -and
        @($canEquip.Body.Instructions | Where-Object { $_.OpCode.Name -like 'bne.un*' }).Count -eq 1 -and
        @($canEquip.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.i4.1' }).Count -eq 1) 'CanEquipBookByGivePassive accepts committed donor id -1'

    # Field signatures alone cannot establish lifecycle. Native creation/loading
    # leaves passive reservedData null; only the succession UI creates that buffer.
    foreach ($target in @(
        @('PassiveModel', '.ctor', 'System.Void', @('LorId', 'System.Int32', 'System.Int32')),
        @('PassiveModel', 'LoadFromSaveData', 'System.Void', @('GameSave.SaveData'))
    )) {
        $method = Get-EquipmentMethod $target[0] $target[1] $target[2] $target[3]
        $body = $method.Body.Instructions
        Assert-Equipment (@($body | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'originData' }).Count -eq 1) "$($target[1]) initializes passive originData"
        Assert-Equipment (@($body | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'reservedData' }).Count -eq 0) "$($target[1]) leaves passive reservedData uninitialized"
        Assert-Equipment (@($body | Where-Object { $_.OpCode.Name -in @('call', 'callvirt') -and $_.Operand.Name -eq 'InitReservedData' }).Count -eq 0) "$($target[1]) does not implicitly initialize a passive draft"
    }
    $bookCtor = Get-EquipmentMethod 'BookModel' '.ctor' 'System.Void' @('BookXmlInfo')
    foreach ($name in @('originData', 'reservedData')) {
        Assert-Equipment (@($bookCtor.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $name }).Count -eq 1) "Book constructor allocates its default empty $name object"
    }
    $bookLoad = Get-EquipmentMethod 'BookModel' 'LoadFromSaveDataExceptId' 'System.Void' @('GameSave.SaveData')
    Assert-Equipment (@($bookLoad.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'reservedData' -or
        $_.OpCode.Name -in @('call', 'callvirt') -and $_.Operand.Name -in @('InitReservedData', 'InitReservedDataForPassiveSuccession') }).Count -eq 0) 'Book loading keeps the constructor-empty reserved object instead of copying committed inheritance'
    $popupInit = Get-EquipmentMethod 'UI.UIPassiveSuccessionPopup' 'InitReservedData' 'System.Void'
    $bookInitCall = @($popupInit.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and $_.Operand.Name -eq 'InitReservedDataForPassiveSuccession' })
    $passiveInitCall = @($popupInit.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and $_.Operand.Name -eq 'InitReservedData' })
    Assert-Equipment ($bookInitCall.Count -eq 1 -and $passiveInitCall.Count -eq 1 -and $bookInitCall[0].Offset -lt $passiveInitCall[0].Offset) 'Succession popup explicitly initializes book then passive reserved buffers'
    $changed = Get-EquipmentMethod 'PassiveModel' 'IsChangedReserved' 'System.Boolean'
    foreach ($name in @('currentpassive', 'receivepassivebookId', 'givePassiveBookId', 'isNegative', 'rare')) {
        Assert-Equipment (@($changed.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq $name }).Count -ge 2) "Native passive draft comparison reads $name from both states"
    }

    $equip = Get-EquipmentMethod 'UnitDataModel' 'EquipBook' 'System.Boolean' @('BookModel', 'System.Boolean', 'System.Boolean')
    $instructions = $equip.Body.Instructions
    Assert-Equipment ($instructions[$instructions.Count - 2].OpCode.Name -eq 'ldc.i4.0' -and $instructions[$instructions.Count - 1].OpCode.Name -eq 'ret') 'EquipBook successful fallthrough returns false; transaction must use postconditions'
    Assert-Equipment (@($instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq '_defaultBook' }).Count -eq 0) 'EquipBook preserves the unit default-book reference'
    Assert-Equipment (@($instructions | Where-Object { $_.OpCode.Name -in @('call', 'callvirt') -and $_.Operand.Name -eq 'SetBasicBookOwner' }).Count -eq 0) 'EquipBook preserves the default page basicBookOwner relation'
    Assert-Equipment (@($instructions | Where-Object { $_.OpCode.Name -eq 'callvirt' -and $_.Operand.Name -eq 'SetOwner' }).Count -eq 2) 'EquipBook changes target/raw-old owner references only'
    $defaultGetter = Get-EquipmentMethod 'UnitDataModel' 'get_defaultBook' 'BookModel'
    Assert-Equipment (@($defaultGetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq '_defaultBook' }).Count -eq 1) 'defaultBook getter reads the persistent default reference'
    $bookGetter = Get-EquipmentMethod 'UnitDataModel' 'get_bookItem' 'BookModel'
    Assert-Equipment (@($bookGetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq '_defaultBook' }).Count -eq 1) 'bookItem falls back to the default when raw equipment is absent'
    $basicOwnerSetter = Get-EquipmentMethod 'BookModel' 'SetBasicBookOwner' 'System.Void' @('UnitDataModel')
    Assert-Equipment (@($basicOwnerSetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' }).Count -eq 1 -and
        @($basicOwnerSetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' })[0].Operand.Name -eq 'basicBookOwner') 'Default page basicBookOwner is distinct from equipped owner'
    Assert-Equipment ((Get-EquipmentType 'PassiveXmlInfo').IsClass) 'currentpassive is a nullable XML class reference; both-null comparison is type compatible'
    $ownerSetter = Get-EquipmentMethod 'BookModel' 'SetOwner' 'System.Void' @('UnitDataModel')
    Assert-Equipment (@($ownerSetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' }).Count -eq 1 -and
        @($ownerSetter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' })[0].Operand.Name -eq 'owner') 'SetOwner is a direct owner assignment'

    $pipeline = @(
        @('UnitDataModel', 'EquipBook', 'System.Boolean', @('BookModel', 'System.Boolean', 'System.Boolean')),
        @('UnitDataModel', 'ReEquipDeck', 'System.Void', @()),
        @('UnitDataModel', 'EmptyDeckToInventoryAll', 'System.Void', @()),
        @('UnitDataModel', 'AddCardInDeckFromInventory', 'CardEquipState', @('LOR_DiceSystem.DiceCardXmlInfo')),
        @('UnitDataModel', 'AddCardFromInventory', 'CardEquipState', @('LorId')),
        @('BookModel', 'EmptyDeckToInventoryAll', 'System.Void', @()),
        @('BookModel', 'AddCardFromInventoryToCurrentDeck', 'CardEquipState', @('LorId')),
        @('BookModel', 'ChangeDeck', 'System.Void', @('System.Int32')),
        @('BookModel', 'SetOwner', 'System.Void', @('UnitDataModel')),
        @('DeckModel', 'EmptyDeckToInventory', 'System.Void', @()),
        @('DeckModel', 'AddCardFromInventory', 'CardEquipState', @('LorId'))
    )
    foreach ($target in $pipeline) {
        $method = Get-EquipmentMethod $target[0] $target[1] $target[2] $target[3]
        $writes = @($method.Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'stfld' -and ($_.Operand.DeclaringType.FullName -like 'PassiveModel*' -or
                $_.Operand.DeclaringType.FullName -eq 'BookModel/BookEquipedBookSavedData' -or
                $_.Operand.Name -in @('originData', 'reservedData', '_activatedAllPassives'))
        })
        $calls = @($method.Body.Instructions | Where-Object {
            $_.OpCode.Name -in @('call', 'callvirt') -and ($_.Operand.DeclaringType.FullName -like 'PassiveModel*' -or
                $_.Operand.Name -in @('ApplyPassiveSuccession', 'InitReservedData', 'ApplyReservedData'))
        })
        Assert-Equipment ($writes.Count -eq 0 -and $calls.Count -eq 0) "$($target[0]).$($target[1]) does not directly rewrite inheritance state or invoke passive commits"
    }
    $changeDeck = Get-EquipmentMethod 'BookModel' 'ChangeDeck' 'System.Void' @('System.Int32')
    Assert-Equipment ($changeDeck.Body.Instructions[1].Operand.Name -eq 'IsMultiDeck' -and
        $changeDeck.Body.Instructions[2].OpCode.Name -like 'brfalse*') 'ChangeDeck leaves ordinary pages on their current deck'

    $result = [pscustomobject]@{
        Success = $true; CheckCount = $equipmentChecks.Count; FieldCount = $equipmentFields.Count;
        MethodCount = $equipmentMethods.Count; Checks = $equipmentChecks.ToArray();
        Execution = 'Metadata and IL only; no game constructors, singletons, saves, or Unity methods invoked.'
    }
    if ($OutputPath) { $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "PASS: $($equipmentChecks.Count) actual game equipment transaction metadata/IL checks ($($equipmentFields.Count) fields, $($equipmentMethods.Count) method signatures)."
} finally { $equipmentAssembly.Dispose() }
