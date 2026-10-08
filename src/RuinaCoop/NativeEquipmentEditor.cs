using System;
using System.Collections.Generic;
#if !EQUIPMENT_PURE_TESTS
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
#endif

namespace RuinaCoop
{
    // A rendered button may only submit for its exact displayed role and book.
    internal struct CorePageClickToken
    {
        internal readonly ProgressSnapshot Snapshot;
        internal readonly byte UnitIndex;
        internal readonly ulong UnitIdentity;
        internal readonly ulong OldBookToken;
        internal readonly ulong TargetBookToken;
        internal readonly uint ClaimRevision;
        internal readonly uint DeckRevision;
        internal readonly ulong Generation;

        internal CorePageClickToken(ProgressSnapshot snapshot, byte unitIndex, ulong target, ulong generation)
        {
            Snapshot = snapshot;
            UnitIndex = unitIndex;
            TargetBookToken = target;
            UnitIdentity = snapshot.UnitDecks[unitIndex].UnitIdentity;
            OldBookToken = snapshot.UnitDecks[unitIndex].BookToken;
            ClaimRevision = snapshot.ClaimRevision;
            DeckRevision = snapshot.DeckRevision;
            Generation = generation;
        }

        internal bool CanSubmit(ProgressSnapshot current, byte unitIndex, ulong sender,
            bool active, bool ready, bool pending, ulong generation)
        {
            if (!active || !ready || pending || generation == 0 || generation != Generation ||
                current == null || !ReferenceEquals(current, Snapshot) ||
                unitIndex != UnitIndex || unitIndex >= current.UnitDecks.Count ||
                current.UnitDecks[unitIndex].UnitIdentity != UnitIdentity ||
                current.UnitDecks[unitIndex].BookToken != OldBookToken ||
                current.ClaimRevision != ClaimRevision || current.DeckRevision != DeckRevision) return false;
            return EquipmentAuthority.Validate(current, sender, new CorePageRequest
            {
                RequestId = 1, StageId = current.SelectedStageId, FloorId = current.SelectedFloorId,
                UnitIndex = UnitIndex, UnitIdentity = UnitIdentity, OldBookToken = OldBookToken,
                TargetBookToken = TargetBookToken, ClaimRevision = ClaimRevision, DeckRevision = DeckRevision
            }) == CorePageResultCode.Accepted;
        }
    }

    internal sealed class CorePagePressState
    {
        private readonly Dictionary<object, CorePageClickToken> _presses = new Dictionary<object, CorePageClickToken>();
        internal void Capture(object button, CorePageClickToken token)
        { if (button != null) _presses[button] = token; }
        internal void Clear() { _presses.Clear(); }
        internal bool TryConsume(object button, ProgressSnapshot current, byte unitIndex, ulong sender,
            bool active, bool ready, bool pending, ulong generation, ulong target, out CorePageClickToken token)
        {
            token = default(CorePageClickToken);
            CorePageClickToken pressed;
            if (button == null || !_presses.TryGetValue(button, out pressed)) return false;
            _presses.Remove(button);
            if (pressed.TargetBookToken != target || !pressed.CanSubmit(current, unitIndex, sender, active, ready, pending, generation))
                return false;
            token = pressed;
            return true;
        }
    }

