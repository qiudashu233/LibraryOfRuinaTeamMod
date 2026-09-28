param(
    [Parameter(Mandatory = $true)][string]$PacketPath,
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina'
)

$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameDir 'LibraryOfRuina_Data\Managed'
$native = Join-Path $GameDir 'LibraryOfRuina_Data\Plugins\x86_64'
$assembly = Join-Path $managed 'Facepunch.Steamworks.Win64.dll'
$packet = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PacketPath))
$env:PATH = $native + ';' + $env:PATH
Add-Type -Path $assembly

$receiverType = @'
using System;
using System.Runtime.InteropServices;
using Steamworks;

public sealed class LocalPacketReceiver : ConnectionManager
{
    public byte[] Packet;
    public override void OnMessage(IntPtr data, int size, long number, long time, int channel)
    {
        Packet = new byte[size];
        Marshal.Copy(data, Packet, 0, size);
    }
}
'@
Add-Type -TypeDefinition $receiverType -ReferencedAssemblies $assembly

function Assert-Received([LocalPacketReceiver]$receiver, [byte[]]$expected, [string]$direction) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($null -eq $receiver.Packet -and [DateTime]::UtcNow -lt $deadline) {
        [Steamworks.SteamClient]::RunCallbacks()
        $receiver.Receive(8)
        Start-Sleep -Milliseconds 10
    }
    if ($null -eq $receiver.Packet) { throw "$direction timed out" }
    if ($receiver.Packet.Length -ne $expected.Length) { throw "$direction changed length" }
    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($receiver.Packet[$index] -ne $expected[$index]) { throw "$direction changed byte $index" }
    }
}

[Steamworks.SteamClient]::Init(1256670, $false)
if (-not [Steamworks.SteamClient]::IsValid) { throw 'Steam initialization failed.' }
try {
    $property = [Steamworks.SteamNetworkingSockets].GetProperty('Internal',
        [Reflection.BindingFlags]'Static,NonPublic')
    $sockets = $property.GetValue($null, $null)
    $method = $sockets.GetType().GetMethod('CreateSocketPair',
        [Reflection.BindingFlags]'Instance,NonPublic')
    foreach ($network in @($false, $true)) {
        $first = [Steamworks.Data.Connection[]]::new(1)
        $second = [Steamworks.Data.Connection[]]::new(1)
        $arguments = [object[]]::new(5)
        $arguments[0] = $first
        $arguments[1] = $second
        $arguments[2] = $network
        $arguments[3] = [Steamworks.Data.NetIdentity]::new()
        $arguments[4] = [Steamworks.Data.NetIdentity]::new()
        if (-not $method.Invoke($sockets, $arguments)) {
            throw "CreateSocketPair failed (network=$network)."
        }
        try {
            $receiveFirst = [LocalPacketReceiver]::new()
            $receiveSecond = [LocalPacketReceiver]::new()
            $receiveFirst.Connection = $first[0]
            $receiveSecond.Connection = $second[0]
            $result = $first[0].SendMessage($packet, [Steamworks.Data.SendType]::Reliable)
            if ($result -ne [Steamworks.Result]::OK) { throw "First send failed: $result" }
            Assert-Received $receiveSecond $packet 'first to second'
            $result = $second[0].SendMessage($packet, [Steamworks.Data.SendType]::Reliable)
            if ($result -ne [Steamworks.Result]::OK) { throw "Reply send failed: $result" }
            Assert-Received $receiveFirst $packet 'second to first'
            Write-Output "PASS: socket pair network=$network, both directions, $($packet.Length) snapshot bytes."
        }
        finally {
            $first[0].Close($false, 0, 'Local test complete') | Out-Null
            $second[0].Close($false, 0, 'Local test complete') | Out-Null
        }
    }
}
finally {
    [Steamworks.SteamClient]::Shutdown()
}
