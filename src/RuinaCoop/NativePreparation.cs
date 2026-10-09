using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RuinaCoop
{
    // Reception presentation is a view, never a client StageController/Library replacement.
    internal static class NativePreparation
    {
        private static readonly PreparationLifecycle<StageModel> Lifecycle = new PreparationLifecycle<StageModel>();
        private static StageModel _nativeStage { get { return Lifecycle.Stage; } }
        private static ulong _context { get { return Lifecycle.ContextId; } }
        private static ulong _failedRoom, _failedContext;
        private static int _nativeCall;
        private static RelaySession _session;
        private static ProgressSnapshot _displayed;
        private static object _controller, _uiData, _previousPhase;
        private static NativeDeckModels _rosterModels, _enemyModels;
        private static readonly List<UnitDataModel> EnemyUnits = new List<UnitDataModel>();
        private static readonly List<RestoreField> Fields = new List<RestoreField>();
        private static readonly Dictionary<GameObject, bool> Visibility = new Dictionary<GameObject, bool>();
        private static readonly List<UnitDataModel> RendererUnits = new List<UnitDataModel>();
        private static readonly List<int> RendererTextures = new List<int>();
        private static readonly List<UnitDataModel> RendererData = new List<UnitDataModel>();
        private static readonly List<object> PhaseStack = new List<object>();
        private static readonly HashSet<SephirahType> InitializedFloors = new HashSet<SephirahType>();
        private static bool _dirty, _rendering, _closing, _rendererCaptured;
        private static ulong _hiddenContext, _hiddenRoom, _generation;
        private static byte _selectedUnit, _wave;
        private static string _status = "";
        private sealed class InputTag { internal ProgressSnapshot Snapshot; internal ulong Generation; internal object Owner; }
        private static readonly Dictionary<object, InputTag> InputTargets = new Dictionary<object, InputTag>();
        private static readonly Dictionary<object, InputTag> Presses = new Dictionary<object, InputTag>();
        internal static bool Active { get { return _session != null && _displayed != null && _displayed.Preparation.Available && !_closing; } }
        internal static string Status { get { return _status; } }
        private static bool GuestView { get { return Active && !_session.IsHost; } }
        // Remains set throughout restoration; CallUIPhase invokes OnClose while Active is already false.
        private static bool GuestProjectionGuard { get { return _session != null && !_session.IsHost && _controller != null; } }
        private static bool InRoom { get { return DeckGuard.Session != null && DeckGuard.Session.IsActive; } }
        private sealed class RestoreField { internal object Target, Value; internal string Name; internal bool Property; }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, "UI.UIController", "PrepareBattle", new[] { typeof(StageClassInfo), typeof(List<DropBookXmlInfo>) }, "PreparingPrefix", "PreparedPostfix");
            Patch(harmony, "UI.UIInvitationRightMainPanel", "SendInvitation", Type.EmptyTypes, "InvitationPrefix");
            Patch(harmony, "UI.UIInvitationRightMainPanel", "ConfirmSendInvitation", Type.EmptyTypes, "InvitationPrefix");
            Patch(harmony, "UI.UIController", "BackBattlePrepare", Type.EmptyTypes, "BackPrefix");
            Patch(harmony, "UI.UIBgScreenChangeAnim", "StartBg", new[] { GameType("UI.UIScreenChangeType") }, "ScreenChangePrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnUIPhaseEnter", new[] { GameType("UI.UIPhase") }, "EnteredPrefix", "EnteredPostfix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnOpen", Type.EmptyTypes, "OpenedPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnClose", Type.EmptyTypes, "ClosedPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "SetNextSephirah", new[] { GameType("UI.UISephirahButton") }, "FloorPrefix", "FloorPostfix");
            Patch(harmony, "UI.UIBattleSettingPanel", "SelectedToggles", new[] { GameType("UI.UICharacterSlot") }, "RosterClickPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "SetToggles", Type.EmptyTypes, "TogglesPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnClickOpenEditPage", new[] { GameType("UI.UIBattleSettingEditTap") }, "EditPrefix");
            foreach (var name in new[] { "OnClickWaveButton", "OnClickFloorButton" }) Patch(harmony, "UI.UIBattleSettingPanel", name, Type.EmptyTypes, "CenterPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnCancel", Type.EmptyTypes, "CancelPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "OnClickBackButton", Type.EmptyTypes, "CancelPrefix");
            Patch(harmony, "UI.UIBattleSettingLibrarianInfoPanel", "SetData", new[] { typeof(UnitDataModel) }, "ProfilePrefix");
            Patch(harmony, "UI.UIBattleSettingLibrarianInfoPanel", "OnPointerClickBattlePageSlot", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") }, "CardEditPrefix");
            Patch(harmony, "UI.UIBattleSettingLibrarianInfoPanel", "OnPointerClickEquipPage", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") }, "CoreEditPrefix");
            foreach (var name in new[] { "OnUpdatePhase", "SetEnemyWaveInBattleSetting" }) Patch(harmony, "UI.UIEnemyCharacterListPanel", name, Type.EmptyTypes, "EnemiesPrefix");
            Patch(harmony, "UI.UIEnemyCharacterListPanel", "ChangeEnemyWave", new[] { typeof(int) }, "WavePrefix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "SetLibrarianCharacterListPanel_Battle", Type.EmptyTypes, "LibrariansPrefix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "OnUpdatePhase", Type.EmptyTypes, "LibrariansPrefix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "OnSelect", new[] { GameType("UI.UICharacterSlot") }, "SelectedPrefix");
            Patch(harmony, "UI.UIEnemyCharacterListPanel", "OnSelect", new[] { GameType("UI.UICharacterSlot") }, "EnemySelectedPrefix");
            // Controls call these getters later; the guest never follows local tutorial/end-content branches.
            Patch(harmony, "UI.UIBattleSettingPanel", "IsRunningTutorial", Type.EmptyTypes, "TutorialPrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "SelectCurrentFloor", Type.EmptyTypes, "GuestUnsafePrefix");
            Patch(harmony, "UI.UIBattleSettingPanel", "UpdateEditPanel", Type.EmptyTypes, "GuestUnsafePrefix");
            foreach (var type in new[] { GameType("UI.UICustomSelectable"), FindLoadedType("UnityEngine.EventSystems.EventTrigger"), FindLoadedType("UnityEngine.UI.Selectable") })
                PatchExternal(harmony, type, "OnPointerDown", new[] { FindLoadedType("UnityEngine.EventSystems.PointerEventData") });
            PatchExternal(harmony, GameType("UI.UICustomSelectable"), "OnSubmit", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") });
            PatchExternal(harmony, FindLoadedType("UnityEngine.EventSystems.EventTrigger"), "OnSubmit", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") });
            PatchExternal(harmony, FindLoadedType("UnityEngine.UI.Button"), "OnSubmit", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") });
            Patch(harmony, "UI.UISlot", "OnPointerDown", new[] { FindLoadedType("UnityEngine.EventSystems.BaseEventData") }, "PressPrefix");
        }
        private static Type GameType(string name) { return typeof(UnitDataModel).Assembly.GetType(name, true); }
        private static Type FindLoadedType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(name, false); if (type != null) return type; }
            var found = Type.GetType(name + ", UnityEngine.UI", true); return found;
        }
        private static void Patch(Harmony harmony, string type, string name, Type[] args, string prefix, string postfix = null)
        {
            var method = AccessTools.Method(GameType(type), name, args);
            if (method == null) throw new MissingMethodException(type, name);
            harmony.Patch(method, prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativePreparation), prefix)),
                postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativePreparation), postfix)));
        }
        private static void PatchExternal(Harmony harmony, Type type, string name, Type[] args)
        { var method = AccessTools.Method(type, name, args); if (method == null) throw new MissingMethodException(type.FullName, name); harmony.Patch(method, new HarmonyMethod(AccessTools.Method(typeof(NativePreparation), "PressPrefix"))); }

        internal static void Capture(RelaySession session, ProgressSnapshot snapshot)
        {
            var value = new PreparationSnapshot(); snapshot.Preparation = value;
            if (session == null || !session.IsHost) return;
            try
            {
                var stage = StageController.Instance.GetStageModel();
                // Late room creation can adopt once. Ending a reception in the
                // same room must not revive the retained StageController model.
                if (Lifecycle.ObserveRoom(session.RoomId, stage,
                    Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase")) == 5)) InitializedFloors.Clear();
                if (!Lifecycle.Matches(session.RoomId, stage)) return;
                var info = stage.ClassInfo;
                value.Phase = PreparationPhase.Editing; value.ContextId = _context;
                if (info == null || info.id == null || !info.id.IsBasic() || info.id.id <= 0)
                { value.Phase = PreparationPhase.Selection; value.ContextId = 0; Unavailable(value, PreparationReason.UnsupportedStage); return; }
                value.StageId = info.id.id;
                if (!IsSupportedInvitation(info, StageController.Instance.IsEndContents))
                { Unavailable(value, PreparationReason.UnsupportedStage); return; }
                if (!snapshot.Stages.Exists(row => row.Id == value.StageId))
                { Unavailable(value, PreparationReason.UnsupportedStage); return; }
                snapshot.SelectedStageId = value.StageId;
                if (IsFailedContext(session.RoomId, value.ContextId))
                { Unavailable(value, PreparationReason.CaptureFailed); return; }
                var floor = StageController.Instance.GetCurrentStageFloorModel();
                var currentWave = StageController.Instance.GetCurrentWaveModel();
                if (floor == null || currentWave == null) throw new InvalidOperationException("Native reception has no challenge floor/wave.");
                value.FloorId = (byte)floor.Sephirah;
                var available = stage.GetAvailableFloorList();
                foreach (var candidate in stage.floorList)
                {
                    var can = candidate != null && available.Contains(candidate) && !candidate.IsUnavailable();
                    if (candidate == null || (byte)candidate.Sephirah < 1 || (byte)candidate.Sephirah > 10) throw new InvalidOperationException("Unsupported native floor.");
                    value.Floors.Add(new PreparationFloorEntry { FloorId = (byte)candidate.Sephirah, CanParticipate = can,
                        Reason = can ? PreparationUnitReason.None : PreparationUnitReason.FloorUnavailable });
                }
                var roster = floor.GetUnitBattleDataList(); // The added-list getter silently auto-selects a librarian.
                var savedFloor = snapshot.Floors.Find(row => (byte)row.Sephirah == value.FloorId);
                if (roster == null || savedFloor == null || roster.Count != savedFloor.UnitReferences.Count || roster.Count > 5)
                    throw new InvalidOperationException("Reception roster differs from the supported library roster.");
                for (var i = 0; i < roster.Count; i++)
                {
                    var battle = roster[i]; var unit = battle == null ? null : battle.unitData;
                    if (unit == null || !ReferenceEquals(unit, savedFloor.UnitReferences[i])) throw new InvalidOperationException("Reception replaces a native librarian.");
                    var can = !battle.isDead && !battle.isLocked && !unit.IsLockUnit();
                    value.Participants.Add(new PreparationUnitEntry { UnitIndex = (byte)i, UnitIdentity = DeckMirror.GetUnitIdentity(unit),
                        CanParticipate = can, Participating = can && battle.IsAddedBattle,
                        Reason = can ? PreparationUnitReason.None : battle.isDead ? PreparationUnitReason.Defeated : PreparationUnitReason.Unavailable });
                }
                if (currentWave.AvailableUnitNumber < 1 || currentWave.AvailableUnitNumber > 5) throw new InvalidOperationException("Unsupported reception unit limit.");
                value.MaxUnits = (byte)currentWave.AvailableUnitNumber;
                value.CurrentWaveIndex = checked((byte)(StageController.Instance.CurrentWave - 1));
                for (var i = 0; i < stage.waveList.Count; i++)
                {
                    var wave = new PreparationWaveEntry { WaveIndex = checked((byte)i) }; value.Waves.Add(wave);
                    if (i != value.CurrentWaveIndex && (LibraryModel.Instance.GetChapter() < 2 || info.currentState != UI.StoryState.Clear))
                    {
                        // Vanilla displays a complete row of unknown placeholders, not the real hidden enemy count.
                        var hiddenSlots = ((IList)NativeUi.Get(NativeUi.Get(FindPanel("UI.UIEnemyCharacterListPanel"), "CharacterList"), "slotList")).Count;
                        for (var j = 0; j < hiddenSlots; j++) wave.Enemies.Add(new PreparationEnemyEntry { EnemyIdentity = ((ulong)i + 1) * 256 + (ulong)j + 1, Unknown = true, Name = "???" });
                        continue;
                    }
                    var enemies = stage.waveList[i].UnitList;
                    for (var j = 0; j < enemies.Count; j++)
                    {
                        var unit = enemies[j].unitData;
                        var enemy = new PreparationEnemyEntry { EnemyIdentity = ((ulong)i + 1) * 256 + (ulong)j + 1,
                            Unknown = info.currentState != UI.StoryState.Clear && unit.isUnknownBattleSetting };
                        wave.Enemies.Add(enemy);
                        if (enemy.Unknown) { enemy.Name = "???"; continue; }
                        if (unit.EnemyUnitId == null || !unit.EnemyUnitId.IsBasic() || unit.bookItem == null || !unit.bookItem.BookId.IsBasic())
                            throw new InvalidOperationException("Unsupported enemy XML identity.");
                        enemy.EnemyId = unit.EnemyUnitId.id; enemy.BookId = unit.bookItem.BookId.id; enemy.Name = unit.name;
                        var deck = new ProgressSnapshot.UnitDeckEntry { BookId = enemy.BookId };
                        DeckMirror.CaptureDisplay(unit, unit.bookItem, deck); CopyDisplay(deck.Display, enemy.Display);
                        enemy.CardsVisible = true;
                        foreach (var card in unit.GetDeckCardModelAll())
                        {
                            var id = card.GetID(); if (id == null || !id.IsBasic() || id.id <= 0) throw new InvalidOperationException("Unsupported enemy combat page.");
                            enemy.Cards.Add(id.id);
                        }
                    }
                }
                value.Available = true; value.Reason = PreparationReason.None;
                string reason; if (!PreparationMirror.Validate(value, out reason)) throw new InvalidOperationException(reason);
                if (PreparationMirror.EncodeContent(value).Length > PreparationMirror.MaxSectionBytes) Unavailable(value, PreparationReason.PacketLimit);
            }
            catch (Exception exception)
            { Unavailable(value, PreparationReason.CaptureFailed); Debug.LogWarning("[RuinaCoop] Native preparation capture: " + exception.GetBaseException().Message); }
        }
        private static void Unavailable(PreparationSnapshot value, PreparationReason reason)
        {
            if (value.StageId <= 0) { value.Phase = PreparationPhase.Selection; value.ContextId = 0; value.FloorId = byte.MaxValue; }
            value.Available = false; value.Reason = reason; value.MaxUnits = 0; value.CurrentWaveIndex = byte.MaxValue;
            value.Floors.Clear(); value.Participants.Clear(); value.Waves.Clear(); value.Controllers.Clear();
        }
        private static void Adopt(StageModel stage)
        { if (Lifecycle.Begin(DeckGuard.Session.RoomId, stage)) InitializedFloors.Clear(); }
        // IsNormalInvitation means a generic book-value invitation in vanilla;
        // story receptions also use PrepareBattle and must be accepted here.
        internal static bool IsSupportedInvitation(StageClassInfo info, bool endContents)
        {
            if (info == null || endContents || info.stageType != StageType.Invitation) return false;
            var id = info.id;
            return id != null && id.IsBasic() && id.id > 0 &&
                !Enum.IsDefined(typeof(EndContentsStageId), id.id) && id.id != SpecialStageIds.olivier;
        }
        private static void InitializeDefaultRoster()
        {
            var floor = StageController.Instance.GetCurrentStageFloorModel(); var wave = StageController.Instance.GetCurrentWaveModel();
            if (floor == null || wave == null || !InitializedFloors.Add(floor.Sephirah)) return;
            var units = floor.GetUnitBattleDataList();
            var remaining = wave.AvailableUnitNumber;
            // This is the host's explicit native preparation transition, never a capture/getter side effect.
            // Init's added-list getter already selects one unit. Seed the full
            // first-N default once per floor, then retain all later user choices.
            foreach (var unit in units)
            {
                unit.IsAddedBattle = remaining > 0 && !unit.isDead && !unit.isLocked && !unit.unitData.IsLockUnit();
                if (unit.IsAddedBattle) remaining--;
            }
        }
        private static bool InvitationPrefix() { return !InRoom || DeckGuard.Session.IsHost; }
        private static bool PreparingPrefix()
        {
            if (!InvitationPrefix()) return false;
            EndHostPreparation("new invitation");
            return true;
        }
        private static void PreparedPostfix()
        { if (InRoom && DeckGuard.Session.IsHost) { Adopt(StageController.Instance.GetStageModel()); InitializeDefaultRoster(); DeckGuard.Session.RefreshPreparation(); } }
        private static bool ScreenChangePrefix(object __0)
        {
            // BackBattlePrepare may only open a confirmation popup. Type 4 is
            // reached after actual confirmation, before any new page renders.
            if (Convert.ToInt32(__0) == 4) EndHostPreparation("confirmed return to invitation");
            return true;
        }
        private static void EndForAcceptedPhase()
        {
            // OnClose runs after CallUIPhase accepted its new phase, but before
            // any new panel opens. A rejected navigation must keep preparation.
            var phase = Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase"));
            if (!_closing && ShouldEndForPhase(phase, NativeDeckEditor.Active, NativeEquipmentEditor.AllowsPhase(phase)))
                EndHostPreparation("UI phase " + phase);
        }
        internal static bool ShouldEndForPhase(int phase, bool editing, bool equipmentPhase)
        { return phase != 5 && !(editing && (phase == 10 || equipmentPhase)); }
        private static void EndHostPreparation(string reason)
        {
            if (!InRoom || !DeckGuard.Session.IsHost || _context == 0) return;
            var context = _context;
            Lifecycle.End(); InitializedFloors.Clear();
            if (_session != null) Close();
            Debug.Log("[RuinaCoop] Host preparation ended: context " + context + ", " + reason + ".");
            DeckGuard.Session.RefreshPreparation();
        }

        internal static bool TrySelectNativeFloor(byte floorId)
        {
            if (!InRoom || !DeckGuard.Session.IsHost || _nativeStage == null || floorId < 1 || floorId > 10) return false;
            var floor = _nativeStage.GetAvailableFloorList().Find(row => (byte)row.Sephirah == floorId);
            if (floor == null || floor.IsUnavailable()) return false;
            _nativeCall++;
            try
            {
                var panel = FindPanel("UI.UIBattleSettingPanel");
                NativeUi.Call(panel, "SetNextSephirah", NativeUi.Call(panel, "FindSephirahButton", (SephirahType)floorId));
                InitializeDefaultRoster();
                NativeUi.Call(FindPanel("UI.UILibrarianCharacterListPanel"), "SetLibrarianCharacterListPanel_Battle");
                return StageController.Instance.GetCurrentStageFloorModel().Sephirah == (SephirahType)floorId;
            }
            finally { _nativeCall--; }
        }
        internal static bool TrySetNativeParticipation(byte index, bool selected)
        {
            if (!InRoom || !DeckGuard.Session.IsHost || _nativeStage == null) return false;
            var floor = StageController.Instance.GetCurrentStageFloorModel(); var wave = StageController.Instance.GetCurrentWaveModel();
            var roster = floor == null ? null : floor.GetUnitBattleDataList();
            if (roster == null || index >= roster.Count || wave == null) return false;
            var unit = roster[index]; if (unit.isDead || unit.isLocked || unit.unitData.IsLockUnit()) return false;
            var count = roster.Count(row => row.IsAddedBattle);
            if ((selected && !unit.IsAddedBattle && count >= wave.AvailableUnitNumber && wave.AvailableUnitNumber != 1) || (!selected && unit.IsAddedBattle && count <= 1)) return false;
            if (selected && wave.AvailableUnitNumber == 1) foreach (var row in roster) row.IsAddedBattle = false;
            unit.IsAddedBattle = selected;
            _nativeCall++;
            try { NativeUi.Call(FindPanel("UI.UILibrarianCharacterListPanel"), "SetLibrarianCharacterListPanel_Battle"); }
            finally { _nativeCall--; }
            return unit.IsAddedBattle == selected;
        }

        internal static void OnSnapshot(RelaySession session, ProgressSnapshot snapshot)
        { if (_session != null && ReferenceEquals(session, _session) && !ReferenceEquals(snapshot, _displayed)) _dirty = true; }
        internal static void Tick(RelaySession session)
        {
            if (_session != null && (!ReferenceEquals(session, _session) || session == null || !session.IsActive || !session.IsReadyForDeck)) Close();
            if (session == null || !session.IsActive || !session.IsReadyForDeck || session.LatestSnapshot == null) return;
            var snapshot = session.LatestSnapshot; var prep = snapshot.Preparation;
            if (IsFailedContext(session.RoomId, prep.ContextId))
            { if (_session != null) Close(); return; }
            if (!prep.Available || prep.Phase != PreparationPhase.Editing)
            { if (_session != null) Close(); _status = prep.Reason == PreparationReason.NotCaptured ? "" : "此接待准备暂不支持：" + prep.Reason; return; }
            if (_session != null && _displayed.Preparation.ContextId != prep.ContextId)
                Close(); // Retire the old stage, renderer and input generation before reopening.
            if (_session == null)
            {
                if (!session.IsHost && session.RoomId == _hiddenRoom && prep.ContextId == _hiddenContext) return;
                if (!session.IsHost && !ControllerVisible()) return;
                _session = session; _displayed = snapshot; _wave = prep.CurrentWaveIndex; _selectedUnit = 0; _dirty = true;
                if (!session.IsHost) try { OpenGuest(); } catch (Exception exception) { Fail(exception); return; }
                else try { _controller = NativeUi.Singleton("UI.UIController"); }
                    catch (Exception exception) { Fail(exception); return; }
            }
            else if (!ReferenceEquals(_displayed, snapshot))
            {
                _displayed = snapshot; _dirty = true;
            }
            if (NativeDeckEditor.Active || !_dirty) return;
            try
            {
                if (session.IsHost)
                {
                    if (Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase")) == 5)
                    {
                        RestoreHostContext();
                        _nativeCall++;
                        try
                        {
                            NativeUi.Call(FindPanel("UI.UILibrarianCharacterListPanel"), "SetLibrarianCharacterListPanel_Battle");
                            var unit = NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUnit") as UnitDataModel;
                            if (unit != null) NativeUi.Call(FindPanel("UI.UIBattleSettingPanel"), "SetLibrarianProfileData", unit);
                            if (_wave >= snapshot.Preparation.Waves.Count) _wave = snapshot.Preparation.CurrentWaveIndex;
                            RenderEnemies(FindPanel("UI.UIEnemyCharacterListPanel"));
                        }
                        finally { _nativeCall--; }
                    }
                    BindInputs();
                    _dirty = false; return;
                }
                if (!NativeUi.IsAlive(_controller)) { Close(); return; }
                if (Convert.ToInt32(NativeUi.Get(_controller, "CurrentUIPhase")) != 5)
                    NativeUi.Call(_controller, "CallUIPhase", Enum.ToObject(GameType("UI.UIPhase"), 5));
                Render(); _dirty = false;
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static void OpenGuest()
        {
            _controller = NativeUi.Singleton("UI.UIController");
            if (!NativeUi.IsAlive(_controller)) throw new InvalidOperationException("请先进入图书馆。");
            _uiData = NativeUi.Get(_controller, "_uiData"); _previousPhase = NativeUi.Get(_controller, "CurrentUIPhase");
            foreach (var field in _uiData.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) Remember(_uiData, field.Name);
            foreach (var phase in (IEnumerable)NativeUi.Get(_controller, "_uiPhaseStack")) PhaseStack.Add(phase);
            CaptureRenderer();
            NativeUi.Set(_uiData, "stage", StageClassInfoList.Instance.GetData(_displayed.Preparation.StageId));
            NativeUi.Set(_uiData, "sephirah", (SephirahType)_displayed.Preparation.FloorId);
            NativeUi.Call(_controller, "CallUIPhase", Enum.ToObject(GameType("UI.UIPhase"), 5));
            Debug.Log("[RuinaCoop] Guest preparation opened: context " + _displayed.Preparation.ContextId +
                ", stage " + _displayed.Preparation.StageId + ", floor " + _displayed.Preparation.FloorId + ".");
        }
        private static void CaptureRenderer()
        {
            if (_rendererCaptured) return;
            var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
            foreach (var unit in (IList)NativeUi.Get(renderer, "currentDataList")) RendererData.Add(unit as UnitDataModel);
            foreach (var slot in (IList)NativeUi.Get(renderer, "characterList"))
            { var unit = NativeUi.Get(slot, "unitModel") as UnitDataModel; RendererUnits.Add(unit); RendererTextures.Add(unit == null ? -1 : unit.textureIndex); }
            _rendererCaptured = true;
        }
        private static bool ControllerVisible()
        {
            var controller = NativeUi.Singleton("UI.UIController");
            if (!NativeUi.IsAlive(controller)) return false;
            var gameObject = NativeUi.Get(controller, "gameObject") as GameObject;
            return gameObject != null && gameObject.activeInHierarchy;
        }
        internal static bool ReturnFromEditor()
        {
            if (!Active || !_session.IsActive) return false;
            NativeDeckEditor.Close();
            if (!Active) return false;
            try { if (_session.IsHost) RestoreHostContext(); NativeUi.Call(NativeUi.Singleton("UI.UIController"), "CallUIPhase", Enum.ToObject(GameType("UI.UIPhase"), 5)); _dirty = true; return true; }
            catch (Exception exception) { Fail(exception); return false; }
        }
        internal static void Resume(RelaySession session)
        {
            _hiddenContext = 0; _hiddenRoom = 0; ClearFailure();
            if (session != null && session.IsHost && session.IsActive) session.RefreshPreparation();
            Tick(session);
        }
        private static void RestoreHostContext()
        {
            if (!InRoom || !DeckGuard.Session.IsHost) return;
            var stage = StageController.Instance.GetStageModel(); var floor = StageController.Instance.GetCurrentStageFloorModel();
            if (stage == null || floor == null) return;
            var controller = _controller ?? NativeUi.Singleton("UI.UIController");
            var data = NativeUi.Get(controller, "_uiData");
            var unit = PickHostUnit(floor.GetUnitBattleDataList(), NativeUi.Get(data, "unit") as UnitDataModel);
            NativeUi.Set(data, "stage", stage.ClassInfo); NativeUi.Set(data, "sephirah", floor.Sephirah); NativeUi.Set(data, "unit", unit);
            var panel = FindPanel("UI.UIBattleSettingPanel");
            NativeUi.Set(panel, "_sephirah", floor.Sephirah);
            NativeUi.Set(panel, "currentSephirahButton", NativeUi.Call(panel, "FindSephirahButton", floor.Sephirah));
        }
        // No constructors or global state: a restored editor unit from another floor is discarded.
        internal static UnitDataModel PickHostUnit(IList<UnitBattleDataModel> roster, UnitDataModel current)
        {
            if (roster == null) return null;
            foreach (var row in roster) if (row != null && row.unitData != null && ReferenceEquals(row.unitData, current)) return current;
            foreach (var row in roster) if (row != null && row.unitData != null && row.IsAddedBattle) return row.unitData;
            foreach (var row in roster) if (row != null && row.unitData != null) return row.unitData;
            return null;
        }
        private static object FindPanel(string type)
        {
            var controller = _controller ?? NativeUi.Singleton("UI.UIController");
            foreach (var panel in (IList)NativeUi.Get(controller, "Panels")) if (panel != null && panel.GetType().FullName == type) return panel;
            throw new InvalidOperationException("Native preparation panel missing: " + type);
        }
        private static void Render()
        {
            if (!GuestView || _rendering || NativeDeckEditor.Active) return;
            _rendering = true;
            try
            {
                var prep = _displayed.Preparation; if (_selectedUnit >= prep.Participants.Count) _selectedUnit = 0;
                if (_wave >= prep.Waves.Count) _wave = prep.CurrentWaveIndex;
                NativeUi.Set(_uiData, "stage", StageClassInfoList.Instance.GetData(prep.StageId));
                if (_rosterModels != null) _rosterModels.Dispose(); _rosterModels = new NativeDeckModels(_displayed);
                NativeUi.Set(_uiData, "sephirah", (SephirahType)prep.FloorId);
                NativeUi.Set(_uiData, "unit", _rosterModels.Units[_selectedUnit]);
                RenderLibrarians(FindPanel("UI.UILibrarianCharacterListPanel")); RenderEnemies(FindPanel("UI.UIEnemyCharacterListPanel"));
                var panel = FindPanel("UI.UIBattleSettingPanel");
                // Guest OnOpen/OnUIPhaseEnter are replaced, so explicitly
                // restore the ordinary preparation layout from any prior page.
                Show(NativeUi.Get(panel, "CentralUIRoot"), true);
                Show(NativeUi.Get(panel, "anim_CenterPanel"), true);
                Show(NativeUi.Get(panel, "SephirahList"), true);
                Put(NativeUi.Get(panel, "cg_NormalFrame"), "alpha", 1f);
                Put(NativeUi.Get(panel, "cg_KeterCompleteOpenFrame"), "alpha", 0f);
                Put(panel, "_sephirah", (SephirahType)prep.FloorId);
                Text(NativeUi.Get(panel, "txt_enemyNametext"), StageNameXmlList.Instance.GetName(StageClassInfoList.Instance.GetData(prep.StageId)));
                Text(NativeUi.Get(panel, "txt_AvailableUnitNumberText"), "出战 " + prep.Participants.Count(row => row.Participating) + "/" + prep.MaxUnits);
                Text(NativeUi.Get(panel, "txt_FloorText"), "挑战楼层：" + (SephirahType)prep.FloorId);
                Text(NativeUi.Get(panel, "txt_WaveButtonText"), "敌方波次 " + (_wave + 1) + "/" + prep.Waves.Count);
                foreach (var button in (IList)NativeUi.Get(panel, "SephirahButtons"))
                {
                    var id = (byte)(SephirahType)NativeUi.Get(button, "sephirahType"); var floor = prep.Floors.Find(row => row.FloorId == id);
                    NativeUi.Call(button, "SetButtonState", Enum.ToObject(GameType("UI.UISephirahButton+ButtonState"), floor != null && floor.CanParticipate ? 0 : 1));
                    NativeUi.Call(button, "SetButtonHighlightedState", Enum.ToObject(GameType("UI.UISephirahButton+UISephirahButtonType"), id == prep.FloorId ? 1 : 0));
                }
                Show(NativeUi.Get(panel, "_editPanel"), false); Show(NativeUi.Get(panel, "waveList"), false);
                RenderProfile(NativeUi.Get(panel, "infoRightPanel"), _rosterModels.Units[_selectedUnit], _displayed.UnitDecks[_selectedUnit].Display, true);
                BindInputs();
                _status = "房主选择挑战楼层与出战名单；当前版本暂不开战。";
            }
            finally { _rendering = false; }
        }
        private static void RenderLibrarians(object panel)
        {
            if (_rosterModels == null) return;
            var list = NativeUi.Get(panel, "CharacterList"); var slots = (IList)NativeUi.Get(list, "slotList");
            if (_rosterModels.Units.Count > slots.Count) throw new InvalidOperationException("Native librarian list is too small.");
            var color = NativeUi.Call(NativeUi.Singleton("UI.UIColorManager"), "GetSephirahColor", (SephirahType)_displayed.Preparation.FloorId);
            // The central preparation buttons represent the host's floors;
            // these library buttons still refer to the guest's own save.
            foreach (var button in (IList)NativeUi.Get(panel, "SephirahSelectionButtons")) Show(button, false);
            Put(list, "isSelectableList", true); Put(list, "currentSelectedSlot", slots[_selectedUnit]);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i]; Remember(slot, "unitData"); Remember(slot, "_unitBattleData"); Put(slot, "_unitBattleData", null);
                foreach (var name in new[] { "isEmptySlot", "isToggleActive", "_isToggleSelected" }) Remember(slot, name);
                var unit = i < _rosterModels.Units.Count ? _rosterModels.Units[i] : null;
                if (unit != null && _displayed.UnitDecks[i].Display.AppearanceAvailable)
                    NativeUi.Call(NativeUi.Singleton("UI.UICharacterRenderer"), "SetCharacter", unit, RendererSlot(true, false, i), true, false);
                NativeUi.Call(slot, "SetSlot", unit, color, false); if (unit == null) continue;
                var state = _displayed.Preparation.Participants[i];
                NativeUi.Call(slot, "SetLockSlot", !state.CanParticipate);
                NativeUi.Call(slot, "SetToggle", true);
                NativeUi.Call(slot, state.Participating ? "SetYesToggleState" : "SetNoToggleState"); // No UnitBattleData is attached.
                NativeUi.Call(slot, "SetSelected", i == _selectedUnit);
                Show(NativeUi.Get(slot, "portraitImage"), _displayed.UnitDecks[i].Display.AppearanceAvailable);
            }
            Show(NativeUi.Get(panel, "ob_tutorialhighlightedFrame"), false);
        }
        private static void RenderEnemies(object panel)
        {
            // Vanilla hides this canvas on Invitation and activates it on
            // BattleSetting; replacing OnUpdatePhase must also activate it.
            var canvas = NativeUi.Get(panel, "cg");
            Put(canvas, "alpha", 1f); Put(canvas, "interactable", true); Put(canvas, "blocksRaycasts", true);
            if (_enemyModels != null) _enemyModels.Dispose(); _enemyModels = null; EnemyUnits.Clear();
            var wave = _displayed.Preparation.Waves[_wave]; var fake = new ProgressSnapshot { SelectedStageId = _displayed.Preparation.StageId, SelectedFloorId = 1 };
            var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth }; fake.Floors.Add(floor);
            foreach (var enemy in wave.Enemies)
            {
                if (enemy.Unknown) continue;
                var entry = new ProgressSnapshot.UnitDeckEntry { UnitIdentity = enemy.EnemyIdentity, BookId = enemy.BookId, BookInstanceId = -1, Fixed = true, Capacity = 64 };
                CopyDisplay(enemy.Display, entry.Display); if (enemy.CardsVisible) entry.Cards.AddRange(enemy.Cards);
                fake.UnitDecks.Add(entry); floor.Units.Add(enemy.Name);
            }
            if (fake.UnitDecks.Count > 0) _enemyModels = new NativeDeckModels(fake);
            var next = 0; foreach (var enemy in wave.Enemies) EnemyUnits.Add(enemy.Unknown ? null : _enemyModels.Units[next++]);
            var list = NativeUi.Get(panel, "CharacterList"); var slots = (IList)NativeUi.Get(list, "slotList");
            if (wave.Enemies.Count > slots.Count) throw new InvalidOperationException("Native enemy preview cannot display this wave completely.");
            var color = NativeUi.Get(NativeUi.Singleton("UI.UIColorManager"), "EnemyUIColor");
            Put(list, "isSelectableList", true); Put(list, "currentSelectedSlot", null);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i]; Remember(slot, "unitData"); Put(slot, "_unitBattleData", null);
                foreach (var name in new[] { "isEmptySlot", "isToggleActive", "_isToggleSelected" }) Remember(slot, name);
                if (i >= wave.Enemies.Count) { NativeUi.Call(slot, "SetSlot", null, color, false); continue; }
                if (wave.Enemies[i].Unknown) { Put(slot, "unitData", null); Show(slot, true); NativeUi.Call(slot, "SetUnknownSlot"); Put(slot, "isEmptySlot", false); }
                else
                {
                    if (wave.Enemies[i].Display.AppearanceAvailable)
                        NativeUi.Call(NativeUi.Singleton("UI.UICharacterRenderer"), "SetCharacter", EnemyUnits[i], RendererSlot(GuestView, true, i), true, true);
                    NativeUi.Call(slot, "SetSlot", EnemyUnits[i], color, false); Show(NativeUi.Get(slot, "portraitImage"), wave.Enemies[i].Display.AppearanceAvailable);
                }
                Show(NativeUi.Get(slot, "toggleRoot"), false);
            }
            Put(panel, "currentWave", (int)_wave); Put(panel, "currentEnemyStageinfo", StageClassInfoList.Instance.GetData(_displayed.Preparation.StageId));
            Show(NativeUi.Get(panel, "StageEnemyListObject"), _displayed.Preparation.Waves.Count > 1);
            var first = wave.Enemies.FindIndex(row => !row.Unknown);
            if (first >= 0) RenderProfile(NativeUi.Get(FindPanel("UI.UIBattleSettingPanel"), "infoLeftPanel"), EnemyUnits[first], wave.Enemies[first].Display, false);
            else NativeUi.Call(NativeUi.Get(FindPanel("UI.UIBattleSettingPanel"), "infoLeftPanel"), "SetUnKnownData");
        }
        private static void RenderProfile(object panel, UnitDataModel unit, ProgressSnapshot.UnitDisplayEntry display, bool librarian)
        {
            if (unit == null) { NativeUi.Call(panel, "SetUnKnownData"); return; }
            Put(panel, "unitdata", unit); NativeUi.Call(panel, "StopProcess");
            Text(NativeUi.Get(panel, "txt_BookName"), unit.bookItem.GetName());
            Put(NativeUi.Get(panel, "img_BookIcon"), "sprite", NativeUi.Get(unit.bookItem, "bookIcon"));
            Put(NativeUi.Get(panel, "img_BookIconGlow"), "sprite", NativeUi.Get(unit.bookItem, "bookIconGlow"));
            NativeUi.Call(NativeUi.Get(panel, "StatsInfo"), "SetData", unit);
            NativeUi.Call(NativeUi.Get(panel, "passiveSlotsPanel"), "SetStatsDataInEquipBook", unit.bookItem);
            NativeUi.Call(NativeUi.Get(panel, "equipedCardListPanel"), "SetData", unit.GetDeckCardModelAll());
            Show(NativeUi.Get(panel, "toggle_ReleaseToggle"), false); Show(NativeUi.Get(panel, "img_Unknown"), false);
            Show(NativeUi.Get(panel, "portrait"), false); // Render textures are bound below only when appearance metadata is available.
            if (display.AppearanceAvailable)
            {
                var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
                var offset = RendererSlot(GuestView, !librarian, librarian ? _selectedUnit : EnemyUnits.IndexOf(unit));
                NativeUi.Call(renderer, "SetCharacter", unit, offset, true, !librarian);
                Put(NativeUi.Get(panel, "portrait"), "texture", NativeUi.Call(renderer, "GetRenderTextureByIndexAndSize", unit.textureIndex));
                Show(NativeUi.Get(panel, "portrait"), true);
            }
            var stats = NativeUi.Get(panel, "StatsInfo");
            foreach (var name in new[] { "emotionText", "speedDiceText", "speedDiceNumText", "playpointText", "resistSlash", "resistPentrate", "resistHit", "resistBreakSlash", "resistBreakPentrate", "resistBreakHit" }) Text(NativeUi.Get(stats, name), "—");
            Text(NativeUi.Get(stats, "hpText"), display.Available ? display.MaxHp.ToString() : "—"); Text(NativeUi.Get(stats, "breakText"), display.Available ? display.Break.ToString() : "—");
            Show(NativeUi.Get(panel, "passiveSlotsPanel"), display.Available); Put(panel, "isBattlePageLock", false); Put(panel, "isEquipPageLock", false);
            var cg = NativeUi.Get(panel, "cg"); Put(cg, "alpha", 1f);
        }
        private static void CopyDisplay(ProgressSnapshot.UnitDisplayEntry source, ProgressSnapshot.UnitDisplayEntry target)
        {
            foreach (var field in typeof(ProgressSnapshot.UnitDisplayEntry).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (!field.IsInitOnly) field.SetValue(target, field.GetValue(source));
            target.PassiveIds.Clear(); target.PassiveIds.AddRange(source.PassiveIds);
        }
        internal static int RendererSlot(bool guest, bool enemy, int index)
        {
            if (index < 0 || index >= 5) throw new ArgumentOutOfRangeException("index");
            // Vanilla host librarians occupy 5..9; mirrored guest librarians
            // occupy 0..4. Enemy previews always use the other five slots.
            return (enemy ? (guest ? 5 : 0) : (guest ? 0 : 5)) + index;
        }
        private static void Remember(object target, string name)
        {
            if (target == null || Fields.Any(row => ReferenceEquals(row.Target, target) && row.Name == name)) return;
            Fields.Add(new RestoreField { Target = target, Name = name, Value = NativeUi.Get(target, name), Property = AccessTools.Field(target.GetType(), name) == null });
        }
        private static void Put(object target, string name, object value) { Remember(target, name); NativeUi.Set(target, name, value); }
        private static void Text(object target, string value) { Put(target, "text", value); }
        private static void Show(object target, bool visible)
        {
            if (!NativeUi.IsAlive(target)) return; var go = target as GameObject ?? NativeUi.Get(target, "gameObject") as GameObject;
            if (go == null) return; if (!Visibility.ContainsKey(go)) Visibility.Add(go, go.activeSelf); go.SetActive(visible);
        }

        private static bool OpenedPrefix(object __instance)
        { if (!GuestProjectionGuard) return true; Put(__instance, "IsActivated", true); NativeUi.Call(__instance, "RevealAnim"); return false; }
        private static bool ClosedPrefix(object __instance)
        {
            if (!GuestProjectionGuard)
            {
                if (InRoom && DeckGuard.Session.IsHost && _context != 0) EndForAcceptedPhase();
                return true;
            }
            NativeUi.Call(NativeUi.Get(__instance, "infoLeftPanel"), "StopProcess");
            NativeUi.Call(NativeUi.Get(__instance, "infoRightPanel"), "StopProcess"); Put(__instance, "IsActivated", false); return false;
        }
        private static bool EnteredPrefix(object __0)
        {
            if (!GuestProjectionGuard) { if (Convert.ToInt32(__0) == 5) RestoreHostContext(); return true; }
            if (Convert.ToInt32(__0) == 5) Render(); return false;
        }
        private static void EnteredPostfix(object __0) { if (!GuestProjectionGuard && Convert.ToInt32(__0) == 5) RestoreHostContext(); }
        private static bool GuestUnsafePrefix() { return !GuestView; }
        private static bool TutorialPrefix(ref bool __result) { if (!GuestView) return true; __result = false; return false; }
        private static bool FloorPrefix(object __0)
        {
            if (_nativeCall != 0 || !InRoom) return true;
            var session = DeckGuard.Session; var snapshot = _session == session ? _displayed : null;
            if (session.IsHost)
            {
                var floor = StageController.Instance.GetCurrentStageFloorModel();
                // Refreshing the already-authoritative floor is UI restoration, not a new shared selection.
                if (floor != null && floor.Sephirah == (SephirahType)NativeUi.Get(__0, "sephirahType")) return true;
            }
            if (session.IsHost && (snapshot == null || !snapshot.Preparation.Available || snapshot.Preparation.ContextId != _context))
                return true; // Native initialization precedes the first published preparation.
            if (_dirty || !ReferenceEquals(snapshot, session.LatestSnapshot)) return false;
            if (!AcceptPress(__0)) return false;
            if (snapshot != null && snapshot.Preparation.Available && snapshot.Preparation.Phase == PreparationPhase.Editing)
                session.RequestPreparationFloor(snapshot, (byte)(SephirahType)NativeUi.Get(__0, "sephirahType"));
            return false;
        }
        private static bool RosterClickPrefix(object __0)
        {
            if (_nativeCall != 0 || !InRoom) return true;
            var session = DeckGuard.Session; var snapshot = _session == session ? _displayed : null;
            if (_dirty || snapshot == null || !ReferenceEquals(snapshot, session.LatestSnapshot) || !snapshot.Preparation.Available) return false;
            if (!AcceptPress(__0)) return false;
            var unit = NativeUi.Get(__0, "unitData") as UnitDataModel;
            var index = GuestView && _rosterModels != null ? _rosterModels.Units.IndexOf(unit) : StageController.Instance.GetCurrentStageFloorModel().GetUnitBattleDataList().FindIndex(row => ReferenceEquals(row.unitData, unit));
            if (index >= 0 && index < snapshot.Preparation.Participants.Count)
                session.RequestPreparationRoster(snapshot, (byte)index, !snapshot.Preparation.Participants[index].Participating);
            return false;
        }
        private static void FloorPostfix()
        {
            if (_nativeCall != 0 || !InRoom || !DeckGuard.Session.IsHost || _nativeStage == null) return;
            InitializeDefaultRoster();
            DeckGuard.Session.RefreshPreparation();
        }
        private static bool TogglesPrefix(object __instance)
        {
            if (!InRoom) return true;
            if (GuestView) return false;
            // Vanilla defaults every list refresh to the first N units. Retain the
            // host's actual selection instead, including the initial Init defaults.
            foreach (var slot in (IList)NativeUi.Get(__instance, "currentAvailbleUnitslots"))
            {
                var battle = NativeUi.Get(slot, "_unitBattleData") as UnitBattleDataModel;
                NativeUi.Call(slot, "SetToggle", battle != null && !battle.isDead && !battle.isLocked && !battle.unitData.IsLockUnit());
                NativeUi.Call(slot, battle != null && battle.IsAddedBattle ? "SetYesToggleState" : "SetNoToggleState");
            }
            NativeUi.Call(__instance, "SetAvailibleText");
            return false;
        }
        private static bool EditPrefix()
        {
            if (!InRoom) return true;
            var session = DeckGuard.Session; var snapshot = _session == session ? _displayed : null;
            if (_dirty || snapshot == null || !ReferenceEquals(snapshot, session.LatestSnapshot) || !snapshot.Preparation.Available) return false;
            var unit = NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUnit") as UnitDataModel;
            var index = GuestView && _rosterModels != null ? _rosterModels.Units.IndexOf(unit) : StageController.Instance.GetCurrentStageFloorModel().GetUnitBattleDataList().FindIndex(row => ReferenceEquals(row.unitData, unit));
            if (index >= 0) NativeDeckEditor.TryOpen(session, snapshot, (byte)index);
            return false;
        }
        private static bool CardEditPrefix(object __instance) { return OpenEditorFromProfile(__instance, false); }
        private static bool CoreEditPrefix(object __instance) { return OpenEditorFromProfile(__instance, true); }
        private static bool OpenEditorFromProfile(object panel, bool core)
        {
            if (!InRoom) return !NativeDeckModels.IsMirrorUnit(NativeUi.Get(panel, "unitdata") as UnitDataModel);
            if (!ReferenceEquals(panel, NativeUi.Get(FindPanel("UI.UIBattleSettingPanel"), "infoRightPanel"))) return false;
            if (!AcceptPress(core ? NativeUi.Get(panel, "equipPageSelectable") : NativeUi.Get(panel, "BattlePageSelectable"))) return false;
            if (!EditPrefix() && NativeDeckEditor.Active && core) NativeEquipmentEditor.TryOpen();
            return false;
        }
        private static bool CenterPrefix() { if (!GuestView) return true; NativeUi.Call(FindPanel("UI.UIBattleSettingPanel"), "SetButtonState", false); return false; }
        private static bool ProfilePrefix(object __instance, UnitDataModel __0)
        {
            var enemyIndex = Active && !NativeDeckEditor.Active ? EnemyUnits.IndexOf(__0) : -1;
            if (__0 != null && enemyIndex >= 0)
            { RenderProfile(__instance, __0, _displayed.Preparation.Waves[_wave].Enemies[enemyIndex].Display, false); return false; }
            if (!GuestView || NativeDeckEditor.Active) return !NativeDeckModels.IsMirrorUnit(__0);
            var index = _rosterModels == null ? -1 : _rosterModels.Units.IndexOf(__0);
            if (index >= 0) RenderProfile(__instance, __0, _displayed.UnitDecks[index].Display, true);
            else { index = EnemyUnits.IndexOf(__0); if (index >= 0) RenderProfile(__instance, __0, _displayed.Preparation.Waves[_wave].Enemies[index].Display, false); else NativeUi.Call(__instance, "SetUnKnownData"); }
            return false;
        }
        private static bool LibrariansPrefix(object __instance) { if (!GuestView || NativeDeckEditor.Active) return true; if (!_rendering) RenderLibrarians(__instance); return false; }
        private static bool EnemiesPrefix(object __instance)
        {
            if (GuestProjectionGuard && _closing) return false;
            if (!Active || NativeDeckEditor.Active || Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase")) != 5) return true;
            if (!_rendering && !_dirty) RenderEnemies(__instance);
            return false;
        }
        private static bool WavePrefix(int __0)
        { if (!Active || NativeDeckEditor.Active) return true; if (_dirty) return false; if (__0 >= 0 && __0 < _displayed.Preparation.Waves.Count) { _wave = (byte)__0; _dirty = true; } return false; }
        private static bool SelectedPrefix(object __0)
        {
            if (!GuestView || NativeDeckEditor.Active) return true; if (_dirty) return false; var unit = NativeUi.Get(__0, "unitData") as UnitDataModel;
            var index = _rosterModels == null ? -1 : _rosterModels.Units.IndexOf(unit);
            if (index >= 0) { _selectedUnit = (byte)index; NativeUi.Set(_uiData, "unit", unit); _dirty = true; }
            return false;
        }
        private static bool EnemySelectedPrefix(object __0)
        {
            if (!Active || NativeDeckEditor.Active) return true;
            if (_dirty) return false;
            var slots = (IList)NativeUi.Get(NativeUi.Get(FindPanel("UI.UIEnemyCharacterListPanel"), "CharacterList"), "slotList"); var index = slots.IndexOf(__0);
            if (index >= 0 && index < EnemyUnits.Count) RenderProfile(NativeUi.Get(FindPanel("UI.UIBattleSettingPanel"), "infoLeftPanel"), EnemyUnits[index], _displayed.Preparation.Waves[_wave].Enemies[index].Display, false);
            return false;
        }
        private static bool BackPrefix() { if (!GuestView) return true; HideGuest(); return false; }
        private static bool CancelPrefix() { if (!GuestView) return true; HideGuest(); return false; }
        private static void HideGuest() { _hiddenContext = _displayed.Preparation.ContextId; _hiddenRoom = _session.RoomId; Close(); }
        internal static bool IsFailedContext(ulong room, ulong context)
        { return room != 0 && context != 0 && room == _failedRoom && context == _failedContext; }
        private static void RecordFailure(ulong room, ulong context) { _failedRoom = room; _failedContext = context; }
        private static void ClearFailure() { _failedRoom = 0; _failedContext = 0; }
        private static void Fail(Exception exception)
        {
            var session = _session; var context = _displayed == null ? 0 : _displayed.Preparation.ContextId;
            if (session != null) RecordFailure(session.RoomId, context);
            _status = "原版接待准备页暂不可用：" + exception.GetBaseException().Message + "；点击查看挑战准备可重试。";
            Debug.LogError("[RuinaCoop] Native preparation: " + exception); Close();
            if (session != null && session.IsHost && session.IsActive) session.RefreshPreparation();
        }
        internal static void Close()
        {
            if (_session == null) return; _closing = true;
            try
            {
                NativeDeckEditor.Close();
                for (var i = Fields.Count - 1; i >= 0; i--)
                { var row = Fields[i]; if (!row.Property || NativeUi.IsAlive(row.Target)) try { NativeUi.Set(row.Target, row.Name, row.Value); } catch (Exception ex) { Debug.LogWarning("[RuinaCoop] Preparation restore " + row.Name + ": " + ex.GetBaseException().Message); } }
                if (_controller != null && NativeUi.IsAlive(_controller))
                {
                    var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
                    if (_rendererCaptured && NativeUi.IsAlive(renderer))
                    {
                        NativeUi.Call(renderer, "DestroyCharacters");
                        foreach (var slot in (IList)NativeUi.Get(renderer, "characterList")) { NativeUi.Set(slot, "unitModel", null); NativeUi.Set(slot, "resName", ""); }
                        for (var i = 0; i < RendererUnits.Count; i++) if (RendererUnits[i] != null) { NativeUi.Call(renderer, "SetCharacter", RendererUnits[i], i, true, false); RendererUnits[i].textureIndex = RendererTextures[i]; }
                        var data = (IList)NativeUi.Get(renderer, "currentDataList"); data.Clear(); foreach (var unit in RendererData) data.Add(unit);
                    }
                    if (_previousPhase != null) NativeUi.Call(_controller, "CallUIPhase", _previousPhase);
                    if (_previousPhase != null)
                    { var stack = NativeUi.Get(_controller, "_uiPhaseStack"); NativeUi.Call(stack, "Clear"); for (var i = PhaseStack.Count - 1; i >= 0; i--) NativeUi.Call(stack, "Push", PhaseStack[i]); }
                }
            }
            catch (Exception exception) { Debug.LogWarning("[RuinaCoop] Preparation cleanup: " + exception.GetBaseException().Message); }
            finally
            {
                foreach (var row in Visibility) if (row.Key != null) row.Key.SetActive(row.Value);
                Fields.Clear(); Visibility.Clear(); RendererUnits.Clear(); RendererTextures.Clear(); RendererData.Clear(); PhaseStack.Clear();
                InputTargets.Clear(); Presses.Clear(); _generation++;
                if (_rosterModels != null) _rosterModels.Dispose(); if (_enemyModels != null) _enemyModels.Dispose();
                _rosterModels = null; _enemyModels = null; EnemyUnits.Clear(); _session = null; _displayed = null; _controller = null; _uiData = null; _previousPhase = null;
                _dirty = false; _rendering = false; _closing = false; _rendererCaptured = false;
            }
        }
        private static void BindInputs()
        {
            _generation++; InputTargets.Clear(); Presses.Clear();
            if (!Active || NativeDeckEditor.Active) return;
            var panel = FindPanel("UI.UIBattleSettingPanel");
            foreach (var button in (IList)NativeUi.Get(panel, "SephirahButtons")) BindInput(button, button);
            foreach (var slot in (IList)NativeUi.Get(NativeUi.Get(FindPanel("UI.UILibrarianCharacterListPanel"), "CharacterList"), "slotList"))
            { BindInput(slot, slot); BindInput(NativeUi.Get(slot, "toggleRoot"), slot); }
            var info = NativeUi.Get(panel, "infoRightPanel");
            foreach (var name in new[] { "BattlePageSelectable", "equipPageSelectable" }) { var selectable = NativeUi.Get(info, name); BindInput(selectable, selectable); }
        }
        private static void BindInput(object target, object owner)
        {
            if (!NativeUi.IsAlive(target)) return;
            var tag = new InputTag { Snapshot = _displayed, Generation = _generation, Owner = owner };
            InputTargets[target] = tag;
            var go = target as GameObject ?? NativeUi.Get(target, "gameObject") as GameObject;
            if (go == null) return;
            InputTargets[go] = tag;
            foreach (var name in new[] { "UI.UICustomSelectable", "UnityEngine.UI.Selectable", "UnityEngine.EventSystems.EventTrigger" })
            {
                var type = name.StartsWith("UI.") ? GameType(name) : FindLoadedType(name);
                foreach (var component in go.GetComponentsInChildren(type, true)) InputTargets[component] = tag;
            }
        }
        private static bool PressPrefix(object __instance)
        {
            InputTag tag; if (InputTargets.TryGetValue(__instance, out tag)) Presses[tag.Owner] = tag;
            return true;
        }
        private static bool AcceptPress(object owner)
        {
            InputTag pressed;
            if (!Presses.TryGetValue(owner, out pressed)) return false;
            Presses.Remove(owner);
            return IsCurrentInput(pressed.Snapshot, pressed.Generation, _displayed, _generation) && !_dirty;
        }
        // Kept pure for compiled lifecycle checks: a refreshed/reopened same-context UI still revokes old presses.
        internal static bool IsCurrentInput(ProgressSnapshot expected, ulong expectedGeneration, ProgressSnapshot current, ulong generation)
        { return expected != null && ReferenceEquals(expected, current) && expectedGeneration == generation && expected.Preparation.Available && expected.Preparation.Phase == PreparationPhase.Editing; }
    }
}
