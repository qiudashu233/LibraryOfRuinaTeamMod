using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RuinaCoop
{
    // Stage 4A initialization probe. Guests use session DTOs and never enter
    // the native battle. No result or native teardown is implemented here.
    internal static class NativeBattleBridge
    {
        private static RelaySession _owner;
        private static bool _ownedScene, _boundary;
        private static int _loadPermit, _stageStartPermit, _initializationDepth;
        internal static bool OwnsNativeScene { get { return _ownedScene; } }
        private static RelaySession Session { get { return DeckGuard.Session; } }
        private static bool InRoom { get { return Session != null && Session.IsActive; } }
        // The latch survives room exit. Removing the networking session must
        // never resume the host's paused verification scene as a solo battle.
        private static bool Protected { get { return _ownedScene || InRoom && Session.BattleCommitted; } }
        private static bool Initializing
        {
            get { return _ownedScene && !_boundary && _owner != null && _owner.IsActive && _owner.IsHost &&
                _owner.BattleCommitted && !_owner.BattlePaused; }
        }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, "BattleSceneRoot", "StartBattle", Type.EmptyTypes, "SceneStartPrefix");
            Patch(harmony, "StageController", "StartBattle", Type.EmptyTypes, "StageStartPrefix", null, "InitializationFinalizer");
            Patch(harmony, "StageController", "OnUpdate", new[] { typeof(float) }, "UserUpdatePrefix");
            foreach (var name in new[] { "OnFixedUpdate", "OnFixedUpdateLate" })
                Patch(harmony, "StageController", name, new[] { typeof(float) }, "FixedPrefix");
            foreach (var name in new[] { "RoundStartPhase_UI", "RoundStartPhase_System", "SortUnitPhase", "DrawCardPhase", "ApplyEnemyCardPhase" })
                Patch(harmony, "StageController", name, Type.EmptyTypes, "InitialPhasePrefix", "InitialPhasePostfix", "InitializationFinalizer");
            Patch(harmony, "StageController", "CheckInput", new[] { typeof(bool) }, "UserUpdatePrefix");
            Patch(harmony, "StageController", "StopSpeedDiceRoll", Type.EmptyTypes, "InputPrefix");
            Patch(harmony, "StageController", "CompleteApplyingLibrarianCardPhase", new[] { typeof(bool) }, "NeverAdvancePrefix");
            foreach (var name in new[] { "SetAutoCardForPlayer", "SetUnequipCardAll" })
                Patch(harmony, "StageController", name, Type.EmptyTypes, "InputPrefix");
            Patch(harmony, "BattlePlayingCardSlotDetail", "AddCard", new[] { typeof(BattleDiceCardModel), typeof(BattleUnitModel), typeof(int), typeof(bool) }, "InputPrefix");
            foreach (var name in new[] { "DestroyCard", "DestroyCardWithoutCurrentAction", "DestroyCardByIdx" })
                Patch(harmony, "BattlePlayingCardSlotDetail", name, new[] { typeof(int) }, "InputPrefix");
            Patch(harmony, "BattlePlayingCardSlotDetail", "DestroyCardAll", Type.EmptyTypes, "InputPrefix");
            foreach (var name in new[] { "EndBattle", "CloseBattleScene", "BattleEndForcelyNotRound", "ClearBattle" })
                Patch(harmony, "StageController", name, Type.EmptyTypes, "NeverAdvancePrefix");
            foreach (var name in new[] { "EndBattlePhase", "EndBattlePhaseAfter", "RoundEndPhase" })
                Patch(harmony, "StageController", name, new[] { typeof(float) }, "NeverAdvancePrefix");
            foreach (var name in new[] { "EndBattlePhase_invitation", "EndBattlePhase_creature" })
                Patch(harmony, "StageController", name, Type.EmptyTypes, "NeverAdvancePrefix");
            Patch(harmony, "StageController", "GameOver", new[] { typeof(bool), typeof(bool) }, "NeverAdvancePrefix");
            Patch(harmony, "UI.UIBgScreenChangeAnim", "StartBg", new[] { GameType("UI.UIScreenChangeType") }, "NeverAdvancePrefix");
            if (AccessTools.Field(typeof(StageController), "_bCalledRoundStart_system") == null)
                throw new MissingFieldException("StageController", "_bCalledRoundStart_system");
            RequireMethod("StageController", "get_UsedBooks", Type.EmptyTypes);
        }
        private static Type GameType(string name) { return typeof(UnitDataModel).Assembly.GetType(name, true); }
        private static MethodInfo RequireMethod(string type, string name, Type[] arguments)
        {
            var method = AccessTools.Method(GameType(type), name, arguments);
            if (method == null) throw new MissingMethodException(type, name);
            return method;
        }
        private static void Patch(Harmony harmony, string type, string name, Type[] arguments, string prefix, string postfix = null, string finalizer = null)
        {
            harmony.Patch(RequireMethod(type, name, arguments),
                prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeBattleBridge), prefix)),
                postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeBattleBridge), postfix)),
                finalizer: finalizer == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeBattleBridge), finalizer)));
        }

        // Called by DeckGuard's existing three native start prefixes.
        internal static bool AllowNativeStart(MethodBase original)
        {
            if (!InRoom && !_ownedScene) return true;
            if (original == null && Initializing && _loadPermit == 1)
            {
                // The legacy shared DeckGuard prefix has no MethodBase. This
                // permit exists only around StartHost's synchronous call.
                _loadPermit = 0;
                return true;
            }
            if (original != null && original.DeclaringType == typeof(GlobalGameManager) && original.Name == "LoadBattleScene")
            {
                if (!Initializing || _loadPermit != 1) return false;
                _loadPermit = 0;
                return true;
            }
            if (!_ownedScene && InRoom && Session.IsHost && (original == null ||
                original.Name == "OnClickBattleStart" || original.Name == "OnClickGameStart")) Session.RequestBattleStart();
            return false;
        }
        internal static int[] GetInvitationBooks()
        {
            var books = StageController.Instance.UsedBooks;
            if (books == null) throw new InvalidOperationException("邀请书列表未初始化。");
            var ids = new List<int>();
            foreach (var book in books)
            {
                if (book == null || !book.IsBasic() || book.id <= 0) throw new InvalidOperationException("4A 尚未适配此邀请书。");
                ids.Add(book.id);
            }
            return ids.ToArray();
        }
        internal static void StartHost(RelaySession session)
        {
            if (session == null || !ReferenceEquals(Session, session) || !session.IsHost || !session.IsActive ||
                !session.BattleCommitted || session.BattlePaused || _ownedScene)
                throw new InvalidOperationException("4A 原版初始化缺少唯一房主提交许可。");
            var stage = StageController.Instance.GetStageModel();
            if (stage == null || stage.ClassInfo == null || stage.ClassInfo.id == null ||
                !stage.ClassInfo.id.IsBasic() || stage.ClassInfo.id.id != 3)
                throw new InvalidOperationException("4A 首幕验证只适配原版尹之事务所接待。");
            if (stage.ClassInfo.GetPrevBattleStory() != null)
                throw new InvalidOperationException("4A 直接加载路径尚未适配此前战剧情。");
            _owner = session; _ownedScene = true; _boundary = false; _loadPermit = 1; _stageStartPermit = 1;
            try
            {
                GlobalGameManager.Instance.LoadBattleScene();
                if (_loadPermit != 0 || _stageStartPermit != 0)
                    throw new InvalidOperationException("原版战斗初始化未消费唯一加载许可。");
            }
            catch (Exception exception) { FreezeFailure("房主原版首幕初始化失败：" + exception.GetBaseException().Message); }
        }
        private static bool SceneStartPrefix()
        {
            if (!InRoom && !_ownedScene) return true;
            return Initializing && _stageStartPermit == 1;
        }
        private static bool StageStartPrefix(out bool __state)
        {
            __state = false;
            if (!InRoom && !_ownedScene) return true;
            if (!Initializing || _stageStartPermit != 1) return false;
            _stageStartPermit = 0; _initializationDepth++; __state = true;
            return true;
        }
        private static bool UserUpdatePrefix() { return !Protected; }
        private static bool FixedPrefix()
        {
            if (!Protected) return true;
            if (!Initializing) return false;
            var phase = Convert.ToInt32(NativeUi.Get(StageController.Instance, "Phase"));
            if (phase >= 0 && phase <= 4) return true;
            if (phase == 5) CaptureBoundary();
            else FreezeFailure("首幕初始化进入未适配的原版阶段 " + phase + "。");
            return false;
        }
        private static bool InitialPhasePrefix(out bool __state)
        {
            __state = false;
            if (!Protected) return true;
            if (!Initializing) return false;
            _initializationDepth++; __state = true;
            return true;
        }
        private static void InitialPhasePostfix(MethodBase __originalMethod, bool __state)
        {
            if (!__state || !Initializing) return;
            var stage = StageController.Instance;
            var phase = Convert.ToInt32(NativeUi.Get(stage, "Phase"));
            if (__originalMethod.Name == "RoundStartPhase_System" && phase == 1 &&
                Convert.ToBoolean(NativeUi.Get(stage, "_bCalledRoundStart_system"))) NativeUi.Call(stage, "StopSpeedDiceRoll");
            else if (__originalMethod.Name == "ApplyEnemyCardPhase")
            {
                if (phase == 5) CaptureBoundary();
                else FreezeFailure("敌方首幕选牌未到达玩家输入边界。");
            }
        }
        private static Exception InitializationFinalizer(Exception __exception, bool __state)
        {
            if (__state) _initializationDepth--;
            if (__state && __exception != null) FreezeFailure("原版首幕阶段失败：" + __exception.GetBaseException().Message);
            return __exception;
        }
        private static void CaptureBoundary()
        {
            if (_boundary || !_ownedScene) return;
            _boundary = true; _loadPermit = 0; _stageStartPermit = 0;
            try { _owner.OnNativeBattleInitialBoundary(); }
            catch (Exception exception) { FreezeFailure("首幕权威状态采集失败：" + exception.GetBaseException().Message); }
        }
        private static void FreezeFailure(string reason)
        {
            _boundary = true; _loadPermit = 0; _stageStartPermit = 0;
            if (_owner != null) _owner.FailBattle(reason);
            Debug.LogWarning("[RuinaCoop] " + reason);
        }
        private static bool InputPrefix() { return !Protected || Initializing && _initializationDepth > 0; }
        private static bool NeverAdvancePrefix() { return !Protected; }

        // The no-result return path is deliberately not implemented: native
        // cleanup invokes virtual end callbacks, which need separate proof.
        internal static bool EndVerification(RelaySession session)
        {
            if (!_ownedScene) return true;
            if (!ReferenceEquals(_owner, session)) return false;
            _boundary = true; _loadPermit = 0; _stageStartPermit = 0;
            session.FailBattle("4A 原版场景已冻结。此验证版本需退出游戏后重新启动，尚不支持游戏内安全返回。");
            return false;
        }
    }
}
