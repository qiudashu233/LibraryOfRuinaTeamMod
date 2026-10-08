using System;
using System.Collections.Generic;
using System.Reflection;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaCoop
{
    internal enum EquipmentTransactionResult { Accepted, Rejected, Failed }

    // Session resolves the displayed room tokens and checks the requesting owner.
    // This adapter validates live vanilla objects and owns the complete local edit.
    internal static class EquipmentTransaction
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo UnitBook = Required(typeof(UnitDataModel), "_bookItem");
        private static readonly FieldInfo UnitAppearanceBook = Required(typeof(UnitDataModel), "_CustomBookItem");
        private static readonly FieldInfo BookDeck = Required(typeof(BookModel), "_deck");
        private static readonly FieldInfo BookDecks = Required(typeof(BookModel), "_deckList");
        private static readonly FieldInfo DeckCards = Required(typeof(DeckModel), "_deck");
        private static readonly FieldInfo InventoryCards = Required(typeof(InventoryModel), "_cardList");
        private static readonly FieldInfo InventoryBooks = Required(typeof(BookInventoryModel), "_bookList");
        private static readonly FieldInfo LibraryFloors = Required(typeof(LibraryModel), "_floorList");
        private static readonly FieldInfo FloorUnits = Required(typeof(LibraryFloorModel), "_unitDataList");

        // A common inventory-row predicate, independent of the selected librarian.
        internal static bool CanUseTarget(BookModel target, out string reason)
        {
            reason = "";
            if (target == null || NativeDeckModels.IsMirrorBook(target)) return Reject("The host key page is unavailable.", out reason);
            var inventory = BookInventoryModel.Instance;
            if (inventory == null || !inventory.GetBookList_equip().Contains(target) || target.instanceId <= 0)
                return Reject("This is not an ordinary host inventory instance.", out reason);
            if (!IsOrdinaryBook(target, false, out reason)) return false;
            if (ReferenceEquals(target, inventory.GetBlackSilenceBook()))
                return Reject("Special key pages are not supported yet.", out reason);
            if (target.owner != null) return Reject("This key page is already equipped.", out reason);
            if (IsPassiveDonor(target) || HasPassiveDraft(target) || !target.CanEquipBookByGivePassive())
                return Reject("This key page is a passive donor or has an unfinished passive draft.", out reason);
            return true;
        }

        internal static bool CanEquip(UnitDataModel unit, BookModel target, out string reason)
        {
            reason = "";
            if (unit == null || NativeDeckModels.IsMirrorUnit(unit)) return Reject("The host librarian is unavailable.", out reason);
            var library = LibraryModel.Instance;
            var floors = library == null ? null : LibraryFloors.GetValue(library) as List<LibraryFloorModel>;
            var floor = floors == null ? null : floors.Find(candidate => candidate != null && candidate.Sephirah == unit.OwnerSephirah);
            var units = floor == null ? null : FloorUnits.GetValue(floor) as List<UnitDataModel>;
            if (units == null || !units.Contains(unit))
                return Reject("The librarian is not registered in the host library.", out reason);
            if (unit.IsChangeItemLock()) return Reject("This librarian's equipment is locked.", out reason);
            if (!IsOrdinaryBook(unit.bookItem, ReferenceEquals(unit.bookItem, unit.defaultBook), out reason)) return false;
            var rawBook = UnitBook.GetValue(unit) as BookModel;
            if (rawBook != null && !ReferenceEquals(rawBook.owner, unit))
                return Reject("The librarian's current key-page ownership is inconsistent.", out reason);
            if (IsPassiveDonor(unit.bookItem) || HasPassiveDraft(unit.bookItem))
                return Reject("The current key page has passive donor links or an unfinished draft.", out reason);
            if (ReferenceEquals(unit.bookItem, target)) return Reject("This key page is already selected.", out reason);
            if (library.PlayHistory.Start_TheBlueReverberationPrimaryBattle == 1 && library.IsClearTheBlueReverberationPrimary(unit.OwnerSephirah))
                return Reject("Equipment is locked on this completed reception floor.", out reason);
            return CanUseTarget(target, out reason);
        }

        private static bool IsOrdinaryBook(BookModel book, bool allowDefault, out string reason)
        {
            reason = "";
            var id = book == null ? null : book.BookId;
            if (ReferenceEquals(id, null) || !id.IsBasic() || id.id <= 0 || book.ClassInfo == null || book.ClassInfo.isError)
                return Reject("Only supported vanilla key pages can be changed.", out reason);
            // 250022 has a dedicated Gebura substitution branch in EquipBook.
            if (id.id == 250022 || book.IsFixedDeck() || book.IsDeckLocked() || book.IsLockByBluePrimary() || book.IsMultiDeck())
                return Reject("Fixed, multiple-deck and special key pages are not supported yet.", out reason);
            if (book.ClassInfo.canNotEquip || !allowDefault && book.ClassInfo.optionList.Contains(BookOption.Basic))
                return Reject("This key page cannot be equipped from inventory.", out reason);
            foreach (var deck in book.GetDeckAll_nocopy())
            {
                if (deck == null) return Reject("This key page has an invalid deck.", out reason);
                foreach (var card in deck.GetCardList_nocopy())
                    if (card == null || ReferenceEquals(card.id, null) || !card.id.IsBasic() || card.id.id <= 0)
                        return Reject("Workshop or invalid combat pages are not supported in this equipment transaction.", out reason);
            }
            return true;
        }

        internal static bool IsPassiveDonor(BookModel book)
        {
            if (book == null) return true;
            foreach (var name in new[] { "originData", "reservedData" })
            {
                var data = Required(typeof(BookModel), name).GetValue(book);
                if (data == null || (int)Required(data.GetType(), "equipedPassiveBookInstanceId").GetValue(data) != -1) return true;
            }
            return false;
        }

        internal static bool HasPassiveDraft(BookModel book)
        {
            if (book == null) return true;
            var original = Required(typeof(BookModel), "originData").GetValue(book);
            var pending = Required(typeof(BookModel), "reservedData").GetValue(book);
            if (original == null || pending == null) return true;
            var originalSources = Required(original.GetType(), "equipedBookIdListInPassive").GetValue(original) as List<int>;
            var pendingSources = Required(pending.GetType(), "equipedBookIdListInPassive").GetValue(pending) as List<int>;
            if (originalSources == null || pendingSources == null) return true;
            // Book constructors allocate an empty reserved object, but loading
            // only fills origin. Passive reserved buffers are created when the
            // vanilla succession popup opens; null is the ordinary loaded state.
            var compareBook = pendingSources.Count != 0 ||
                (int)Required(pending.GetType(), "equipedPassiveBookInstanceId").GetValue(pending) != -1;
            var passives = Required(typeof(BookModel), "_activatedAllPassives").GetValue(book) as List<PassiveModel>;
            if (passives == null) return true;
            foreach (var passive in passives)
            {
                if (passive == null) return true;
                var originalPassive = Required(typeof(PassiveModel), "originData").GetValue(passive);
                var pendingPassive = Required(typeof(PassiveModel), "reservedData").GetValue(passive);
                if (originalPassive == null) return true;
                if (pendingPassive == null) continue;
                compareBook = true;
                if (!SavedDataEquals(originalPassive, pendingPassive)) return true;
            }
            return compareBook && !SavedDataEquals(original, pending);
        }

        private static bool SavedDataEquals(object left, object right)
        {
            if (left == null || right == null || left.GetType() != right.GetType()) return false;
            foreach (var field in left.GetType().GetFields(Fields))
            {
                var a = field.GetValue(left); var b = field.GetValue(right);
                var list = a as List<int>;
                if (list != null)
                {
                    var other = b as List<int>;
                    if (other == null || list.Count != other.Count) return false;
                    for (var i = 0; i < list.Count; i++) if (list[i] != other[i]) return false;
                }
                else if (field.Name == "currentpassive")
                {
                    // Removed native passive slots can retain null on both sides.
                    if (ReferenceEquals(a, b)) continue;
                    var passiveA = a as PassiveXmlInfo; var passiveB = b as PassiveXmlInfo;
                    if (passiveA == null || passiveB == null || passiveA.id != passiveB.id ||
                        passiveA.isNegative != passiveB.isNegative || passiveA.rare != passiveB.rare) return false;
                }
                else if (!Equals(a, b)) return false;
            }
            return true;
        }

        internal static EquipmentTransactionResult Equip(UnitDataModel unit, BookModel target, Func<bool> publish, out string reason)
        {
            var localReason = "";
            EquipmentTransactionResult result;
            try
            {
                // false,false preserves the game's item locks and reruns normal
                // deck restrictions. The bool result is always false in vanilla;
                // the adapter verifies the resulting object relationships instead.
                result = DeckGuard.RunAuthorized(() => Apply(unit, target, publish, out localReason));
            }
            catch (Exception exception)
            {
                localReason = "Equipment could not be edited: " + exception.GetBaseException().Message;
                Debug.LogError("[RuinaCoop] Equipment authorization failed: " + exception);
                result = EquipmentTransactionResult.Failed;
            }
            reason = localReason;
            return result;
        }

        private static EquipmentTransactionResult Apply(UnitDataModel unit, BookModel target, Func<bool> publish, out string reason)
        {
            reason = "";
            if (!CanEquip(unit, target, out reason)) return EquipmentTransactionResult.Rejected;
            EditState before = null;
            try
            {
                before = new EditState(unit, target);
                unit.EquipBookForUI(target, false, false);
                if (!before.CheckEquipped(target))
                {
                    before.Restore();
                    reason = "The vanilla game did not equip the requested key-page instance.";
                    return EquipmentTransactionResult.Rejected;
                }
                // The real inventory equip UI selects neutral/book appearance.
                unit.appearanceType = Gender.N;
                if (publish != null && !publish())
                {
                    before.Restore();
                    reason = "The updated equipment snapshot could not be published.";
                    return EquipmentTransactionResult.Failed;
                }
                return EquipmentTransactionResult.Accepted;
            }
            catch (Exception exception)
            {
                if (before != null) before.Restore();
                reason = "Equipment was rolled back: " + exception.GetBaseException().Message;
                Debug.LogError("[RuinaCoop] Equipment transaction rolled back: " + exception);
                return EquipmentTransactionResult.Failed;
            }
        }

        private static FieldInfo Required(Type type, string name)
        {
            var field = type.GetField(name, Fields);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static bool Reject(string message, out string reason) { reason = message; return false; }

        private sealed class EditState
        {
            private readonly UnitDataModel _unit;
            private readonly BookModel _rawBook;
            private readonly BookModel _appearanceBook;
            private readonly Gender _appearance;
            private readonly InventoryModel _inventory;
            private readonly BookInventoryModel _bookInventory;
            private readonly List<DiceCardItemModel> _cards;
            private readonly List<DiceCardItemModel> _originalCards;
            private readonly int[] _counts;
            private readonly List<BookModel> _books;
            private readonly List<BookModel> _originalBooks;
            private readonly List<BookState> _bookStates = new List<BookState>();

            internal EditState(UnitDataModel unit, BookModel target)
            {
                _unit = unit;
                _rawBook = UnitBook.GetValue(unit) as BookModel;
                _appearanceBook = UnitAppearanceBook.GetValue(unit) as BookModel;
                _appearance = unit.appearanceType;
                _inventory = InventoryModel.Instance;
                _bookInventory = BookInventoryModel.Instance;
                _cards = InventoryCards.GetValue(_inventory) as List<DiceCardItemModel>;
                _books = InventoryBooks.GetValue(_bookInventory) as List<BookModel>;
                if (_cards == null || _books == null) throw new InvalidOperationException("The host inventory is unavailable.");
                _originalCards = new List<DiceCardItemModel>(_cards);
                _counts = new int[_originalCards.Count];
                for (var i = 0; i < _counts.Length; i++) _counts[i] = _originalCards[i].num;
                _originalBooks = new List<BookModel>(_books);
                AddBook(unit.bookItem);
                AddBook(target);
                AddBook(_appearanceBook);
            }

            private void AddBook(BookModel book)
            {
                if (book == null || _bookStates.Exists(state => ReferenceEquals(state.Book, book))) return;
                _bookStates.Add(new BookState(book));
                // Committed inheritance is preserved. Capture its source/recipient
                // closure too so a failed capture or unexpected lazy reconciliation
                // cannot leave a source page locked or partially rewritten.
                foreach (var name in new[] { "originData", "reservedData" })
                {
                    var data = Required(typeof(BookModel), name).GetValue(book);
                    if (data == null) continue;
                    var ids = Required(data.GetType(), "equipedBookIdListInPassive").GetValue(data) as List<int>;
                    if (ids != null) foreach (var id in ids) AddBook(ResolveRelatedBook(id));
                    var receiver = (int)Required(data.GetType(), "equipedPassiveBookInstanceId").GetValue(data);
                    if (receiver != -1) AddBook(ResolveRelatedBook(receiver));
                }
            }

            private BookModel ResolveRelatedBook(int instanceId)
            {
                BookModel book = null;
                foreach (var candidate in _originalBooks)
                {
                    if (candidate == null || candidate.instanceId != instanceId) continue;
                    if (book != null) throw new InvalidOperationException("A passive key-page instance id is ambiguous in the host inventory.");
                    book = candidate;
                }
                if (book == null) throw new InvalidOperationException("A passive source key-page instance is missing from the host inventory.");
                return book;
            }

            internal bool CheckEquipped(BookModel target)
            {
                if (!ReferenceEquals(UnitBook.GetValue(_unit), target) || !ReferenceEquals(_unit.bookItem, target) ||
                    !ReferenceEquals(target.owner, _unit) || !ReferenceEquals(InventoryCards.GetValue(_inventory), _cards) ||
                    !ReferenceEquals(InventoryBooks.GetValue(_bookInventory), _books) ||
                    _books.Count != _originalBooks.Count) return false;
                for (var i = 0; i < _books.Count; i++) if (!ReferenceEquals(_books[i], _originalBooks[i])) return false;
                if (_rawBook != null && _rawBook.owner != null) return false;
                foreach (var state in _bookStates)
                    if (!state.CheckStructure() || !state.CheckOwner(ReferenceEquals(state.Book, target) ? _unit :
                        ReferenceEquals(state.Book, _rawBook) ? null : state.OriginalOwner)) return false;
                return true;
            }

            internal void Restore()
            {
                UnitBook.SetValue(_unit, _rawBook);
                UnitAppearanceBook.SetValue(_unit, _appearanceBook);
                _unit.appearanceType = _appearance;
                foreach (var state in _bookStates) state.Restore();
                InventoryCards.SetValue(_inventory, _cards);
                _cards.Clear();
                for (var i = 0; i < _originalCards.Count; i++) { _originalCards[i].num = _counts[i]; _cards.Add(_originalCards[i]); }
                InventoryBooks.SetValue(_bookInventory, _books);
                _books.Clear(); _books.AddRange(_originalBooks);
            }
        }

        private sealed class BookState
        {
            internal readonly BookModel Book;
            private readonly UnitDataModel _owner;
            private readonly int _instanceId;
            private readonly DeckModel _current;
            private readonly List<DeckModel> _decks;
            private readonly List<DeckModel> _originalDecks;
            private readonly List<DeckState> _states = new List<DeckState>();
            private readonly FieldInfo _activeField;
            private readonly List<PassiveModel> _active;
            private readonly List<PassiveModel> _originalActive;
            private readonly List<PassiveState> _passives = new List<PassiveState>();
            private readonly FieldInfo _originField;
            private readonly FieldInfo _reservedField;
            private readonly ObjectState _origin;
            private readonly ObjectState _reserved;
            internal UnitDataModel OriginalOwner { get { return _owner; } }
            internal BookState(BookModel book)
            {
                Book = book;
                _owner = book.owner;
                _instanceId = book.instanceId;
                _activeField = Required(typeof(BookModel), "_activatedAllPassives");
                _active = _activeField.GetValue(book) as List<PassiveModel>;
                if (_active == null) throw new InvalidOperationException("The host key-page passive list is unavailable.");
                _originalActive = new List<PassiveModel>(_active);
                foreach (var passive in _originalActive)
                {
                    if (passive == null) throw new InvalidOperationException("The host key page contains an invalid passive.");
                    if (!_passives.Exists(state => ReferenceEquals(state.Passive, passive))) _passives.Add(new PassiveState(passive));
                }
                _originField = Required(typeof(BookModel), "originData");
                _reservedField = Required(typeof(BookModel), "reservedData");
                _origin = new ObjectState(_originField.GetValue(book));
                _reserved = new ObjectState(_reservedField.GetValue(book));
                _current = BookDeck.GetValue(book) as DeckModel;
                _decks = BookDecks.GetValue(book) as List<DeckModel>;
                if (_decks == null || _current == null) throw new InvalidOperationException("The host key-page decks are unavailable.");
                _originalDecks = new List<DeckModel>(_decks);
                foreach (var deck in _originalDecks) Add(deck);
                Add(_current);
            }
            private void Add(DeckModel deck)
            {
                if (deck == null) throw new InvalidOperationException("The host key page contains an invalid deck.");
                if (!_states.Exists(state => ReferenceEquals(state.Deck, deck))) _states.Add(new DeckState(deck));
            }
            internal bool CheckStructure()
            {
                if (Book.instanceId != _instanceId || !ReferenceEquals(BookDeck.GetValue(Book), _current) ||
                    !ReferenceEquals(BookDecks.GetValue(Book), _decks) || _decks.Count != _originalDecks.Count)
                    return false;
                for (var i = 0; i < _decks.Count; i++) if (!ReferenceEquals(_decks[i], _originalDecks[i])) return false;
                foreach (var state in _states) if (!state.SameList()) return false;
                return true;
            }
            internal bool CheckOwner(UnitDataModel expected) { return ReferenceEquals(Book.owner, expected); }
            internal void Restore()
            {
                Book.owner = _owner;
                Book.instanceId = _instanceId;
                BookDeck.SetValue(Book, _current);
                BookDecks.SetValue(Book, _decks);
                _decks.Clear(); _decks.AddRange(_originalDecks);
                foreach (var state in _states) state.Restore();
                _activeField.SetValue(Book, _active);
                _active.Clear(); _active.AddRange(_originalActive);
                _originField.SetValue(Book, _origin.Target);
                _reservedField.SetValue(Book, _reserved.Target);
                _origin.Restore(); _reserved.Restore();
                foreach (var state in _passives) state.Restore();
            }
        }

        private sealed class PassiveState
        {
            internal readonly PassiveModel Passive;
            private readonly FieldInfo _originField;
            private readonly FieldInfo _reservedField;
            private readonly ObjectState _fields;
            private readonly ObjectState _origin;
            private readonly ObjectState _reserved;
            internal PassiveState(PassiveModel passive)
            {
                Passive = passive;
                _fields = new ObjectState(passive);
                _originField = Required(typeof(PassiveModel), "originData");
                _reservedField = Required(typeof(PassiveModel), "reservedData");
                _origin = new ObjectState(_originField.GetValue(passive));
                _reserved = new ObjectState(_reservedField.GetValue(passive));
            }
            internal void Restore()
            {
                _fields.Restore();
                _originField.SetValue(Passive, _origin.Target);
                _reservedField.SetValue(Passive, _reserved.Target);
                _origin.Restore(); _reserved.Restore();
            }
        }

        // Saved-data classes contain scalar/XML references and (for books) one
        // List<int>. Restore the original objects and list identity, without
        // invoking save loaders, migrations or passive reconciliation methods.
        private sealed class ObjectState
        {
            internal readonly object Target;
            private readonly FieldInfo[] _fields;
            private readonly object[] _values;
            private readonly List<int>[] _integers;
            internal ObjectState(object target)
            {
                Target = target;
                _fields = target == null ? new FieldInfo[0] : target.GetType().GetFields(Fields);
                _values = new object[_fields.Length];
                _integers = new List<int>[_fields.Length];
                for (var i = 0; i < _fields.Length; i++)
                {
                    _values[i] = _fields[i].GetValue(target);
                    var integers = _values[i] as List<int>;
                    if (integers != null) _integers[i] = new List<int>(integers);
                }
            }
            internal void Restore()
            {
                for (var i = 0; i < _fields.Length; i++)
                {
                    _fields[i].SetValue(Target, _values[i]);
                    if (_integers[i] == null) continue;
                    var list = (List<int>)_values[i];
                    list.Clear(); list.AddRange(_integers[i]);
                }
            }
        }

        private sealed class DeckState
        {
            internal readonly DeckModel Deck;
            private readonly List<DiceCardXmlInfo> _list;
            private readonly List<DiceCardXmlInfo> _original;
            internal DeckState(DeckModel deck)
            {
                Deck = deck;
                _list = DeckCards.GetValue(deck) as List<DiceCardXmlInfo>;
                if (_list == null || !ReferenceEquals(_list, deck.GetCardList_nocopy())) throw new InvalidOperationException("Invalid host deck collection.");
                _original = new List<DiceCardXmlInfo>(_list);
            }
            internal bool SameList() { return ReferenceEquals(DeckCards.GetValue(Deck), _list); }
            internal void Restore() { DeckCards.SetValue(Deck, _list); _list.Clear(); _list.AddRange(_original); }
        }
    }
}