    internal static class CorePageUiAccess
    {
        // Rendering uses current ownership/readiness, while click submission also
        // requires the unchanged press token and the editor's dirty-state guard.
        internal static string ReadOnlyReason(ProgressSnapshot snapshot, byte unitIndex,
            ProgressSnapshot.CoreBookEntry entry, ulong sender, bool canEdit, bool ready, bool pending, bool bound)
        {
            if (!bound || snapshot == null || entry == null || unitIndex >= snapshot.UnitDecks.Count ||
                unitIndex >= snapshot.ClaimOwners.Count) return "界面绑定已失效";
            if (snapshot.ClaimOwners[unitIndex] == 0) return "馆员尚未认领";
            if (snapshot.ClaimOwners[unitIndex] != sender) return "其他玩家的馆员";
            if (snapshot.DecksFrozen) return "战斗准备已锁定";
            var deck = snapshot.UnitDecks[unitIndex];
            if (deck.Fixed) return "当前是固定牌组";
            if (deck.MultiDeck) return "当前是多牌组";
            if (!canEdit) return "馆员当前不可编辑";
            if (!ready) return "联机尚未就绪";
            if (pending) return "等待房主确认";
            if ((entry.Flags & CoreBookFlags.Equipped) != 0) return "该书页已装备";
            if (entry.Kind == CoreBookKind.Default) return "默认书页不可选";
            if (entry.Kind == CoreBookKind.Special) return "特殊书页只读";
            var targetReason = FlagReason(entry.Flags);
            if (targetReason != null) return targetReason;
            var result = EquipmentAuthority.Validate(snapshot, sender, new CorePageRequest
            {
                RequestId = 1, StageId = snapshot.SelectedStageId, FloorId = snapshot.SelectedFloorId,
                UnitIndex = unitIndex, UnitIdentity = deck.UnitIdentity, OldBookToken = deck.BookToken,
                TargetBookToken = entry.BookToken, ClaimRevision = snapshot.ClaimRevision, DeckRevision = snapshot.DeckRevision
            });
            if (result == CorePageResultCode.Accepted) return null;
            if (result == CorePageResultCode.UnsupportedInventory) return "房主库存暂不可用";
            if (result == CorePageResultCode.UnsupportedCurrentBook)
            {
                var current = snapshot.CoreBooks.Find(book => book.BookToken == deck.BookToken);
                var reason = current == null ? null : FlagReason(current.Flags & ~CoreBookFlags.Equipped);
                return reason == null ? "当前书页不支持更换" : "当前书页：" + reason;
            }
            return "书页状态已失效";
        }

        private static string FlagReason(CoreBookFlags flags)
        {
            if ((flags & CoreBookFlags.OwnerMismatch) != 0) return "书页归属不一致";
            if ((flags & CoreBookFlags.PassiveBound) != 0) return "被动来源被占用";
            if ((flags & CoreBookFlags.DraftMismatch) != 0) return "被动草稿未应用";
            if ((flags & CoreBookFlags.Locked) != 0) return "书页被锁定";
            if ((flags & CoreBookFlags.FixedDeck) != 0) return "固定牌组只读";
            if ((flags & CoreBookFlags.MultiDeck) != 0) return "多牌组只读";
            if ((flags & CoreBookFlags.UnsupportedId) != 0) return "非原版核心书页";
            if ((flags & CoreBookFlags.InvalidInstance) != 0) return "书页实例不支持";
            if ((flags & CoreBookFlags.UnsupportedCards) != 0) return "含不支持的战斗页";
            if ((flags & CoreBookFlags.UnsupportedDisplay) != 0) return "书页属性暂不同步";
            if ((flags & CoreBookFlags.CannotEquip) != 0) return "原版禁止装备";
            return flags == CoreBookFlags.None ? null : "书页暂不支持";
        }
    }

#if !EQUIPMENT_PURE_TESTS
    // The original page keeps its layout and lists, but never reads the local
    // BookInventory. Only host DTOs and detached, permanently guarded books enter it.
    internal static class NativeEquipmentEditor
    {
        private sealed class SlotBinding
        {
            internal BookModel Book;
            internal CorePageClickToken Token;
        }

        private sealed class SavedField
        {
            internal object Owner;
            internal FieldInfo Field;
            internal object Value;
            internal object[] Items;
            internal List<DictionaryEntry> Entries;
        }

