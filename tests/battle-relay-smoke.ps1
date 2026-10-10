# Compiled Relay integration structure plus pure callbacks and host barrier.
# Does not call Steam, Unity, native battle methods, inventory or saves.
param(
    [string]$GameDir = 'D:\game\steamapps\common\Library Of Ruina',
    [string]$ModAssembly = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\RuinaCoop.dll'),
    [string]$CecilPath = (Join-Path $PSScriptRoot '..\src\RuinaCoop\bin\Release\net46\Mono.Cecil.dll'),
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$battleOutput=$OutputPath
. (Join-Path $PSScriptRoot 'guard-target-smoke.ps1') -GameDir $GameDir -ModAssembly $ModAssembly -LoadOnly
Add-Type -Path $CecilPath
$battleAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($guardModPath)
$battleChecks=New-Object 'System.Collections.Generic.List[string]'
function Assert-BattleRelay([bool]$Value,[string]$Name){if(!$Value){throw "Battle relay check failed: $Name"};$battleChecks.Add($Name)}
function Battle-Calls($Method,[string]$Name){return ,@($Method.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt')-and$_.Operand.Name-eq$Name})}
try {
    $relay=$battleAssembly.MainModule.Types|Where-Object FullName -eq 'RuinaCoop.RelaySession'
    $capture=$relay.Methods|Where-Object Name -eq CaptureAndBroadcast
    $instructions=@($capture.Body.Instructions)
    $finalFlag=@($instructions|Where-Object {$_.OpCode.Name-eq'ldfld'-and$_.Operand.Name-eq'_validatingBattleConfiguration'})
    $oldFreeze=@($instructions|Where-Object {$_.OpCode.Name-eq'ldfld'-and$_.Operand.Name-eq'DecksFrozen'})
    $freezeStore=@($instructions|Where-Object {$_.OpCode.Name-eq'stfld'-and$_.Operand.Name-eq'DecksFrozen'})
    Assert-BattleRelay ($finalFlag.Count-ge1-and$oldFreeze.Count-eq1-and$freezeStore.Count-eq1) 'Final capture has distinct validation-mode, published freeze metadata and freeze write'
    $metadataFlag=@($finalFlag|Where-Object Offset -lt $oldFreeze[0].Offset|Select-Object -Last 1)
    Assert-BattleRelay ($metadataFlag.Count-eq1-and$metadataFlag[0].Offset-lt$oldFreeze[0].Offset-and$oldFreeze[0].Offset-lt$freezeStore[0].Offset) 'Final capture reads published freeze metadata before canonical deck revision comparison'
    Assert-BattleRelay ((Battle-Calls $capture 'get_PreparationFrozen').Count-eq1-and(Battle-Calls $capture 'SameDeckData').Count-eq1) 'Normal snapshots still publish real edit locks and compare production equipment canonical'
    $validator=$relay.Methods|Where-Object Name -eq ValidateFinalBattleConfiguration
    foreach($call in @('CaptureAndBroadcast','get_AllControllersReady','FromPreparation','Encode','DigestEquals')){
        Assert-BattleRelay ((Battle-Calls $validator $call).Count-ge1) "Final commit validates live dependencies and manifest digest: $call"
    }
    Assert-BattleRelay (@($validator.Body.ExceptionHandlers|Where-Object HandlerType -eq Finally).Count-eq1) 'Final validation always exits recapture mode through finally'
    $receive=$relay.Methods|Where-Object Name -eq ReceiveBattleMessage
    Assert-BattleRelay ($receive.Parameters[0].ParameterType.FullName-eq'RuinaCoop.RelaySession/GuestRelayConnection'-and(Battle-Calls $receive 'PostGuest').Count-eq1) 'Battle message callback uses authenticated current source gate'
    $gate=$relay.Methods|Where-Object Name -eq PostGuest
    Assert-BattleRelay ((Battle-Calls $gate 'IsCurrentGuestSource').Count-eq1) 'Battle source gate checks generation before enqueue'
    $queued=@(foreach($type in $relay.NestedTypes){foreach($method in $type.Methods|Where-Object HasBody){if((Battle-Calls $method 'IsCurrentGuestSource').Count-eq1){$method}}})
    Assert-BattleRelay ($queued.Count-eq1) 'Main-thread gate rechecks source and generation before execution'
    $manager=$relay.NestedTypes|Where-Object Name -eq GuestRelayConnection
    $network=$manager.Methods|Where-Object Name -eq OnMessage
    Assert-BattleRelay ((Battle-Calls $network 'IsBattlePacket').Count-eq1-and(Battle-Calls $network 'ReceiveBattleMessage').Count-eq1) 'Battle magic routes malformed battle envelopes away from progress decoding'
    $failure=$relay.Methods|Where-Object Name -eq FailBattle
    Assert-BattleRelay (@($failure.Body.Instructions|Where-Object {$_.OpCode.Name-eq'stfld'-and$_.Operand.Name-eq'_guestBattleCommitted'}).Count-eq0) 'Guest failure cannot invent or clear commit authority'
    $freezeAuthority=$relay.Methods|Where-Object Name -eq get_PreparationFrozen
    $freezeCalls=@($freezeAuthority.Body.Instructions|Where-Object {$_.OpCode.Name-in@('call','callvirt')})
    Assert-BattleRelay ($freezeCalls[0].Operand.Name-eq'get_BattleActive') 'Live edit authority checks active battle before native scene state'
    Assert-BattleRelay (@($freezeAuthority.Body.Instructions|Where-Object {$_.OpCode.Name-eq'ldc.i4.1'}).Count-ge1) 'Active battle edit guard has an unconditional frozen return'
    $ack=$relay.Methods|Where-Object Name -eq HandleBattleGuestPacket
    foreach($name in @('AcknowledgeOffer','AcknowledgeState')){
        $calls=Battle-Calls $ack $name
        Assert-BattleRelay ($calls.Count-eq1-and$calls[0].Operand.Parameters.Count-eq3) "Guest $name includes receive-time deadline check"
    }
    $catchup=$relay.Methods|Where-Object Name -eq BroadcastBattleCatchup
    Assert-BattleRelay ((Battle-Calls $catchup 'get_Committed').Count-eq1-and(Battle-Calls $catchup 'get_BattleInitialized').Count-eq1-and(Battle-Calls $catchup 'get_Frozen').Count-eq1) 'Catchup resends commit, initialized state and frozen failure according to authority'
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
public static class RuinaBattleRelayPureChecks
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static string[] Run(Assembly assembly)
    {
        var checks = new List<string>();
        Type relay = assembly.GetType("RuinaCoop.RelaySession", true), recoveryType = assembly.GetType("RuinaCoop.GuestRelayRecovery", true);
        Type manager = relay.GetNestedType("GuestRelayConnection", F);
        object session = FormatterServices.GetUninitializedObject(relay), source = FormatterServices.GetUninitializedObject(manager);
        object recovery = Activator.CreateInstance(recoveryType, true); recoveryType.GetMethod("Begin", F).Invoke(recovery, new object[] { 0f });
        var queue = new Queue<Action>(); int executions = 0; Action action = () => executions++;
        relay.GetField("_onMainThread", F).SetValue(session, (Action<Action>)queue.Enqueue);
        relay.GetField("_guestRecovery", F).SetValue(session, recovery);
        relay.GetField("_guestConnection", F).SetValue(session, source); manager.GetField("Generation", F).SetValue(source, (uint)1);
        var post = relay.GetMethod("PostGuest", F);
        post.Invoke(session, new object[] { source, action });
        Check(queue.Count == 1 && executions == 0, "Current battle source queues main-thread work", checks);
        recoveryType.GetMethod("ScheduleFailure", F).Invoke(recovery, new object[] { 0f, true }); queue.Dequeue()();
        Check(executions == 0, "Queued battle callback from failed generation is revoked", checks);
        post.Invoke(session, new object[] { source, action }); Check(queue.Count == 0, "Failed generation cannot enqueue battle callbacks", checks);
        object replacement = FormatterServices.GetUninitializedObject(manager); manager.GetField("Generation", F).SetValue(replacement, (uint)2);
        relay.GetField("_guestConnection", F).SetValue(session, replacement);
        post.Invoke(session, new object[] { source, action }); Check(queue.Count == 0, "Previous manager cannot enqueue on replacement connection", checks);
        post.Invoke(session, new object[] { replacement, action }); queue.Dequeue()(); Check(executions == 1, "Current replacement manager can execute once", checks);
        post.Invoke(session, new object[] { replacement, action }); relay.GetField("_stopped", F).SetValue(session, true); queue.Dequeue()();
        Check(executions == 1, "Leaving invalidates queued battle work", checks);
        relay.GetField("_stopped", F).SetValue(session, false);

        Type coordinatorType = assembly.GetType("RuinaCoop.BattleStartCoordinator", true);
        object coordinator = Activator.CreateInstance(coordinatorType, true);
        bool began = (bool)coordinatorType.GetMethod("Begin", F).Invoke(coordinator,
            new object[] { (ulong)10, (ulong)100, (ulong)20, (uint)3, new byte[32], (ulong)1, new ulong[] { 1 }, 0d, 10d });
        Check(began, "Compiled host initialization barrier starts", checks);
        relay.GetField("_isHost", F).SetValue(session, true); relay.GetField("_battle", F).SetValue(session, coordinator);
        Check((bool)relay.GetProperty("BattleActive", F).GetValue(session, null), "Host active battle authority is independent of published metadata", checks);
        bool committed = (bool)coordinatorType.GetMethod("TryCommit", F).Invoke(coordinator, new object[] { (Func<bool>)(() => true), 1d });
        Check(committed && (bool)relay.GetProperty("BattleCommitted", F).GetValue(session, null), "Compiled commit authority becomes irreversible", checks);
        coordinatorType.GetMethod("Fail", F).Invoke(coordinator, new object[] { "timeout after commit" });
        Check((bool)relay.GetProperty("BattleActive", F).GetValue(session, null) && (bool)coordinatorType.GetProperty("Frozen", F).GetValue(coordinator, null), "Frozen committed host keeps active battle authority", checks);
        Check((bool)coordinatorType.GetProperty("Committed", F).GetValue(coordinator, null) && !(bool)coordinatorType.GetProperty("CanReturnToPreparation", F).GetValue(coordinator, null), "Postcommit failure cannot resume preparation", checks);

        object guest = FormatterServices.GetUninitializedObject(relay);
        FieldInfo phaseField = relay.GetField("_guestBattlePhase", F);
        phaseField.SetValue(guest, Enum.Parse(phaseField.FieldType, "Frozen"));
        Check(!(bool)relay.GetProperty("BattleCommitted", F).GetValue(guest, null) && (bool)relay.GetProperty("BattlePaused", F).GetValue(guest, null), "Precommit guest failure pauses battle without claiming commit", checks);
        relay.GetField("_guestBattleCommitted", F).SetValue(guest, true);
        Check((bool)relay.GetProperty("BattleCommitted", F).GetValue(guest, null), "Committed guest remains committed while frozen", checks);
        return checks.ToArray();
    }
    private static void Check(bool valid, string name, List<string> checks)
    { if(!valid) throw new InvalidOperationException(name); checks.Add(name); }
}
'@
    foreach($check in [RuinaBattleRelayPureChecks]::Run($GuardSmokeModAssembly)){Assert-BattleRelay $true $check}
    $result=[ordered]@{Success=$true;CheckCount=$battleChecks.Count;Execution='Compiled pure Relay callback generation/source and authority properties, plus Cecil integration audit; no Steam/Unity/native battle/inventory/save execution.';ModAssemblySha256=(Get-FileHash -LiteralPath $guardModPath -Algorithm SHA256).Hash;Checks=$battleChecks.ToArray()}
    if($battleOutput){$result|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $battleOutput -Encoding UTF8}
    "PASS: $($battleChecks.Count) battle relay integration/source/authority checks."
} finally {$battleAssembly.Dispose()}
