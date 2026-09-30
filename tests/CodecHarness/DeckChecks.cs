using System;
using System.IO;
using System.Linq;
using RuinaCoop;
using UI;

internal static class DeckChecks
{
    internal static void Run(ulong roomId, ulong ownerOne, ulong ownerTwo)
    {
        Protocol(roomId);
        Authority(ownerOne, ownerTwo);
        Snapshot(roomId, ownerOne, ownerTwo);
        Capture(roomId, ownerOne);
    }

    private static void Protocol(ulong roomId)
    {
        var request = new DeckRequest
        {
            RequestId = uint.MaxValue, StageId = int.MaxValue, FloorId = byte.MaxValue,
            UnitIndex = byte.MaxValue, ClaimRevision = uint.MaxValue, DeckRevision = uint.MaxValue,
            CardId = int.MaxValue, Action = DeckAction.Remove
        };
        var packet = DeckProtocol.EncodeRequest(roomId, request);
        Check(packet.Length == 36 && DeckProtocol.TryDecodeRequest(packet, roomId, out var decoded) &&
            decoded.RequestId == request.RequestId && decoded.StageId == request.StageId &&
            decoded.FloorId == request.FloorId && decoded.UnitIndex == request.UnitIndex &&
            decoded.ClaimRevision == request.ClaimRevision && decoded.DeckRevision == request.DeckRevision &&
            decoded.CardId == request.CardId && decoded.Action == request.Action,
            "deck request round trip at field boundaries");
        Check(!DeckProtocol.TryDecodeRequest(null, roomId, out _) &&
            !DeckProtocol.TryDecodeRequest(packet, roomId + 1, out _) &&
            !DeckProtocol.TryDecodeRequest(packet.Concat(new byte[] { 0 }).ToArray(), roomId, out _),
            "deck request null, room binding and trailing data");
        for (var length = 0; length < packet.Length; length++)
        {
            Check(!DeckProtocol.TryDecodeRequest(packet.Take(length).ToArray(), roomId, out _),
                "deck request rejects truncation at " + length);
        }
        Check(!DeckProtocol.TryDecodeRequest(ChangeByte(packet, 0, 0), roomId, out _) &&
            !DeckProtocol.TryDecodeRequest(ChangeByte(packet, 4, 2), roomId, out _),
            "deck request magic and wire version");
        Check(!DeckProtocol.TryDecodeRequest(ChangeInt(packet, 13, 0), roomId, out _),
            "deck request ID zero rejected");
        foreach (var cardId in new[] { 0, -1, int.MinValue })
        {
            Check(!DeckProtocol.TryDecodeRequest(ChangeInt(packet, 31, cardId), roomId, out _),
                "deck request nonpositive card ID " + cardId);
        }
        for (var action = 0; action <= byte.MaxValue; action++)
        {
            Check(DeckProtocol.TryDecodeRequest(ChangeByte(packet, 35, (byte)action), roomId, out _) ==
                (action == (byte)DeckAction.Add || action == (byte)DeckAction.Remove),
                "deck request action whitelist " + action);
        }

        var reply = new DeckReply
        {
            RequestId = uint.MaxValue, Result = DeckResultCode.Failed,
            DeckRevision = uint.MaxValue, VanillaState = byte.MaxValue
        };
        var replyPacket = DeckProtocol.EncodeReply(roomId, reply);
        Check(replyPacket.Length == 23 && DeckProtocol.TryDecodeReply(replyPacket, roomId, out var parsed) &&
            parsed.RequestId == reply.RequestId && parsed.Result == reply.Result &&
            parsed.DeckRevision == reply.DeckRevision && parsed.VanillaState == reply.VanillaState,
            "deck reply round trip at field boundaries");
        Check(!DeckProtocol.TryDecodeReply(null, roomId, out _) &&
            !DeckProtocol.TryDecodeReply(replyPacket, roomId + 1, out _) &&
            !DeckProtocol.TryDecodeReply(replyPacket.Concat(new byte[] { 0 }).ToArray(), roomId, out _) &&
            !DeckProtocol.TryDecodeReply(ChangeByte(replyPacket, 0, 0), roomId, out _) &&
            !DeckProtocol.TryDecodeReply(ChangeByte(replyPacket, 4, 2), roomId, out _) &&
            !DeckProtocol.TryDecodeReply(ChangeInt(replyPacket, 13, 0), roomId, out _),
            "deck reply binding, header, zero ID and exact size");
        for (var length = 0; length < replyPacket.Length; length++)
        {
            Check(!DeckProtocol.TryDecodeReply(replyPacket.Take(length).ToArray(), roomId, out _),
                "deck reply rejects truncation at " + length);
        }
        for (var result = 0; result <= byte.MaxValue; result++)
        {
            Check(DeckProtocol.TryDecodeReply(ChangeByte(replyPacket, 17, (byte)result), roomId, out _) ==
                (result <= (byte)DeckResultCode.Failed), "deck reply result whitelist " + result);
        }
    }

