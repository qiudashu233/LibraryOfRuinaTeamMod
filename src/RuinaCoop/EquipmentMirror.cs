using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace RuinaCoop
{
    // Read-only host inventory projection. Detached guest books are never registered here.
    internal static class EquipmentMirror
    {
        internal const int MaxBooks = 2048;
        private const int MaxPacketBytes = 65536;
        private const CoreBookFlags AllFlags = CoreBookFlags.Equipped | CoreBookFlags.PassiveBound |
            CoreBookFlags.CannotEquip | CoreBookFlags.FixedDeck | CoreBookFlags.MultiDeck | CoreBookFlags.Locked |
            CoreBookFlags.UnsupportedId | CoreBookFlags.InvalidInstance | CoreBookFlags.OwnerMismatch |
            CoreBookFlags.UnsupportedCards | CoreBookFlags.UnsupportedDisplay | CoreBookFlags.DraftMismatch;
        private static readonly FieldInfo AllFloorsField = typeof(LibraryModel).GetField(
            "_floorList", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly ConditionalWeakTable<BookModel, BookIdentity> Identities =
            new ConditionalWeakTable<BookModel, BookIdentity>();
        private static long _nextIdentity;
        private sealed class BookIdentity { internal ulong Value; }
        private sealed class Location
        {
            internal UnitDataModel Unit;
            internal byte Floor;
            internal byte Index;
            internal bool Mismatch;
        }

        private static BookIdentity CreateIdentity(BookModel unused)
        {
            var token = Interlocked.Increment(ref _nextIdentity);
            if (token <= 0) throw new InvalidOperationException("Core book token space exhausted.");
            return new BookIdentity { Value = (ulong)token };
        }

        private static ulong Token(BookModel book)
        { return book == null ? 0 : Identities.GetValue(book, CreateIdentity).Value; }

        // Call after claims and DeckMirror.Capture. Unsupported core inventory never hides card data.
        internal static void Capture(ProgressSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            SetUnavailable(snapshot, CoreBooksReason.NotCaptured);
            try
            {
                var selected = snapshot.Floors.Find(floor => (byte)floor.Sephirah == snapshot.SelectedFloorId);
                for (var i = 0; i < snapshot.UnitDecks.Count; i++)
                {
                    var unit = selected != null && i < selected.UnitReferences.Count
                        ? selected.UnitReferences[i] as UnitDataModel : null;
                    snapshot.UnitDecks[i].BookToken = unit == null ? 0 : Token(unit.bookItem);
                }
                var inventory = BookInventoryModel.Instance;
                var equipList = inventory.GetBookList_equip();
                var allBooks = inventory.GetBookListAll();
                var floors = AllFloorsField == null ? null : AllFloorsField.GetValue(LibraryModel.Instance)
                    as List<LibraryFloorModel>;
                if (equipList == null || allBooks == null || floors == null)
                { SetUnavailable(snapshot, CoreBooksReason.UnsupportedData); return; }
                var equippedInventory = new HashSet<BookModel>(equipList.Where(book => book != null));
                var books = new HashSet<BookModel>(allBooks.Where(book => book != null));
                var defaults = new HashSet<BookModel>();
                var locations = new Dictionary<BookModel, Location>();
                foreach (var floor in floors)
                {
                    if (floor == null || (byte)floor.Sephirah < 1 || (byte)floor.Sephirah > 10) continue;
                    var units = floor.GetUnitDataList();
                    if (units == null || units.Count > 5)
                    { SetUnavailable(snapshot, CoreBooksReason.UnsupportedData); return; }
                    for (var i = 0; i < units.Count; i++)
                    {
                        var unit = units[i]; if (unit == null) continue;
                        if (unit.defaultBook != null) { defaults.Add(unit.defaultBook); books.Add(unit.defaultBook); }
                        AddLocation(books, locations, unit.bookItem, unit, (byte)floor.Sephirah, (byte)i);
                        AddLocation(books, locations, unit.GetCustomBookItemData(), unit, (byte)floor.Sephirah, (byte)i);
                    }
                }
                if (books.Count > MaxBooks) { SetUnavailable(snapshot, CoreBooksReason.TooManyBooks); return; }
                // GetFloor creates missing floors; the verified _floorList reflection above avoids that mutation.
                var special = inventory.GetBlackSilenceBook();
                var rows = new List<ProgressSnapshot.CoreBookEntry>(books.Count);
                foreach (var book in books)
                    rows.Add(CaptureBook(book, equippedInventory.Contains(book), defaults.Contains(book),
                        ReferenceEquals(book, special), locations));
                rows.Sort((left, right) => left.BookToken.CompareTo(right.BookToken));
                var baseSize = snapshot.Encode(1).Length;
                snapshot.CoreBooks.AddRange(rows);
                snapshot.CoreBooksAvailable = true;
                snapshot.CoreBooksReason = CoreBooksReason.None;
                var coreSize = EncodeContent(snapshot).Length;
                if (baseSize + coreSize - 5 > MaxPacketBytes)
                    SetUnavailable(snapshot, CoreBooksReason.PacketLimit);
            }
            catch
            {
                SetUnavailable(snapshot, CoreBooksReason.CaptureFailed);
            }
        }

        private static void SetUnavailable(ProgressSnapshot snapshot, CoreBooksReason reason)
        {
            snapshot.CoreBooks.Clear(); snapshot.CoreBooksAvailable = false; snapshot.CoreBooksReason = reason;
        }

        private static void AddLocation(HashSet<BookModel> books, Dictionary<BookModel, Location> locations,
            BookModel book, UnitDataModel unit, byte floor, byte index)
        {
            if (book == null) return;
            books.Add(book);
            Location existing;
            if (locations.TryGetValue(book, out existing))
            {
                if (!ReferenceEquals(existing.Unit, unit) || existing.Floor != floor || existing.Index != index)
                    existing.Mismatch = true;
            }
            else locations.Add(book, new Location { Unit = unit, Floor = floor, Index = index });
        }

        private static ProgressSnapshot.CoreBookEntry CaptureBook(BookModel book, bool inventoryBook,
            bool defaultBook, bool specialBook, Dictionary<BookModel, Location> locations)
        {
            var row = new ProgressSnapshot.CoreBookEntry
            { BookToken = Token(book), BookInstanceId = book.instanceId, BookReference = book };
            var id = book.BookId;
            row.BookId = id != null && id.IsBasic() && id.id > 0 ? id.id : 0;
            if (row.BookId == 0) row.Flags |= CoreBookFlags.UnsupportedId;
            var xml = book.ClassInfo;
            defaultBook |= book.IsBasicBook();
            row.Kind = defaultBook ? CoreBookKind.Default :
                specialBook || row.BookId == 250022 || !inventoryBook || row.BookId == 0 || xml == null ||
                    xml.canNotEquip || book.IsFixedDeck() || book.IsMultiDeck()
                    ? CoreBookKind.Special : CoreBookKind.Ordinary;
            if (!inventoryBook || defaultBook || xml == null || xml.canNotEquip)
                row.Flags |= CoreBookFlags.CannotEquip;
            if (row.Kind == CoreBookKind.Ordinary && row.BookInstanceId <= 0)
                row.Flags |= CoreBookFlags.InvalidInstance;
            if (book.IsFixedDeck()) row.Flags |= CoreBookFlags.FixedDeck;
            if (book.IsMultiDeck()) row.Flags |= CoreBookFlags.MultiDeck;
            if (book.IsDeckLocked() || book.IsLockByBluePrimary()) row.Flags |= CoreBookFlags.Locked;
            if (book.originData == null || book.reservedData == null ||
                book.originData.equipedPassiveBookInstanceId != -1 ||
                book.reservedData.equipedPassiveBookInstanceId != -1)
                row.Flags |= CoreBookFlags.PassiveBound;
            if (HasPassiveDraft(book)) row.Flags |= CoreBookFlags.DraftMismatch;
            Location location;
            if (locations.TryGetValue(book, out location))
            {
                row.Flags |= CoreBookFlags.Equipped;
                row.OccupiedFloorId = location.Floor; row.OccupiedUnitIndex = location.Index;
                if (location.Mismatch || book.owner != null && !ReferenceEquals(book.owner, location.Unit))
                    row.Flags |= CoreBookFlags.OwnerMismatch;
                if (location.Unit.IsChangeItemLock()) row.Flags |= CoreBookFlags.Locked;
            }
            if (book.owner != null)
            {
                row.Flags |= CoreBookFlags.Equipped;
                if (location == null) row.Flags |= CoreBookFlags.OwnerMismatch;
            }
            if (row.BookId > 0 && xml != null) CaptureDisplay(book, row);
            CaptureCards(book, row);
            return row;
        }

        private static bool HasPassiveDraft(BookModel book)
        {
            var original = book.originData; var pending = book.reservedData;
            if (original == null || pending == null || original.equipedBookIdListInPassive == null ||
                pending.equipedBookIdListInPassive == null) return true;
            var passives = book.GetPassiveModelList();
            if (passives == null) return true;
            var initialized = false;
            foreach (var passive in passives)
            {
                if (passive == null || passive.originData == null) return true;
                // Vanilla construction and save loading create origin only. The popup
                // creates reserved data when inheritance editing actually begins.
                if (passive.reservedData == null) continue;
                initialized = true;
                var old = passive.originData; var next = passive.reservedData;
                if (old.receivepassivebookId != next.receivepassivebookId ||
                    old.givePassiveBookId != next.givePassiveBookId ||
                    !SamePassiveId(old.currentpassive, next.currentpassive)) return true;
            }
            // A loaded receiver has committed sources but a constructor-default
            // empty book reserve. Ignore only that unused default buffer; a
            // non-default book-only pending edit must still match committed data.
            if (!initialized && pending.equipedPassiveBookInstanceId == -1 &&
                pending.equipedBookIdListInPassive.Count == 0) return false;
            return original.equipedPassiveBookInstanceId != pending.equipedPassiveBookInstanceId ||
                !original.equipedBookIdListInPassive.SequenceEqual(pending.equipedBookIdListInPassive);
        }

        private static bool SamePassiveId(PassiveXmlInfo left, PassiveXmlInfo right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.id == null || right.id == null) return false;
            return left.isNegative == right.isNegative && left.rare == right.rare &&
                left.id.id == right.id.id && string.Equals(left.id.packageId, right.id.packageId,
                StringComparison.Ordinal);
        }

        private static void CaptureDisplay(BookModel book, ProgressSnapshot.CoreBookEntry row)
        {
            var display = row.Display;
            try
            {
                display.MaxHp = book.HP; display.Break = book.Break;
                display.Available = display.MaxHp > 0 && display.MaxHp <= DeckMirror.MaxDisplayStat &&
                    display.Break >= 0 && display.Break <= DeckMirror.MaxDisplayStat;
                var ids = new HashSet<int>();
                var passives = book.GetPassiveInfoList(false);
                if (passives == null) display.Available = false;
                else foreach (var info in passives)
                {
                    var id = info == null || info.passive == null ? null : info.passive.id;
                    if (id == null || !id.IsBasic() || id.id <= 0 || id.id == 9999999)
                    { display.Available = false; continue; }
                    if (ids.Add(id.id)) display.PassiveIds.Add(id.id);
                }
                if (display.PassiveIds.Count > DeckMirror.MaxPassiveIds) display.Available = false;
            }
            catch { display.Available = false; }
            if (!display.Available)
            { display.PassiveIds.Clear(); row.Flags |= CoreBookFlags.UnsupportedDisplay; }
        }

        private static void CaptureCards(BookModel book, ProgressSnapshot.CoreBookEntry row)
        {
            if ((row.Flags & CoreBookFlags.MultiDeck) != 0) return;
            var cards = book.GetCardListFromCurrentDeck();
            if (cards == null) { row.Flags |= CoreBookFlags.UnsupportedCards; return; }
            foreach (var card in cards)
            {
                var id = card == null ? null : card.id;
                if (id == null || !id.IsBasic() || id.id <= 0 || row.CurrentCards.Count >= DeckMirror.MaxCardsPerDeck)
                { row.Flags |= CoreBookFlags.UnsupportedCards; row.CurrentCards.Clear(); return; }
                row.CurrentCards.Add(id.id);
            }
        }

        internal static bool TryResolveBook(ProgressSnapshot snapshot, ulong token, out BookModel book)
        {
            book = null;
            if (snapshot == null || !snapshot.CoreBooksAvailable || token == 0) return false;
            var row = snapshot.CoreBooks.Find(entry => entry.BookToken == token);
            book = row == null ? null : row.BookReference as BookModel;
            return book != null;
        }

        // Included with deck content in the single authoritative DeckRevision.
        internal static byte[] EncodeContent(ProgressSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            { WriteData(writer, snapshot, true); writer.Flush(); return stream.ToArray(); }
        }

        internal static void WriteData(BinaryWriter writer, ProgressSnapshot snapshot, bool canonical)
        {
            string reason;
            if (!ValidateData(snapshot, out reason)) throw new InvalidOperationException(reason);
            writer.Write((byte)(snapshot.CoreBooksAvailable ? 1 : 0));
            writer.Write((byte)snapshot.CoreBooksReason);
            writer.Write((byte)(snapshot.CoreBooksAvailable ? snapshot.UnitDecks.Count : 0));
            if (snapshot.CoreBooksAvailable)
                foreach (var unit in snapshot.UnitDecks) writer.Write(unit.BookToken);
            writer.Write((ushort)snapshot.CoreBooks.Count);
            var rows = canonical ? snapshot.CoreBooks.OrderBy(book => book.BookToken)
                : (IEnumerable<ProgressSnapshot.CoreBookEntry>)snapshot.CoreBooks;
            foreach (var row in rows)
            {
                writer.Write(row.BookToken); writer.Write(row.BookId); writer.Write(row.BookInstanceId);
                writer.Write((byte)row.Kind); writer.Write((ushort)row.Flags);
                writer.Write(row.OccupiedFloorId); writer.Write(row.OccupiedUnitIndex);
                writer.Write((byte)(row.Display.Available ? 1 : 0));
                if (row.Display.Available)
                {
                    writer.Write(row.Display.MaxHp); writer.Write(row.Display.Break);
                    writer.Write((byte)row.Display.PassiveIds.Count);
                    var ids = canonical ? row.Display.PassiveIds.OrderBy(id => id) :
                        (IEnumerable<int>)row.Display.PassiveIds;
                    foreach (var id in ids) writer.Write(id);
                }
                writer.Write((byte)row.CurrentCards.Count);
                var cards = canonical ? row.CurrentCards.OrderBy(id => id) : (IEnumerable<int>)row.CurrentCards;
                foreach (var id in cards) writer.Write(id);
            }
        }

        internal static bool TryReadData(BinaryReader reader, ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            var available = reader.ReadByte();
            if (available > 1) return Reject(out reason, "Core inventory availability flag is invalid.");
            snapshot.CoreBooksAvailable = available != 0;
            snapshot.CoreBooksReason = (CoreBooksReason)reader.ReadByte();
            var currentCount = reader.ReadByte();
            if (currentCount != (snapshot.CoreBooksAvailable ? snapshot.UnitDecks.Count : 0))
                return Reject(out reason, "Current core token count does not match the selected roster.");
            if (snapshot.CoreBooksAvailable)
                foreach (var unit in snapshot.UnitDecks) unit.BookToken = reader.ReadUInt64();
            var count = reader.ReadUInt16();
            if (count > MaxBooks) return Reject(out reason, "Core inventory count exceeds the limit.");
            for (var i = 0; i < count; i++)
            {
                var row = new ProgressSnapshot.CoreBookEntry
                {
                    BookToken = reader.ReadUInt64(), BookId = reader.ReadInt32(), BookInstanceId = reader.ReadInt32(),
                    Kind = (CoreBookKind)reader.ReadByte(), Flags = (CoreBookFlags)reader.ReadUInt16(),
                    OccupiedFloorId = reader.ReadByte(), OccupiedUnitIndex = reader.ReadByte()
                };
                available = reader.ReadByte();
                if (available > 1) return Reject(out reason, "Core detail availability flag is invalid.");
                row.Display.Available = available != 0;
                if (row.Display.Available)
                {
                    row.Display.MaxHp = reader.ReadInt32(); row.Display.Break = reader.ReadInt32();
                    var passiveCount = reader.ReadByte();
                    if (passiveCount > DeckMirror.MaxPassiveIds)
                        return Reject(out reason, "Core effective passive count exceeds the limit.");
                    for (var p = 0; p < passiveCount; p++) row.Display.PassiveIds.Add(reader.ReadInt32());
                }
                var cardCount = reader.ReadByte();
                if (cardCount > DeckMirror.MaxCardsPerDeck)
                    return Reject(out reason, "Core stored deck count exceeds the limit.");
                for (var card = 0; card < cardCount; card++) row.CurrentCards.Add(reader.ReadInt32());
                snapshot.CoreBooks.Add(row);
            }
            return ValidateData(snapshot, out reason);
        }

        private static bool ValidateData(ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            if (snapshot.UnitDecks.Count > 5 || snapshot.CoreBooks.Count > MaxBooks ||
                snapshot.CoreBooksReason > CoreBooksReason.UnsupportedData ||
                snapshot.CoreBooksAvailable != (snapshot.CoreBooksReason == CoreBooksReason.None) ||
                !snapshot.CoreBooksAvailable && snapshot.CoreBooks.Count != 0)
                return Reject(out reason, "Core inventory state or count is invalid.");
            var books = new Dictionary<ulong, ProgressSnapshot.CoreBookEntry>();
            foreach (var row in snapshot.CoreBooks)
            {
                if (row == null || row.BookToken == 0 || row.BookId < 0 || row.Kind > CoreBookKind.Special ||
                    (row.Flags & ~AllFlags) != 0 || books.ContainsKey(row.BookToken))
                    return Reject(out reason, "Core inventory identity, kind or flags are invalid.");
                books.Add(row.BookToken, row);
                if (row.BookId == 0 && (row.Flags & CoreBookFlags.UnsupportedId) == 0 ||
                    row.Kind == CoreBookKind.Ordinary && row.BookInstanceId <= 0 &&
                        (row.Flags & CoreBookFlags.InvalidInstance) == 0)
                    return Reject(out reason, "Core inventory page or instance ID is unsupported without a flag.");
                var emptyLocation = row.OccupiedFloorId == PrepClaims.NoFloor && row.OccupiedUnitIndex == byte.MaxValue;
                var validLocation = row.OccupiedFloorId >= 1 && row.OccupiedFloorId <= 10 && row.OccupiedUnitIndex < 5;
                if ((row.Flags & CoreBookFlags.Equipped) == 0 ? !emptyLocation :
                    !validLocation && !(emptyLocation && (row.Flags & CoreBookFlags.OwnerMismatch) != 0))
                    return Reject(out reason, "Core inventory occupancy is inconsistent.");
                var display = row.Display;
                if (display.AppearanceAvailable || display.PassiveIds.Count > DeckMirror.MaxPassiveIds ||
                    row.CurrentCards.Count > DeckMirror.MaxCardsPerDeck || row.CurrentCards.Any(id => id <= 0) ||
                    (row.Flags & CoreBookFlags.UnsupportedCards) != 0 && row.CurrentCards.Count != 0)
                    return Reject(out reason, "Core detail or stored deck exceeds supported bounds.");
                if (display.Available)
                {
                    if (display.MaxHp <= 0 || display.MaxHp > DeckMirror.MaxDisplayStat || display.Break < 0 ||
                        display.Break > DeckMirror.MaxDisplayStat || display.PassiveIds.Any(id => id <= 0 || id == 9999999) ||
                        display.PassiveIds.Distinct().Count() != display.PassiveIds.Count)
                        return Reject(out reason, "Core actual stats or effective passive IDs are invalid.");
                }
                else if (row.Kind == CoreBookKind.Ordinary && (row.Flags & CoreBookFlags.UnsupportedDisplay) == 0)
                    return Reject(out reason, "Ordinary core detail is missing without an unsupported flag.");
            }
            if (snapshot.CoreBooksAvailable)
            {
                var selected = new HashSet<ulong>();
                foreach (var unit in snapshot.UnitDecks)
                {
                    if (unit == null) return Reject(out reason, "Current core roster is invalid.");
                    if (unit.BookToken == 0)
                    { if (unit.BookId > 0) return Reject(out reason, "Current core token is missing."); continue; }
                    ProgressSnapshot.CoreBookEntry current;
                    if (!selected.Add(unit.BookToken) || !books.TryGetValue(unit.BookToken, out current) ||
                        current.BookId != unit.BookId || current.BookInstanceId != unit.BookInstanceId)
                        return Reject(out reason, "Current core token does not match its inventory instance.");
                }
            }
            return true;
        }

        private static bool Reject(out string reason, string message)
        { reason = message; return false; }
    }
}
