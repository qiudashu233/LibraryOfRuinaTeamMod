using System;
using System.Collections.Generic;
using System.Linq;
#if !PASSIVE_PURE_TESTS
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;
#endif

namespace RuinaCoop
{
    internal struct NativePassiveChoice
    {
        internal ulong SourceBookToken;
        internal byte SourceSlotIndex;
        internal int ExpectedOriginPassiveId;
    }

    internal struct NativePassiveEvent
    {
        internal readonly NativePassiveDraft Owner;
        internal readonly ProgressSnapshot Snapshot;
        internal readonly ulong Generation;
        internal NativePassiveEvent(NativePassiveDraft owner)
        { Owner = owner; Snapshot = owner.Snapshot; Generation = owner.Generation; }
    }

    // Only this local DTO draft changes while selecting. A room request receives
    // complete copies of the final slots and selected sources on explicit apply.
    internal sealed class NativePassiveDraft
    {
        internal const int EmptyPassive = 9999999;
        private readonly ulong _room;
        private readonly ulong _local;
        private readonly int _stage;
        private readonly byte _floor;
        private readonly ulong[] _roster;
        private readonly ulong _unitIdentity;
        private readonly ulong _bookToken;
        private readonly int _bookId;
        private readonly int _bookInstance;
        private readonly bool _initiallyOwned;
        private readonly List<ulong> _sources = new List<ulong>();
        private readonly NativePassiveChoice[] _choices;
        internal ProgressSnapshot Snapshot { get; private set; }
        internal byte UnitIndex { get; private set; }
        internal bool Valid { get; private set; }
        internal bool Conflict { get; private set; }
        internal bool Submitted { get; private set; }
        internal ulong Generation { get; private set; }
        internal ProgressSnapshot.PassiveBookEntry Receiver { get; private set; }
        internal bool CanEdit { get { return Valid && !Conflict && !Submitted && _initiallyOwned &&
            !Snapshot.DecksFrozen && (Receiver.Flags & PassiveBookFlags.ReceiverAllowed) != 0; } }
        internal ulong BookToken { get { return _bookToken; } }
        internal int SlotCount { get { return _choices.Length; } }
        internal int TotalCost
        {
            get
            {
                var result = 0;
                foreach (var choice in _choices) if (choice.SourceBookToken != 0)
                    result += SourceSlot(choice).Cost;
                return result;
            }
        }

        private NativePassiveDraft(ulong room, ProgressSnapshot snapshot, byte unitIndex, ulong local)
        {
            _room = room; _local = local; _stage = snapshot.SelectedStageId; _floor = snapshot.SelectedFloorId;
            Snapshot = snapshot; UnitIndex = unitIndex;
            var deck = snapshot.UnitDecks[unitIndex]; _unitIdentity = deck.UnitIdentity;
            _bookToken = deck.BookToken; _bookId = deck.BookId; _bookInstance = deck.BookInstanceId;
            _roster = snapshot.UnitDecks.Select(unit => unit.UnitIdentity).ToArray();
            _initiallyOwned = snapshot.ClaimOwners[unitIndex] == local;
            Receiver = snapshot.PassiveBooks.Find(book => book.BookToken == _bookToken);
            _sources.AddRange(Receiver.SourceTokens);
            _choices = new NativePassiveChoice[Receiver.Slots.Count];
            for (var i = 0; i < _choices.Length; i++)
            {
                var slot = Receiver.Slots[i];
                _choices[i] = new NativePassiveChoice { SourceBookToken = slot.SourceBookToken,
                    SourceSlotIndex = slot.SourceSlotIndex, ExpectedOriginPassiveId = slot.SourceBookToken == 0 ? slot.OriginId :
                        snapshot.PassiveBooks.Find(book => book.BookToken == slot.SourceBookToken).Slots[slot.SourceSlotIndex].OriginId };
            }
            Valid = true; Generation = 1;
        }

        internal static bool TryCreate(ulong room, ProgressSnapshot snapshot, byte unitIndex, ulong local, out NativePassiveDraft draft)
        {
            draft = null;
            if (room == 0 || local == 0 || snapshot == null || !snapshot.PassivesAvailable ||
                snapshot.SelectedStageId <= 0 || snapshot.SelectedFloorId < 1 || snapshot.SelectedFloorId > 10 ||
                unitIndex >= snapshot.UnitDecks.Count || snapshot.ClaimOwners.Count != snapshot.UnitDecks.Count) return false;
            var deck = snapshot.UnitDecks[unitIndex];
            var receiver = snapshot.PassiveBooks.Find(book => book.BookToken == deck.BookToken);
            if (deck.UnitIdentity == 0 || deck.BookToken == 0 || receiver == null || receiver.Slots.Count == 0 ||
                (receiver.Flags & PassiveBookFlags.Unsupported) != 0) return false;
            foreach (var slot in receiver.Slots)
                if (slot.SourceBookToken != 0)
                {
                    var source = snapshot.PassiveBooks.Find(book => book.BookToken == slot.SourceBookToken);
                    if (source == null || slot.SourceSlotIndex >= source.Slots.Count) return false;
                }
            draft = new NativePassiveDraft(room, snapshot, unitIndex, local);
            return true;
        }

