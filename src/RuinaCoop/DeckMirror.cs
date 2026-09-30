using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RuinaCoop
{
    // The guest receives data only. All inventory and deck mutations remain on the host.
    internal static class DeckMirror
    {
        internal const int MaxCardsPerDeck = 64;
        internal const int MaxCardStock = 2048;
        internal const int MaxStockCount = 1000000;
        private const int MaxUnits = 5;

        internal static void Capture(ProgressSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            snapshot.UnitDecks.Clear();
            snapshot.CardStock.Clear();
            if (snapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                return;
            }
            var floor = snapshot.Floors.Find(candidate =>
                (byte)candidate.Sephirah == snapshot.SelectedFloorId);
            if (snapshot.SelectedStageId <= 0 || floor == null ||
                floor.Units.Count > MaxUnits || floor.UnitReferences.Count != floor.Units.Count)
            {
                throw new InvalidOperationException("Selected floor is not ready for a deck snapshot.");
            }

            foreach (var reference in floor.UnitReferences)
            {
                var unit = reference as UnitDataModel;
                var book = unit == null ? null : unit.bookItem;
                var deck = new ProgressSnapshot.UnitDeckEntry { Fixed = true };
                if (book != null)
                {
                    var bookId = book.BookId;
                    deck.BookId = !ReferenceEquals(bookId, null) && bookId.IsBasic() && bookId.id > 0
                        ? bookId.id : 0;
                    deck.BookInstanceId = book.instanceId;
                    deck.Capacity = book.GetDeckSize();
                    deck.Fixed = deck.BookId == 0 || book.IsFixedDeck() || book.IsDeckLocked() ||
                        unit.IsChangeItemLock();
                    deck.MultiDeck = book.IsMultiDeck();
                    var cards = book.GetCardListFromCurrentDeck();
                    if (cards != null)
                    {
                        foreach (var card in cards)
                        {
                            var id = card == null ? null : card.id;
                            if (ReferenceEquals(id, null) || !id.IsBasic() || id.id <= 0)
                            {
                                // Workshop cards cannot be represented by an integer vanilla ID.
                                deck.Fixed = true;
                                continue;
                            }
                            deck.Cards.Add(id.id);
                        }
                    }
                }
                snapshot.UnitDecks.Add(deck);
            }

            // GetCardList includes the game's unlimited basic cards as synthetic x99 rows.
            // Keep the maximum when a synthetic and saved row share an ID.
            var stock = new Dictionary<int, int>();
            foreach (var item in InventoryModel.Instance.GetCardList())
            {
                if (item == null || item.num <= 0)
                {
                    continue;
                }
                var id = item.GetID();
                if (ReferenceEquals(id, null) || !id.IsBasic() || id.id <= 0)
                {
                    continue;
                }
                int existing;
                if (!stock.TryGetValue(id.id, out existing) || item.num > existing)
                {
                    stock[id.id] = item.num;
                }
            }
            foreach (var item in stock.OrderBy(pair => pair.Key))
            {
                snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry
                {
                    Id = item.Key,
                    Count = item.Value
                });
            }
            string reason;
            if (!ValidateData(snapshot, out reason))
            {
                throw new InvalidOperationException(reason);
            }
        }

        // Ignores sequence and revisions. Card ordering does not change deck semantics.
        internal static byte[] EncodeContent(ProgressSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(snapshot.SelectedStageId);
                writer.Write(snapshot.SelectedFloorId);
                WriteData(writer, snapshot, true);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // Shared format for snapshots and deterministic local content comparison.
        internal static void WriteData(BinaryWriter writer, ProgressSnapshot snapshot, bool canonical)
        {
            string reason;
            if (!ValidateData(snapshot, out reason))
            {
                throw new InvalidOperationException(reason);
            }
            writer.Write((byte)(snapshot.DecksFrozen ? 1 : 0));
            writer.Write((byte)snapshot.UnitDecks.Count);
            foreach (var deck in snapshot.UnitDecks)
            {
                writer.Write(deck.BookId);
                writer.Write(deck.BookInstanceId);
                writer.Write((byte)deck.Capacity);
                writer.Write((byte)((deck.Fixed ? 1 : 0) | (deck.MultiDeck ? 2 : 0)));
                writer.Write((byte)deck.Cards.Count);
                var cards = canonical ? deck.Cards.OrderBy(id => id) : (IEnumerable<int>)deck.Cards;
                foreach (var id in cards)
                {
                    writer.Write(id);
                }
            }
            writer.Write((ushort)snapshot.CardStock.Count);
            var stock = canonical ? snapshot.CardStock.OrderBy(item => item.Id)
                : (IEnumerable<ProgressSnapshot.CardStockEntry>)snapshot.CardStock;
            foreach (var item in stock)
            {
                writer.Write(item.Id);
                writer.Write(item.Count);
            }
        }

        internal static bool TryReadData(BinaryReader reader, ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            var frozen = reader.ReadByte();
            if (frozen > 1)
            {
                return Reject(out reason, "Deck freeze flag is invalid.");
            }
            snapshot.DecksFrozen = frozen == 1;
            var deckCount = reader.ReadByte();
            if (deckCount > MaxUnits)
            {
                return Reject(out reason, "Deck count exceeds the librarian limit.");
            }
            for (var i = 0; i < deckCount; i++)
            {
                var deck = new ProgressSnapshot.UnitDeckEntry
                {
                    BookId = reader.ReadInt32(),
                    BookInstanceId = reader.ReadInt32(),
                    Capacity = reader.ReadByte()
                };
                var flags = reader.ReadByte();
                if (flags > 3)
                {
                    return Reject(out reason, "Deck " + i + " has invalid flags.");
                }
                deck.Fixed = (flags & 1) != 0;
                deck.MultiDeck = (flags & 2) != 0;
                var cardCount = reader.ReadByte();
                if (cardCount > MaxCardsPerDeck)
                {
                    return Reject(out reason, "Deck " + i + " exceeds the card limit.");
                }
                for (var card = 0; card < cardCount; card++)
                {
                    deck.Cards.Add(reader.ReadInt32());
                }
                snapshot.UnitDecks.Add(deck);
            }
            var stockCount = reader.ReadUInt16();
            if (stockCount > MaxCardStock)
            {
                return Reject(out reason, "Card stock exceeds the entry limit.");
            }
            for (var i = 0; i < stockCount; i++)
            {
                snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry
                {
                    Id = reader.ReadInt32(),
                    Count = reader.ReadInt32()
                });
            }
            return ValidateData(snapshot, out reason);
        }

        private static bool ValidateData(ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            if (snapshot.UnitDecks.Count > MaxUnits || snapshot.CardStock.Count > MaxCardStock)
            {
                return Reject(out reason, "Deck or stock entry count exceeds the limit.");
            }
            if (snapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                if (snapshot.UnitDecks.Count != 0 || snapshot.CardStock.Count != 0)
                {
                    return Reject(out reason, "Deck data exists without a selected floor.");
                }
            }
            else
            {
                var floor = snapshot.Floors.Find(candidate =>
                    (byte)candidate.Sephirah == snapshot.SelectedFloorId);
                if (snapshot.SelectedStageId <= 0 || floor == null ||
                    snapshot.UnitDecks.Count != floor.Units.Count)
                {
                    return Reject(out reason, "Decks do not match the selected stage and floor.");
                }
            }
            for (var i = 0; i < snapshot.UnitDecks.Count; i++)
            {
                var deck = snapshot.UnitDecks[i];
                if (deck == null || deck.BookId < 0 || (deck.BookId == 0 && !deck.Fixed))
                {
                    return Reject(out reason, "Deck " + i + " has an invalid key page ID.");
                }
                if (deck.Capacity < 0 || deck.Capacity > MaxCardsPerDeck ||
                    deck.Cards.Count > MaxCardsPerDeck ||
                    (!deck.Fixed && !deck.MultiDeck && (deck.Capacity == 0 || deck.Cards.Count > deck.Capacity)))
                {
                    return Reject(out reason, "Deck " + i + " has an invalid capacity or card count.");
                }
                foreach (var id in deck.Cards)
                {
                    if (id <= 0)
                    {
                        return Reject(out reason, "Deck " + i + " has a nonpositive card ID.");
                    }
                }
            }
            var stockIds = new HashSet<int>();
            foreach (var item in snapshot.CardStock)
            {
                if (item == null || item.Id <= 0 || item.Count <= 0 || item.Count > MaxStockCount)
                {
                    return Reject(out reason, "Card stock has an invalid ID or count.");
                }
                if (!stockIds.Add(item.Id))
                {
                    return Reject(out reason, "Card stock repeats ID " + item.Id + ".");
                }
            }
            return true;
        }

        private static bool Reject(out string reason, string message)
        {
            reason = message;
            return false;
        }
    }
}
