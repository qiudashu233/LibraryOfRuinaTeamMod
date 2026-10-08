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
if ($compressionPacket[4] -ne 7) { throw 'Build the wire7 snapshot mod before running this smoke.' }
$compressionStart = $compressionPacket.Length - $compressionWire.Length
$compressionRawLength = [BitConverter]::ToInt32($compressionPacket, $compressionStart + 4)
$compressionPackedLength = [BitConverter]::ToInt32($compressionPacket, $compressionStart + 8)
if ($compressionRawLength -ne 2 -or $compressionPackedLength -ne $compressionWire.Length - 12 -or
    $compressionPacket[$compressionStart + 2] -ne 1) { throw 'Unexpected empty passive pool compression schema.' }
Test-CompressionPacket $compressionPacket $true 'Complete compressed passive pool decodes'
for ($length = 0; $length -lt $compressionPackedLength; $length++) {
    # Update the advertised compressed length too: only checking total packet
    # length would miss a silently accepted Deflate stream without its EOS.
    $bad = New-Object byte[] ($compressionStart + 12 + $length)
    [Buffer]::BlockCopy($compressionPacket, 0, $bad, 0, $bad.Length)
    Set-CompressionInt32 $bad ($compressionStart + 8) $length
    Test-CompressionPacket $bad $false "Deflate prefix/EOS truncation with updated packed length $length"
}
$bad = New-Object byte[] ($compressionPacket.Length + 1)
[Buffer]::BlockCopy($compressionPacket, 0, $bad, 0, $compressionPacket.Length)
Set-CompressionInt32 $bad ($compressionStart + 8) ($compressionPackedLength + 1)
Test-CompressionPacket $bad $false 'Compressed trailing byte is rejected'
$bad = New-Object byte[] ($compressionPacket.Length + $compressionPackedLength)
[Buffer]::BlockCopy($compressionPacket, 0, $bad, 0, $compressionPacket.Length)
[Buffer]::BlockCopy($compressionPacket, $compressionStart + 12, $bad, $compressionPacket.Length, $compressionPackedLength)
Set-CompressionInt32 $bad ($compressionStart + 8) ($compressionPackedLength * 2)
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
    RawBytes = $compressionRawLength; PackedBytes = $compressionPackedLength
    Checks = $compressionChecks.ToArray()
}
if ($compressionRequestedOutput) {
    $compressionReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $compressionRequestedOutput -Encoding UTF8
}
Write-Output "PASS: $($compressionChecks.Count) actual net46 passive compression codec checks; CLR $([Environment]::Version); normal packet $($compressionPacket.Length) bytes."
