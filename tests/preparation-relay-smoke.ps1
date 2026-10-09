param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$preparationRequestedOutput = $OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$preparationFlags = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$preparationSessionType = $GuardSmokeModAssembly.GetType('RuinaCoop.RelaySession', $true)
$preparationSnapshotType = $GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot', $true)
$preparationControllerType = $GuardSmokeModAssembly.GetType('RuinaCoop.PreparationControllerEntry', $true)
$preparationReplyType = $GuardSmokeModAssembly.GetType('RuinaCoop.PreparationReadyReply', $true)
$preparationResultType = $GuardSmokeModAssembly.GetType('RuinaCoop.PreparationReadyResultCode', $true)
$preparationComplete = $preparationSessionType.GetMethod('CompletePreparationReadyReply', $preparationFlags)
$preparationPendingProperty = $preparationSessionType.GetProperty('PreparationReadyPending', $preparationFlags)
$preparationLocalIdField = $preparationSessionType.GetField('_localPlayerId', $preparationFlags)
$preparationChecks = New-Object 'System.Collections.Generic.List[string]'
$preparationSkipped = New-Object 'System.Collections.Generic.List[string]'
function Set-PreparationField([object]$Value, [string]$Name, [object]$FieldValue) {
    $field = $Value.GetType().GetField($Name, $preparationFlags)
    if ($null -eq $field) { throw "Missing compiled preparation ACK field $Name" }
    $field.SetValue($Value, $FieldValue)
}
function Get-PreparationField([object]$Value, [string]$Name) {
    return ,($Value.GetType().GetField($Name, $preparationFlags).GetValue($Value))
}
function New-PreparationSnapshot([uint32]$Revision, [bool]$Ready = $false, [bool]$IncludeController = $true) {
    $snapshot = [Activator]::CreateInstance($preparationSnapshotType, $true)
    $preparation = Get-PreparationField $snapshot 'Preparation'
    Set-PreparationField $preparation 'Revision' $Revision
    if ($IncludeController) {
        $controller = [Activator]::CreateInstance($preparationControllerType, $true)
        Set-PreparationField $controller 'PlayerId' ([uint64]123)
        Set-PreparationField $controller 'Connected' $true
        Set-PreparationField $controller 'Ready' $Ready
        (Get-PreparationField $preparation 'Controllers').Add($controller)
    }
    return $snapshot
}
function New-PreparationSession {
    # No RelaySession constructor, Steam connection, game singleton, or save access.
    $session = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($preparationSessionType)
    Set-PreparationField $session '_isHost' $false
    Set-PreparationField $session '_guestAuthenticated' $true
    Set-PreparationField $session '_pendingReadyRequest' ([uint32]7)
    Set-PreparationField $session '_pendingReadyValue' $true
    Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 11)
    if ($null -ne $preparationLocalIdField) { $preparationLocalIdField.SetValue($session, [uint64]123) }
    return $session
}
function New-PreparationReply([uint32]$RequestId, [uint32]$Revision, [string]$Result = 'Accepted') {
    return [RuinaGuardSmokeLoader]::CreateFields($preparationReplyType,
        [string[]]@('RequestId', 'Revision', 'Result'),
        [object[]]@($RequestId, $Revision, [Enum]::Parse($preparationResultType, $Result)))
}
function Invoke-PreparationReply([object]$Session, [object]$Reply) {
    $preparationComplete.Invoke($Session, [object[]]@($Reply)) | Out-Null
}
function Assert-Preparation([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL preparation ACK: $Name" }
    $preparationChecks.Add($Name)
}
$session = New-PreparationSession
$reply = New-PreparationReply 7 12
Invoke-PreparationReply $session $reply
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7 -and $preparationPendingProperty.GetValue($session, $null)) 'Accepted reply ahead of snapshot preserves pending ready'
Assert-Preparation ($null -ne (Get-PreparationField $session '_deferredReadyReply')) 'Ahead accepted reply is deferred'
Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 13)
Invoke-PreparationReply $session (Get-PreparationField $session '_deferredReadyReply')
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0 -and -not $preparationPendingProperty.GetValue($session, $null)) 'Advanced preparation revision completes obsolete accepted ready request'
Assert-Preparation ($null -eq (Get-PreparationField $session '_deferredReadyReply')) 'Completion clears deferred ready reply'
$status = $preparationSessionType.GetProperty('PreparationStatus', $preparationFlags).GetValue($session, $null)
Invoke-PreparationReply $session $reply
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0 -and $preparationSessionType.GetProperty('PreparationStatus', $preparationFlags).GetValue($session, $null) -eq $status) 'Duplicate completed ACK does not change ready status'
$session = New-PreparationSession
Set-PreparationField $session '<LatestSnapshot>k__BackingField' $null
Invoke-PreparationReply $session $reply
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7 -and $null -ne (Get-PreparationField $session '_deferredReadyReply')) 'ACK without a snapshot remains pending'
foreach ($requestId in @([uint32]4, [uint32]6, [uint32]8)) {
    $session = New-PreparationSession
    Invoke-PreparationReply $session (New-PreparationReply $requestId 11)
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7 -and $null -eq (Get-PreparationField $session '_deferredReadyReply')) "Unknown request ID $requestId cannot complete ready request"
}
foreach ($case in @(@('_stopped', $true, 'Stopped session'), @('_isHost', $true, 'Host session'), @('_guestAuthenticated', $false, 'Unverified guest'))) {
    $session = New-PreparationSession
    Set-PreparationField $session $case[0] $case[1]
    Invoke-PreparationReply $session (New-PreparationReply 7 11)
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7 -and $null -eq (Get-PreparationField $session '_deferredReadyReply')) ($case[2] + ' ignores ready ACK')
}
$session = New-PreparationSession
Invoke-PreparationReply $session (New-PreparationReply 7 11 'Failed')
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0) 'Rejected current-revision ready request completes without controller readiness'
$session = New-PreparationSession
Invoke-PreparationReply $session (New-PreparationReply 7 12 'Failed')
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7) 'Rejected ACK ahead of snapshot also waits for its revision'
Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 12)
Invoke-PreparationReply $session (Get-PreparationField $session '_deferredReadyReply')
Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0) 'Matching snapshot completes deferred rejected ACK'
if ($null -ne $preparationLocalIdField) {
    $session = New-PreparationSession
    Invoke-PreparationReply $session (New-PreparationReply 7 11)
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7 -and $null -ne (Get-PreparationField $session '_deferredReadyReply')) 'Same-revision snapshot with old ready value cannot complete accepted ACK'
    Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 11 $true)
    Invoke-PreparationReply $session (Get-PreparationField $session '_deferredReadyReply')
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0 -and $null -eq (Get-PreparationField $session '_deferredReadyReply')) 'Same-revision authoritative ready value completes accepted ACK'
    $session = New-PreparationSession
    Set-PreparationField $session '_pendingReadyValue' $false
    Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 11 $true)
    Invoke-PreparationReply $session (New-PreparationReply 7 11)
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7) 'Pending unready waits while authoritative controller is still ready'
    Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 11 $false)
    Invoke-PreparationReply $session (Get-PreparationField $session '_deferredReadyReply')
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 0) 'Authoritative unready value completes unready ACK'
    $session = New-PreparationSession
    Set-PreparationField $session '<LatestSnapshot>k__BackingField' (New-PreparationSnapshot 11 $false $false)
    Invoke-PreparationReply $session (New-PreparationReply 7 11)
    Assert-Preparation ((Get-PreparationField $session '_pendingReadyRequest') -eq 7) 'Missing controller cannot satisfy same-revision accepted ACK'
}
else {
    # The actual Facepunch SteamClient.SteamId getter calls ISteamUser.GetSteamID.
    # Do not invoke that native API from an external Framework smoke process.
    $preparationSkipped.Add('Same-revision accepted ACK: compiled session has no cached local identity, and SteamClient.SteamId is a native API call.')
}
$report = [pscustomobject]@{
    Passed = $true; CheckCount = $preparationChecks.Count; Runtime = [Environment]::Version.ToString()
    ModAssembly = $GuardSmokeModAssembly.Location
    ModSha256 = (Get-FileHash -LiteralPath $GuardSmokeModAssembly.Location -Algorithm SHA256).Hash
    TestedMethod = $preparationComplete.ToString(); CachedLocalIdentity = ($null -ne $preparationLocalIdField)
    Checks = $preparationChecks.ToArray(); Skipped = $preparationSkipped.ToArray()
}
if ($preparationRequestedOutput) { $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $preparationRequestedOutput -Encoding UTF8 }
Write-Output "PASS: $($preparationChecks.Count) compiled preparation ACK checks; CLR $([Environment]::Version); skipped $($preparationSkipped.Count) native-dependent group(s)."
