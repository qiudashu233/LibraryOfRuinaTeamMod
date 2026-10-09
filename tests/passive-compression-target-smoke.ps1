# Run with Windows PowerShell after building Release. Reflects the actual net46
# snapshot DTO/codec only; no patches, game models, singleton calls, or saves.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$compressionRequestedOutput = $OutputPath
# Reuse the C# assembly resolver, avoiding re-entrant PowerShell loader callbacks.
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
$compressionFlags = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
$compressionType = $GuardSmokeModAssembly.GetType('RuinaCoop.ProgressSnapshot', $true)
$compressionMirror = $GuardSmokeModAssembly.GetType('RuinaCoop.PassiveMirror', $true)
$compressionSnapshot = [Activator]::CreateInstance($compressionType, $true)
foreach ($name in @('CoreBooksAvailable', 'PassivesAvailable')) {
    $compressionType.GetField($name, $compressionFlags).SetValue($compressionSnapshot, $true)
}
foreach ($name in @('CoreBooksReason', 'PassivesReason')) {
    $field = $compressionType.GetField($name, $compressionFlags)
    $field.SetValue($compressionSnapshot, [Enum]::ToObject($field.FieldType, 0))
}
$compressionPacket = [byte[]]$compressionType.GetMethod('Encode', $compressionFlags).Invoke(
    $compressionSnapshot, [object[]]@([UInt64]987))
$compressionWire = [byte[]]$compressionMirror.GetMethod('EncodeWireData', $compressionFlags).Invoke(
    $null, [object[]]@($compressionSnapshot))
$compressionPreparationType = $GuardSmokeModAssembly.GetType('RuinaCoop.PreparationMirror', $true)
$compressionPreparation = $compressionType.GetField('Preparation', $compressionFlags).GetValue($compressionSnapshot)
$compressionPreparationStream = New-Object System.IO.MemoryStream
$compressionPreparationWriter = New-Object System.IO.BinaryWriter -ArgumentList $compressionPreparationStream
try {
    $compressionPreparationType.GetMethod('WriteData', $compressionFlags).Invoke(
        $null, [object[]]@($compressionPreparationWriter.PSObject.BaseObject, $compressionPreparation, $false)) | Out-Null
    $compressionPreparationWire = [byte[]]$compressionPreparationStream.ToArray()
}
finally { $compressionPreparationWriter.Dispose() }
$compressionDecode = @($compressionType.GetMethods($compressionFlags) | Where-Object {
    $_.Name -eq 'TryDecode' -and $_.GetParameters().Count -eq 4
})[0]
$compressionChecks = New-Object 'System.Collections.Generic.List[object]'
function Test-CompressionPacket([byte[]]$Packet, [bool]$Expected, [string]$Name) {
    $arguments = [object[]]@($Packet, [UInt64]987, $null, $null)
    $accepted = [bool]$compressionDecode.Invoke($null, $arguments)
    if ($accepted -ne $Expected) { throw "FAIL Framework codec: $Name; reason: $($arguments[3])" }
    $compressionChecks.Add([pscustomobject]@{
        Name = $Name; Accepted = $accepted; Expected = $Expected; Reason = $arguments[3]
    })
}
function Set-CompressionInt32([byte[]]$Packet, [int]$Offset, [int]$Value) {
    [Buffer]::BlockCopy([BitConverter]::GetBytes($Value), 0, $Packet, $Offset, 4)
}
if ($compressionPacket[4] -ne 8) { throw 'Build the wire8 snapshot mod before running this smoke.' }
$compressionStart = $compressionPacket.Length - $compressionWire.Length - $compressionPreparationWire.Length
$compressionRawLength = [BitConverter]::ToInt32($compressionPacket, $compressionStart + 4)
$compressionPackedLength = [BitConverter]::ToInt32($compressionPacket, $compressionStart + 8)
if ($compressionRawLength -ne 2 -or $compressionPackedLength -ne $compressionWire.Length - 12 -or
    $compressionPacket[$compressionStart + 2] -ne 1) { throw 'Unexpected empty passive pool compression schema.' }
