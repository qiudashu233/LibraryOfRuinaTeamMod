using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuinaCoop;
using UI;
using LOR_DiceSystem;

internal static class EquipmentCodecChecks
{
    private const ulong Room = 987;
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new Exception("FAIL equipment codec: " + name);
    }

    internal static int Run()
    {
        _checks = 0;
        RoundTrip();
        Malformed();
        Limits();
        Capture();
        return _checks;
    }

    private static ProgressSnapshot Snapshot()
    {
        var value = new ProgressSnapshot
        {
            Sequence = 1, SelectedStageId = 101, SelectedFloorId = 1,
            ClaimRevision = 2, DeckRevision = 7,
            CoreBooksAvailable = true, CoreBooksReason = CoreBooksReason.None
        };
        value.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Stage" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        floor.Units.Add("Unit"); value.Floors.Add(floor); value.ClaimOwners.Add(123);
        var deck = new ProgressSnapshot.UnitDeckEntry
        { UnitIdentity = 100, BookId = 200, BookInstanceId = 300, BookToken = 400, Capacity = 9 };
        deck.Cards.Add(600); value.UnitDecks.Add(deck);
        value.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 600, Count = 8 });
        var current = Row(400, 200, 300);
        current.Flags = CoreBookFlags.Equipped; current.OccupiedFloorId = 1; current.OccupiedUnitIndex = 0;
        value.CoreBooks.Add(current); value.CoreBooks.Add(Row(500, 200, 301));
        return value;
    }

    private static ProgressSnapshot.CoreBookEntry Row(ulong token, int id, int instance)
    {
        var row = new ProgressSnapshot.CoreBookEntry { BookToken = token, BookId = id, BookInstanceId = instance };
        row.Display.Available = true; row.Display.MaxHp = 75; row.Display.Break = 45;
        row.Display.PassiveIds.Add(1001); row.Display.PassiveIds.Add(1002);
        row.CurrentCards.Add(600); row.CurrentCards.Add(601); row.CurrentCards.Add(600);
        return row;
    }

    private static ProgressSnapshot Decode(byte[] packet, string name)
    {
        ProgressSnapshot result; string reason;
        Check(ProgressSnapshot.TryDecode(packet, Room, out result, out reason), name + " decode: " + reason);
        return result;
    }

    private static void RoundTrip()
    {
        var original = Snapshot();
        original.CoreBooks[1].BookReference = new object();
        var packet = original.Encode(Room);
        Check(packet[4] == 5, "snapshot wire version five");
        var decoded = Decode(packet, "complete core inventory");
        Check(decoded.CoreBooksAvailable && decoded.CoreBooksReason == CoreBooksReason.None, "availability round trip");
        Check(decoded.UnitDecks[0].BookToken == 400 && decoded.CoreBooks.Count == 2, "current and inventory tokens round trip");
        Check(decoded.CoreBooks[0].OccupiedFloorId == 1 && decoded.CoreBooks[0].OccupiedUnitIndex == 0 &&
            decoded.CoreBooks[0].Flags == CoreBookFlags.Equipped, "cross-floor occupancy fields round trip");
        Check(decoded.CoreBooks[1].BookId == 200 && decoded.CoreBooks[1].BookInstanceId == 301 &&
            decoded.CoreBooks[1].BookToken == 500, "same XML retains separate instance identity");
        Check(decoded.CoreBooks[1].Display.Available && decoded.CoreBooks[1].Display.MaxHp == 75 &&
            decoded.CoreBooks[1].Display.Break == 45 && decoded.CoreBooks[1].Display.PassiveIds.SequenceEqual(new[] { 1001, 1002 }),
            "actual stats and effective passives round trip");
        Check(decoded.CoreBooks[1].CurrentCards.SequenceEqual(new[] { 600, 601, 600 }), "stored deck duplicates and ordering round trip");
        BookModel resolved;
        Check(decoded.CoreBooks.All(row => row.BookReference == null) &&
            !EquipmentMirror.TryResolveBook(decoded, 500, out resolved), "physical host references never cross wire");
        var content = EquipmentMirror.EncodeContent(original);
        original.Sequence += 10; original.ClaimRevision += 10; original.DeckRevision += 10;
        original.CoreBooks.Reverse();
        foreach (var row in original.CoreBooks) { row.Display.PassiveIds.Reverse(); row.CurrentCards.Reverse(); }
        Check(content.SequenceEqual(EquipmentMirror.EncodeContent(original)), "core comparison ignores revisions and list presentation order");
        original.CoreBooks[0].CurrentCards.Add(602);
        Check(!content.SequenceEqual(EquipmentMirror.EncodeContent(original)), "stored card multiset changes content hash");
        foreach (var reason in new[] { CoreBooksReason.NotCaptured, CoreBooksReason.TooManyBooks, CoreBooksReason.PacketLimit,
            CoreBooksReason.CaptureFailed, CoreBooksReason.UnsupportedData })
        {
            var unavailable = Snapshot(); unavailable.CoreBooks.Clear(); unavailable.CoreBooksAvailable = false; unavailable.CoreBooksReason = reason;
            Check(EquipmentMirror.EncodeContent(unavailable).Length == 5, "unavailable extension is five bytes: " + reason);
            var copy = Decode(unavailable.Encode(Room), "unavailable " + reason);
            Check(!copy.CoreBooksAvailable && copy.CoreBooksReason == reason && copy.CoreBooks.Count == 0 &&
                copy.UnitDecks[0].BookToken == 0 && copy.UnitDecks[0].Cards.SequenceEqual(new[] { 600 }) && copy.CardStock[0].Count == 8,
                "unavailable inventory leaves card data usable: " + reason);
        }
    }

    private static Dictionary<string, int> Offsets(byte[] packet, ProgressSnapshot value)
    {
        var fields = new Dictionary<string, int>();
        using (var stream = new MemoryStream(packet))
        using (var reader = new BinaryReader(stream))
        {
            stream.Position = packet.Length - EquipmentMirror.EncodeContent(value).Length;
            fields["available"] = (int)stream.Position; reader.ReadByte();
            fields["reason"] = (int)stream.Position; reader.ReadByte();
            fields["currentCount"] = (int)stream.Position; var currentCount = reader.ReadByte();
            for (var i = 0; i < currentCount; i++)
            { fields["current" + i] = (int)stream.Position; reader.ReadUInt64(); }
            fields["count"] = (int)stream.Position; var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                var prefix = "row" + i + ".";
                fields[prefix + "token"] = (int)stream.Position; reader.ReadUInt64();
                fields[prefix + "id"] = (int)stream.Position; reader.ReadInt32();
                fields[prefix + "instance"] = (int)stream.Position; reader.ReadInt32();
                fields[prefix + "kind"] = (int)stream.Position; reader.ReadByte();
                fields[prefix + "flags"] = (int)stream.Position; reader.ReadUInt16();
                fields[prefix + "floor"] = (int)stream.Position; reader.ReadByte();
                fields[prefix + "unit"] = (int)stream.Position; reader.ReadByte();
                fields[prefix + "display"] = (int)stream.Position; var display = reader.ReadByte();
                if (display != 0)
                {
                    fields[prefix + "hp"] = (int)stream.Position; reader.ReadInt32();
                    fields[prefix + "bp"] = (int)stream.Position; reader.ReadInt32();
                    fields[prefix + "passiveCount"] = (int)stream.Position; var passives = reader.ReadByte();
                    for (var p = 0; p < passives; p++)
                    { fields[prefix + "passive" + p] = (int)stream.Position; reader.ReadInt32(); }
                }
                fields[prefix + "cardCount"] = (int)stream.Position; var cards = reader.ReadByte();
                for (var c = 0; c < cards; c++)
                { fields[prefix + "card" + c] = (int)stream.Position; reader.ReadInt32(); }
            }
            Check(stream.Position == packet.Length, "core extension offsets consume exact wire payload");
        }
        return fields;
    }

    private static void BadWire(byte[] packet, Dictionary<string, int> offsets, string field, byte[] replacement, string name)
    {
        var copy = (byte[])packet.Clone();
        Buffer.BlockCopy(replacement, 0, copy, offsets[field], replacement.Length);
        BadPacket(copy, name);
    }

    private static void BadPacket(byte[] packet, string name)
    {
        ProgressSnapshot result; string reason;
        Check(!ProgressSnapshot.TryDecode(packet, Room, out result, out reason) && result == null, name);
    }

    private static void BadDto(Action<ProgressSnapshot> mutate, string name)
    {
        var value = Snapshot(); mutate(value);
        try { value.Encode(Room); }
        catch (InvalidOperationException) { Check(true, name); return; }
        catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("FAIL equipment codec: malformed DTO accepted: " + name);
    }

    private static void Malformed()
    {
        var value = Snapshot(); var packet = value.Encode(Room); var offsets = Offsets(packet, value);
        foreach (var mutation in new[] {
            Tuple.Create("available", (byte)2), Tuple.Create("reason", (byte)6), Tuple.Create("currentCount", (byte)0),
            Tuple.Create("row1.kind", (byte)3), Tuple.Create("row1.floor", (byte)1), Tuple.Create("row0.unit", (byte)5),
            Tuple.Create("row0.floor", (byte)0), Tuple.Create("row1.display", (byte)2),
            Tuple.Create("row1.passiveCount", (byte)65), Tuple.Create("row1.cardCount", (byte)65) })
            BadWire(packet, offsets, mutation.Item1, new[] { mutation.Item2 }, "reject byte field " + mutation.Item1);
        BadWire(packet, offsets, "reason", new[] { (byte)CoreBooksReason.NotCaptured }, "available inventory requires reason None");
        BadWire(packet, offsets, "count", BitConverter.GetBytes((ushort)2049), "decoder book count exceeds 2048");
        BadWire(packet, offsets, "current0", BitConverter.GetBytes((ulong)0), "current positive book requires token");
        BadWire(packet, offsets, "current0", BitConverter.GetBytes((ulong)999), "current token must be present");
        BadWire(packet, offsets, "row1.token", BitConverter.GetBytes((ulong)400), "duplicate physical book token");
        BadWire(packet, offsets, "row1.token", BitConverter.GetBytes((ulong)0), "zero physical book token");
        BadWire(packet, offsets, "row1.flags", BitConverter.GetBytes((ushort)0x8000), "unknown core flags");
        foreach (var field in new[] { "row1.id", "row1.instance", "row1.hp", "row1.passive0", "row1.card0" })
            BadWire(packet, offsets, field, BitConverter.GetBytes(0), "zero required value " + field);
        BadWire(packet, offsets, "row1.id", BitConverter.GetBytes(-1), "negative page ID");
        BadWire(packet, offsets, "row1.instance", BitConverter.GetBytes(-1), "ordinary negative instance requires explicit flag");
        BadWire(packet, offsets, "row1.hp", BitConverter.GetBytes(1000001), "HP upper bound");
        BadWire(packet, offsets, "row1.bp", BitConverter.GetBytes(-1), "BP lower bound");
        BadWire(packet, offsets, "row1.bp", BitConverter.GetBytes(1000001), "BP upper bound");
        BadWire(packet, offsets, "row1.passive0", BitConverter.GetBytes(9999999), "placeholder passive ID excluded");
        BadWire(packet, offsets, "row1.passive1", BitConverter.GetBytes(1001), "duplicate effective passive ID excluded");
        for (var length = 0; length < packet.Length; length++) BadPacket(packet.Take(length).ToArray(), "truncated core packet " + length);
        BadPacket(packet.Concat(new byte[] { 0 }).ToArray(), "trailing payload rejected");
        BadPacket(new byte[65537], "over 64KiB packet rejected");
        var oldWire = (byte[])packet.Clone(); oldWire[4] = 4; BadPacket(oldWire, "old wire explicitly rejected");
        BadDto(s => s.CoreBooks[1].BookToken = 400, "encoder duplicate token");
        BadDto(s => s.CoreBooks[1].Kind = (CoreBookKind)255, "encoder unknown kind");
        BadDto(s => s.CoreBooks[1].Flags = (CoreBookFlags)0x8000, "encoder unknown flags");
        BadDto(s => s.CoreBooksAvailable = false, "encoder unavailable with nonempty list");
        BadDto(s => s.CoreBooksReason = CoreBooksReason.PacketLimit, "encoder available with failure reason");
        BadDto(s => s.CoreBooks[1].Display.Available = false, "encoder missing ordinary detail without unsupported flag");
        BadDto(s => s.CoreBooks[1].Display.AppearanceAvailable = true, "core inventory cannot carry arbitrary appearance graph");
        BadDto(s => s.CoreBooks[1].Flags = CoreBookFlags.UnsupportedCards, "unsupported stored deck must be empty");
        BadDto(s => s.UnitDecks[0].BookInstanceId++, "current token and instance must match");
        BadDto(s => s.UnitDecks[0].BookId++, "current token and page must match");
        BadDto(s => s.CoreBooks[1].Display.PassiveIds.Add(1001), "encoder duplicate passive");
    }

    private static void Limits()
    {
        var value = Snapshot(); var row = value.CoreBooks[1];
        row.Display.MaxHp = DeckMirror.MaxDisplayStat; row.Display.Break = 0;
        row.Display.PassiveIds.Clear(); row.CurrentCards.Clear();
        for (var i = 0; i < 64; i++) { row.Display.PassiveIds.Add(2000 + i); row.CurrentCards.Add(600); }
        var copy = Decode(value.Encode(Room), "exact card/passive bounds");
        Check(copy.CoreBooks[1].Display.MaxHp == 1000000 && copy.CoreBooks[1].Display.Break == 0 &&
            copy.CoreBooks[1].CurrentCards.Count == 64 && copy.CoreBooks[1].Display.PassiveIds.Count == 64, "inclusive stat/list bounds");
        BadDto(s => { s.CoreBooks[1].CurrentCards.Clear(); for (var i = 0; i < 65; i++) s.CoreBooks[1].CurrentCards.Add(600); }, "encoder 65 stored cards");
        BadDto(s => { s.CoreBooks[1].Display.PassiveIds.Clear(); for (var i = 0; i < 65; i++) s.CoreBooks[1].Display.PassiveIds.Add(2000 + i); }, "encoder 65 passives");
        value = new ProgressSnapshot { CoreBooksAvailable = true, CoreBooksReason = CoreBooksReason.None };
        for (var i = 0; i < EquipmentMirror.MaxBooks; i++) value.CoreBooks.Add(new ProgressSnapshot.CoreBookEntry
        { BookToken = (ulong)i + 1, BookId = 200, BookInstanceId = i + 1, Kind = CoreBookKind.Special });
        Check(Decode(value.Encode(Room), "exact book bound").CoreBooks.Count == 2048, "decoder accepts 2048 compact read-only rows");
        value.CoreBooks.Add(new ProgressSnapshot.CoreBookEntry { BookToken = 2049, BookId = 200, BookInstanceId = 2049, Kind = CoreBookKind.Special });
        try { value.Encode(Room); throw new Exception("FAIL equipment codec: 2049 books accepted"); }
        catch (InvalidOperationException) { Check(true, "encoder rejects 2049 books"); }
    }

    private static BookModel Physical(int id, int instance)
    {
        var book = new BookModel { BookId = new LorId { id = id }, instanceId = instance, HP = 88, Break = 55 };
        book.Cards.Add(new DiceCardXmlInfo { id = new LorId { id = 600 } });
        book.Passives.Add(new BookPassiveInfo { passive = new PassiveXmlInfo { id = new LorId { id = 4001 } } });
        return book;
    }

    private static ProgressSnapshot CaptureSnapshot(UnitDataModel unit)
    {
        var value = new ProgressSnapshot { SelectedStageId = 101, SelectedFloorId = 1 };
        value.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Stage" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        floor.Units.Add(unit.name); floor.UnitReferences.Add(unit); value.Floors.Add(floor); value.ClaimOwners.Add(123);
        DeckMirror.Capture(value); EquipmentMirror.Capture(value);
        return value;
    }

    private static ProgressSnapshot.CoreBookEntry Find(ProgressSnapshot value, BookModel book)
    { return value.CoreBooks.Find(row => ReferenceEquals(row.BookReference, book)); }

    private static void Capture()
    {
        var oldLibrary = LibraryModel.Instance; var oldInventory = BookInventoryModel.Instance;
        var oldCards = InventoryModel.Instance;
        try
        {
            LibraryModel.Instance = new LibraryModel(); BookInventoryModel.Instance = new BookInventoryModel();
            InventoryModel.Instance = new InventoryModel();
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = 600 }, num = 8 });
            var equipped = Physical(200, 101); var anotherFloorBook = Physical(200, 102); var target = Physical(200, 103);
            var appearance = Physical(202, 104); var basic = Physical(100, -1); basic.Basic = true;
            var special = Physical(250022, 105); special.HP = 222; special.Break = 111;
            var unit = new UnitDataModel { name = "Same name", bookItem = equipped, defaultBook = basic, AppearanceBook = appearance,
                MaxHp = 125, Break = 66 };
            var other = new UnitDataModel { name = "Same name", bookItem = anotherFloorBook };
            equipped.owner = unit; appearance.owner = unit; anotherFloorBook.owner = other;
            var floor = new LibraryFloorModel { Sephirah = SephirahType.Malkuth }; floor.Units.Add(unit);
            var secondFloor = new LibraryFloorModel { Sephirah = SephirahType.Yesod }; secondFloor.Units.Add(other);
            LibraryModel.Instance.OpenedFloors.Add(floor); LibraryModel.Instance.OpenedFloors.Add(secondFloor);
            BookInventoryModel.Instance.Books.AddRange(new[] { equipped, anotherFloorBook, target, appearance, special });
            var value = CaptureSnapshot(unit);
            Check(value.CoreBooksAvailable && value.CoreBooksReason == CoreBooksReason.None && value.CoreBooks.Count == 6, "capture complete inventory plus every existing floor default");
            var current = Find(value, equipped); var elsewhere = Find(value, anotherFloorBook); var available = Find(value, target);
            Check(current.BookToken != elsewhere.BookToken && current.BookToken != available.BookToken && elsewhere.BookToken != available.BookToken,
                "same XML instances receive separate tokens");
            Check(current.OccupiedFloorId == 1 && elsewhere.OccupiedFloorId == 2 &&
                (elsewhere.Flags & CoreBookFlags.Equipped) != 0, "cross-floor ownership is captured");
            Check(Find(value, appearance).OccupiedFloorId == 1 && (Find(value, appearance).Flags & CoreBookFlags.Equipped) != 0,
                "custom appearance book remains occupied");
            Check(current.Display.MaxHp == 88 && current.Display.Break == 55 && value.UnitDecks[0].Display.MaxHp == 125,
                "book details use actual book stats while role keeps gift-inclusive totals");
            Check(Find(value, basic).Kind == CoreBookKind.Default && Find(value, basic).Display.Available &&
                Find(value, special).Kind == CoreBookKind.Special && Find(value, special).Display.MaxHp == 222,
                "default and special book details are actual and read only");
            Check(available.CurrentCards.SequenceEqual(new[] { 600 }) && available.Display.PassiveIds.SequenceEqual(new[] { 4001 }),
                "unused page retains its stored deck and effective passives");
            BookModel resolved;
            Check(EquipmentMirror.TryResolveBook(value, available.BookToken, out resolved) && ReferenceEquals(resolved, target), "host token resolves exact physical object");
            var repeated = CaptureSnapshot(unit);
            Check(Find(repeated, equipped).BookToken == current.BookToken && Find(repeated, target).BookToken == available.BookToken,
                "tokens are stable across captures");
            Check(ReferenceEquals(unit.bookItem, equipped) && ReferenceEquals(equipped.owner, unit) && equipped.Cards.Count == 1 &&
                target.originData.equipedBookIdListInPassive.Count == 0 && LibraryModel.Instance.OpenedFloors.Count == 2,
                "capture leaves host owner, deck, inheritance and floor collection untouched");
            var replacement = Physical(200, 103);
            BookInventoryModel.Instance.Books.Remove(target); BookInventoryModel.Instance.Books.Add(replacement);
            var replaced = CaptureSnapshot(unit);
            Check(Find(replaced, replacement).BookToken != available.BookToken &&
                !EquipmentMirror.TryResolveBook(replaced, available.BookToken, out resolved), "recreated object with same instance number cannot reuse old token");
            BookInventoryModel.Instance.Books.Remove(replacement); BookInventoryModel.Instance.Books.Add(target);
            target.originData.equipedBookIdListInPassive.Add(999); target.reservedData.equipedBookIdListInPassive.Add(999);
            var passive = new PassiveModel();
            passive.originData.currentpassive = new PassiveXmlInfo { id = new LorId { id = 4001 } };
            passive.reservedData.currentpassive = new PassiveXmlInfo { id = new LorId { id = 4001 } };
            target.PassiveModels.Add(passive);
            available = Find(CaptureSnapshot(unit), target);
            Check(available.Flags == CoreBookFlags.None && available.Display.PassiveIds.SequenceEqual(new[] { 4001 }), "committed receiver and equal passive ID clones are eligible");
            target.originData.equipedPassiveBookInstanceId = 101; target.reservedData.equipedPassiveBookInstanceId = 101;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.PassiveBound) != 0, "actual passive donor is occupied");
            target.originData.equipedPassiveBookInstanceId = -1; target.reservedData.equipedPassiveBookInstanceId = -1;
            target.reservedData.equipedBookIdListInPassive[0] = 998;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.DraftMismatch) != 0, "unsubmitted source list draft is explicit");
            target.reservedData.equipedBookIdListInPassive[0] = 999;
            passive.reservedData.currentpassive.id.id = 4002;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.DraftMismatch) != 0, "unsubmitted passive selection draft is explicit");
            passive.originData.currentpassive = null; passive.reservedData.currentpassive = null;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.DraftMismatch) == 0, "matching null passive slots are committed metadata");
            passive.reservedData.receivepassivebookId = 5;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.DraftMismatch) != 0, "unsubmitted passive receiver draft is explicit");
            passive.reservedData.receivepassivebookId = -1;
            target.BlueLocked = true;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.Locked) != 0, "blue-primary lock captured"); target.BlueLocked = false;
            target.Cards.Clear(); for (var i = 0; i < 65; i++) target.Cards.Add(new DiceCardXmlInfo { id = new LorId { id = 600 } });
            available = Find(CaptureSnapshot(unit), target);
            Check((available.Flags & CoreBookFlags.UnsupportedCards) != 0 && available.CurrentCards.Count == 0, "oversized stored deck is a disabled complete row");
            target.Cards.Clear(); target.Cards.Add(new DiceCardXmlInfo { id = new LorId { id = 600 } });
            target.Passives.Clear(); for (var i = 0; i < 65; i++) target.Passives.Add(new BookPassiveInfo { passive = new PassiveXmlInfo { id = new LorId { id = 2000 + i } } });
            value = CaptureSnapshot(unit); available = Find(value, target);
            Check(value.CoreBooksAvailable && !available.Display.Available && available.Display.PassiveIds.Count == 0 &&
                (available.Flags & CoreBookFlags.UnsupportedDisplay) != 0, "oversized detail disables only its row");
            target.Passives.Clear(); target.HP = 0;
            Check((Find(CaptureSnapshot(unit), target).Flags & CoreBookFlags.UnsupportedDisplay) != 0, "invalid actual HP explicitly unsupported"); target.HP = 88;
            target.ClassInfo = null;
            available = Find(CaptureSnapshot(unit), target);
            Check(available.Kind == CoreBookKind.Special && (available.Flags & CoreBookFlags.CannotEquip) != 0 && !available.Display.Available,
                "unknown XML row remains explicit and cannot borrow client metadata"); target.ClassInfo = new BookXmlInfo();
            BookInventoryModel.Instance.Books.Clear();
            for (var i = 0; i < 2049; i++) BookInventoryModel.Instance.Books.Add(Physical(300, i + 1000));
            value = CaptureSnapshot(unit);
            Check(!value.CoreBooksAvailable && value.CoreBooksReason == CoreBooksReason.TooManyBooks && value.CoreBooks.Count == 0,
                "too many physical books clear whole pool without truncation");
            Check(value.UnitDecks[0].Cards.Count == 1 && value.CardStock[0].Count == 8 && Decode(value.Encode(Room), "too many inventory fallback").UnitDecks[0].BookToken == 0,
                "book count overflow retains legacy card snapshot");
            BookInventoryModel.Instance.Books.Clear();
            for (var i = 0; i < 140; i++)
            {
                var large = Physical(300, i + 1000); large.Passives.Clear(); large.Cards.Clear();
                for (var p = 0; p < 64; p++)
                { large.Passives.Add(new BookPassiveInfo { passive = new PassiveXmlInfo { id = new LorId { id = 2000 + p } } });
                    large.Cards.Add(new DiceCardXmlInfo { id = new LorId { id = 600 } }); }
                BookInventoryModel.Instance.Books.Add(large);
            }
            value = CaptureSnapshot(unit);
            Check(!value.CoreBooksAvailable && value.CoreBooksReason == CoreBooksReason.PacketLimit && value.CoreBooks.Count == 0,
                "metadata packet overflow clears complete pool instead of truncating");
            Check(Decode(value.Encode(Room), "packet limit fallback").CardStock[0].Count == 8, "packet overflow retains card stock");
            BookInventoryModel.Instance = null;
            value = CaptureSnapshot(unit);
            Check(!value.CoreBooksAvailable && value.CoreBooksReason == CoreBooksReason.CaptureFailed && value.UnitDecks[0].Cards.Count == 1,
                "capture failure preserves card snapshot with explicit reason");
        }
        finally
        { LibraryModel.Instance = oldLibrary; BookInventoryModel.Instance = oldInventory; InventoryModel.Instance = oldCards; }
    }
}
