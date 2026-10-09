# Pure compiled callback isolation and metadata checks. No Steam initialization,
# socket, Unity API, original game model constructor, inventory or save access.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$recoveryOutput=$OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
Add-Type -Path $CecilPath
$recoveryCecil=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($guardModPath)
$recoveryChecks=New-Object 'System.Collections.Generic.List[string]'
function Assert-Recovery([bool]$Value,[string]$Name){if(!$Value){throw "Relay recovery check failed: $Name"};$recoveryChecks.Add($Name)}
function Recovery-Calls($Method,[string]$Name){return ,@($Method.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt')-and$_.Operand.Name-eq$Name})}
try {
    $relay=$recoveryCecil.MainModule.Types|Where-Object FullName -eq 'RuinaCoop.RelaySession'
    $post=$relay.Methods|Where-Object Name -eq PostGuest
    Assert-Recovery ($null-ne$post) 'Compiled callback source gate exists'
    Assert-Recovery ((Recovery-Calls $post 'IsCurrentGuestSource').Count-eq1) 'Source gate checks connection before enqueue'
    $queuedChecks=@(foreach($type in $relay.NestedTypes){foreach($method in $type.Methods|Where-Object HasBody){if((Recovery-Calls $method 'IsCurrentGuestSource').Count-eq1){$method}}})
    Assert-Recovery ($queuedChecks.Count-eq1) 'Queued callback rechecks connection when executed'
    foreach($name in @('ReceiveChallenge','ReceiveAccepted','HostConnected','HostDisconnected','ReceiveHostMessage','ReceiveClaimReply','ReceiveDeckReply','ReceivePreparationReadyReply','ReceiveCorePageReply','ReceivePassiveReply')){
        $method=$relay.Methods|Where-Object Name -eq $name
        Assert-Recovery ($method.Parameters[0].ParameterType.FullName-eq'RuinaCoop.RelaySession/GuestRelayConnection'-and(Recovery-Calls $method 'PostGuest').Count-eq1) "Late guest callback uses current source gate: $name"
    }
    $tick=$relay.Methods|Where-Object Name -eq Tick
    Assert-Recovery ((Recovery-Calls $tick 'Expire').Count-eq1-and(Recovery-Calls $tick 'TryBeginRetry').Count-eq1) 'Tick enforces initial verification timeout and retries'
    $recoveryType=$recoveryCecil.MainModule.Types|Where-Object FullName -eq 'RuinaCoop.GuestRelayRecovery'
    $retry=$recoveryType.Methods|Where-Object Name -eq TryBeginRetry
    Assert-Recovery ($retry.Parameters.Count-eq2-and$retry.Parameters[1].ParameterType.FullName-eq'System.Boolean') 'Retry state takes explicit relay readiness without calling Steam'
    Assert-Recovery ((Recovery-Calls $tick 'get_Status').Count-ge1) 'Guest retry checks actual Steam relay availability'
    $stop=$relay.Methods|Where-Object Name -eq Stop
    Assert-Recovery ((Recovery-Calls $stop 'Stop').Count-ge1) 'Session stop cancels retry state'
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
public static class RuinaRelayRecoveryCallbackChecks
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static string[] Run(Assembly assembly)
    {
        Type relay = assembly.GetType("RuinaCoop.RelaySession", true), recoveryType = assembly.GetType("RuinaCoop.GuestRelayRecovery", true);
        Type manager = relay.GetNestedType("GuestRelayConnection", Flags);
        object session = FormatterServices.GetUninitializedObject(relay), source = FormatterServices.GetUninitializedObject(manager);
        object recovery = Activator.CreateInstance(recoveryType, true);
        recoveryType.GetMethod("Begin", Flags).Invoke(recovery, new object[] { 0f });
        Queue<Action> queue = new Queue<Action>();
        relay.GetField("_onMainThread", Flags).SetValue(session, (Action<Action>)queue.Enqueue);
        relay.GetField("_guestRecovery", Flags).SetValue(session, recovery);
        relay.GetField("_guestConnection", Flags).SetValue(session, source);
        manager.GetField("Generation", Flags).SetValue(source, (uint)1);
        MethodInfo post = relay.GetMethod("PostGuest", Flags);
        List<string> checks = new List<string>(); int calls = 0; Action action = () => calls++;
        post.Invoke(session, new object[] { source, action });
        Check(queue.Count == 1 && calls == 0, "Current connection callback waits for main-thread dispatch", checks);
        queue.Dequeue()(); Check(calls == 1, "Current queued callback executes once", checks);
        post.Invoke(session, new object[] { source, action });
        recoveryType.GetMethod("ScheduleFailure", Flags).Invoke(recovery, new object[] { 0f, true });
        queue.Dequeue()(); Check(calls == 1, "Failure invalidates an already queued callback before reconnect", checks);
        post.Invoke(session, new object[] { source, action });
        Check(queue.Count == 0, "Old generation cannot enqueue new callbacks", checks);
        object replacement = FormatterServices.GetUninitializedObject(manager);
        manager.GetField("Generation", Flags).SetValue(replacement, (uint)2);
        relay.GetField("_guestConnection", Flags).SetValue(session, replacement);
        post.Invoke(session, new object[] { source, action });
        Check(queue.Count == 0, "Old manager cannot enqueue after replacement", checks);
        post.Invoke(session, new object[] { replacement, action }); queue.Dequeue()();
        Check(calls == 2, "New generation callback executes", checks);
        post.Invoke(session, new object[] { replacement, action }); relay.GetField("_stopped", Flags).SetValue(session, true);
        queue.Dequeue()(); Check(calls == 2, "Leaving invalidates an already queued callback", checks);
        post.Invoke(session, new object[] { replacement, action });
        Check(queue.Count == 0, "Stopped session cannot enqueue callbacks", checks);
        relay.GetField("_stopped", Flags).SetValue(session, false);
        post.Invoke(session, new object[] { replacement, action }); recoveryType.GetMethod("Stop", Flags).Invoke(recovery, null);
        queue.Dequeue()(); Check(calls == 2, "Recovery stop revokes callbacks while manager reference remains", checks);
        MethodInfo temporary = relay.GetMethod("TemporaryInitialFailure", Flags); Type reason = temporary.GetParameters()[0].ParameterType;
        foreach(string name in new string[] { "Remote_BadCert", "Remote_Timeout", "Misc_Timeout", "Misc_RelayConnectivity", "Misc_SteamConnectivity", "Misc_NoRelaySessionsToClient", "Local_NetworkConfig" })
            Check((bool)temporary.Invoke(null, new object[] { Enum.Parse(reason, name) }), "Temporary first-connection failure can retry: " + name, checks);
        foreach(string name in new string[] { "Remote_BadCrypt", "Remote_BadProtocolVersion", "App_Generic", "Local_Rights" })
            Check(!(bool)temporary.Invoke(null, new object[] { Enum.Parse(reason, name) }), "Permanent or closed connection is not auto-retried: " + name, checks);
        return checks.ToArray();
    }
    private static void Check(bool valid, string name, List<string> checks)
    { if(!valid) throw new InvalidOperationException(name); checks.Add(name); }
}
'@
    foreach($check in [RuinaRelayRecoveryCallbackChecks]::Run($GuardSmokeModAssembly)){Assert-Recovery $true $check}
    $result=[ordered]@{Success=$true;CheckCount=$recoveryChecks.Count;Execution='Compiled pure callback source/generation and failure classification plus Cecil IL; no Steam/Unity/game/save execution.';ModAssemblySha256=(Get-FileHash -LiteralPath $guardModPath -Algorithm SHA256).Hash;Checks=$recoveryChecks.ToArray()}
    if($recoveryOutput){$result|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $recoveryOutput -Encoding UTF8}
    "PASS: $($recoveryChecks.Count) relay recovery/source isolation checks."
} finally {$recoveryCecil.Dispose()}