function New-CompressionPacket([byte[]]$Packed) {
    # Replace only the compressed data. Keep the complete original preparation
    # suffix, so malformed streams cannot pass a rejection check just because
    # the new snapshot section was accidentally omitted.
    $rebuilt = New-Object byte[] ($compressionStart + 12 + $Packed.Length + $compressionPreparationWire.Length)
    [Buffer]::BlockCopy($compressionPacket, 0, $rebuilt, 0, $compressionStart + 12)
    if ($Packed.Length -gt 0) { [Buffer]::BlockCopy($Packed, 0, $rebuilt, $compressionStart + 12, $Packed.Length) }
    [Buffer]::BlockCopy($compressionPacket, $compressionStart + $compressionWire.Length,
        $rebuilt, $compressionStart + 12 + $Packed.Length, $compressionPreparationWire.Length)
    Set-CompressionInt32 $rebuilt ($compressionStart + 8) $Packed.Length
    return ,$rebuilt
}
$compressionPacked = New-Object byte[] $compressionPackedLength
[Buffer]::BlockCopy($compressionPacket, $compressionStart + 12, $compressionPacked, 0, $compressionPackedLength)
Test-CompressionPacket $compressionPacket $true 'Complete compressed passive pool decodes'
Test-CompressionPacket (New-CompressionPacket $compressionPacked) $true 'Rebuilt normal pool with complete preparation suffix decodes'
for ($length = 0; $length -lt $compressionPackedLength; $length++) {
    # Update the advertised compressed length too: only checking total packet
    # length would miss a silently accepted Deflate stream without its EOS.
    $packedPrefix = New-Object byte[] $length
    if ($length -gt 0) { [Buffer]::BlockCopy($compressionPacked, 0, $packedPrefix, 0, $length) }
    $bad = New-CompressionPacket $packedPrefix
    Test-CompressionPacket $bad $false "Deflate prefix/EOS truncation with updated packed length $length"
}
$packedTail = New-Object byte[] ($compressionPackedLength + 1)
[Buffer]::BlockCopy($compressionPacked, 0, $packedTail, 0, $compressionPackedLength)
$bad = New-CompressionPacket $packedTail
Test-CompressionPacket $bad $false 'Compressed trailing byte is rejected'
$packedConcatenated = New-Object byte[] ($compressionPackedLength * 2)
[Buffer]::BlockCopy($compressionPacked, 0, $packedConcatenated, 0, $compressionPackedLength)
[Buffer]::BlockCopy($compressionPacked, 0, $packedConcatenated, $compressionPackedLength, $compressionPackedLength)
$bad = New-CompressionPacket $packedConcatenated
Test-CompressionPacket $bad $false 'Concatenated Deflate stream is rejected'
foreach ($length in @(1, 3, 4194305)) {
    $bad = [byte[]]$compressionPacket.Clone()
    Set-CompressionInt32 $bad ($compressionStart + 4) $length
    Test-CompressionPacket $bad $false "Expanded length bound/short stream $length"
}
$bad = [byte[]]$compressionPacket.Clone(); $bad[$compressionStart + 12] = 7
Test-CompressionPacket $bad $false 'Illegal Deflate block type is rejected'
$bad = [byte[]]$compressionPacket.Clone(); $bad[$compressionStart + 2] = 2
Test-CompressionPacket $bad $false 'Unknown compression codec is rejected'
$bad = [byte[]]$compressionPacket.Clone(); $bad[$compressionStart + 3] = 1
Test-CompressionPacket $bad $false 'Nonzero reserved compression header is rejected'
$compressionReport = [pscustomobject]@{
    Passed = $true; CheckCount = $compressionChecks.Count; Runtime = [Environment]::Version.ToString()
    GameAssembly = $GuardSmokeGameAssembly.Location; ModAssembly = $GuardSmokeModAssembly.Location
    ModSha256 = (Get-FileHash -LiteralPath $GuardSmokeModAssembly.Location -Algorithm SHA256).Hash
    SnapshotWire = $compressionPacket[4]; PacketBytes = $compressionPacket.Length
    RawBytes = $compressionRawLength; PackedBytes = $compressionPackedLength; PreparationBytes = $compressionPreparationWire.Length
    Checks = $compressionChecks.ToArray()
}
if ($compressionRequestedOutput) {
    $compressionReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $compressionRequestedOutput -Encoding UTF8
}
Write-Output "PASS: $($compressionChecks.Count) actual net46 passive compression codec checks; CLR $([Environment]::Version); normal packet $($compressionPacket.Length) bytes."
