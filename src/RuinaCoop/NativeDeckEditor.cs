using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LOR_DiceSystem;
using Steamworks;
using UnityEngine;

namespace RuinaCoop
{
    // The game page is a view of detached models. Every card click is a command
    // for the displayed host snapshot; vanilla model mutation remains blocked.
    internal static class NativeDeckEditor
    {
        private static RelaySession _session;
        private static NativeDeckBinding _binding;
        private static NativeDeckModels _models;
        private static object _controller;
        private static object _previousPhase;
        private static readonly Dictionary<FieldInfo, object> PreviousUi = new Dictionary<FieldInfo, object>();
        private static readonly Dictionary<FieldInfo, object> PreviousTutorial = new Dictionary<FieldInfo, object>();
        private static readonly Dictionary<GameObject, bool> PreviousVisibility = new Dictionary<GameObject, bool>();
        private static readonly List<UnitDataModel> PreviousRenderUnits = new List<UnitDataModel>();
        private static readonly List<int> PreviousTextureIndices = new List<int>();
        private static readonly List<UnitDataModel> PreviousRendererData = new List<UnitDataModel>();
        private static readonly List<object> PreviousPhaseStack = new List<object>();
        private static object _uiData;
        private static object _history;
        private static bool _closing;
        private static bool _refreshing;
        private static bool _dirty;
        private static bool _renderDirty;
        private static string _status = "";
        private static Vector2 _rosterScroll;
        internal static bool Active { get { return _binding != null && _binding.Valid && !_closing; } }
        internal static UnitDataModel Current { get { return Active ? _models.Units[_binding.UnitIndex] : null; } }
        internal static RelaySession Session { get { return Active ? _session : null; } }
        internal static ProgressSnapshot Snapshot { get { return Active ? _binding.Snapshot : null; } }
        internal static byte UnitIndex { get { return Active ? _binding.UnitIndex : byte.MaxValue; } }
        internal static bool CanEdit { get { return Active && _binding.CanEdit; } }
        internal static bool ReadyForInput { get { return CanEdit && !_dirty && _session.IsReadyForDeck && !_session.DeckRequestPending; } }
        internal static BookModel CreateDetachedBook(ProgressSnapshot.CoreBookEntry entry)
        { return _models.CreateDetachedBook(entry); }
        internal static void UpdateDetachedBook(BookModel book, ProgressSnapshot.CoreBookEntry entry)
        { _models.UpdateDetachedBook(book, entry); }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, "UI.UICardPanel", "OnUpdatePhase", new string[0], "CardPanelPrefix");
            Patch(harmony, "UI.UICardPanel", "OnOpen", new string[0], null, "CardOpenedPostfix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "OnUpdatePhase", new string[0], "RosterPrefix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "OnSetSephirah", new[] { "SephirahType" }, "RosterPrefix");
            Patch(harmony, "UI.UILibrarianCharacterListPanel", "SetLibrarianCharacterListPanel_Default", new[] { "SephirahType" }, "RosterPrefix");
            Patch(harmony, "UI.UIController", "SetSelectedUnit", new[] { "UnitDataModel" }, "SelectedUnitPrefix");
            Patch(harmony, "UI.UIController", "SetCurrentSephirah", new[] { "SephirahType" }, "SephirahPrefix");
            Patch(harmony, "UI.UIController", "CallUIPhase", new[] { "UI.UIPhase" }, "PhasePrefix");
            Patch(harmony, "UI.UIController", "CallUIPhase", new[] { "System.Int32" }, "PhasePrefix");
            Patch(harmony, "UI.UIController", "CallUIPhase_RevelAnim", new[] { "UI.UIPhase" }, "PhasePrefix");
            Patch(harmony, "UI.UIController", "CallUIPhase_FullTransitionAnim", new[] { "UI.UIPhase", "System.Action" }, "PhasePrefix");
            Patch(harmony, "UI.UIInvenCardListScroll", "SetData", new[] { "System.Collections.Generic.List`1<DiceCardItemModel>", "UnitDataModel" }, "InventoryPrefix");
            Patch(harmony, "UI.UIInvenCardSlot", "SetSlotState", new string[0], "StockStatePrefix");
            Patch(harmony, "UI.UIStoryGradeFilter", "Activate", new string[0], "GradeFilterPrefix");
            Patch(harmony, "UI.UILibrarianEquipDeckPanel", "SetData", new string[0], null, "DeckPanelPostfix");
            Patch(harmony, "UI.UILibrarianEquipDeckPanel", "SetDeckButton", new string[0], null, "DeckPanelPostfix");
            Patch(harmony, "UI.UILibrarianEquipDeckPanel", "IsLockBattleBluePrimary", new string[0], "FalseInNativePrefix");
            Patch(harmony, "UI.UILibrarianInfoInCardPhase", "SetData", new[] { "UnitDataModel" }, null, "ProfilePostfix");
            Patch(harmony, "UI.UILibrarianInfoInCardPhase", "CheckDisabledBluePrimary", new string[0], "UnsafePrefix");
            Patch(harmony, "UnitDataModel", "IsLockUnit", new string[0], "MirrorUnlockedPrefix");
            Patch(harmony, "UnitDataModel", "isLockUnitForBluePrimary", new string[0], "MirrorUnlockedPrefix");
            Patch(harmony, "BookModel", "TryGainUniquePassive", new string[0], "MirrorPassivePrefix");
            Patch(harmony, "UI.UIMainTutorialManager", "StartBattlePageTutorial", new string[0], "UnsafePrefix");
            foreach (var name in new[] { "OnClickSaveDeckButton", "OnClickOpenDeckListButton", "OnClickClearDeckButton" })
                Patch(harmony, "UI.UILibrarianEquipDeckPanel", name, new string[0], "UnsafePrefix");
            Patch(harmony, "UI.UIInvenCardSlot", "OnClickCardEquipInfoButton", new string[0], "UnsafePrefix");
            Patch(harmony, "UI.UICardEquipInfoPanel", "OpenCardEquipInfo", new[] { "DiceCardItemModel", "System.Boolean" }, "UnsafePrefix");
            Patch(harmony, "UI.UILibrarianInfoInCardPhase", "OnClickReleaseToggle", new string[0], "UnsafePrefix");
            Patch(harmony, "UI.UILibrarianInfoInCardPhase", "OnPointerClickPassiveSlot", new[] { "UnityEngine.EventSystems.BaseEventData" }, "PassiveClickPrefix");
            Patch(harmony, "UI.UILibrarianInfoInCardPhase", "OnPointerClickEquipPage", new[] { "UnityEngine.EventSystems.BaseEventData" }, "CorePageClickPrefix");
        }

        private static void Patch(Harmony harmony, string typeName, string methodName, string[] parameters,
            string prefixName, string postfixName = null)
        {
            var type = typeof(UnitDataModel).Assembly.GetType(typeName, true);
            MethodInfo original = null;
            foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (candidate.Name != methodName) continue;
                var args = candidate.GetParameters();
                if (args.Length != parameters.Length) continue;
                var match = true;
                for (var i = 0; i < args.Length; i++) if (TypeName(args[i].ParameterType) != parameters[i]) match = false;
                if (!match) continue;
                if (original != null) throw new AmbiguousMatchException(typeName + "." + methodName);
                original = candidate;
            }
            if (original == null) throw new MissingMethodException(typeName, methodName);
            harmony.Patch(original,
                prefix: prefixName == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeDeckEditor), prefixName)),
                postfix: postfixName == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeDeckEditor), postfixName)));
        }

        private static string TypeName(Type type)
        {
            if (!type.IsGenericType) return type.FullName;
            var args = type.GetGenericArguments();
            var names = new string[args.Length];
            for (var i = 0; i < args.Length; i++) names[i] = TypeName(args[i]);
            return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", names) + ">";
        }

        internal static void Tick(RelaySession session)
        {
            if (_binding == null) return;
            var oldSnapshot = _binding.Snapshot;
            if (!ReferenceEquals(session, _session) || session == null || !session.IsReadyForDeck ||
                !ControllerVisible() ||
                session.PreparationFrozen || !AcceptSnapshot(session.RoomId, session.LatestSnapshot))
            { Close(); return; }
            if (!ReferenceEquals(oldSnapshot, _binding.Snapshot))
            { _dirty = true; _renderDirty |= !SameAppearance(oldSnapshot, _binding.Snapshot); }
            NativePassiveEditor.Tick();
            if (!_dirty) return;
            try
            {
                _models.Update(_binding.Snapshot);
                Refresh();
                _dirty = false;
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static bool ControllerVisible()
        {
            if (_controller == null || (_controller is UnityEngine.Object && (UnityEngine.Object)_controller == null)) return false;
            var gameObject = NativeUi.Get(_controller, "gameObject") as GameObject;
            return gameObject != null && gameObject.activeInHierarchy;
        }

        internal static void OnSnapshot(RelaySession session, ProgressSnapshot snapshot)
        {
            if (_binding == null || !ReferenceEquals(session, _session)) return;
            var oldSnapshot = _binding.Snapshot;
            if (!AcceptSnapshot(session.RoomId, snapshot)) { Close(); return; }
            _dirty = true;
            _renderDirty |= !SameAppearance(oldSnapshot, _binding.Snapshot);
        }

        private static bool AcceptSnapshot(ulong roomId, ProgressSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Sequence > _binding.Snapshot.Sequence &&
                _binding.UnitIndex < snapshot.UnitDecks.Count &&
                _binding.TryRebindCorePage(roomId, snapshot, _binding.Snapshot.UnitDecks[_binding.UnitIndex].UnitIdentity))
                return true;
            return _binding.TryUpdate(roomId, snapshot);
        }

        private static bool SameAppearance(ProgressSnapshot left, ProgressSnapshot right)
        {
            if (left == null || right == null || left.UnitDecks.Count != right.UnitDecks.Count) return false;
            for (var i = 0; i < left.UnitDecks.Count; i++)
            {
                var l = left.UnitDecks[i];
                var r = right.UnitDecks[i];
                var a = l.Display;
                var b = r.Display;
                if (l.BookId != r.BookId || a.AppearanceAvailable != b.AppearanceAvailable ||
                    a.DefaultBookId != b.DefaultBookId || a.CustomBookId != b.CustomBookId ||
                    a.IsSephirah != b.IsSephirah || a.Gender != b.Gender || a.AppearanceType != b.AppearanceType ||
                    !string.Equals(a.CharacterSkin, b.CharacterSkin, StringComparison.Ordinal) ||
                    a.UseCustom != b.UseCustom || a.SpecialCustomId != b.SpecialCustomId ||
                    a.FrontHair != b.FrontHair || a.BackHair != b.BackHair || a.Eye != b.Eye ||
                    a.Brow != b.Brow || a.Mouth != b.Mouth || a.Head != b.Head ||
                    a.HairColor != b.HairColor || a.EyeColor != b.EyeColor || a.SkinColor != b.SkinColor || a.Height != b.Height)
                    return false;
            }
            return true;
        }

        private static void Changed() { _dirty = true; }

        internal static void DrawSessionControls(RelaySession session)
        {
            if (session == null || !session.IsActive) return;
            var snapshot = Active ? _binding.Snapshot : session.LatestSnapshot;
            if (snapshot == null || snapshot.SelectedFloorId == PrepClaims.NoFloor || snapshot.UnitDecks.Count == 0) return;
            var floor = snapshot.Floors.Find(entry => (byte)entry.Sephirah == snapshot.SelectedFloorId);
            if (floor == null) return;
            var previousEnabled = GUI.enabled;
            GUILayout.BeginArea(new Rect(Math.Max(0, Screen.width - 370), 15, 355, Math.Min(Screen.height - 25, 430)), GUI.skin.box);
            try
            {
                GUILayout.Label("联机准备 · " + floor.Sephirah);
                GUILayout.Label(Active ? (_binding.CanEdit ? "原版配装编辑 · 房主权威" : "原版配装查看 · 只读") : "选择馆员进入原版卡组页");
                if (session.DeckRequestPending) GUILayout.Label("等待房主确认与卡组快照…");
                if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
                if (Active) GUILayout.Label(session.DeckStatus);
                if (Active && !string.IsNullOrEmpty(NativeEquipmentEditor.Status)) GUILayout.Label(NativeEquipmentEditor.Status);
                if (Active && !string.IsNullOrEmpty(NativePassiveEditor.Status)) GUILayout.Label(NativePassiveEditor.Status);
                _rosterScroll = GUILayout.BeginScrollView(_rosterScroll, GUILayout.Height(210));
                for (byte i = 0; i < snapshot.UnitDecks.Count; i++)
                {
                    var owner = snapshot.ClaimOwners[i];
                    var mine = owner == SteamClient.SteamId.Value;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(floor.Units[i] + (mine ? " · 我" : owner == 0 ? " · 未认领" : " · 已认领"), GUILayout.Width(155));
                    GUI.enabled = session.IsReadyForDeck && !session.PreparationFrozen && !NativePassiveEditor.Active;
                    if (GUILayout.Button(mine ? "编辑角色卡组" : "查看卡组", GUILayout.Width(105)))
                    {
                        if (Active) Select(i); else Open(session, snapshot, i);
                    }
                    GUI.enabled = session.IsReadyForDeck && !session.DeckRequestPending && !snapshot.DecksFrozen && !NativePassiveEditor.Active && (mine || owner == 0);
                    if (GUILayout.Button(mine ? "释放" : "认领", GUILayout.Width(55)))
                        session.RequestClaim(snapshot, i, mine ? ClaimAction.Release : ClaimAction.Claim);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                GUI.enabled = !NativePassiveEditor.Active;
                if (Active && GUILayout.Button("查看/更换核心书页")) NativeEquipmentEditor.TryOpen();
                if (Active && GUILayout.Button("查看/编辑被动")) NativePassiveEditor.TryOpen();
                GUI.enabled = true;
                if (Active && GUILayout.Button("返回准备")) Close();
            }
            finally { GUI.enabled = previousEnabled; GUILayout.EndArea(); }
        }

        private static void Open(RelaySession session, ProgressSnapshot snapshot, byte index)
        {
            if (!session.IsReadyForDeck || session.PreparationFrozen) return;
            try
            {
                _controller = NativeUi.Singleton("UI.UIController");
                if (!ControllerVisible())
                { _status = "先继续游戏，进入图书馆后再打开联机卡组。"; return; }
                NativeDeckBinding binding;
                if (!NativeDeckBinding.TryCreate(session.RoomId, snapshot, index, SteamClient.SteamId.Value, out binding))
                { _status = "房主尚未选择接待与楼层。"; return; }
                _models = new NativeDeckModels(snapshot);
                _session = session;
                _binding = binding;
                _uiData = NativeUi.Get(_controller, "_uiData");
                PreviousUi.Clear();
                foreach (var field in _uiData.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    PreviousUi[field] = field.GetValue(_uiData);
                _previousPhase = NativeUi.Get(_controller, "CurrentUIPhase");
                PreviousPhaseStack.Clear();
                foreach (var phase in (IEnumerable)NativeUi.Get(_controller, "_uiPhaseStack")) PreviousPhaseStack.Add(phase);
                CaptureRender();
                _history = LibraryModel.Instance.PlayHistory;
                PreviousTutorial.Clear();
                foreach (var name in new[] { "tutorial_EnterBattlePagePanel", "tutorial_SelectLibrarianSlot" })
                {
                    var field = AccessTools.Field(_history.GetType(), name);
                    if (field == null) throw new MissingFieldException(_history.GetType().Name, name);
                    PreviousTutorial[field] = field.GetValue(_history);
                }
                session.DeckStateChanged += Changed;
                NativeUi.Set(_uiData, "sephirah", (SephirahType)snapshot.SelectedFloorId);
                NativeUi.Set(_uiData, "unit", Current);
                _renderDirty = true;
                _status = "卡组和普通核心书更换由房主确认；预设与被动继承暂不可修改。";
                NativeUi.Call(_controller, "CallUIPhase", Enum.ToObject(_previousPhase.GetType(), 10));
                Refresh();
                _dirty = false;
                RestoreTutorial();
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void Select(byte index)
        {
            if (!Active || NativePassiveEditor.Active || !_binding.TrySelectUnit(index)) return;
            NativeUi.Set(_uiData, "unit", Current);
            _dirty = true;
        }

        internal static bool TryHandleDeckUi(object instance, MethodBase method, object[] args)
        {
            if (!Active || method == null || method.DeclaringType.FullName != "UI.UIEquipDeckCardList") return false;
            var name = method.Name;
            if (name != "OnClickCardSlotByInven" && name != "OnClickCardSlotByDeck" &&
                name != "InsertCardSlot" && name != "RemoveCardSlot") return false;
            try
            {
                if (NativePassiveEditor.Active || _dirty || !ReferenceEquals(NativeUi.Get(instance, "currentunit"), Current) ||
                    args == null || args.Length != 1 || args[0] == null) return true;
                var card = NativeUi.Get(args[0], "CardModel") as DiceCardItemModel;
                if (card == null || !card.GetID().IsBasic()) return true;
                var action = name == "OnClickCardSlotByInven" || name == "InsertCardSlot" ? DeckAction.Add : DeckAction.Remove;
                var token = _binding.CaptureEvent();
                if (!_session.IsReadyForDeck || _session.DeckRequestPending || !_binding.CanSubmit(token, card.GetID().id, action))
                { _status = "此馆员当前只读、卡牌受限，或上一项操作尚未确认。"; return true; }
                _session.RequestDeckEdit(token.Snapshot, token.UnitIndex, card.GetID().id, action);
                _dirty = true;
            }
            catch (Exception exception) { Fail(exception); }
            return true;
        }

        private static void Refresh()
        {
            if (!Active || _refreshing) return;
            _refreshing = true;
            try
            {
                NativeUi.Set(_uiData, "unit", Current);
                if (NativeEquipmentEditor.Refresh())
                {
                    if (Active) RenderRoster(FindPanel("UI.UILibrarianCharacterListPanel"));
                    RestoreTutorial();
                    return;
                }
                var panel = CardPanel();
                CardPanelPrefix(panel);
                RestoreTutorial();
            }
            finally { _refreshing = false; }
        }

        private static object CardPanel() { return FindPanel("UI.UICardPanel"); }

        internal static object FindPanel(string typeName)
        {
            foreach (var panel in (IList)NativeUi.Get(_controller, "Panels"))
                if (panel != null && panel.GetType().FullName == typeName) return panel;
            throw new InvalidOperationException("Native page panel is unavailable: " + typeName);
        }

        private static bool CardPanelPrefix(object __instance)
        {
            if (!Active) return true;
            RenderRoster(FindPanel("UI.UILibrarianCharacterListPanel"));
            NativeUi.Call(NativeUi.Get(__instance, "_equipInfoDeckPanel"), "SetData");
            var inventory = NativeUi.Get(__instance, "_invenCardList");
            NativeUi.Set(inventory, "_unitdata", Current);
            var cards = NativeUi.Get(inventory, "_originCardList") as List<DiceCardItemModel>;
            cards.Clear(); cards.AddRange(_models.Stock);
            var scrollWindow = NativeUi.Get(NativeUi.Get(inventory, "scrollBar"), "scrollWindow");
            var position = (Vector2)NativeUi.Get(scrollWindow, "anchoredPosition");
            NativeUi.Call(inventory, "ApplyFilterAll");
            var row = (int)NativeUi.Get(inventory, "curRow");
            var slotHeight = (float)NativeUi.Get(inventory, "slotHeight");
            NativeUi.Call(NativeUi.Get(inventory, "scrollBar"), "SetWindowPosition", position.x, Math.Min(position.y, row * slotHeight + slotHeight - 1));
            NativeUi.Call(NativeUi.Get(__instance, "librarianInfoPanel"), "SetData", Current);
            NativeUi.Call(NativeUi.Get(__instance, "CardEquipInfoPanel"), "CloseCardEquipInfo");
            NativeUi.Set(NativeUi.Get(NativeUi.Get(__instance, "_equipInfoDeckPanel"), "_equipDeckPanel"), "changed", false);
            return false;
        }

        private static bool InventoryPrefix(ref List<DiceCardItemModel> __0, ref UnitDataModel __1)
        { if (Active) { __0 = _models.Stock; __1 = Current; } return true; }

        private static bool RosterPrefix(object __instance)
        { if (!Active) return true; RenderRoster(__instance); return false; }

        private static void RenderRoster(object panel)
        {
            if (!Active) return;
            var list = NativeUi.Get(panel, "CharacterList");
            var slots = (IList)NativeUi.Get(list, "slotList");
            var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
            var color = NativeUi.Call(NativeUi.Singleton("UI.UIColorManager"), "GetSephirahColor", (SephirahType)_binding.Snapshot.SelectedFloorId);
            NativeUi.Set(list, "isSelectableList", true);
            NativeUi.Set(list, "currentSelectedSlot", slots[_binding.UnitIndex]);
            for (var i = 0; i < slots.Count; i++)
            {
                var unit = i < _models.Units.Count ? _models.Units[i] : null;
                if (unit != null && _renderDirty && _binding.Snapshot.UnitDecks[i].Display.AppearanceAvailable)
                    NativeUi.Call(renderer, "SetCharacter", unit, i, true, false);
                NativeUi.Call(slots[i], "SetSlot", unit, color, false);
                if (unit == null) continue;
                Hide(NativeUi.Get(slots[i], "toggleRoot"));
                Visibility(NativeUi.Get(slots[i], "portraitImage"), _binding.Snapshot.UnitDecks[i].Display.AppearanceAvailable);
                NativeUi.Call(slots[i], "SetSelected", i == _binding.UnitIndex);
            }
            _renderDirty = false;
            foreach (var button in (IList)NativeUi.Get(panel, "SephirahSelectionButtons")) Hide(button);
            Hide(NativeUi.Get(panel, "ob_tutorialhighlightedFrame"));
            // UpdateFrameToSephirah also initializes floor buttons from the local
            // Library and emits a floor-change event. Only its coloring is needed.
            NativeUi.Call(panel, "SetColor", color);
        }

        private static bool SelectedUnitPrefix(UnitDataModel __0, ref bool __result)
        {
            if (!Active) return true;
            var index = _models.Units.IndexOf(__0);
            __result = index >= 0 && _binding.TrySelectUnit((byte)index);
            if (__result) { NativeUi.Set(_uiData, "unit", Current); _dirty = true; }
            return false;
        }

        private static bool SephirahPrefix(ref bool __result) { if (!Active) return true; __result = false; return false; }
        private static bool PhasePrefix(object[] __args)
        {
            if (Active && Convert.ToInt32(__args[0]) != 10 && !NativeEquipmentEditor.AllowsPhase(Convert.ToInt32(__args[0]))) Close();
            return true;
        }
        private static bool CorePageClickPrefix()
        {
            if (!Active) return true;
            NativeEquipmentEditor.TryOpen();
            return false;
        }
        private static bool PassiveClickPrefix()
        {
            if (!Active) return true;
            NativePassiveEditor.TryOpen();
            return false;
        }
        private static bool UnsafePrefix() { return !Active; }
        private static bool FalseInNativePrefix(ref bool __result) { if (!Active) return true; __result = false; return false; }
        private static bool MirrorUnlockedPrefix(UnitDataModel __instance, ref bool __result)
        { if (!NativeDeckModels.IsMirrorUnit(__instance)) return true; __result = false; return false; }
        private static bool MirrorPassivePrefix(BookModel __instance, ref bool __result)
        {
            if (!NativeDeckModels.IsConstructingMirrors && !NativeDeckModels.IsMirrorBook(__instance)) return true;
            __result = false;
            return false;
        }
        private static void CardOpenedPostfix() { if (Active) RestoreTutorial(); }

        private static void DeckPanelPostfix(object __instance)
        {
            if (!Active) return;
            foreach (var name in new[] { "button_SaveDeckButton", "button_OpenDeckListButton", "button_EmptyDeckButton", "button_CloseDeckButton" })
                Hide(NativeUi.Get(__instance, name));
        }

        private static void ProfilePostfix(object __instance)
        {
            if (!Active) return;
            foreach (var name in new[] { "toggle_ReleaseToggle", "toggle_ReleaseToggle_Controller" })
                Hide(NativeUi.Get(__instance, name));
            var display = _binding.Snapshot.UnitDecks[_binding.UnitIndex].Display;
            Visibility(NativeUi.Get(__instance, "portrait"), display.AppearanceAvailable);
            Visibility(NativeUi.Get(__instance, "passiveSlotsPanel"), display.Available);
            NativeUi.Set(__instance, "isDisabledPassiveSuccession", false);
            var stats = NativeUi.Get(__instance, "StatsInfo");
            foreach (var name in new[] { "emotionText", "speedDiceText", "speedDiceNumText", "playpointText", "resistSlash", "resistPentrate", "resistHit", "resistBreakSlash", "resistBreakPentrate", "resistBreakHit" })
                NativeUi.Set(NativeUi.Get(stats, name), "text", "—");
            NativeUi.Set(NativeUi.Get(stats, "hpText"), "text", display.Available ? display.MaxHp.ToString() : "—");
            NativeUi.Set(NativeUi.Get(stats, "breakText"), "text", display.Available ? display.Break.ToString() : "—");
        }

        private static bool StockStatePrefix(object __instance)
        {
            if (!Active) return true;
            var card = NativeUi.Get(__instance, "CardModel") as DiceCardItemModel;
            if (card == null) return false;
            var state = 0;
            var reason = "";
            var deck = _binding.Snapshot.UnitDecks[_binding.UnitIndex];
            var id = card.GetID().id;
            var basic = card.ClassInfo.optionList.Contains(CardOption.Basic);
            if (!basic && card.num <= 0) { state = 3; reason = "房主库存不足"; }
            if (deck.Cards.FindAll(value => value == id).Count >= card.GetLimit() || deck.Cards.Count >= deck.Capacity)
            { state = 1; reason = "卡组数量限制"; }
            var floorOwners = 0;
            foreach (var unit in _binding.Snapshot.UnitDecks) if (unit.Cards.Contains(id)) floorOwners++;
            if (floorOwners >= card.ClassInfo.FloorLimit) { state = 2; reason = "楼层装备数量限制"; }
            var book = Current.bookItem;
            if (card.ClassInfo.optionList.Contains(CardOption.OnlyPage) && !book.GetOnlyCards().Exists(xml => xml.id == card.GetID()))
            { state = 4; reason = "专属书页限制"; }
            else if (book.ClassInfo.RangeType == EquipRangeType.Melee && card.GetSpec().Ranged == CardRange.Far)
            { state = 5; reason = "核心书页不支持远程牌"; }
            else if (book.ClassInfo.RangeType == EquipRangeType.Range && card.GetSpec().Ranged == CardRange.Near)
            { state = 6; reason = "核心书页不支持近战牌"; }
            if (!_binding.CanEdit || !_session.IsReadyForDeck || _session.DeckRequestPending)
            { state = 3; reason = _session.DeckRequestPending ? "等待确认" : "只读"; }
            NativeUi.Set(__instance, "slotState", state);
            NativeUi.Active(NativeUi.Get(__instance, "deckLimitRoot"), state != 0);
            NativeUi.Set(NativeUi.Get(__instance, "txt_deckLimit"), "text", reason);
            NativeUi.Call(__instance, "SetGrayScale", state != 0);
            NativeUi.Call(__instance, "RefreshNumbersData");
            return false;
        }

        private static bool GradeFilterPrefix(object __instance)
        {
            if (!Active) return true;
            var canvas = NativeUi.Get(__instance, "canvasGroup");
            NativeUi.Set(canvas, "alpha", 1f);
            NativeUi.Set(canvas, "interactable", true);
            NativeUi.Set(canvas, "blocksRaycasts", true);
            var slots = (IList)NativeUi.Get(__instance, "gradeSlots");
            for (var i = 0; i < slots.Count; i++) Visibility(slots[i], i < _binding.Snapshot.Chapter);
            return false;
        }

        private static void CaptureRender()
        {
            PreviousRenderUnits.Clear(); PreviousTextureIndices.Clear();
            var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
            PreviousRendererData.Clear();
            foreach (var unit in (IList)NativeUi.Get(renderer, "currentDataList")) PreviousRendererData.Add(unit as UnitDataModel);
            foreach (var slot in (IList)NativeUi.Get(renderer, "characterList"))
            {
                var unit = NativeUi.Get(slot, "unitModel") as UnitDataModel;
                PreviousRenderUnits.Add(unit);
                PreviousTextureIndices.Add(unit == null ? -1 : unit.textureIndex);
            }
        }

        private static void Hide(object component)
        { Visibility(component, false); }

        internal static void Visibility(object component, bool visible)
        {
            if (component == null) return;
            var gameObject = component as GameObject ?? NativeUi.Get(component, "gameObject") as GameObject;
            if (!PreviousVisibility.ContainsKey(gameObject)) PreviousVisibility.Add(gameObject, gameObject.activeSelf);
            gameObject.SetActive(visible);
        }

        private static void RestoreTutorial()
        { if (_history != null) foreach (var pair in PreviousTutorial) pair.Key.SetValue(_history, pair.Value); }

        private static void Fail(Exception exception)
        {
            _status = "原版联机卡组页暂不可用：" + exception.GetBaseException().Message;
            Debug.LogError("[RuinaCoop] Native deck page failed: " + exception);
            Close();
        }

        internal static void Close()
        {
            if (_binding == null && _models == null) return;
            _closing = true;
            try
            {
                if (_session != null) _session.DeckStateChanged -= Changed;
                NativePassiveEditor.Close();
                NativeEquipmentEditor.Close();
                // A vanilla OnClose may save the host library. Restore these
                // flags before triggering any original panel transition.
                RestoreTutorial();
                foreach (var pair in PreviousUi) if (_uiData != null) pair.Key.SetValue(_uiData, pair.Value);
                // On application shutdown Unity may have already destroyed the UI.
                // Managed state and mirror ownership still retire in finally.
                if (!NativeUi.IsAlive(_controller)) return;
                // Hidden native panels retain their unit fields between phases.
                // Rebind them before retiring the mirror registry, even when the
                // user's previous page did not include the card panel.
                if (_uiData != null)
                {
                    var localUnit = NativeUi.Get(_uiData, "unit") as UnitDataModel;
                    var cardPanel = CardPanel();
                    var equip = NativeUi.Get(cardPanel, "_equipInfoDeckPanel");
                    NativeUi.Set(equip, "_unitdata", localUnit);
                    NativeUi.Set(NativeUi.Get(equip, "_equipDeckPanel"), "currentunit", localUnit);
                    NativeUi.Set(NativeUi.Get(cardPanel, "_invenCardList"), "_unitdata", localUnit);
                    NativeUi.Set(NativeUi.Get(cardPanel, "librarianInfoPanel"), "unitdata", localUnit);
                    if (localUnit != null && NativeUi.IsAlive(cardPanel)) NativeUi.Call(cardPanel, "OnUpdatePhase");
                    else
                    {
                        ((IList)NativeUi.Get(NativeUi.Get(cardPanel, "_invenCardList"), "_originCardList")).Clear();
                        ((IList)NativeUi.Get(NativeUi.Get(cardPanel, "_invenCardList"), "_currentCardListForFilter")).Clear();
                    }
                }
                var renderer = NativeUi.Singleton("UI.UICharacterRenderer");
                // DestroyCharacters releases the asset references/cameras. It does
                // not clear unitModel/resName, so explicitly clear empty slots too.
                if (NativeUi.IsAlive(renderer))
                {
                    NativeUi.Call(renderer, "DestroyCharacters");
                    foreach (var slot in (IList)NativeUi.Get(renderer, "characterList"))
                    {
                        NativeUi.Set(slot, "unitModel", null);
                        NativeUi.Set(slot, "resName", "");
                    }
                    for (var i = 0; i < PreviousRenderUnits.Count; i++)
                    {
                        var unit = PreviousRenderUnits[i];
                        if (unit == null) continue;
                        NativeUi.Call(renderer, "SetCharacter", unit, i, true, false);
                        unit.textureIndex = PreviousTextureIndices[i];
                    }
                }
                if (_controller != null && _previousPhase != null) NativeUi.Call(_controller, "CallUIPhase", _previousPhase);
                if (NativeUi.IsAlive(renderer))
                {
                    var cachedUnits = (IList)NativeUi.Get(renderer, "currentDataList");
                    cachedUnits.Clear();
                    foreach (var unit in PreviousRendererData) cachedUnits.Add(unit);
                }
                var phaseStack = NativeUi.Get(_controller, "_uiPhaseStack");
                NativeUi.Call(phaseStack, "Clear");
                for (var i = PreviousPhaseStack.Count - 1; i >= 0; i--) NativeUi.Call(phaseStack, "Push", PreviousPhaseStack[i]);
            }
            catch (Exception exception) { Debug.LogError("[RuinaCoop] Native UI restoration failed: " + exception); }
            finally
            {
                RestoreTutorial();
                foreach (var pair in PreviousVisibility) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                PreviousVisibility.Clear(); PreviousUi.Clear(); PreviousTutorial.Clear();
                PreviousRenderUnits.Clear(); PreviousTextureIndices.Clear();
                PreviousRendererData.Clear(); PreviousPhaseStack.Clear();
                if (_binding != null) _binding.Close();
                if (_models != null) _models.Dispose();
                _models = null; _binding = null; _session = null; _uiData = null; _history = null;
                _controller = null; _previousPhase = null; _dirty = false; _renderDirty = false;
                _closing = false;
            }
        }
    }
}