        private static readonly Dictionary<ulong, BookModel> Books = new Dictionary<ulong, BookModel>();
        private static readonly Dictionary<BookModel, ProgressSnapshot.CoreBookEntry> Entries = new Dictionary<BookModel, ProgressSnapshot.CoreBookEntry>();
        private static readonly Dictionary<object, SlotBinding> Slots = new Dictionary<object, SlotBinding>();
        private static readonly Dictionary<object, SlotBinding> Buttons = new Dictionary<object, SlotBinding>();
        private static readonly CorePagePressState Presses = new CorePagePressState();
        private static readonly List<SavedField> Saved = new List<SavedField>();
        private static readonly HashSet<object> Captured = new HashSet<object>();
        private static readonly Dictionary<GameObject, bool> Visibility = new Dictionary<GameObject, bool>();
        private static readonly Dictionary<object, bool> Interactable = new Dictionary<object, bool>();
        private static readonly Dictionary<object, Dictionary<string, object>> Properties = new Dictionary<object, Dictionary<string, object>>();
        private static readonly List<BookModel> AllBooks = new List<BookModel>();
        private static ProgressSnapshot _displayed;
        private static object _panel;
        private static bool _opened;
        private static bool _refreshing;
        private static ulong _generation;
        private static int _unavailableRows;
        internal static string Status { get; private set; }
        private static bool Active { get { return _opened && NativeDeckEditor.Active; } }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, "UI.UIEquipPageInventoryPanel", "OnOpen", "PanelOpenPrefix");
            Patch(harmony, "UI.UIEquipPageInventoryPanel", "OnUpdatePhase", "PanelUpdatePrefix");
            Patch(harmony, "UI.UIEquipPageInventoryPanel", "OnClose", "PanelClosePrefix");
            Patch(harmony, "UI.UIEquipPageInventoryLeftPanel", "UpdateEquipPageList", "LeftListPrefix", typeof(bool));
            Patch(harmony, "UI.UIEquipPageScrollList", "FilterBookModels", "FilterPrefix", typeof(List<BookModel>));
            Patch(harmony, "UI.UIEquipPagePreviewPanel", "SetData", null, new[] { typeof(BookModel) }, "PreviewPostfix");
            Patch(harmony, "UI.UIEquipPagePreviewPanel", "SetPassiveBookInfoPanel", "PassivePanelPrefix");
            Patch(harmony, "UI.UIOriginEquipPageSlot", "SetActiveSlot", "SlotActivePrefix", typeof(bool));
            var pointer = typeof(UnitDataModel).Assembly.GetType("UI.UIInvenEquipPageSlot", true)
                .GetMethod("OnPointerClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .GetParameters()[0].ParameterType;
            var selectable = typeof(UnitDataModel).Assembly.GetType("UI.UICustomSelectable", true);
            var down = selectable.GetMethod("OnPointerDown", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .GetParameters()[0].ParameterType;
            Patch(harmony, "UI.UICustomSelectable", "OnPointerDown", "PressPrefix", down);
            Patch(harmony, "UI.UICustomSelectable", "OnSubmit", "PressPrefix", pointer);
            foreach (var type in new[] { "UI.UIInvenEquipPageSlot", "UI.UIInvenLeftEquipPageSlot" })
            {
                Patch(harmony, type, "SetData", "SlotDataPrefix", typeof(BookModel));
                Patch(harmony, type, "SetOperatingPanel", "OperatingPrefix");
                Patch(harmony, type, "SetActiveOperatinPanel", "OperatingVisibilityPrefix", typeof(bool));
                Patch(harmony, type, "OnPointerClick", "SlotClickPrefix", pointer);
                foreach (var method in new[] { "OnClickPassiveSuccessionButton", "OnClickRelaseButton", "OnClickBookMarkButton" })
                    Patch(harmony, type, method, "UnsafeSlotPrefix");
            }
            var rect = typeof(RectTransform);
            Patch(harmony, "UI.UIMainTutorialManager", "StartEquipPageOpenTutorial", "TutorialPrefix");
            Patch(harmony, "UI.UIMainTutorialManager", "StartEquipPageChangeTutorial", "TutorialPrefix");
            Patch(harmony, "UI.UIMainTutorialManager", "StartEquipPageClickTutorial", "TutorialPrefix", new[] { rect, rect });
            // This query feeds the card-equipment popup and can traverse real books.
            // Existing native card guards block opening that popup; retain that guard.
        }

        private static void Patch(Harmony harmony, string type, string name, string prefix, params Type[] args)
        { Patch(harmony, type, name, prefix, args, null); }

        private static void Patch(Harmony harmony, string type, string name, string prefix, Type[] args, string postfix)
        {
            var original = typeof(UnitDataModel).Assembly.GetType(type, true).GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
            if (original == null) throw new MissingMethodException(type, name);
            harmony.Patch(original, prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeEquipmentEditor), prefix)),
                postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(NativeEquipmentEditor), postfix)));
        }

        internal static bool AllowsPhase(int phase) { return _opened && phase == 8; }

        internal static bool TryOpen()
        {
            if (!NativeDeckEditor.Active || !NativeDeckEditor.Session.IsReadyForDeck) return false;
            var snapshot = NativeDeckEditor.Snapshot;
            if (!snapshot.CoreBooksAvailable || snapshot.CoreBooks.Count == 0)
            { Status = "房主核心书页库存暂不可用：" + snapshot.CoreBooksReason; return false; }
            try
            {
                if (!_opened)
                {
                    _panel = NativeDeckEditor.FindPanel("UI.UIEquipPageInventoryPanel");
                    Capture(_panel);
                    Capture(NativeUi.Get(_panel, "_equipPageScrollListPanel"));
                    Capture(NativeUi.Get(_panel, "_equipLeftPanel"));
                    Capture(NativeUi.Get(NativeUi.Get(_panel, "_equipLeftPanel"), "equipPageList"));
                    Capture(NativeUi.Get(_panel, "_librarianInfo"));
                    Capture(NativeUi.Get(_panel, "_equipPagePreviewPanel"));
                    var preview = NativeUi.Get(_panel, "_equipPagePreviewPanel");
                    Capture(NativeUi.Get(preview, "passiveSlotsPanel"));
                    Capture(NativeUi.Get(preview, "equipedCardListPanel"));
                    AdvanceGeneration();
                    _opened = true;
                }
                RefreshBooks(snapshot);
                Status = "普通核心书页更换由房主确认；已占用、特殊、被动来源占用和未应用草稿书页只读。" +
                    (_unavailableRows == 0 ? "" : " 有 " + _unavailableRows + " 本非原版或无法解析书页未显示。");
                if (AllBooks.Count == 0) { Status = "房主库存中没有可展示的原版核心书页。"; Close(); return false; }
                var controller = NativeUi.Singleton("UI.UIController");
                var phase = NativeUi.Get(controller, "CurrentUIPhase");
                NativeUi.Call(controller, "CallUIPhase", Enum.ToObject(phase.GetType(), 8));
                Refresh();
                return true;
            }
            catch (Exception exception) { Fail(exception); return false; }
        }

        internal static bool Refresh()
        {
            if (!Active || Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase")) != 8) return false;
            if (_refreshing) return true;
            _refreshing = true;
            try
            {
                var snapshot = NativeDeckEditor.Snapshot;
                if (!snapshot.CoreBooksAvailable || snapshot.CoreBooks.Count == 0)
                { Status = "核心书页库存已失效，请返回卡组页。"; NativeDeckEditor.Close(); return true; }
                RefreshBooks(snapshot);
                if (AllBooks.Count == 0)
                { Status = "房主库存中没有可展示的原版核心书页。"; NativeDeckEditor.Close(); return true; }
                AdvanceGeneration();
                Slots.Clear(); Buttons.Clear();
                NativeUi.Call(_panel, "HidePreviewPanel");
                NativeUi.Set(_panel, "_currentSelectedSlot", null);
                NativeUi.Set(_panel, "isSaveCheck", false);
                NativeUi.Call(NativeUi.Get(_panel, "_equipPageScrollListPanel"), "SetData", AllBooks, NativeDeckEditor.Current, true);
                NativeUi.Call(NativeUi.Get(_panel, "_librarianInfo"), "SetData", NativeDeckEditor.Current);
                LeftListPrefix(NativeUi.Get(_panel, "_equipLeftPanel"), true);
                return true;
            }
            catch (Exception exception) { Fail(exception); return true; }
            finally { _refreshing = false; }
        }

        private static void RefreshBooks(ProgressSnapshot snapshot)
        {
            if (ReferenceEquals(_displayed, snapshot)) return;
            Entries.Clear(); AllBooks.Clear(); _unavailableRows = 0;
            foreach (var entry in snapshot.CoreBooks)
            {
                if (entry.BookId <= 0 || BookXmlList.Instance.GetData(new LorId(entry.BookId), false) == null)
                { _unavailableRows++; continue; }
                BookModel book;
                if (!Books.TryGetValue(entry.BookToken, out book) || book.BookId.id != entry.BookId || book.instanceId != entry.BookInstanceId)
                { book = NativeDeckEditor.CreateDetachedBook(entry); Books[entry.BookToken] = book; }
                else NativeDeckEditor.UpdateDetachedBook(book, entry);
                Entries.Add(book, entry);
                AllBooks.Add(book);
            }
            _displayed = snapshot;
        }

        private static void AdvanceGeneration() { unchecked { _generation++; if (_generation == 0) _generation++; } }

        private static bool PanelOpenPrefix(object __instance)
        {
            if (!Active) return true;
            SetVisible(__instance, true);
            SetVisible(NativeUi.Get(__instance, "_librarianInfo"), true);
            NativeUi.Call(__instance, "SetActiveCg", true);
            NativeUi.Call(__instance, "RevealAnim");
            NativeUi.Call(NativeUi.Get(__instance, "_equipPageScrollListPanel"), "OpenInit");
            NativeUi.Set(__instance, "currentOverSlot", null);
            NativeUi.Set(__instance, "_currentSelectedSlot", null);
            NativeUi.Set(__instance, "isSaveCheck", false);
            NativeUi.Set(__instance, "isPreviewVisible", false);
            return false;
        }

        private static bool SlotActivePrefix(object __instance, bool __0)
        {
            var name = __instance.GetType().FullName;
            if (name != "UI.UIInvenEquipPageSlot" && name != "UI.UIInvenLeftEquipPageSlot") return true;
            if (!Active) return !NativeDeckModels.IsMirrorBook(NativeUi.Get(__instance, "_bookDataModel") as BookModel);
            Capture(__instance);
            var canvas = NativeUi.Get(__instance, "cg");
            if (canvas == null) SetVisible(__instance, __0);
            else
            {
                SetProperty(canvas, "alpha", __0 ? 1f : 0f);
                SetProperty(canvas, "interactable", __0);
                SetProperty(canvas, "blocksRaycasts", __0);
            }
            SetInteractable(NativeUi.Get(__instance, "selectable"), __0);
            if (!__0) { NativeUi.Set(__instance, "_bookDataModel", null); Slots.Remove(__instance); }
            return false;
        }

        private static bool PanelUpdatePrefix() { if (!Active) return true; Refresh(); return false; }

        private static bool PanelClosePrefix(object __instance)
        {
            if (!Active) return true;
            NativeUi.Call(__instance, "SetActiveCg", false);
            SetVisible(NativeUi.Get(__instance, "_librarianInfo"), false);
            NativeUi.Set(__instance, "isSaveCheck", false);
            return false;
        }

        private static bool LeftListPrefix(object __instance, bool __0)
        {
            if (!Active) return true;
            var list = NativeUi.Get(__instance, "equipPageList");
            var equipped = AllBooks.FindAll(book => (Entries[book].Flags & CoreBookFlags.Equipped) != 0);
            // Bookmarks are local-save data and are deliberately unavailable here.
            SetVisible(NativeUi.Get(__instance, "button_BookMark"), false);
            SetVisible(NativeUi.Get(__instance, "button_EquipedBook"), true);
            NativeUi.Set(__instance, "currentShowState", 1);
            NativeUi.Call(list, "SetBooksData", equipped, NativeDeckEditor.Current, __0);
            return false;
        }

        private static bool FilterPrefix(object __instance, List<BookModel> __0, ref List<BookModel> __result)
        {
            if (!Active) return true;
            SetVisible(NativeUi.Get(__instance, "bookSortFilter"), false);
            var selectedGrades = (IList)NativeUi.Call(NativeUi.Get(__instance, "GradeFilter"), "GetStoryGradeFilter");
            __result = new List<BookModel>();
            foreach (var book in __0)
            {
                if (!Entries.ContainsKey(book)) continue;
                var accepted = selectedGrades.Count == 0;
                foreach (var grade in selectedGrades) if (Convert.ToInt32(grade) == book.ClassInfo.Chapter) accepted = true;
                if (accepted) __result.Add(book);
            }
            return false;
        }

        private static bool SlotDataPrefix(object __instance, BookModel __0)
        {
            if (!Active) return !NativeDeckModels.IsMirrorBook(__0);
            Capture(__instance);
            NativeUi.Set(__instance, "_bookDataModel", __0);
            NativeUi.Set(__instance, "isEmptyBook", __0 == null);
            SetVisible(__instance, __0 != null);
            if (__0 == null) { Slots.Remove(__instance); return false; }
            ProgressSnapshot.CoreBookEntry entry;
            if (!Entries.TryGetValue(__0, out entry)) return false;
            var binding = new SlotBinding { Book = __0,
                Token = new CorePageClickToken(_displayed, NativeDeckEditor.UnitIndex, entry.BookToken, _generation) };
            Slots[__instance] = binding;
            var selectable = NativeUi.Get(NativeUi.Get(__instance, "button_Equip"), "selectable");
            if (selectable != null) Buttons[selectable] = binding;
            SetProperty(NativeUi.Get(__instance, "BookName"), "text", __0.GetName() +
                ((entry.Flags & CoreBookFlags.Equipped) != 0 ? " · 已装备" : entry.Kind != CoreBookKind.Ordinary || entry.Flags != CoreBookFlags.None ? " · 只读" : ""));
            SetProperty(NativeUi.Get(__instance, "Icon"), "sprite", __0.bookIcon);
            SetProperty(NativeUi.Get(__instance, "IconGlow"), "sprite", __0.bookIconGlow);
            NativeUi.Set(__instance, "isBlock", false);
            SetVisible(NativeUi.Get(__instance, "ob_blockFrame"), false);
            SetVisible(NativeUi.Get(__instance, "ob_equipRoot"), false);
            var state = NativeUi.Get(__instance, "currenSlotState");
            NativeUi.Call(__instance, "SetColorFrame", Enum.ToObject(state.GetType(), 0));
            OperatingPrefix(__instance);
            return false;
        }

        private static bool OperatingPrefix(object __instance)
        {
            var book = NativeUi.Get(__instance, "_bookDataModel") as BookModel;
            if (!Active) return !NativeDeckModels.IsMirrorBook(book);
            foreach (var name in new[] { "button_BookMark", "button_PassiveSuccession", "button_ReleaseButton", "button_EmptyDeck" })
                SetVisible(NativeUi.Get(__instance, name), false);
            var button = NativeUi.Get(__instance, "button_Equip");
            // Refresh visibility; the original press identity is checked separately.
            SetVisible(button, false);
            SlotBinding binding;
            ProgressSnapshot.CoreBookEntry entry = null;
            var bound = book != null && Slots.TryGetValue(__instance, out binding) &&
                ReferenceEquals(book, binding.Book) && Entries.TryGetValue(book, out entry);
            var reason = CorePageUiAccess.ReadOnlyReason(NativeDeckEditor.Snapshot, NativeDeckEditor.UnitIndex, entry,
                SteamClient.SteamId.Value, NativeDeckEditor.CanEdit, NativeDeckEditor.Session.IsReadyForDeck,
                NativeDeckEditor.Session.DeckRequestPending, bound);
            var enabled = reason == null;
            SetVisible(button, true);
            SetInteractable(button, enabled);
            SetProperty(NativeUi.Get(__instance, "txt_equipButton"), "text", enabled ? "更换核心书页" : reason);
            return false;
        }

        // Pointer press belongs to the row rendered when it began. A recycled
        // button must not adopt a new row token when its old mouse-up arrives.
        private static bool PressPrefix(object __instance)
        {
            SlotBinding binding;
            if (Active && Buttons.TryGetValue(__instance, out binding)) Presses.Capture(__instance, binding.Token);
            return true;
        }

        private static bool OperatingVisibilityPrefix(object __instance, bool __0)
        {
            if (!Active) return !NativeDeckModels.IsMirrorBook(NativeUi.Get(__instance, "_bookDataModel") as BookModel);
            SetVisible(NativeUi.Get(__instance, "ob_OperatingPanel"), true);
            var canvas = NativeUi.Get(__instance, "cg_operatingPanel");
            SetProperty(canvas, "alpha", __0 ? 1f : 0f);
            SetProperty(canvas, "blocksRaycasts", __0);
            SetProperty(canvas, "interactable", __0);
            return false;
        }

        private static bool SlotClickPrefix(object __instance)
        {
            var book = NativeUi.Get(__instance, "_bookDataModel") as BookModel;
            if (!Active) return !NativeDeckModels.IsMirrorBook(book);
            SlotBinding binding;
            if (!_refreshing && Slots.TryGetValue(__instance, out binding) && ReferenceEquals(book, binding.Book))
                NativeUi.Call(_panel, "OnClickSlot", __instance);
            return false;
        }

        internal static bool TryHandleCorePageClick(object slot)
        {
            var book = NativeUi.Get(slot, "_bookDataModel") as BookModel;
            if (!Active) return NativeDeckModels.IsMirrorBook(book);
            try
            {
                SlotBinding binding;
                CorePageClickToken pressed;
                var selectable = NativeUi.Get(NativeUi.Get(slot, "button_Equip"), "selectable");
                if (Convert.ToInt32(NativeUi.Get(NativeUi.Singleton("UI.UIController"), "CurrentUIPhase")) != 8 ||
                    !Slots.TryGetValue(slot, out binding) || !ReferenceEquals(book, binding.Book) ||
                    !NativeDeckEditor.CanEdit || !Presses.TryConsume(selectable, NativeDeckEditor.Snapshot, NativeDeckEditor.UnitIndex,
                        SteamClient.SteamId.Value, Active, NativeDeckEditor.ReadyForInput, NativeDeckEditor.Session.DeckRequestPending,
                        _generation, binding.Token.TargetBookToken, out pressed))
                { Status = "此书页或馆员当前只读、界面已更新，或上一项操作尚未确认。"; return true; }
                NativeDeckEditor.Session.RequestCorePageEdit(pressed.Snapshot, pressed.UnitIndex, pressed.TargetBookToken);
                Status = NativeDeckEditor.Session.DeckStatus;
            }
            catch (Exception exception) { Fail(exception); }
            return true;
        }

        private static bool UnsafeSlotPrefix(object __instance)
        { return !Active && !NativeDeckModels.IsMirrorBook(NativeUi.Get(__instance, "_bookDataModel") as BookModel); }
        private static bool TutorialPrefix() { return !Active; }

        private static bool PassivePanelPrefix(object __instance)
        {
            var book = NativeUi.Get(__instance, "bookDataModel") as BookModel;
            if (!Active && !NativeDeckModels.IsMirrorBook(book)) return true;
            SetProperty(NativeUi.Get(__instance, "cg_receivedPassiveBookListPanel"), "alpha", 0f);
            SetProperty(NativeUi.Get(__instance, "cg_givePassiveBookPanel"), "alpha", 0f);
            foreach (var name in new[] { "cg_receivedPassiveBookListPanel", "cg_givePassiveBookPanel" })
            {
                SetProperty(NativeUi.Get(__instance, name), "interactable", false);
                SetProperty(NativeUi.Get(__instance, name), "blocksRaycasts", false);
            }
            return false;
        }

        private static void PreviewPostfix(object __instance, BookModel __0)
        {
            ProgressSnapshot.CoreBookEntry entry;
            if (!Active || __0 == null || !Entries.TryGetValue(__0, out entry)) return;
            SetVisible(NativeUi.Get(__instance, "passiveSlotsPanel"), entry.Display.Available);
            SetVisible(NativeUi.Get(__instance, "equipedCardListPanel"), (entry.Flags & CoreBookFlags.UnsupportedCards) == 0);
            var stats = NativeUi.Get(__instance, "StatsInfo");
            foreach (var name in new[] { "emotionText", "speedDiceText", "speedDiceNumText", "playpointText", "resistSlash", "resistPentrate", "resistHit", "resistBreakSlash", "resistBreakPentrate", "resistBreakHit" })
                SetProperty(NativeUi.Get(stats, name), "text", "—");
            SetProperty(NativeUi.Get(stats, "hpText"), "text", entry.Display.Available ? entry.Display.MaxHp.ToString() : "—");
            SetProperty(NativeUi.Get(stats, "breakText"), "text", entry.Display.Available ? entry.Display.Break.ToString() : "—");
        }

        private static void Capture(object owner)
        {
            if (owner == null || !Captured.Add(owner)) return;
            for (var type = owner.GetType(); type != null && type.Assembly == typeof(UnitDataModel).Assembly; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(owner);
                    var state = new SavedField { Owner = owner, Field = field, Value = value };
                    var list = value as IList;
                    if (list != null) { state.Items = new object[list.Count]; list.CopyTo(state.Items, 0); }
                    var dictionary = value as IDictionary;
                    if (dictionary != null) { state.Entries = new List<DictionaryEntry>(); foreach (DictionaryEntry entry in dictionary) state.Entries.Add(entry); }
                    Saved.Add(state);
                }
        }

        private static void SetVisible(object component, bool visible)
        {
            if (component == null) return;
            var gameObject = component as GameObject ?? NativeUi.Get(component, "gameObject") as GameObject;
            if (gameObject == null) return;
            if (!Visibility.ContainsKey(gameObject)) Visibility.Add(gameObject, gameObject.activeSelf);
            gameObject.SetActive(visible);
        }

        private static void SetInteractable(object component, bool value)
        {
            if (component == null) return;
            if (!Interactable.ContainsKey(component)) Interactable.Add(component, (bool)NativeUi.Get(component, "interactable"));
            NativeUi.Set(component, "interactable", value);
        }

        private static void SetProperty(object component, string property, object value)
        {
            if (component == null) return;
            Dictionary<string, object> original;
            if (!Properties.TryGetValue(component, out original))
            { original = new Dictionary<string, object>(); Properties.Add(component, original); }
            if (!original.ContainsKey(property)) original.Add(property, NativeUi.Get(component, property));
            NativeUi.Set(component, property, value);
        }

        internal static void Close()
        {
            _opened = false;
            AdvanceGeneration();
            Slots.Clear(); Buttons.Clear(); Presses.Clear(); Entries.Clear(); Books.Clear(); AllBooks.Clear(); _displayed = null;
            foreach (var state in Saved)
            {
                try
                {
                    state.Field.SetValue(state.Owner, state.Value);
                    var list = state.Value as IList;
                    if (list != null && state.Items != null)
                    {
                        if (list.IsFixedSize) for (var i = 0; i < state.Items.Length; i++) list[i] = state.Items[i];
                        else { list.Clear(); foreach (var item in state.Items) list.Add(item); }
                    }
                    var dictionary = state.Value as IDictionary;
                    if (dictionary != null && state.Entries != null)
                    { dictionary.Clear(); foreach (var entry in state.Entries) dictionary.Add(entry.Key, entry.Value); }
                }
                catch (Exception exception) { Debug.LogError("[RuinaCoop] Equipment UI field restoration failed: " + exception); }
            }
            foreach (var pair in Visibility) if (NativeUi.IsAlive(pair.Key)) pair.Key.SetActive(pair.Value);
            foreach (var pair in Interactable)
                if (NativeUi.IsAlive(pair.Key))
                    try { NativeUi.Set(pair.Key, "interactable", pair.Value); } catch (Exception exception) { Debug.LogError(exception); }
            foreach (var pair in Properties)
                if (NativeUi.IsAlive(pair.Key)) foreach (var property in pair.Value)
                    try { NativeUi.Set(pair.Key, property.Key, property.Value); } catch (Exception exception) { Debug.LogError(exception); }
            Saved.Clear(); Captured.Clear(); Visibility.Clear(); Interactable.Clear(); Properties.Clear(); _panel = null; _refreshing = false;
        }

        private static void Fail(Exception exception)
        {
            Status = "联机核心书页界面暂不可用：" + exception.GetBaseException().Message;
            Debug.LogError("[RuinaCoop] Native equipment editor failed: " + exception);
            NativeDeckEditor.Close();
        }
    }
#endif
}