        internal NativePassiveEvent CaptureEvent() { return new NativePassiveEvent(this); }
        internal bool IsCurrent(NativePassiveEvent token)
        { return Valid && ReferenceEquals(token.Owner, this) && ReferenceEquals(token.Snapshot, Snapshot) && token.Generation == Generation; }
        internal NativePassiveChoice Choice(int index) { return _choices[index]; }
        internal ulong[] Sources() { return _sources.ToArray(); }
        internal NativePassiveChoice[] Choices() { return (NativePassiveChoice[])_choices.Clone(); }
        internal PassiveSelection[] Selections()
        {
            return _choices.Select(choice => new PassiveSelection { Mode = choice.SourceBookToken == 0 ?
                PassiveSelectionMode.RestoreNative : PassiveSelectionMode.Inherit, SourceBookToken = choice.SourceBookToken,
                SourceSlotIndex = choice.SourceSlotIndex, ExpectedOriginPassiveId = choice.ExpectedOriginPassiveId }).ToArray();
        }
        internal ProgressSnapshot.PassiveBookEntry Source(ulong token)
        { return Snapshot.PassiveBooks.Find(book => book.BookToken == token); }
        internal ProgressSnapshot.PassiveSlotEntry SourceSlot(NativePassiveChoice choice)
        { return Source(choice.SourceBookToken).Slots[choice.SourceSlotIndex]; }
        internal bool IsSelected(ulong token) { return _sources.Contains(token); }
        internal bool CanAttach(ulong token)
        {
            var source = Source(token);
            return token != _bookToken && source != null && (source.Flags & PassiveBookFlags.SourceAllowed) != 0 &&
                (source.Flags & PassiveBookFlags.Unsupported) == 0 &&
                (source.ReceiverBookToken == 0 || source.ReceiverBookToken == _bookToken) &&
                (_sources.Contains(token) || _sources.Count < Receiver.MaxSources);
        }
        internal bool TryAttach(NativePassiveEvent token, ulong source)
        {
            if (!CanEdit || !IsCurrent(token) || !CanAttach(source)) return false;
            if (_sources.Contains(source)) return true;
            _sources.Add(source); Advance(); return true;
        }
        internal bool TryDetach(NativePassiveEvent token, ulong source)
        {
            if (!CanEdit || !IsCurrent(token) || !_sources.Remove(source)) return false;
            for (var i = 0; i < _choices.Length; i++) if (_choices[i].SourceBookToken == source) Restore(i);
            Advance(); return true;
        }
        internal bool TryRestore(NativePassiveEvent token, int index)
        {
            if (!CanEdit || !IsCurrent(token) || index < 0 || index >= _choices.Length || _choices[index].SourceBookToken == 0) return false;
            Restore(index); Advance(); return true;
        }
        private void Restore(int index)
        { _choices[index] = new NativePassiveChoice { SourceSlotIndex = byte.MaxValue, ExpectedOriginPassiveId = Receiver.Slots[index].OriginId }; }
        internal bool TryInherit(NativePassiveEvent token, ulong sourceToken, byte sourceSlot)
        {
            if (!CanEdit || !IsCurrent(token) || !_sources.Contains(sourceToken) || !CanAttach(sourceToken)) return false;
            var source = Source(sourceToken);
            if (sourceSlot >= source.Slots.Count) return false;
            var passive = source.Slots[sourceSlot];
            if ((passive.Flags & PassiveSlotFlags.CanGive) == 0 || (passive.Flags & PassiveSlotFlags.Hidden) != 0 ||
                passive.OriginId == EmptyPassive || (passive.Flags & PassiveSlotFlags.Given) != 0 && source.ReceiverBookToken != _bookToken) return false;
            var empty = -1;
            for (var i = 0; i < _choices.Length; i++)
            {
                var native = Receiver.Slots[i]; var choice = _choices[i];
                var effective = choice.SourceBookToken == 0 ? native : SourceSlot(choice);
                if (effective.OriginId == passive.OriginId || passive.InnerTypeId != -1 && effective.InnerTypeId == passive.InnerTypeId)
                    return false;
                if (empty < 0 && choice.SourceBookToken == 0 && native.OriginId == EmptyPassive &&
                    (native.Flags & PassiveSlotFlags.CanReceive) != 0 && (native.Flags & PassiveSlotFlags.Locked) == 0) empty = i;
            }
            if (empty < 0 || TotalCost + passive.Cost > Receiver.MaxCost) return false;
            _choices[empty] = new NativePassiveChoice { SourceBookToken = sourceToken, SourceSlotIndex = sourceSlot,
                ExpectedOriginPassiveId = passive.OriginId };
            Advance(); return true;
        }
        internal bool TryRestoreAll(NativePassiveEvent token)
        {
            if (!CanEdit || !IsCurrent(token)) return false;
            for (var i = 0; i < _choices.Length; i++) Restore(i);
            _sources.Clear(); Advance(); return true;
        }
        internal bool TryUpdate(ulong room, ProgressSnapshot snapshot)
        {
            if (!Valid) return false;
            if (room != _room || snapshot == null || snapshot.SelectedStageId != _stage || snapshot.SelectedFloorId != _floor ||
                snapshot.UnitDecks.Count != _roster.Length || snapshot.ClaimOwners.Count != _roster.Length ||
                snapshot.UnitDecks.Where((unit, i) => unit.UnitIdentity != _roster[i]).Any() ||
                snapshot.UnitDecks[UnitIndex].UnitIdentity != _unitIdentity || snapshot.UnitDecks[UnitIndex].BookToken != _bookToken ||
                snapshot.UnitDecks[UnitIndex].BookId != _bookId || snapshot.UnitDecks[UnitIndex].BookInstanceId != _bookInstance ||
                (_initiallyOwned && snapshot.ClaimOwners[UnitIndex] != _local) || snapshot.DecksFrozen || !snapshot.PassivesAvailable)
            { Close(); return false; }
            if (snapshot.Sequence < Snapshot.Sequence || ReferenceEquals(snapshot, Snapshot)) return true;
            if (snapshot.ClaimRevision != Snapshot.ClaimRevision || snapshot.DeckRevision != Snapshot.DeckRevision)
            { if (!Conflict) { Conflict = true; Advance(); } return true; }
            Snapshot = snapshot; Receiver = Source(_bookToken); Advance(); return true;
        }
        internal bool MarkSubmitted(NativePassiveEvent token, ProgressSnapshot latest)
        {
            if (!CanEdit || !IsCurrent(token) || !ReferenceEquals(Snapshot, latest)) return false;
            Submitted = true; Advance(); return true;
        }
        internal void SubmissionRejected() { if (!Valid) return; Submitted = false; Advance(); }
        internal void Close() { Valid = false; Submitted = false; Advance(); }
        private void Advance() { unchecked { Generation++; if (Generation == 0) Generation++; } }
    }

#if !PASSIVE_PURE_TESTS
    internal static class NativePassiveEditor
    {
        private sealed class BookTag { internal NativePassiveDraft Owner; internal ulong Token; internal int MaxCost; }
        private sealed class SlotTag { internal NativePassiveEvent Event; internal ulong BookToken; internal byte Slot; internal int Kind; internal object OwnerSlot; }
        private sealed class SavedField { internal object Owner; internal FieldInfo Field; internal object Value; internal object[] Items; }
        private sealed class Confirmation
        {
            internal NativePassiveDraft Owner; internal NativePassiveEvent Event; internal bool Apply;
            internal PassiveSelection[] Slots; internal ulong[] Sources;
            internal void Invoke(bool accepted)
            {
                if (!accepted || !Active || !ReferenceEquals(_draft, Owner)) return;
                if (!Owner.IsCurrent(Event)) { Status = "界面已更新，请重新确认本次操作。"; return; }
                if (!Apply) { Close(); return; }
                if (!CanInput() || !Owner.MarkSubmitted(Event, _session.LatestSnapshot)) return;
                // Neither array aliases the mutable draft or a UI slot.
                if (!_session.RequestPassiveEdit(Event.Snapshot, Owner.UnitIndex,
                    (PassiveSelection[])Slots.Clone(), (ulong[])Sources.Clone())) Owner.SubmissionRejected();
                Status = _session.DeckStatus;
                if (Active) Render();
            }
        }
        private static readonly ConditionalWeakTable<BookModel, BookTag> MarkedBooks = new ConditionalWeakTable<BookModel, BookTag>();
        private static readonly ConditionalWeakTable<PassiveModel, object> MarkedPassives = new ConditionalWeakTable<PassiveModel, object>();
        private static readonly ConditionalWeakTable<object, object> MarkedPopups = new ConditionalWeakTable<object, object>();
        private static readonly object Mark = new object();
        private static readonly Dictionary<ulong, BookModel> Books = new Dictionary<ulong, BookModel>();
        private static readonly Dictionary<PassiveModel, Tuple<ulong, byte>> Passives = new Dictionary<PassiveModel, Tuple<ulong, byte>>();
        private static readonly Dictionary<object, SlotTag> Slots = new Dictionary<object, SlotTag>();
        private static readonly Dictionary<object, SlotTag> Selectables = new Dictionary<object, SlotTag>();
        private static readonly Dictionary<object, SlotTag> Presses = new Dictionary<object, SlotTag>();
        private static readonly List<SavedField> Saved = new List<SavedField>();
        private static readonly HashSet<object> Captured = new HashSet<object>();
        private static readonly Dictionary<GameObject, bool> Visibility = new Dictionary<GameObject, bool>();
        private static readonly HashSet<GameObject> NewRows = new HashSet<GameObject>();
        private static readonly Dictionary<object, Dictionary<string, object>> Properties = new Dictionary<object, Dictionary<string, object>>();
        private static NativePassiveDraft _draft;
        private static NativeDeckModels _factory;
        private static object _popup;
        private static RelaySession _session;
        private static bool _opening, _closing, _rendering;
        private static ulong _renderedGeneration;
        private static bool InRoom { get { return DeckGuard.Session != null && DeckGuard.Session.IsActive; } }
        internal static bool Active { get { return _draft != null && _draft.Valid && !_closing; } }
        internal static string Status { get; private set; }

