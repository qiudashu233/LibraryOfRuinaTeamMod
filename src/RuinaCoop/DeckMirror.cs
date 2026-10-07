using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RuinaCoop
{
    // The guest receives data only. All inventory and deck mutations remain on the host.
    internal static class DeckMirror
    {
        internal const int MaxCardsPerDeck = 64;
        internal const int MaxCardStock = 2048;
        internal const int MaxStockCount = 1000000;
        private const int MaxUnits = 5;
        internal const int MaxPassiveIds = 64;
        internal const int MaxDisplayStat = 1000000;
        internal const int MaxSkinBytes = 128;
        internal const int MaxPartId = 1023;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly ConditionalWeakTable<UnitDataModel, UnitToken> UnitTokens =
            new ConditionalWeakTable<UnitDataModel, UnitToken>();
        private static long _nextUnitIdentity;
        private sealed class UnitToken { internal ulong Value; }

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
                if (unit != null)
                {
                    deck.UnitIdentity = UnitTokens.GetValue(unit, CreateUnitToken).Value;
                    CaptureDisplay(unit, book, deck);
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

        private static UnitToken CreateUnitToken(UnitDataModel unused)
        {
            var value = Interlocked.Increment(ref _nextUnitIdentity);
            if (value <= 0) throw new InvalidOperationException("Unit identity token space exhausted.");
            return new UnitToken { Value = (ulong)value };
        }

        private static int VanillaId(LorId id, int absent)
        {
            return !ReferenceEquals(id, null) && id.IsBasic() && id.id > 0 ? id.id : absent;
        }

        private static void CaptureDisplay(UnitDataModel unit, BookModel book, ProgressSnapshot.UnitDeckEntry deck)
        {
            var display = deck.Display;
            if (book == null || deck.BookId <= 0) return;
            display.MaxHp = unit.MaxHp;
            display.Break = unit.Break;
            display.Available = display.MaxHp > 0 && display.MaxHp <= MaxDisplayStat &&
                display.Break >= 0 && display.Break <= MaxDisplayStat;
            var passiveIds = new HashSet<int>();
            foreach (var info in book.GetPassiveInfoList(false))
            {
                var id = info == null || info.passive == null ? 0 : VanillaId(info.passive.id, 0);
                if (id <= 0)
                {
                    display.Available = false;
                    continue;
                }
                if (passiveIds.Add(id)) display.PassiveIds.Add(id);
            }
            if (display.PassiveIds.Count > MaxPassiveIds)
                throw new InvalidOperationException("Effective passive list exceeds the display limit.");

            var defaultBook = unit.defaultBook;
            var appearanceBook = unit.CustomBookItem;
            var custom = unit.customizeData;
            display.DefaultBookId = defaultBook == null ? 0 : VanillaId(defaultBook.BookId, 0);
            display.CustomBookId = appearanceBook != null && !ReferenceEquals(appearanceBook, book)
                ? VanillaId(appearanceBook.BookId, 0) : 0;
            display.IsSephirah = unit.isSephirah;
            display.Gender = (byte)unit.gender;
            display.AppearanceType = (byte)unit.appearanceType;
            display.CharacterSkin = appearanceBook == null ? "" : appearanceBook.GetCharacterName() ?? "";
            display.AppearanceAvailable = display.DefaultBookId > 0 && appearanceBook != null &&
                VanillaId(appearanceBook.BookId, 0) > 0 && custom != null &&
                string.IsNullOrEmpty(unit.workshopSkin);
            if (custom == null) return;
            display.UseCustom = custom.UseCustomData;
            display.SpecialCustomId = VanillaId(custom.specialCustomID, -1);
            display.FrontHair = custom.frontHairID;
            display.BackHair = custom.backHairID;
            display.Eye = custom.eyeID;
            display.Brow = custom.browID;
            display.Mouth = custom.mouthID;
            display.Head = custom.headID;
            display.Height = custom.height;
            uint hair, eye, skin;
            if (!TryPackColor(custom.hairColor, out hair) || !TryPackColor(custom.eyeColor, out eye) ||
                !TryPackColor(custom.skinColor, out skin)) display.AppearanceAvailable = false;
            else { display.HairColor = hair; display.EyeColor = eye; display.SkinColor = skin; }
            if (!ReferenceEquals(custom.specialCustomID, null) && !custom.specialCustomID.IsBasic())
                display.AppearanceAvailable = false;
            string reason;
            if (display.AppearanceAvailable && !ValidateAppearance(display, out reason))
                display.AppearanceAvailable = false;
        }

        private static bool TryPackColor(Color color, out uint packed)
        {
            packed = 0;
            foreach (var value in new[] { color.r, color.g, color.b, color.a })
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f) return false;
                packed = (packed << 8) | (uint)Math.Round(value * 255f);
            }
            return true;
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
            writer.Write((byte)snapshot.UnitDecks.Count);
            foreach (var deck in snapshot.UnitDecks) WriteDisplay(writer, deck, canonical);
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
            var displayCount = reader.ReadByte();
            if (displayCount != snapshot.UnitDecks.Count)
                return Reject(out reason, "Display metadata count does not match the selected roster.");
            for (var i = 0; i < displayCount; i++)
                if (!TryReadDisplay(reader, snapshot.UnitDecks[i], out reason)) return false;
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
            var unitIds = new HashSet<ulong>();
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
                if (deck.UnitIdentity != 0 && !unitIds.Add(deck.UnitIdentity))
                    return Reject(out reason, "Selected roster repeats a unit identity token.");
                if (!ValidateDisplay(deck, out reason)) return false;
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

        private static void WriteDisplay(BinaryWriter writer, ProgressSnapshot.UnitDeckEntry deck, bool canonical)
        {
            var display = deck.Display;
            writer.Write(deck.UnitIdentity);
            writer.Write((byte)(display.Available ? 1 : 0));
            if (display.Available)
            {
                writer.Write(display.MaxHp);
                writer.Write(display.Break);
                writer.Write((byte)display.PassiveIds.Count);
                var ids = canonical ? display.PassiveIds.OrderBy(id => id) : (IEnumerable<int>)display.PassiveIds;
                foreach (var id in ids) writer.Write(id);
            }
            writer.Write((byte)(display.AppearanceAvailable ? 1 : 0));
            if (!display.AppearanceAvailable) return;
            writer.Write(display.DefaultBookId);
            writer.Write(display.CustomBookId);
            writer.Write((byte)((display.IsSephirah ? 1 : 0) | (display.UseCustom ? 2 : 0)));
            writer.Write(display.Gender);
            writer.Write(display.AppearanceType);
            var skin = StrictUtf8.GetBytes(display.CharacterSkin ?? "");
            writer.Write((ushort)skin.Length);
            writer.Write(skin);
            writer.Write(display.SpecialCustomId);
            foreach (var part in new[] { display.FrontHair, display.BackHair, display.Eye,
                display.Brow, display.Mouth, display.Head }) writer.Write(part);
            writer.Write(display.HairColor);
            writer.Write(display.EyeColor);
            writer.Write(display.SkinColor);
            writer.Write(display.Height);
        }

        private static bool TryReadDisplay(BinaryReader reader, ProgressSnapshot.UnitDeckEntry deck, out string reason)
        {
            reason = null;
            var display = deck.Display;
            deck.UnitIdentity = reader.ReadUInt64();
            var available = reader.ReadByte();
            if (available > 1) return Reject(out reason, "Display availability flag is invalid.");
            display.Available = available != 0;
            if (display.Available)
            {
                display.MaxHp = reader.ReadInt32();
                display.Break = reader.ReadInt32();
                var passiveCount = reader.ReadByte();
                if (passiveCount > MaxPassiveIds) return Reject(out reason, "Effective passive count exceeds the limit.");
                for (var i = 0; i < passiveCount; i++) display.PassiveIds.Add(reader.ReadInt32());
            }
            var appearance = reader.ReadByte();
            if (appearance > 1) return Reject(out reason, "Appearance availability flag is invalid.");
            display.AppearanceAvailable = appearance != 0;
            if (!display.AppearanceAvailable) return true;
            display.DefaultBookId = reader.ReadInt32();
            display.CustomBookId = reader.ReadInt32();
            var flags = reader.ReadByte();
            if (flags > 3) return Reject(out reason, "Appearance flags are invalid.");
            display.IsSephirah = (flags & 1) != 0;
            display.UseCustom = (flags & 2) != 0;
            display.Gender = reader.ReadByte();
            display.AppearanceType = reader.ReadByte();
            var skinBytes = reader.ReadUInt16();
            if (skinBytes > MaxSkinBytes || skinBytes > reader.BaseStream.Length - reader.BaseStream.Position)
                return Reject(out reason, "Character skin name length is invalid.");
            display.CharacterSkin = StrictUtf8.GetString(reader.ReadBytes(skinBytes));
            display.SpecialCustomId = reader.ReadInt32();
            display.FrontHair = reader.ReadInt32();
            display.BackHair = reader.ReadInt32();
            display.Eye = reader.ReadInt32();
            display.Brow = reader.ReadInt32();
            display.Mouth = reader.ReadInt32();
            display.Head = reader.ReadInt32();
            display.HairColor = reader.ReadUInt32();
            display.EyeColor = reader.ReadUInt32();
            display.SkinColor = reader.ReadUInt32();
            display.Height = reader.ReadInt32();
            return true;
        }

        private static bool ValidateDisplay(ProgressSnapshot.UnitDeckEntry deck, out string reason)
        {
            reason = null;
            var display = deck.Display;
            if ((display.Available || display.AppearanceAvailable) && (deck.UnitIdentity == 0 || deck.BookId <= 0))
                return Reject(out reason, "Native display metadata has no valid unit or key page identity.");
            if (display.Available)
            {
                if (display.MaxHp <= 0 || display.MaxHp > MaxDisplayStat || display.Break < 0 || display.Break > MaxDisplayStat)
                    return Reject(out reason, "Native display health or break is invalid.");
                if (display.PassiveIds.Count > MaxPassiveIds)
                    return Reject(out reason, "Effective passive count exceeds the limit.");
                var ids = new HashSet<int>();
                foreach (var id in display.PassiveIds)
                    if (id <= 0 || id == 9999999 || !ids.Add(id))
                        return Reject(out reason, "Effective passive IDs are invalid or duplicated.");
            }
            return !display.AppearanceAvailable || ValidateAppearance(display, out reason);
        }

        private static bool ValidateAppearance(ProgressSnapshot.UnitDisplayEntry display, out string reason)
        {
            reason = null;
            if (display.DefaultBookId <= 0 || display.CustomBookId < 0 || display.Gender > 4 || display.AppearanceType > 4 ||
                (display.SpecialCustomId != -1 && display.SpecialCustomId <= 0))
                return Reject(out reason, "Character identity or appearance enum is invalid.");
            if (display.Height < 140 || display.Height > 3000 ||
                new[] { display.FrontHair, display.BackHair, display.Eye, display.Brow, display.Mouth, display.Head }
                    .Any(part => part < -1 || part > MaxPartId))
                return Reject(out reason, "Character appearance part or height exceeds the limit.");
            var skin = display.CharacterSkin ?? "";
            if (skin.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || skin.Contains("..") || skin.Any(char.IsControl))
                return Reject(out reason, "Character skin must be a resource name, not a path.");
            try
            {
                if (StrictUtf8.GetByteCount(skin) > MaxSkinBytes)
                    return Reject(out reason, "Character skin name exceeds the byte limit.");
            }
            catch (EncoderFallbackException) { return Reject(out reason, "Character skin contains malformed Unicode."); }
            return true;
        }

        private static bool Reject(out string reason, string message)
        {
            reason = message;
            return false;
        }
    }
}