    private static void Authority(ulong ownerOne, ulong ownerTwo)
    {
        var snapshot = NewSnapshot(ownerOne, ownerTwo);
        var request = NewRequest(snapshot);
        Expect(snapshot, ownerOne, request, DeckResultCode.Accepted, "owner can add stocked card");
        request.Action = DeckAction.Remove;
        request.CardId = 100;
        Expect(snapshot, ownerOne, request, DeckResultCode.Accepted, "owner can remove an equipped card");
        Check(snapshot.UnitDecks[0].Cards.SequenceEqual(new[] { 100, 100, 101 }) &&
            snapshot.CardStock[0].Count == 1 && snapshot.DeckRevision == 22,
            "authority validation never mutates deck, inventory or revision");
        request = NewRequest(snapshot);
        Expect(null, ownerOne, request, DeckResultCode.NotReady, "no snapshot");
        Expect(snapshot, 0, request, DeckResultCode.InvalidRequest, "zero sender");
        Expect(snapshot, ownerTwo, request, DeckResultCode.NotOwner, "other owner cannot edit slot");
        snapshot.ClaimOwners[0] = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.NotOwner, "unclaimed slot cannot be edited");
        snapshot.ClaimOwners[0] = ownerOne;
        request.RequestId = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidRequest, "zero request ID");
        request = NewRequest(snapshot);
        request.Action = (DeckAction)255;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidRequest, "unknown action");
        foreach (var cardId in new[] { 0, -1, int.MinValue, int.MaxValue })
        {
            request = NewRequest(snapshot);
            request.CardId = cardId;
            Expect(snapshot, ownerOne, request, DeckResultCode.InvalidCard, "invalid or unknown card " + cardId);
        }
        request = NewRequest(snapshot);
        request.StageId++;
        Expect(snapshot, ownerOne, request, DeckResultCode.NotReady, "wrong selected stage");
        request = NewRequest(snapshot);
        request.FloorId++;
        Expect(snapshot, ownerOne, request, DeckResultCode.NotReady, "wrong selected floor");
        request = NewRequest(snapshot);
        snapshot.SelectedStageId = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.NotReady, "no selected stage");
        snapshot.SelectedStageId = request.StageId;
        snapshot.SelectedFloorId = PrepClaims.NoFloor;
        Expect(snapshot, ownerOne, request, DeckResultCode.NotReady, "no selected floor");
        snapshot.SelectedFloorId = request.FloorId;
        request.ClaimRevision--;
        Expect(snapshot, ownerOne, request, DeckResultCode.StaleClaim, "stale claim revision");
        request = NewRequest(snapshot);
        request.DeckRevision--;
        Expect(snapshot, ownerOne, request, DeckResultCode.StaleDeck, "stale deck revision");
        request = NewRequest(snapshot);
        request.UnitIndex = byte.MaxValue;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidRequest, "slot outside selected roster");
        request = NewRequest(snapshot);
        snapshot.DecksFrozen = true;
        Expect(snapshot, ownerOne, request, DeckResultCode.Frozen, "started reception freezes decks");
        snapshot.DecksFrozen = false;
        snapshot.UnitDecks[0].Fixed = true;
        Expect(snapshot, ownerOne, request, DeckResultCode.UnsupportedBook, "fixed deck is unsupported");
        snapshot.UnitDecks[0].Fixed = false;
        snapshot.UnitDecks[0].MultiDeck = true;
        Expect(snapshot, ownerOne, request, DeckResultCode.UnsupportedBook, "multiple decks are unsupported");
        snapshot.UnitDecks[0].MultiDeck = false;
        snapshot.UnitDecks[0].BookId = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.UnsupportedBook, "missing key page is unsupported");
        snapshot.UnitDecks[0].BookId = 1001;
        snapshot.CardStock[0].Count = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidCard, "no remaining card stock");
        snapshot.CardStock[0].Count = -1;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidCard, "negative card stock cannot grant card");
        snapshot.CardStock[0].Count = 1;
        request.Action = DeckAction.Remove;
        request.CardId = 102;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidCard, "remove requires card in this deck");
        request.CardId = 200;
        Expect(snapshot, ownerOne, request, DeckResultCode.InvalidCard, "another deck's card cannot be removed");
        request.CardId = 100;
        snapshot.CardStock[0].Count = 0;
        Expect(snapshot, ownerOne, request, DeckResultCode.Accepted, "remove needs no inventory copy");
        snapshot.CardStock[0].Count = 1;
        request = NewRequest(snapshot);
        snapshot.UnitDecks[0].Capacity = snapshot.UnitDecks[0].Cards.Count;
        Expect(snapshot, ownerOne, request, DeckResultCode.Accepted,
            "capacity and duplicate limits remain vanilla's decision");

        var competing = NewRequest(snapshot);
        competing.UnitIndex = 1;
        competing.RequestId = 2;
        Expect(snapshot, ownerTwo, competing, DeckResultCode.Accepted, "second owner sees last available copy");
        snapshot.DeckRevision++;
        snapshot.CardStock[0].Count = 0;
        snapshot.UnitDecks[0].Cards.Add(request.CardId);
        Expect(snapshot, ownerTwo, competing, DeckResultCode.StaleDeck,
            "concurrent request using last copy is stale after host commit");
        competing.DeckRevision = snapshot.DeckRevision;
        Expect(snapshot, ownerTwo, competing, DeckResultCode.InvalidCard,
            "retry with fresh revision cannot overspend shared stock");
    }

    private static void Snapshot(ulong roomId, ulong ownerOne, ulong ownerTwo)
    {
        var source = NewSnapshot(ownerOne, ownerTwo);
        source.DecksFrozen = true;
        source.DeckRevision = uint.MaxValue;
        source.UnitDecks[1].Fixed = true;
        source.UnitDecks[1].MultiDeck = true;
        var packet = source.Encode(roomId);
        Check(ProgressSnapshot.TryDecode(packet, roomId, out var decoded) &&
            decoded.DeckRevision == uint.MaxValue && decoded.DecksFrozen && decoded.UnitDecks.Count == 2 &&
            decoded.UnitDecks[0].BookId == 1001 && decoded.UnitDecks[0].BookInstanceId == 1 &&
            decoded.UnitDecks[0].Capacity == 9 && !decoded.UnitDecks[0].Fixed &&
            decoded.UnitDecks[0].Cards.SequenceEqual(new[] { 100, 100, 101 }) &&
            decoded.UnitDecks[1].Fixed && decoded.UnitDecks[1].MultiDeck &&
            decoded.CardStock.Count == 2 && decoded.CardStock[0].Id == 100 && decoded.CardStock[0].Count == 1 &&
            decoded.CardStock[1].Id == 101 && decoded.CardStock[1].Count == 2,
            "deck snapshot round trip preserves identity, flags, duplicate cards, stock and revision");
        var offsets = Locate(packet);
        Reject(ChangeByte(packet, offsets.Frozen, 2), roomId, "invalid frozen flag");
        Reject(ChangeByte(packet, offsets.DeckCount, 6), roomId, "too many unit decks");
        Reject(ChangeByte(packet, offsets.DeckCount, 1), roomId, "deck count mismatches selected floor");
        Reject(ChangeInt(packet, offsets.FirstDeck, -1), roomId, "negative key page ID");
        Reject(ChangeInt(packet, offsets.FirstDeck, 0), roomId, "absent key page must be view only");
        Reject(ChangeByte(packet, offsets.FirstDeck + 8, 65), roomId, "deck capacity exceeds limit");
        Reject(ChangeByte(packet, offsets.FirstDeck + 8, 2), roomId, "regular deck exceeds its capacity");
        Reject(ChangeByte(packet, offsets.FirstDeck + 8, 0), roomId, "regular deck cannot have zero capacity");
        Reject(ChangeByte(packet, offsets.FirstDeck + 9, 4), roomId, "unknown deck flag");
        Reject(ChangeByte(packet, offsets.FirstDeck + 10, 65), roomId, "too many cards in a deck");
        Reject(ChangeInt(packet, offsets.FirstDeck + 11, 0), roomId, "zero deck card ID");
        Reject(ChangeInt(packet, offsets.FirstDeck + 11, int.MinValue), roomId, "negative deck card ID");
        Reject(ChangeUShort(packet, offsets.StockCount, 2049), roomId, "too many stock entries");
        Reject(ChangeInt(packet, offsets.FirstStock, 0), roomId, "zero stock ID");
        Reject(ChangeInt(packet, offsets.FirstStock, -1), roomId, "negative stock ID");
        Reject(ChangeInt(packet, offsets.FirstStock + 4, 0), roomId, "zero stock count");
        Reject(ChangeInt(packet, offsets.FirstStock + 4, -1), roomId, "negative stock count");
        Reject(ChangeInt(packet, offsets.FirstStock + 4, 1000001), roomId, "stock count exceeds limit");
        Reject(ChangeInt(packet, offsets.FirstStock + 8, 100), roomId, "duplicate stock ID");
        Reject(ChangeByte(packet, offsets.SelectedFloor, PrepClaims.NoFloor), roomId,
            "deck data without a selected floor");
        Reject(ChangeByte(packet, offsets.FloorId, (byte)SephirahType.None), roomId, "None is not a library floor");
        Reject(ChangeByte(packet, offsets.FloorId, (byte)SephirahType.ETC), roomId, "ETC is not a library floor");
        Reject(ChangeByte(packet, offsets.FloorId, 255), roomId, "unknown floor enum");
        Reject(ChangeByte(packet, offsets.StageState, 255), roomId, "unknown stage enum");
        Reject(ChangeByte(packet, offsets.StageName, 255), roomId, "malformed UTF8 name");
        Reject(ChangeByte(packet, 4, 2), roomId, "old snapshot wire version");
        for (var length = 0; length < packet.Length; length++)
        {
            Reject(packet.Take(length).ToArray(), roomId, "deck snapshot truncation at " + length);
        }
        Reject(packet.Concat(new byte[] { 0 }).ToArray(), roomId, "deck snapshot trailing data");
        var empty = new ProgressSnapshot().Encode(roomId);
        Check(empty.Length == 46 && ProgressSnapshot.TryDecode(empty, roomId, out var emptyDecoded) &&
            emptyDecoded.UnitDecks.Count == 0 && emptyDecoded.CardStock.Count == 0,
            "minimum snapshot without a selected floor");

        var boundary = NewSnapshot(ownerOne, ownerTwo);
        boundary.UnitDecks[0].Fixed = true;
        boundary.UnitDecks[0].Capacity = 64;
        boundary.UnitDecks[0].Cards.Clear();
        boundary.UnitDecks[0].Cards.AddRange(Enumerable.Repeat(int.MaxValue, 64));
        boundary.CardStock.Clear();
        for (var index = 1; index <= 2048; index++)
        {
            boundary.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = index, Count = 1000000 });
        }
        Check(ProgressSnapshot.TryDecode(boundary.Encode(roomId), roomId, out var boundaryDecoded) &&
            boundaryDecoded.UnitDecks[0].Cards.Count == 64 && boundaryDecoded.CardStock.Count == 2048 &&
            boundaryDecoded.CardStock[2047].Count == 1000000, "deck and stock exact supported limits");
        boundary.UnitDecks[0].Cards.Add(1);
        Throws(() => boundary.Encode(roomId), "encoder rejects card count overflow");
        boundary.UnitDecks[0].Cards.RemoveAt(64);
        boundary.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 2049, Count = 1 });
        Throws(() => boundary.Encode(roomId), "encoder rejects stock count overflow");
        boundary.CardStock.RemoveAt(2048);
        boundary.UnitDecks[0].Capacity = 65;
        Throws(() => boundary.Encode(roomId), "encoder rejects capacity overflow");

        var noCapacity = NewSnapshot(ownerOne, ownerTwo);
        noCapacity.UnitDecks[0].Cards.Clear();
        noCapacity.UnitDecks[0].Capacity = 0;
        Throws(() => noCapacity.Encode(roomId), "encoder rejects empty editable deck with zero capacity");

        var content = DeckMirror.EncodeContent(source);
        source.Sequence--;
        source.ClaimRevision++;
        source.DeckRevision--;
        source.UnitDecks[0].Cards.Reverse();
        source.CardStock.Reverse();
        Check(content.SequenceEqual(DeckMirror.EncodeContent(source)),
            "content comparison ignores revisions and card/inventory order");
        source.UnitDecks[0].BookInstanceId++;
        Check(!content.SequenceEqual(DeckMirror.EncodeContent(source)),
            "replacing key page instance changes deck content");

        var unicode = NewSnapshot(ownerOne, ownerTwo);
        unicode.Stages[0].Name = new string('x', 253) + "😀";
        Check(ProgressSnapshot.TryDecode(unicode.Encode(roomId), roomId, out var unicodeDecoded) &&
            unicodeDecoded.Stages[0].Name == new string('x', 253),
            "name truncation preserves complete surrogate pairs and valid UTF8");
    }

    private static void Capture(ulong roomId, ulong owner)
    {
        var source = NewSnapshot(owner, owner);
        var original = InventoryModel.Instance.Cards.ToArray();
        InventoryModel.Instance.Cards.Clear();
        try
        {
            var regular = new BookModel { BookId = new LorId { id = 500 }, instanceId = 42 };
            regular.Cards.Add(new LOR_DiceSystem.DiceCardXmlInfo { id = new LorId { id = 100 } });
            regular.Cards.Add(new LOR_DiceSystem.DiceCardXmlInfo { id = new LorId { id = 100 } });
            var locked = new BookModel { BookId = new LorId { id = 501 }, instanceId = 43, MultiDeck = true };
            locked.Cards.Add(new LOR_DiceSystem.DiceCardXmlInfo
                { id = new LorId { id = 700, packageId = "Workshop" } });
            source.Floors[0].UnitReferences.Add(new UnitDataModel { name = "one", bookItem = regular });
            source.Floors[0].UnitReferences.Add(new UnitDataModel { name = "two", bookItem = locked });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = 100 }, num = 3 });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = 100 }, num = 99 });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = 101 }, num = 2 });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = 102 }, num = 0 });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel { Id = new LorId { id = -1 }, num = 4 });
            InventoryModel.Instance.Cards.Add(new DiceCardItemModel
                { Id = new LorId { id = 100, packageId = "Workshop" }, num = 999 });
            InventoryModel.Instance.Cards.Add(null);
            DeckMirror.Capture(source);
            Check(source.UnitDecks.Count == 2 && source.UnitDecks[0].BookId == 500 &&
                source.UnitDecks[0].BookInstanceId == 42 && source.UnitDecks[0].Cards.SequenceEqual(new[] { 100, 100 }) &&
                !source.UnitDecks[0].Fixed && source.UnitDecks[1].Fixed && source.UnitDecks[1].MultiDeck &&
                source.UnitDecks[1].Cards.Count == 0, "capture vanilla deck identity and view-only workshop card");
            Check(source.CardStock.Count == 2 && source.CardStock[0].Id == 100 && source.CardStock[0].Count == 99 &&
                source.CardStock[1].Id == 101 && source.CardStock[1].Count == 2,
                "capture stock filters invalid cards and keeps maximum duplicate synthetic row");
            Check(ProgressSnapshot.TryDecode(source.Encode(roomId), roomId, out _),
                "captured deck snapshot passes decoder validation");
            regular.Locked = true;
            DeckMirror.Capture(source);
            Check(source.UnitDecks[0].Fixed, "locked book captured as view only");
            regular.Locked = false;
            ((UnitDataModel)source.Floors[0].UnitReferences[0]).Locked = true;
            DeckMirror.Capture(source);
            Check(source.UnitDecks[0].Fixed, "locked librarian captured as view only");
            source.Floors[0].UnitReferences[0] = null;
            DeckMirror.Capture(source);
            Check(source.UnitDecks[0].BookId == 0 && source.UnitDecks[0].Fixed,
                "missing librarian or key page is view only");
            Check(ProgressSnapshot.TryDecode(source.Encode(roomId), roomId, out _),
                "missing key page with fixed zero capacity still round trips");
            source.SelectedFloorId = PrepClaims.NoFloor;
            source.ClaimOwners.Clear();
            DeckMirror.Capture(source);
            Check(source.UnitDecks.Count == 0 && source.CardStock.Count == 0,
                "clearing selected floor clears all mirrored deck and stock data");
        }
        finally
        {
            InventoryModel.Instance.Cards.Clear();
            InventoryModel.Instance.Cards.AddRange(original);
        }
    }

    private sealed class PacketOffsets
    {
        internal int StageState;
        internal int StageName;
        internal int FloorId;
        internal int SelectedFloor;
        internal int Frozen;
        internal int DeckCount;
        internal int FirstDeck;
        internal int StockCount;
        internal int FirstStock;
    }

    private static PacketOffsets Locate(byte[] packet)
    {
        var offsets = new PacketOffsets();
        using (var reader = new BinaryReader(new MemoryStream(packet)))
        {
            reader.BaseStream.Position = 29;
            var stages = reader.ReadUInt16();
            for (var index = 0; index < stages; index++)
            {
                reader.ReadInt32();
                reader.ReadInt32();
                if (index == 0) offsets.StageState = (int)reader.BaseStream.Position;
                reader.ReadByte();
                var length = reader.ReadUInt16();
                if (index == 0) offsets.StageName = (int)reader.BaseStream.Position;
                reader.ReadBytes(length);
            }
            var floors = reader.ReadByte();
            for (var index = 0; index < floors; index++)
            {
                if (index == 0) offsets.FloorId = (int)reader.BaseStream.Position;
                reader.ReadByte();
                reader.ReadInt32();
                var units = reader.ReadByte();
                for (var unit = 0; unit < units; unit++) reader.ReadBytes(reader.ReadUInt16());
            }
            offsets.SelectedFloor = (int)reader.BaseStream.Position;
            reader.ReadByte();
            reader.ReadUInt32();
            var owners = reader.ReadByte();
            for (var index = 0; index < owners; index++) reader.ReadUInt64();
            reader.ReadUInt32();
            offsets.Frozen = (int)reader.BaseStream.Position;
            reader.ReadByte();
            offsets.DeckCount = (int)reader.BaseStream.Position;
            var decks = reader.ReadByte();
            offsets.FirstDeck = (int)reader.BaseStream.Position;
            for (var index = 0; index < decks; index++)
            {
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadByte();
                reader.ReadByte();
                var cards = reader.ReadByte();
                for (var card = 0; card < cards; card++) reader.ReadInt32();
            }
            offsets.StockCount = (int)reader.BaseStream.Position;
            reader.ReadUInt16();
            offsets.FirstStock = (int)reader.BaseStream.Position;
        }
        return offsets;
    }

    private static byte[] ChangeUShort(byte[] source, int offset, ushort value)
    {
        var packet = (byte[])source.Clone();
        BitConverter.GetBytes(value).CopyTo(packet, offset);
        return packet;
    }

    private static void Reject(byte[] packet, ulong roomId, string name)
    {
        Check(!ProgressSnapshot.TryDecode(packet, roomId, out var decoded, out var reason) &&
            decoded == null && !string.IsNullOrEmpty(reason), name + " rejected with diagnostics");
    }

    private static void Throws(Action action, string name)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception("FAIL: " + name);
    }

    private static ProgressSnapshot NewSnapshot(ulong ownerOne, ulong ownerTwo)
    {
        var snapshot = new ProgressSnapshot
        {
            Sequence = uint.MaxValue, Chapter = 7, LibraryLevel = 60,
            SelectedStageId = 101, SelectedFloorId = (byte)SephirahType.Malkuth,
            ClaimRevision = 11, DeckRevision = 22
        };
        snapshot.Stages.Add(new ProgressSnapshot.StageEntry
        {
            Id = 101, Chapter = 7, State = StoryState.Open, Name = "Reception 接待"
        });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth, Level = 6 };
        floor.Units.Add("罗兰");
        floor.Units.Add("Angela");
        snapshot.Floors.Add(floor);
        snapshot.ClaimOwners.Add(ownerOne);
        snapshot.ClaimOwners.Add(ownerTwo);
        var first = new ProgressSnapshot.UnitDeckEntry { BookId = 1001, BookInstanceId = 1, Capacity = 9 };
        first.Cards.AddRange(new[] { 100, 100, 101 });
        snapshot.UnitDecks.Add(first);
        var second = new ProgressSnapshot.UnitDeckEntry
        {
            BookId = 1002, BookInstanceId = 2, Capacity = 9
        };
        second.Cards.Add(200);
        snapshot.UnitDecks.Add(second);
        snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 100, Count = 1 });
        snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 101, Count = 2 });
        return snapshot;
    }

    private static DeckRequest NewRequest(ProgressSnapshot snapshot)
    {
        return new DeckRequest
        {
            RequestId = 1, StageId = snapshot.SelectedStageId, FloorId = snapshot.SelectedFloorId,
            UnitIndex = 0, ClaimRevision = snapshot.ClaimRevision, DeckRevision = snapshot.DeckRevision,
            CardId = 100, Action = DeckAction.Add
        };
    }

    private static void Expect(ProgressSnapshot snapshot, ulong sender, DeckRequest request,
        DeckResultCode expected, string name)
    {
        var actual = DeckAuthority.Validate(snapshot, sender, request);
        Check(actual == expected, name + ": expected " + expected + ", got " + actual);
    }

    private static byte[] ChangeByte(byte[] source, int offset, byte value)
    {
        var packet = (byte[])source.Clone();
        packet[offset] = value;
        return packet;
    }

    private static byte[] ChangeInt(byte[] source, int offset, int value)
    {
        var packet = (byte[])source.Clone();
        BitConverter.GetBytes(value).CopyTo(packet, offset);
        return packet;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
