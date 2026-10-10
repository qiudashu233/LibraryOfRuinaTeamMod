using System;
using System.Reflection;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("Bridge regression: " + name); _checks++; }
    private static bool Gate(string name) { return (bool)HarnessCalls.Call(name); }
    // Test-process isolation only. Production intentionally exposes no reset.
    private static RelaySession Fresh(bool host = true, bool committed = false)
    {
        var type = typeof(NativeBattleBridge);
        foreach (var name in new[] { "_owner", "_ownedScene", "_boundary", "_loadPermit", "_stageStartPermit", "_initializationDepth" })
        {
            var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            field.SetValue(null, field.FieldType == typeof(bool) ? (object)false : field.FieldType == typeof(int) ? (object)0 : null);
        }
        GlobalGameManager.Instance = new GlobalGameManager();
        StageController.Instance = new StageController();
        DeckGuard.Session = new RelaySession { IsHost = host, BattleCommitted = committed };
        return DeckGuard.Session;
    }
    private static void RejectStart(RelaySession session, string name)
    {
        var rejected = false;
        try { NativeBattleBridge.StartHost(session); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && GlobalGameManager.Instance.SceneLoads == 0 && !NativeBattleBridge.OwnsNativeScene, name);
    }
    private static void Main()
    {
        Fresh(); DeckGuard.Session = null;
        Check(NativeBattleBridge.AllowNativeStart(null), "ordinary solo entry remains available");
        Check(Gate("UserUpdatePrefix") && Gate("FixedPrefix") && Gate("InputPrefix") && Gate("NeverAdvancePrefix"), "ordinary solo gates are untouched");
        var host = Fresh();
        Check(!NativeBattleBridge.AllowNativeStart(null) && host.Requests == 1, "host UI start asks network barrier and cannot run original");
        Check(!NativeBattleBridge.AllowNativeStart(typeof(GlobalGameManager).GetMethod("LoadBattleScene")) && host.Requests == 1, "identified raw scene entry cannot bypass barrier");
        RejectStart(host, "uncommitted host cannot load");
        var guest = Fresh(false, true);
        Check(!NativeBattleBridge.AllowNativeStart(null) && guest.Requests == 0, "guest cannot request host start through native UI");
        RejectStart(guest, "committed guest never loads native scene");
        Check(!Gate("SceneStartPrefix"), "guest direct native-root entry is blocked");
        var guestArgs = new object[] { false };
        Check(!(bool)HarnessCalls.Call("StageStartPrefix", guestArgs) && !(bool)guestArgs[0], "guest direct StageController start is blocked");
        host = Fresh(true, true); host.IsActive = false;
        RejectStart(host, "stopped host cannot load");
        host = Fresh(true, true); host.BattlePaused = true;
        RejectStart(host, "failed committed initialization cannot resume");
        host = Fresh(true, true); StageController.Instance.Model.ClassInfo.id.id = 4;
        RejectStart(host, "unsupported reception is rejected before native ownership");
        host = Fresh(true, true); StageController.Instance.Model.ClassInfo.id.Basic = false;
        RejectStart(host, "workshop reception cannot impersonate base stage 3");
        host = Fresh(true, true); StageController.Instance.Model.ClassInfo.PreviousBattleStory = new object();
        RejectStart(host, "previous-battle story is not silently skipped");
        host = Fresh(true, true);
        NativeBattleBridge.StartHost(host);
        Check(NativeBattleBridge.OwnsNativeScene && GlobalGameManager.Instance.SceneLoads == 1 && GlobalGameManager.Instance.NativeStarts == 1, "committed host consumes one scene and one stage permit");
        Check(!NativeBattleBridge.AllowNativeStart(null) && !Gate("SceneStartPrefix"), "consumed permit cannot load or initialize twice");
        Check(!Gate("UserUpdatePrefix") && !Gate("InputPrefix") && !Gate("NeverAdvancePrefix"), "user input and settlement are disabled before initial snapshot");
        Check(Gate("FixedPrefix"), "authorized native phases may initialize");
        var phaseArgs = new object[] { false };
        Check((bool)HarnessCalls.Call("InitialPhasePrefix", phaseArgs) && (bool)phaseArgs[0] && Gate("InputPrefix"), "internal AI initialization scope may mutate cards");
        HarnessCalls.Call("InitializationFinalizer", new object[] { null, phaseArgs[0] });
        Check(!Gate("InputPrefix"), "UI input has no initialization depth after native scope ends");
        StageController.Instance.Phase = 1; StageController.Instance._bCalledRoundStart_system = true;
        phaseArgs = new object[] { false }; HarnessCalls.Call("InitialPhasePrefix", phaseArgs);
        HarnessCalls.Call("InitialPhasePostfix", new object[] { typeof(StageController).GetMethod("RoundStartPhase_System"), true });
        HarnessCalls.Call("InitializationFinalizer", new object[] { null, phaseArgs[0] });
        Check(StageController.Instance.Phase == 3 && StageController.Instance.StopRolls == 1, "first-round speed setup advances without enabling user input");
        StageController.Instance.Phase = 5;
        phaseArgs = new object[] { false }; HarnessCalls.Call("InitialPhasePrefix", phaseArgs);
        HarnessCalls.Call("InitialPhasePostfix", new object[] { typeof(StageController).GetMethod("ApplyEnemyCardPhase"), true });
        HarnessCalls.Call("InitializationFinalizer", new object[] { null, phaseArgs[0] });
        Check(host.Boundaries == 1 && host.WasPausedInsideCapture, "first-round capture observes the pause latch already set");
        Check(!Gate("FixedPrefix") && !Gate("InputPrefix") && !Gate("NeverAdvancePrefix"), "first-round input and resolution remain stopped after capture");
        HarnessCalls.Call("CaptureBoundary");
        Check(host.Boundaries == 1, "duplicate boundary callback cannot capture twice");
        host.IsActive = false; DeckGuard.Session = null;
        Check(!Gate("UserUpdatePrefix") && !Gate("FixedPrefix") && !Gate("InputPrefix") && !Gate("NeverAdvancePrefix"), "room exit cannot resume the latched native scene");
        Check(!NativeBattleBridge.AllowNativeStart(null) && !Gate("SceneStartPrefix"), "room exit cannot transform verification into a solo restart");
        Check(!NativeBattleBridge.EndVerification(host) && NativeBattleBridge.OwnsNativeScene && host.LastFailure.Contains("重新启动"), "unproved teardown is rejected with explicit restart boundary");
        Check(!NativeBattleBridge.EndVerification(new RelaySession()) && NativeBattleBridge.OwnsNativeScene, "another session cannot clear native ownership");
        host = Fresh(true, true); GlobalGameManager.Instance.FailNativeStart = true;
        NativeBattleBridge.StartHost(host);
        Check(host.Failures == 1 && NativeBattleBridge.OwnsNativeScene && !Gate("FixedPrefix"), "native initialization error leaves persistent frozen ownership");
        host = Fresh(true, true); NativeBattleBridge.StartHost(host); StageController.Instance.Phase = 28;
        Check(!Gate("FixedPrefix") && host.Failures == 1 && !Gate("NeverAdvancePrefix"), "unexpected result phase is frozen rather than executed");
        Fresh(); StageController.Instance.UsedBooks.Add(new LorId { id = 101 });
        var ids = NativeBattleBridge.GetInvitationBooks(); ids[0] = 999;
        Check(StageController.Instance.UsedBooks[0].id == 101, "manifest invitation IDs are a detached copy of original used books");
        StageController.Instance.UsedBooks.Add(new LorId { id = 102, Basic = false });
        var booksRejected = false;
        try { NativeBattleBridge.GetInvitationBooks(); } catch (InvalidOperationException) { booksRejected = true; }
        Check(booksRejected, "unsupported invitation books are rejected");
        Console.WriteLine("Native battle bridge regression passed: " + _checks + " checks; no Unity, Steam, game DLL or saves loaded.");
    }
}
