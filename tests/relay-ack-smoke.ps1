param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath,
    [switch]$CorePages
)

$ErrorActionPreference = 'Stop'
$ackRequestedOutputPath = $OutputPath
# Reuse dependency resolution only; do not install or execute any game patch targets.
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$ackFlags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$ackSessionType = $GuardSmokeModAssembly.GetType('RuinaCoop.RelaySession', $true)
$ackSnapshotType = $GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot', $true)
$ackReplyName = if ($CorePages) { 'CorePageReply' } else { 'DeckReply' }
$ackResultName = if ($CorePages) { 'CorePageResultCode' } else { 'DeckResultCode' }
$ackMethodName = if ($CorePages) { 'CompleteCorePageReply' } else { 'CompleteDeckReply' }
$ackReplyType = $GuardSmokeModAssembly.GetType(('RuinaCoop.' + $ackReplyName), $true)
$ackResultType = $GuardSmokeModAssembly.GetType(('RuinaCoop.' + $ackResultName), $true)
$ackComplete = $ackSessionType.GetMethod($ackMethodName, $ackFlags)
$ackPendingProperty = $ackSessionType.GetProperty('DeckRequestPending', $ackFlags)
$ackChecks = New-Object 'System.Collections.Generic.List[string]'
function Set-AckField([object]$Object, [string]$Name, [object]$Value) {
    if ($CorePages) { $Name = $Name.Replace('_pendingDeckRequest', '_pendingCorePageRequest').Replace('_lastDeckReply', '_lastCorePageReply').Replace('_deferredDeckReply', '_deferredCorePageReply') }
    $field = $Object.GetType().GetField($Name, $ackFlags)
    if ($null -eq $field) { throw "Missing compiled ACK state: $Name" }
    $field.SetValue($Object, $Value)
}
function Get-AckField([object]$Object, [string]$Name) {
    if ($CorePages) { $Name = $Name.Replace('_pendingDeckRequest', '_pendingCorePageRequest').Replace('_lastDeckReply', '_lastCorePageReply').Replace('_deferredDeckReply', '_deferredCorePageReply') }
    return $Object.GetType().GetField($Name, $ackFlags).GetValue($Object)
}
function New-AckSnapshot([uint32]$Revision) {
    # Snapshot construction only allocates DTO lists, with no game/save model access.
    $snapshot = [Activator]::CreateInstance($ackSnapshotType, $true)
    Set-AckField $snapshot 'DeckRevision' $Revision
    return $snapshot
}
function New-AckSession {
    # Deliberately bypass RelaySession's constructor; no Steam sockets or singleton calls.
    $session = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($ackSessionType)
    Set-AckField $session '_isHost' $false
    Set-AckField $session '_guestAuthenticated' $true
    Set-AckField $session '_pendingDeckRequest' ([uint32]7)
    Set-AckField $session '_lastDeckReply' ([uint32]4)
    Set-AckField $session '<LatestSnapshot>k__BackingField' (New-AckSnapshot 11)
    return $session
}
function New-AckReply([uint32]$RequestId, [uint32]$Revision) {
    # Build the value type inside C# so PowerShell argument boxing cannot copy it
    # before its internal fields are assigned.
    return [RuinaGuardSmokeLoader]::CreateFields($ackReplyType,
        [string[]]@('RequestId', 'DeckRevision', 'Result'),
        [object[]]@($RequestId, $Revision, [Enum]::Parse($ackResultType, 'Accepted')))
}
function Assert-Ack([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $ackChecks.Add($Name)
}
function Invoke-Ack([object]$Session, [object]$Reply) {
    $ackComplete.Invoke($Session, [object[]]@($Reply)) | Out-Null
}

$session = New-AckSession
$reply = New-AckReply 7 12
Invoke-Ack $session $reply
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 7 -and
    $ackPendingProperty.GetValue($session, $null)) 'Accepted reply ahead of snapshot keeps editing pending'
Assert-Ack ($null -ne (Get-AckField $session '_deferredDeckReply') -and
    (Get-AckField $session '_lastDeckReply') -eq 4) 'Ahead reply is deferred without advancing last reply'
Set-AckField $session '<LatestSnapshot>k__BackingField' (New-AckSnapshot 12)
Invoke-Ack $session (Get-AckField $session '_deferredDeckReply')
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 0 -and
    -not $ackPendingProperty.GetValue($session, $null)) 'Matching authoritative snapshot permits pending request completion'
Assert-Ack ($null -eq (Get-AckField $session '_deferredDeckReply') -and
    (Get-AckField $session '_lastDeckReply') -eq 7) 'Completed request clears deferred reply and records its ID'
