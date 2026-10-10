using System;
using System.Collections.Generic;
using System.Reflection;

// Test doubles only. No references to Unity, Steam, native game DLLs or saves.
public sealed class UnitDataModel { }
public sealed class BattleDiceCardModel { }
public sealed class BattleUnitModel { }
public sealed class LorId
{
    public int id; public bool Basic = true;
    public bool IsBasic() { return Basic; }
}
public sealed class StageClassInfo
{
    public LorId id = new LorId { id = 3 };
    public object PreviousBattleStory;
    public object GetPrevBattleStory() { return PreviousBattleStory; }
}
public sealed class StageModel { public StageClassInfo ClassInfo = new StageClassInfo(); }
public sealed class StageController
{
    public static StageController Instance = new StageController();
    public readonly List<LorId> UsedBooks = new List<LorId>();
    public StageModel Model = new StageModel();
    public int Phase, StopRolls;
    public bool _bCalledRoundStart_system;
    public StageModel GetStageModel() { return Model; }
    public void RoundStartPhase_System() { }
    public void ApplyEnemyCardPhase() { }
    public void StopSpeedDiceRoll() { StopRolls++; Phase = 3; }
}
public sealed class GlobalGameManager
{
    public static GlobalGameManager Instance = new GlobalGameManager();
    public int SceneLoads, NativeStarts;
    public bool FailNativeStart;
    public void LoadBattleScene()
    {
        if (!RuinaCoop.NativeBattleBridge.AllowNativeStart(null)) return;
        SceneLoads++;
        if (!(bool)HarnessCalls.Call("SceneStartPrefix")) return;
        var args = new object[] { false };
        if (!(bool)HarnessCalls.Call("StageStartPrefix", args)) return;
        NativeStarts++;
        var error = FailNativeStart ? new InvalidOperationException("stub native start failure") : null;
        HarnessCalls.Call("InitializationFinalizer", new object[] { error, args[0] });
    }
}
public static class HarnessCalls
{
    internal static object Call(string name, params object[] args)
    { return typeof(RuinaCoop.NativeBattleBridge).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
}
namespace HarmonyLib
{
    public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) { } }
    public sealed class Harmony
    { public void Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null) { throw new InvalidOperationException("Harness never installs runtime patches."); } }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] arguments = null)
        { return arguments == null ? type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) : type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, null, arguments, null); }
        public static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); }
    }
}
namespace UnityEngine
{
    public static class Debug { public static void LogWarning(object text) { } }
}
namespace RuinaCoop
{
    internal static class DeckGuard { internal static RelaySession Session; }
    internal sealed class RelaySession
    {
        internal bool IsActive = true, IsHost = true, BattleCommitted, BattlePaused;
        internal int Requests, Boundaries, Failures;
        internal string LastFailure;
        internal bool WasPausedInsideCapture;
        internal bool RequestBattleStart() { Requests++; return true; }
        internal void OnNativeBattleInitialBoundary()
        {
            Boundaries++;
            WasPausedInsideCapture = !(bool)HarnessCalls.Call("InputPrefix") && !(bool)HarnessCalls.Call("FixedPrefix");
        }
        internal void FailBattle(string reason) { Failures++; LastFailure = reason; BattlePaused = true; }
    }
    internal static class NativeUi
    {
        internal static object Get(object target, string name)
        {
            var field = target.GetType().GetField(name);
            if (field != null) return field.GetValue(target);
            return target.GetType().GetProperty(name).GetValue(target);
        }
        internal static object Call(object target, string name, params object[] args)
        { return target.GetType().GetMethod(name).Invoke(target, args); }
    }
}