        internal static void Install(Harmony harmony)
        {
            var game = typeof(BookModel).Assembly;
            var popup = game.GetType("UI.UIPassiveSuccessionPopup", true);
            var apply = popup.GetMethod("SetData").GetParameters()[1].ParameterType;
            var pointer = game.GetType("UI.UIPassiveSuccessionBookSlot", true).GetMethod("OnPointerClick").GetParameters()[0].ParameterType;
            var down = game.GetType("UI.UICustomSelectable", true).GetMethod("OnPointerDown").GetParameters()[0].ParameterType;
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "SetData", "SetDataPrefix", typeof(UnitDataModel), apply);
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "SetDataOnly", "SetDataOnlyPrefix", typeof(BookModel), apply);
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "Open", "OpenPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "Close", "ClosePrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "InitReservedData", "InitPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "EquipBook", "AttachPrefix", typeof(BookModel));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "UnEquipBook", "DetachPrefix", typeof(BookModel));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "UnEquipBookOtherBook", "OtherBookPrefix", typeof(BookModel));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "ChangePassive", "ChangePrefix", game.GetType("UI.UIPassiveSuccessionCenterPassiveSlot", true));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "ReleasePassive", "ReleasePrefix", typeof(PassiveModel));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "ReleasePassiveReverse", "ReleaseReversePrefix", game.GetType("UI.UIPassiveSuccessionSlot", true));
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "OnClickReleaseAllEquipedBookButton", "ResetPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "OnClickApplyButton", "ApplyPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "OnClickCancelButton", "CancelPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "OnCancel", "CancelPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionPopup", "CloseDefault", "UnsafePrefix");
            foreach (var name in new[] { "<OnClickApplyButton>b__42_0", "<OnClickCancelButton>b__43_0" })
                Patch(harmony, "UI.UIPassiveSuccessionPopup", name, "LateCallbackPrefix", typeof(bool));
            Patch(harmony, "UI.UIPassiveSuccessionBookSlot", "<OnPointerClick>b__41_0", "SourceLatePrefix", typeof(bool));
            Patch(harmony, "UI.UIPassiveSuccessionBookSlot", "SetDisabledByTheBluePrimary", "DisabledPrefix");
            Patch(harmony, "UI.UIPassiveSuccessionBookListPanel", "SetPreviewData", "PreviewPrefix", game.GetType("UI.UIPassiveSuccessionBookSlot", true));
            Patch(harmony, "BookModel", "GetEquipedBookList", "EquippedQueryPrefix", typeof(bool));
            Patch(harmony, "BookModel", "GetGiveBookModel", "ReceiverQueryPrefix");
            Patch(harmony, "BookModel", "GetMaxPassiveCost", "BudgetPrefix");
            Patch(harmony, "BookModel", "CanToGivePassiveBook", "CanGiveBookPrefix", typeof(bool));
            Patch(harmony, "UI.UICustomSelectable", "OnPointerDown", "PressPrefix", down);
            Patch(harmony, "UI.UICustomSelectable", "OnSubmit", "PressPrefix", pointer);
            var trigger = pointer.Assembly.GetType("UnityEngine.EventSystems.EventTrigger", true);
            foreach (var input in new[] { "OnPointerDown", "OnSubmit" })
                harmony.Patch(trigger.GetMethod(input), prefix: new HarmonyMethod(AccessTools.Method(typeof(NativePassiveEditor), "PressPrefix")));
            harmony.Patch(pointer.Assembly.GetType("UnityEngine.UI.Selectable", true).GetMethod("OnPointerDown"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(NativePassiveEditor), "PressPrefix")));
            harmony.Patch(pointer.Assembly.GetType("UnityEngine.UI.Button", true).GetMethod("OnSubmit"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(NativePassiveEditor), "PressPrefix")));
            PatchPostfix(harmony, "UI.UIPassiveSuccessionCenterEquipBookSlot", "SetData", "SlotPostfix", typeof(BookModel));
            Patch(harmony, "UI.UIPassiveSuccessionCenterEquipBookSlot", "OnClickReleaseButton", "SlotClickPrefix");
            foreach (var type in new[] { "UI.UIPassiveSuccessionBookSlot", "UI.UIPassiveSuccessionCenterPassiveSlot", "UI.UIPassiveSuccessionSlot", "UIPassiveSuccessionEquipBookSlot" })
            {
                Patch(harmony, type, "OnPointerClick", "SlotClickPrefix", pointer);
                var data = type == "UI.UIPassiveSuccessionSlot" ? "SetDataModel" : "SetData";
                PatchPostfix(harmony, type, data, "SlotPostfix", type.Contains("PassiveSlot") || type == "UI.UIPassiveSuccessionSlot" ? typeof(PassiveModel) : typeof(BookModel));
            }
            foreach (var type in new[] { "UI.UIPassiveSuccessionBookSlot", "UI.UIPassiveSuccessionCenterPassiveSlot" })
                Patch(harmony, type, "OnXEvent", "XPrefix");
        }
        private static void Patch(Harmony harmony, string type, string name, string prefix, params Type[] args)
        {
            var method = typeof(BookModel).Assembly.GetType(type, true).GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
            if (method == null) throw new MissingMethodException(type, name);
            harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(NativePassiveEditor), prefix)));
        }
        private static void PatchPostfix(Harmony harmony, string type, string name, string postfix, params Type[] args)
        {
            var method = typeof(BookModel).Assembly.GetType(type, true).GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
            if (method == null) throw new MissingMethodException(type, name);
            harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(NativePassiveEditor), postfix)));
        }
        internal static bool TryOpen()
        {
            if (Active) return true;
            if (!NativeDeckEditor.Active || !NativeDeckEditor.Session.IsReadyForDeck || NativeDeckEditor.Session.DeckRequestPending) return false;
            NativePassiveDraft draft;
            if (!NativePassiveDraft.TryCreate(NativeDeckEditor.Session.RoomId, NativeDeckEditor.Snapshot,
                NativeDeckEditor.UnitIndex, SteamClient.SteamId.Value, out draft))
            { Status = "房主被动数据暂不可用或该核心书页不支持被动编辑。"; return false; }
            try
            {
                _draft = draft; _session = NativeDeckEditor.Session; _factory = new NativeDeckModels();
                _popup = NativeUi.Singleton("UI.UIPassiveSuccessionPopup");
                object marker; if (!MarkedPopups.TryGetValue(_popup, out marker)) MarkedPopups.Add(_popup, Mark);
                CaptureTree(_popup);
                foreach (var component in ((Component)_popup).GetComponentsInChildren<Component>(true))
                    if (component.GetType().Assembly == typeof(BookModel).Assembly) CaptureTree(component);
                BuildModels();
                _opening = true;
                try { NativeUi.Call(_popup, "Open"); } finally { _opening = false; }
                NativeUi.Set(_popup, "_currentUnit", null);
                NativeUi.Set(_popup, "_currentBookModel", Books[draft.BookToken]);
                NativeUi.Set(_popup, "_applyEvent", null);
                SetVisible(NativeUi.Get(_popup, "ob_Profile"), false);
                NativeUi.Call(NativeUi.Get(_popup, "currentBook"), "SetData", Books[draft.BookToken]);
                Status = draft.CanEdit ? "选择只修改本地草稿；应用后由房主确认。" : "当前馆员或核心书页只读。";
                Render();
                NativeUi.Call(NativeUi.Get(_popup, "anim"), "SetTrigger", "Open");
                return true;
            }
            catch (Exception exception) { Fail(exception); return false; }
        }
        private static void BuildModels()
        {
            foreach (var entry in _draft.Snapshot.PassiveBooks)
            {
                var core = _draft.Snapshot.CoreBooks.Find(candidate => candidate.BookToken == entry.BookToken);
                if (core == null || core.BookId <= 0 || (entry.Flags & PassiveBookFlags.Unsupported) != 0) continue;
                var book = _factory.CreateDetachedBook(core);
                Books.Add(entry.BookToken, book);
                MarkedBooks.Add(book, new BookTag { Owner = _draft, Token = entry.BookToken, MaxCost = entry.MaxCost });
            }
            if (!Books.ContainsKey(_draft.BookToken)) throw new InvalidOperationException("Receiver key page is unavailable.");
            foreach (var entry in _draft.Snapshot.PassiveBooks)
            {
                BookModel book; if (!Books.TryGetValue(entry.BookToken, out book)) continue;
                var passives = new List<PassiveModel>();
                for (var i = 0; i < entry.Slots.Count; i++)
                {
                    var slot = entry.Slots[i]; var passive = new PassiveModel(new LorId(slot.OriginId), book.instanceId, 0);
                    passive.originpassive = ClonePassiveXml(PassiveXmlList.Instance.GetData(new LorId(slot.OriginId)), slot, false);
                    var origin = NativeUi.Get(passive, "originData");
                    NativeUi.Set(origin, "currentpassive", ClonePassiveXml(PassiveXmlList.Instance.GetData(new LorId(slot.CurrentId)), slot, true));
                    NativeUi.Set(origin, "receivepassivebookId", slot.SourceBookToken == 0 ? book.instanceId : Instance(slot.SourceBookToken));
                    NativeUi.Set(origin, "givePassiveBookId", (slot.Flags & PassiveSlotFlags.Given) == 0 ? book.instanceId : Instance(entry.ReceiverBookToken));
                    NativeUi.Call(passive, "InitReservedData");
                    passives.Add(passive); Passives.Add(passive, Tuple.Create(entry.BookToken, (byte)i));
                    MarkedPassives.Add(passive, Mark);
                }
                NativeUi.Set(book, "_activatedAllPassives", passives);
                var data = NativeUi.Get(book, "originData");
                NativeUi.Set(data, "equipedPassiveBookInstanceId", entry.ReceiverBookToken == 0 ? -1 : Instance(entry.ReceiverBookToken));
                NativeUi.Set(data, "equipedBookIdListInPassive", entry.SourceTokens.Select(Instance).ToList());
                NativeUi.Call(book, "InitReservedDataForPassiveSuccession");
            }
        }
        private static PassiveXmlInfo ClonePassiveXml(PassiveXmlInfo template, ProgressSnapshot.PassiveSlotEntry slot, bool current)
        {
            if (template == null) throw new InvalidOperationException("Host passive XML is unavailable.");
            var copy = new PassiveXmlInfo();
            foreach (var field in typeof(PassiveXmlInfo).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                field.SetValue(copy, field.GetValue(template));
            if (copy.param != null) copy.param = new List<int>(copy.param);
            NativeUi.Set(copy, "_id", current ? slot.CurrentId : slot.OriginId);
            copy.cost = current ? slot.CurrentCost : slot.Cost;
            copy.rare = (Rarity)(current ? slot.CurrentRarity : slot.OriginRarity);
            copy.isNegative = current ? slot.CurrentNegative : (slot.Flags & PassiveSlotFlags.Negative) != 0;
            if (!current)
            {
                copy.InnerTypeId = slot.InnerTypeId;
                copy.isLock = (slot.Flags & PassiveSlotFlags.Locked) != 0;
                copy.isHide = (slot.Flags & PassiveSlotFlags.Hidden) != 0;
                copy.CanGivePassive = (slot.Flags & PassiveSlotFlags.CanGive) != 0;
                copy.CanReceivePassive = (slot.Flags & PassiveSlotFlags.CanReceive) != 0;
            }
            return copy;
        }
        private static int Instance(ulong token)
        {
            BookModel book; if (!Books.TryGetValue(token, out book)) throw new InvalidOperationException("Passive link is unavailable.");
            return book.instanceId;
        }
        private static void SyncReserved()
        {
            foreach (var entry in _draft.Snapshot.PassiveBooks)
            {
                BookModel book; if (!Books.TryGetValue(entry.BookToken, out book)) continue;
                NativeUi.Call(book, "InitReservedDataForPassiveSuccession");
                foreach (var passive in book.GetPassiveModelList()) NativeUi.Call(passive, "InitReservedData");
                if (entry.ReceiverBookToken == _draft.BookToken)
                    foreach (var passive in book.GetPassiveModelList())
                        NativeUi.Set(NativeUi.Get(passive, "reservedData"), "givePassiveBookId", book.instanceId);
            }
            var receiver = Books[_draft.BookToken]; var data = NativeUi.Get(receiver, "reservedData");
            NativeUi.Set(data, "equipedBookIdListInPassive", _draft.Sources().Select(Instance).ToList());
            // Detached old sources are freed even when no passive was borrowed.
            foreach (var token in _draft.Receiver.SourceTokens.Concat(_draft.Sources()).Distinct())
                NativeUi.Set(NativeUi.Get(Books[token], "reservedData"), "equipedPassiveBookInstanceId", _draft.IsSelected(token) ? receiver.instanceId : -1);
            for (var i = 0; i < _draft.SlotCount; i++)
            {
                var choice = _draft.Choice(i); var passive = receiver.GetPassiveModelList()[i]; var reserved = NativeUi.Get(passive, "reservedData");
                if (choice.SourceBookToken == 0)
                {
                    NativeUi.Set(reserved, "currentpassive", passive.originpassive);
                    NativeUi.Set(reserved, "receivepassivebookId", receiver.instanceId);
                }
                else
                {
                    var source = Books[choice.SourceBookToken].GetPassiveModelList()[choice.SourceSlotIndex];
                    NativeUi.Set(reserved, "currentpassive", source.originpassive);
                    NativeUi.Set(reserved, "receivepassivebookId", Instance(choice.SourceBookToken));
                    NativeUi.Set(NativeUi.Get(source, "reservedData"), "givePassiveBookId", receiver.instanceId);
                }
            }
        }
        private static void Render()
        {
            if (!Active || _rendering || !NativeUi.IsAlive(_popup)) return;
            _rendering = true;
            try
            {
                SyncReserved(); Slots.Clear(); Selectables.Clear();
                var receiver = Books[_draft.BookToken];
                NativeUi.Call(NativeUi.Get(_popup, "equipBookList"), "SetData", SelectedBooks());
                NativeUi.Call(NativeUi.Get(_popup, "equipPassiveList"), "SetEquipModelData", receiver.GetPassiveModelList());
                NativeUi.Call(NativeUi.Get(_popup, "centerBookListPanel"), "SetBooksData", SelectedBooks());
                var sources = _draft.Snapshot.PassiveBooks.Where(entry => entry.BookToken != _draft.BookToken &&
                    (entry.Flags & PassiveBookFlags.SourceAllowed) != 0 && Books.ContainsKey(entry.BookToken)).Select(entry => Books[entry.BookToken]).ToList();
                NativeUi.Call(NativeUi.Get(_popup, "rightBookListPanel"), "SetPassiveBooksData", sources);
                NativeUi.Call(_popup, "SetCostData");
                NativeUi.Set(NativeUi.Get(_popup, "cg"), "alpha", 1f);
                _renderedGeneration = _draft.Generation;
            }
            finally { _rendering = false; }
        }
        private static List<BookModel> SelectedBooks()
        { return Active ? _draft.Sources().Where(Books.ContainsKey).Select(token => Books[token]).ToList() : new List<BookModel>(); }
        private static bool CanInput()
        { return Active && !_rendering && _draft.CanEdit && NativeDeckEditor.ReadyForInput && ReferenceEquals(_session, NativeDeckEditor.Session) && !_session.DeckRequestPending; }
        internal static void Tick()
        {
            if (!Active) return;
            try
            {
                if (!NativeDeckEditor.Active || !ReferenceEquals(_session, NativeDeckEditor.Session) || !_session.IsReadyForDeck ||
                    NativeDeckEditor.UnitIndex != _draft.UnitIndex || !NativeUi.IsAlive(_popup) ||
                    !_draft.TryUpdate(_session.RoomId, _session.LatestSnapshot)) { Close(); return; }
                if (_draft.Submitted && !_session.DeckRequestPending)
                { Status = _session.DeckStatus; Close(); return; }
                if (_draft.Conflict) Status = "房主状态已更新，草稿保留供查看；请取消后重新打开再应用。";
                if (_renderedGeneration != _draft.Generation) Render();
            }
            catch (Exception exception) { Fail(exception); }
        }
        private static bool Shield(object popup)
        {
            object marker; return InRoom || Active || popup != null && MarkedPopups.TryGetValue(popup, out marker);
        }
        private static bool SetDataPrefix(object __instance, UnitDataModel __0)
        {
            if (!InRoom && !Active && !NativeDeckModels.IsMirrorUnit(__0))
            { MarkedPopups.Remove(__instance); return true; }
            if (NativeDeckEditor.Active && ReferenceEquals(__0, NativeDeckEditor.Current)) TryOpen();
            else Status = "请在联机馆员编辑页打开被动界面。";
            return false;
        }
        private static bool SetDataOnlyPrefix(object __instance, BookModel __0)
        {
            if (!InRoom && !Active && !IsPrivateBook(__0))
            { MarkedPopups.Remove(__instance); return true; }
            if (NativeDeckEditor.Active && ReferenceEquals(__0, NativeDeckEditor.Current.bookItem)) TryOpen();
            else Status = "请在联机馆员编辑页打开被动界面。";
            return false;
        }
        private static bool OpenPrefix(object __instance) { return _opening && ReferenceEquals(__instance, _popup) || !Shield(__instance); }
        private static bool InitPrefix(object __instance) { return !Shield(__instance); }
        private static bool ClosePrefix(object __instance)
        { if (_closing) return true; if (Active && ReferenceEquals(__instance, _popup)) { Close(); return false; } return true; }
        private static bool UnsafePrefix(object __instance) { return !Shield(__instance); }
        private static bool IsPrivateBook(BookModel book)
        { BookTag tag; return NativeDeckModels.IsMirrorBook(book) || book != null && MarkedBooks.TryGetValue(book, out tag); }
        private static bool IsPrivatePassive(PassiveModel passive)
        { object marker; return passive != null && MarkedPassives.TryGetValue(passive, out marker); }
        private static bool OtherBookPrefix(object __instance, BookModel __0) { return !Shield(__instance) && !IsPrivateBook(__0); }
        private static bool LateCallbackPrefix(object __instance) { return !Shield(__instance); }
        private static bool SourceLatePrefix(object __instance)
        {
            var panel = NativeUi.Get(__instance, "panel");
            var popup = panel == null ? null : NativeUi.Get(panel, "panel");
            return !Shield(popup) && !IsPrivateBook(NativeUi.Get(__instance, "currentbookmodel") as BookModel);
        }
        private static void Changed(bool changed)
        { if (changed) { Status = "草稿尚未应用。"; Render(); } else Status = "此项只读，或超过来源/费用限制，或被动不兼容。"; }
        private static bool AttachPrefix(object __instance, BookModel __0, ref bool __result)
        { if (!Shield(__instance)) { if (!IsPrivateBook(__0)) return true; __result = false; return false; } BookTag tag; __result = CanInput() && __0 != null && MarkedBooks.TryGetValue(__0, out tag) && ReferenceEquals(tag.Owner, _draft) && _draft.TryAttach(_draft.CaptureEvent(), tag.Token); Changed(__result); return false; }
        private static bool DetachPrefix(object __instance, BookModel __0)
        { if (!Shield(__instance)) return !IsPrivateBook(__0); BookTag tag; Changed(CanInput() && __0 != null && MarkedBooks.TryGetValue(__0, out tag) && ReferenceEquals(tag.Owner, _draft) && _draft.TryDetach(_draft.CaptureEvent(), tag.Token)); return false; }
        private static bool ChangePrefix(object __instance, object __0, ref bool __result)
        { var model = NativeUi.Get(__0, "passivemodel") as PassiveModel; if (!Shield(__instance)) { if (!IsPrivatePassive(model)) return true; __result = false; return false; } Tuple<ulong, byte> p; __result = CanInput() && model != null && Passives.TryGetValue(model, out p) && _draft.TryInherit(_draft.CaptureEvent(), p.Item1, p.Item2); Changed(__result); return false; }
        private static bool ReleasePrefix(object __instance, PassiveModel __0)
        {
            if (!Shield(__instance)) return !IsPrivatePassive(__0); Tuple<ulong, byte> p;
            if (!CanInput() || __0 == null || !Passives.TryGetValue(__0, out p)) return false;
            for (var i = 0; i < _draft.SlotCount; i++) if (_draft.Choice(i).SourceBookToken == p.Item1 && _draft.Choice(i).SourceSlotIndex == p.Item2)
            { Changed(_draft.TryRestore(_draft.CaptureEvent(), i)); break; }
            return false;
        }
        private static bool ReleaseReversePrefix(object __instance, object __0)
        { var model = NativeUi.Get(__0, "passivemodel") as PassiveModel; if (!Shield(__instance)) return !IsPrivatePassive(model); Tuple<ulong, byte> p; Changed(CanInput() && model != null && Passives.TryGetValue(model, out p) && p.Item1 == _draft.BookToken && _draft.TryRestore(_draft.CaptureEvent(), p.Item2)); return false; }
        private static bool ResetPrefix(object __instance)
        { if (!Shield(__instance)) return true; Changed(CanInput() && _draft.TryRestoreAll(_draft.CaptureEvent())); return false; }
        private static bool ApplyPrefix(object __instance)
        { if (!Shield(__instance)) return true; if (CanInput()) Confirm(true); else Status = _draft != null && _draft.Conflict ? "房主状态已更新，请取消后重新打开。" : "当前只读或等待房主确认。"; return false; }
        private static bool CancelPrefix(object __instance)
        {
            if (!Shield(__instance)) return true;
            if (Active) Confirm(false);
            else if (NativeUi.IsAlive(__instance))
            {
                // A vanilla popup may have been open before joining the room.
                // Closing its UI preserves the real unsubmitted reserved data.
                _closing = true;
                try { NativeUi.Call(__instance, "Close"); }
                finally { _closing = false; }
            }
            return false;
        }
        private static void Confirm(bool apply)
        {
            var confirmation = new Confirmation { Owner = _draft, Event = _draft.CaptureEvent(), Apply = apply,
                Slots = _draft.Selections(), Sources = _draft.Sources() };
            var alarm = NativeUi.Singleton("UI.UIAlarmPopup");
            var method = alarm.GetType().GetMethods().First(info => info.Name == "SetAlarmText" && info.GetParameters().Length == 5);
            var p = method.GetParameters();
            var callback = Delegate.CreateDelegate(p[2].ParameterType, confirmation, typeof(Confirmation).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic));
            method.Invoke(alarm, new object[] { Enum.ToObject(p[0].ParameterType, apply ? 21 : 24), Enum.ToObject(p[1].ParameterType, 1), callback, "", "" });
        }
        private static bool EquippedQueryPrefix(BookModel __instance, ref List<BookModel> __result)
        { BookTag tag; if (!MarkedBooks.TryGetValue(__instance, out tag)) return true; __result = Active && ReferenceEquals(tag.Owner, _draft) && tag.Token == _draft.BookToken ? SelectedBooks() : new List<BookModel>(); return false; }
        private static bool ReceiverQueryPrefix(BookModel __instance, ref BookModel __result)
        { BookTag tag; if (!MarkedBooks.TryGetValue(__instance, out tag)) return true; __result = Active && ReferenceEquals(tag.Owner, _draft) && _draft.IsSelected(tag.Token) ? Books[_draft.BookToken] : null; return false; }
        private static bool BudgetPrefix(BookModel __instance, ref int __result)
        { BookTag tag; if (!MarkedBooks.TryGetValue(__instance, out tag)) return true; __result = tag.MaxCost; return false; }
        private static bool CanGiveBookPrefix(BookModel __instance, ref bool __result)
        { BookTag tag; if (!MarkedBooks.TryGetValue(__instance, out tag)) return true; __result = Active && ReferenceEquals(tag.Owner, _draft) && _draft.CanAttach(tag.Token); return false; }
        private static bool DisabledPrefix(object __instance)
        {
            var book = NativeUi.Get(__instance, "currentbookmodel") as BookModel;
            if (!InRoom && !NativeDeckModels.IsMirrorBook(book)) return true;
            BookTag tag; var enabled = Active && book != null && MarkedBooks.TryGetValue(book, out tag) && _draft.CanEdit && _draft.CanAttach(tag.Token);
            NativeUi.Set(__instance, "isDisabled", !enabled);
            SetVisible(NativeUi.Get(__instance, "ob_blockFrame"), !enabled);
            return false;
        }
        private static bool PreviewPrefix(object __instance, object __0)
        {
            var book = NativeUi.Get(__0, "currentbookmodel") as BookModel;
            if (!InRoom && !Active && !NativeDeckModels.IsMirrorBook(book)) return true;
            SetVisible(NativeUi.Get(__instance, "ob_OtherEquipInfo"), false);
            if (Active && book != null && MarkedBooks.TryGetValue(book, out var tag) && ReferenceEquals(tag.Owner, _draft))
                NativeUi.Call(NativeUi.Get(__instance, "previewPanel"), "SetData", book);
            return false;
        }
        private static void SlotPostfix(object __instance)
        {
            if (!Active) return;
            if (!Captured.Contains(__instance)) NewRows.Add(((Component)__instance).gameObject);
            Capture(__instance);
            var name = __instance.GetType().FullName; var binding = new SlotTag { Event = _draft.CaptureEvent(), OwnerSlot = __instance };
            if (name == "UI.UIPassiveSuccessionBookSlot" || name == "UIPassiveSuccessionEquipBookSlot" || name == "UI.UIPassiveSuccessionCenterEquipBookSlot")
            {
                var book = NativeUi.Get(__instance, name == "UI.UIPassiveSuccessionCenterEquipBookSlot" ? "_currentbookmodel" : name.StartsWith("UI.") ? "currentbookmodel" : "bookmodel") as BookModel;
                BookTag tag; if (book == null || !MarkedBooks.TryGetValue(book, out tag)) return;
                binding.BookToken = tag.Token; binding.Kind = name == "UI.UIPassiveSuccessionCenterEquipBookSlot" ? 4 : name.StartsWith("UI.") ? 0 : 3;
            }
            else
            {
                var model = NativeUi.Get(__instance, "passivemodel") as PassiveModel; Tuple<ulong, byte> p;
                if (model == null || !Passives.TryGetValue(model, out p)) return;
                binding.BookToken = p.Item1; binding.Slot = p.Item2; binding.Kind = name.Contains("Center") ? 1 : 2;
            }
            Slots[__instance] = binding;
            var selectable = name == "UIPassiveSuccessionEquipBookSlot" ? NativeUi.Get(__instance, "gameObject") :
                name == "UI.UIPassiveSuccessionCenterEquipBookSlot" ? NativeUi.Get(__instance, "button_UnEquipButton") : NativeUi.Get(__instance, "selectable");
            if (selectable != null) Selectables[selectable] = binding;
            if (name == "UI.UIPassiveSuccessionCenterEquipBookSlot" && selectable != null)
                Selectables[NativeUi.Get(selectable, "gameObject")] = binding;
            if (name == "UIPassiveSuccessionEquipBookSlot")
                foreach (var component in ((Component)__instance).GetComponentsInChildren<Component>(true))
                    if (component.GetType().FullName == "UnityEngine.EventSystems.EventTrigger") Selectables[component.gameObject] = binding;
        }
        private static bool PressPrefix(object __instance)
        {
            if (!Active) return true;
            SlotTag tag; var key = __instance;
            if (!Selectables.TryGetValue(key, out tag))
            { key = NativeUi.Get(__instance, "gameObject"); if (key == null || !Selectables.TryGetValue(key, out tag)) return true; }
            Presses[tag.OwnerSlot] = tag; return true;
        }
        private static bool SlotClickPrefix(object __instance)
        {
            SlotTag shown; if (!Slots.TryGetValue(__instance, out shown)) return !InRoom && !IsPrivateSlot(__instance);
            SlotTag pressed;
            if (!Presses.TryGetValue(__instance, out pressed)) return false;
            Presses.Remove(__instance);
            if (!Active || !ReferenceEquals(shown, pressed) || !_draft.IsCurrent(pressed.Event) || !CanInput()) return false;
            Operate(pressed); return false;
        }
        private static bool XPrefix(object __instance)
        {
            SlotTag tag; if (!Slots.TryGetValue(__instance, out tag)) return !InRoom && !IsPrivateSlot(__instance);
            if (tag.Kind == 0) return true; // Vanilla X moves focus to the filter.
            if (CanInput() && _draft.IsCurrent(tag.Event))
            {
                if (tag.Kind == 1) Changed(_draft.TryDetach(tag.Event, tag.BookToken));
                else Operate(tag);
            }
            return false;
        }
        private static bool IsPrivateSlot(object slot)
        {
            var type = slot.GetType().FullName;
            if (type == "UIPassiveSuccessionEquipBookSlot") return IsPrivateBook(NativeUi.Get(slot, "bookmodel") as BookModel);
            if (type == "UI.UIPassiveSuccessionCenterEquipBookSlot") return IsPrivateBook(NativeUi.Get(slot, "_currentbookmodel") as BookModel);
            if (type == "UI.UIPassiveSuccessionBookSlot") return IsPrivateBook(NativeUi.Get(slot, "currentbookmodel") as BookModel);
            return IsPrivatePassive(NativeUi.Get(slot, "passivemodel") as PassiveModel);
        }
        private static void Operate(SlotTag tag)
        {
            var changed = false;
            if (tag.Kind == 0) changed = _draft.IsSelected(tag.BookToken) ? _draft.TryDetach(tag.Event, tag.BookToken) : _draft.TryAttach(tag.Event, tag.BookToken);
            else if (tag.Kind == 3 || tag.Kind == 4) changed = _draft.TryDetach(tag.Event, tag.BookToken);
            else if (tag.Kind == 2) changed = _draft.TryRestore(tag.Event, tag.Slot);
            else
            {
                for (var i = 0; i < _draft.SlotCount; i++)
                    if (_draft.Choice(i).SourceBookToken == tag.BookToken && _draft.Choice(i).SourceSlotIndex == tag.Slot)
                    { Changed(_draft.TryRestore(tag.Event, i)); return; }
                changed = _draft.TryInherit(tag.Event, tag.BookToken, tag.Slot);
            }
            Changed(changed);
        }
        private static void CaptureTree(object owner)
        {
            if (owner == null || Captured.Contains(owner)) return;
            Capture(owner);
            // UI containers and existing row objects are captured, not game models
            // or static XML. New rows retain weak guarded mirror marks after close.
            foreach (var field in owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(owner); if (value == null) continue;
                if (value is Component && value.GetType().Assembly == typeof(BookModel).Assembly) CaptureTree(value);
                var list = value as IList;
                if (list != null) foreach (var item in list) if (item is Component && item.GetType().Assembly == typeof(BookModel).Assembly) CaptureTree(item);
            }
        }
        private static void Capture(object owner)
        {
            if (owner == null || !Captured.Add(owner)) return;
            for (var type = owner.GetType(); type != null && type.Assembly == typeof(BookModel).Assembly; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(owner); var list = value as IList;
                    var state = new SavedField { Owner = owner, Field = field, Value = value };
                    if (list != null) { state.Items = new object[list.Count]; list.CopyTo(state.Items, 0); }
                    Saved.Add(state);
                }
        }
        private static void SetVisible(object component, bool visible)
        {
            if (!NativeUi.IsAlive(component)) return;
            var gameObject = component as GameObject ?? NativeUi.Get(component, "gameObject") as GameObject;
            if (!NativeUi.IsAlive(gameObject)) return;
            if (!Visibility.ContainsKey(gameObject)) Visibility.Add(gameObject, gameObject.activeSelf);
            gameObject.SetActive(visible);
        }
        internal static void Close()
        {
            if (_closing) return;
            _closing = true;
            try
            {
                RetireDraft();
                if (NativeUi.IsAlive(_popup))
                    try { NativeUi.Call(_popup, "Close"); }
                    catch (Exception exception) { Debug.LogError("[RuinaCoop] Passive popup close failed: " + exception); }
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
                    }
                    catch (Exception exception) { Debug.LogError("[RuinaCoop] Passive UI restoration failed: " + exception); }
                }
                foreach (var pair in Visibility) if (NativeUi.IsAlive(pair.Key))
                    try { pair.Key.SetActive(pair.Value); } catch (Exception exception) { Debug.LogError(exception); }
                foreach (var row in NewRows) if (NativeUi.IsAlive(row)) UnityEngine.Object.Destroy(row);
            }
            finally
            {
                _draft = null; _popup = null; _session = null;
                if (_factory != null) _factory.Dispose(); _factory = null;
                Books.Clear(); Passives.Clear(); Slots.Clear(); Selectables.Clear(); Presses.Clear(); Saved.Clear(); Captured.Clear(); Visibility.Clear(); Properties.Clear(); NewRows.Clear();
                _opening = false; _rendering = false; _renderedGeneration = 0; _closing = false;
            }
        }
        private static void RetireDraft()
        {
            var draft = _draft;
            _draft = null;
            if (draft != null) draft.Close();
        }
        private static void Fail(Exception exception)
        {
            Status = "联机被动界面暂不可用：" + exception.GetBaseException().Message;
            Debug.LogError("[RuinaCoop] Native passive editor failed: " + exception);
            Close();
        }
    }
#endif
}