$completedStatus = $ackSessionType.GetProperty('DeckStatus', $ackFlags).GetValue($session, $null)
Invoke-Ack $session $reply
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 0 -and
    $ackSessionType.GetProperty('DeckStatus', $ackFlags).GetValue($session, $null) -eq $completedStatus) 'Duplicate completed reply leaves state unchanged'

$session = New-AckSession
Set-AckField $session '<LatestSnapshot>k__BackingField' (New-AckSnapshot 13)
Invoke-Ack $session (New-AckReply 7 12)
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 0) 'A newer authoritative snapshot also satisfies the accepted reply'
$session = New-AckSession
Set-AckField $session '<LatestSnapshot>k__BackingField' $null
Invoke-Ack $session (New-AckReply 7 0)
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 7 -and
    $null -ne (Get-AckField $session '_deferredDeckReply')) 'A reply without any host snapshot stays pending'
foreach ($requestId in @([uint32]4, [uint32]6, [uint32]8)) {
    $session = New-AckSession
    Invoke-Ack $session (New-AckReply $requestId 11)
    Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 7 -and
        $null -eq (Get-AckField $session '_deferredDeckReply')) "Unknown or old request ID $requestId cannot unlock editing"
}
foreach ($case in @(
    @('_stopped', $true, 'Stopped session'),
    @('_isHost', $true, 'Host session'),
    @('_guestAuthenticated', $false, 'Unverified guest')
)) {
    $session = New-AckSession
    Set-AckField $session $case[0] $case[1]
    Invoke-Ack $session (New-AckReply 7 11)
    Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 7 -and
        $null -eq (Get-AckField $session '_deferredDeckReply')) ($case[2] + ' reply cannot unlock editing')
}
$session = New-AckSession
Set-AckField $session '_lastDeckReply' ([uint32]7)
Invoke-Ack $session (New-AckReply 7 11)
Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 7) 'Already recorded request ID cannot complete another pending request'
if ($CorePages) {
    $session = New-AckSession
    $validate = $ackSessionType.GetMethod('ValidateDisplayedDeckRequest', $ackFlags)
    Assert-Ack (-not $validate.Invoke($session, [object[]]@((Get-AckField $session '<LatestSnapshot>k__BackingField')))) 'Core page pending state also blocks card or another equipment submission'
    $ackSessionType.GetField('_pendingDeckRequest', $ackFlags).SetValue($session, [uint32]9)
    Invoke-Ack $session (New-AckReply 7 11)
    Assert-Ack ((Get-AckField $session '_pendingDeckRequest') -eq 0 -and $ackPendingProperty.GetValue($session, $null)) 'Core page completion cannot accidentally clear another card pending state'
    $requestType = $GuardSmokeModAssembly.GetType('RuinaCoop.CorePageRequest', $true)
    $match = $ackSessionType.GetMethod('CorePageRequestsMatch', $ackFlags)
    $requestNames = [string[]]@('RequestId', 'StageId', 'FloorId', 'UnitIndex', 'UnitIdentity', 'OldBookToken', 'TargetBookToken', 'ClaimRevision', 'DeckRevision')
    $requestValues = [object[]]@([uint32]7, [int]101, [byte]1, [byte]0, [uint64]400, [uint64]500, [uint64]600, [uint32]11, [uint32]12)
    $originalRequest = [RuinaGuardSmokeLoader]::CreateFields($requestType, $requestNames, $requestValues)
    $copyRequest = [RuinaGuardSmokeLoader]::CreateFields($requestType, $requestNames, $requestValues)
    Assert-Ack ($match.Invoke($null, [object[]]@($originalRequest, $copyRequest))) 'An exact request retry matches the cached response without another mutation'
    for ($index = 0; $index -lt $requestNames.Length; $index++) {
        $differentValues = [object[]]$requestValues.Clone()
        $type = $requestValues[$index].GetType()
        $differentValues[$index] = [Convert]::ChangeType(([uint64]$requestValues[$index] + 1), $type)
        $differentRequest = [RuinaGuardSmokeLoader]::CreateFields($requestType, $requestNames, $differentValues)
        Assert-Ack (-not $match.Invoke($null, [object[]]@($originalRequest, $differentRequest))) ("Changed " + $requestNames[$index] + ' cannot reuse a previously accepted result')
    }
}
$report = [pscustomobject]@{
    Passed = $true; ModAssembly = $GuardSmokeModAssembly.Location
    ModSha256 = (Get-FileHash -LiteralPath $GuardSmokeModAssembly.Location -Algorithm SHA256).Hash
    TestedMethod = $ackComplete.ToString(); CheckCount = $ackChecks.Count; Checks = $ackChecks.ToArray()
}
if ($ackRequestedOutputPath) { $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ackRequestedOutputPath -Encoding UTF8 }
Write-Output "PASS: $($ackChecks.Count) checks against compiled RelaySession.$ackMethodName; no Steam, Unity native, or game save model operations."
