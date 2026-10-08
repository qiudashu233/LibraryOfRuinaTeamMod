using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuinaCoop;
using UI;

internal static class PassiveCodecChecks
{
    private const ulong Room = 987;
    private static int _checks;
    private static void Check(bool value, string name)
    { _checks++; if (!value) throw new Exception("FAIL passive codec: " + name); }
    internal static int Run()
    { _checks = 0; Protocol(); Metadata(); Capture(); return _checks; }
    private static ProgressSnapshot Snapshot()
    {
        var snapshot = new ProgressSnapshot { Sequence = 1, SelectedStageId = 101, SelectedFloorId = 1,
            ClaimRevision = 2, DeckRevision = 7, CoreBooksAvailable = true, CoreBooksReason = CoreBooksReason.None };
        snapshot.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Stage" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth }; floor.Units.Add("Host");
        snapshot.Floors.Add(floor); snapshot.ClaimOwners.Add(123);
        snapshot.UnitDecks.Add(new ProgressSnapshot.UnitDeckEntry { UnitIdentity = 100, BookToken = 10, BookId = 200, BookInstanceId = 1000, Capacity = 9 });
        snapshot.UnitDecks[0].Cards.Add(600);
        snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 600, Count = 8 });
        snapshot.CoreBooks.Add(Core(10, 1000)); snapshot.CoreBooks[0].Flags = CoreBookFlags.Equipped;
        snapshot.CoreBooks[0].OccupiedFloorId = 1; snapshot.CoreBooks[0].OccupiedUnitIndex = 0;
        snapshot.CoreBooks.Add(Core(20, 2000)); snapshot.CoreBooks.Add(Core(30, 3000));
        return snapshot;
    }
    private static ProgressSnapshot.CoreBookEntry Core(ulong token, int instance)
    {
        var entry = new ProgressSnapshot.CoreBookEntry { BookToken = token, BookId = 200, BookInstanceId = instance };
        entry.Display.Available = true; entry.Display.MaxHp = 100; entry.Display.Break = 50;
        return entry;
    }
    private static PassiveRequest Request()
    { return new PassiveRequest { RequestId = 9, StageId = 101, FloorId = 1, UnitIndex = 0, UnitIdentity = 100,
        BookToken = 10, ClaimRevision = 2, DeckRevision = 7, SourceBookTokens = new ulong[] { 20, 30 },
        Slots = new[] { new PassiveSelection { Mode = PassiveSelectionMode.RestoreNative, SourceSlotIndex = 255, ExpectedOriginPassiveId = 101 },
            new PassiveSelection { Mode = PassiveSelectionMode.Inherit, SourceBookToken = 20, SourceSlotIndex = 0, ExpectedOriginPassiveId = 202 } } }; }
    private static void Protocol()
    {
        var request = Request(); var packet = PassiveProtocol.EncodeRequest(Room, request); PassiveRequest parsed;
        Check(PassiveProtocol.TryDecodeRequest(packet, Room, out parsed), "request round trip");
        Check(parsed.RequestId == 9 && parsed.UnitIdentity == 100 && parsed.BookToken == 10 && parsed.Slots.Length == 2 &&
            parsed.Slots[1].SourceBookToken == 20 && parsed.Slots[1].SourceSlotIndex == 0 && parsed.Slots[1].ExpectedOriginPassiveId == 202 &&
            parsed.SourceBookTokens.SequenceEqual(new ulong[] { 20, 30 }), "fixed slot identity and zero borrowed source preserved");
        for (var length = 0; length < packet.Length; length++)
            Check(!PassiveProtocol.TryDecodeRequest(packet.Take(length).ToArray(), Room, out _), "truncated request " + length);
        Check(!PassiveProtocol.TryDecodeRequest(packet.Concat(new byte[] { 0 }).ToArray(), Room, out _), "request trailing byte");
        Check(!PassiveProtocol.TryDecodeRequest(packet, Room + 1, out _), "request bound to room");
        foreach (var offset in new[] { 0, 4, 13, 17, 21, 22, 23, 31 })
        { var bad = (byte[])packet.Clone(); if (offset <= 4) bad[offset] ^= 255; else if (offset == 22) bad[offset] = 255;
            else Array.Clear(bad, offset, offset == 13 || offset == 17 ? 4 : offset >= 23 ? 8 : 1);
            Check(!PassiveProtocol.TryDecodeRequest(bad, Room, out _), "invalid identity/header at " + offset); }
        var mutated = (byte[])packet.Clone(); mutated[47] = 65; Check(!PassiveProtocol.TryDecodeRequest(mutated, Room, out _), "request too many slots");
        mutated = (byte[])packet.Clone(); mutated[48] = 2; Check(!PassiveProtocol.TryDecodeRequest(mutated, Room, out _), "unknown selection mode");
        foreach (var bad in new ActionRef[] {
            delegate(ref PassiveRequest r) { r.SourceBookTokens = new ulong[] { 20, 20 }; },
            delegate(ref PassiveRequest r) { r.SourceBookTokens = new ulong[] { 10 }; },
            delegate(ref PassiveRequest r) { r.SourceBookTokens = new ulong[] { 0 }; },
            delegate(ref PassiveRequest r) { r.SourceBookTokens = new ulong[] { 30 }; },
            delegate(ref PassiveRequest r) { r.Slots[0].SourceSlotIndex = 0; },
            delegate(ref PassiveRequest r) { r.Slots[1].SourceSlotIndex = 64; },
            delegate(ref PassiveRequest r) { r.Slots[1].ExpectedOriginPassiveId = PassiveMirror.EmptyId; },
            delegate(ref PassiveRequest r) { r.Slots[1].ExpectedOriginPassiveId = 0; },
            delegate(ref PassiveRequest r) { r.Slots = new PassiveSelection[0]; },
            delegate(ref PassiveRequest r) { r.SourceBookTokens = null; } })
        { request = Request(); bad(ref request); Check(!PassiveProtocol.ValidRequest(request), "invalid request DTO");
            var rejected = false; try { PassiveProtocol.EncodeRequest(Room, request); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "encoder rejects invalid request DTO"); }
        request = Request(); request.Slots = Enumerable.Range(0, 64).Select(i => new PassiveSelection { Mode = PassiveSelectionMode.RestoreNative,
            SourceSlotIndex = 255, ExpectedOriginPassiveId = PassiveMirror.EmptyId }).ToArray(); request.SourceBookTokens = new ulong[] { 20, 30, 40, 50 };
        packet = PassiveProtocol.EncodeRequest(Room, request);
        Check(packet.Length == 977 && PassiveProtocol.TryDecodeRequest(packet, Room, out parsed) && parsed.Slots.Length == 64, "maximum bounded request");
        foreach (PassiveResultCode code in Enum.GetValues(typeof(PassiveResultCode)))
        {
            var reply = new PassiveReply { RequestId = 9, Result = code, DeckRevision = 11 }; var bytes = PassiveProtocol.EncodeReply(Room, reply);
            Check(PassiveProtocol.TryDecodeReply(bytes, Room, out var response) && response.Result == code && response.DeckRevision == 11, "ACK round trip " + code);
        }
        var ack = PassiveProtocol.EncodeReply(Room, new PassiveReply { RequestId = 9, Result = PassiveResultCode.Accepted, DeckRevision = 11 });
        for (var length = 0; length < ack.Length; length++) Check(!PassiveProtocol.TryDecodeReply(ack.Take(length).ToArray(), Room, out _), "truncated ACK " + length);
        ack[17] = 18; Check(!PassiveProtocol.TryDecodeReply(ack, Room, out _), "unknown ACK result");
    }
    private delegate void ActionRef(ref PassiveRequest request);
    private static ProgressSnapshot.PassiveSlotEntry Slot(int id, int cost = 0, PassiveSlotFlags flags = PassiveSlotFlags.CanGive | PassiveSlotFlags.CanReceive)
    { return new ProgressSnapshot.PassiveSlotEntry { OriginId = id, CurrentId = id, Cost = cost, CurrentCost = cost, Flags = flags, SourceSlotIndex = 255 }; }
    private static ProgressSnapshot MetadataSample()
    {
        var snapshot = Snapshot(); snapshot.PassivesAvailable = true; snapshot.PassivesReason = PassivesReason.None;
        var target = new ProgressSnapshot.PassiveBookEntry { BookToken = 10, Flags = PassiveBookFlags.ReceiverAllowed, MaxCost = 12 };
        target.SourceTokens.AddRange(new ulong[] { 20, 30 }); target.Slots.Add(Slot(101, -2, PassiveSlotFlags.Negative | PassiveSlotFlags.Locked));
        target.Slots[0].CurrentNegative = true;
        var received = Slot(PassiveMirror.EmptyId); received.CurrentId = 202; received.CurrentCost = 3; received.CurrentRarity = 2;
        received.SourceBookToken = 20; received.SourceSlotIndex = 0; target.Slots.Add(received);
        snapshot.PassiveBooks.Add(target);
        var source = new ProgressSnapshot.PassiveBookEntry { BookToken = 20, Flags = PassiveBookFlags.SourceAllowed, MaxCost = 12, ReceiverBookToken = 10 };
        source.Slots.Add(Slot(202, 3, PassiveSlotFlags.CanGive | PassiveSlotFlags.Given)); source.Slots[0].OriginRarity = source.Slots[0].CurrentRarity = 2;
        snapshot.PassiveBooks.Add(source); snapshot.CoreBooks[1].Flags = CoreBookFlags.PassiveBound;
        source = new ProgressSnapshot.PassiveBookEntry { BookToken = 30, Flags = PassiveBookFlags.SourceAllowed, MaxCost = 12, ReceiverBookToken = 10 };
        source.Slots.Add(Slot(303, 1)); snapshot.PassiveBooks.Add(source); snapshot.CoreBooks[2].Flags = CoreBookFlags.PassiveBound;
        return snapshot;
    }
    private static void Metadata()
    {
        var sample = MetadataSample(); var packet = sample.Encode(Room);
        ProgressSnapshot parsed;
        Check(ProgressSnapshot.TryDecode(packet, Room, out parsed) && packet[4] == 6, "wire6 complete passive metadata round trip");
        Check(parsed.PassivesAvailable && parsed.PassivesReason == PassivesReason.None && parsed.PassiveBooks.Count == 3, "complete metadata availability");
        var slot = parsed.PassiveBooks[0].Slots[1];
        Check(slot.OriginId == PassiveMirror.EmptyId && slot.CurrentId == 202 && slot.CurrentCost == 3 && slot.CurrentRarity == 2 &&
            slot.SourceBookToken == 20 && slot.SourceSlotIndex == 0 && parsed.PassiveBooks[0].SourceTokens.SequenceEqual(new ulong[] { 20, 30 }), "committed source graph and empty native identity preserved");
        Check(parsed.PassiveBooks[0].Slots[0].CurrentNegative && parsed.PassiveBooks[0].Slots[0].Cost == -2 &&
            parsed.PassiveBooks[0].Slots[0].Flags == (PassiveSlotFlags.Negative | PassiveSlotFlags.Locked), "locked negative native slot retained");
        var content = PassiveMirror.EncodeContent(sample); sample.PassiveBooks.Reverse(); sample.PassiveBooks[2].SourceTokens.Reverse();
        sample.Sequence++; sample.ClaimRevision++; sample.DeckRevision++;
        Check(content.SequenceEqual(PassiveMirror.EncodeContent(sample)), "canonical content ignores revisions and inventory/source ordering");
        var nativeOrder = PassiveMirror.EncodeContent(sample); sample.PassiveBooks[2].Slots.Reverse();
        Check(!nativeOrder.SequenceEqual(PassiveMirror.EncodeContent(sample)), "native slot ordering is semantic");
        foreach (var mutation in new Action<ProgressSnapshot>[] {
            s => s.PassivesReason = PassivesReason.PacketLimit,
            s => s.PassiveBooks.RemoveAt(0),
            s => s.PassiveBooks[0].BookToken = 0,
            s => s.PassiveBooks[1].BookToken = 10,
            s => s.PassiveBooks[0].MaxCost = 13,
            s => s.PassiveBooks[0].MaxSources = 5,
            s => s.PassiveBooks[0].SourceTokens.Add(20),
            s => s.PassiveBooks[0].SourceTokens.Add(0),
            s => s.PassiveBooks[0].SourceTokens.Add(10),
            s => s.PassiveBooks[0].Slots[0].Cost = -1001,
            s => s.PassiveBooks[0].Slots[0].CurrentCost = 1001,
            s => s.PassiveBooks[0].Slots[0].OriginId = 0,
            s => s.PassiveBooks[0].Slots[0].InnerTypeId = -2,
            s => s.PassiveBooks[0].Slots[0].OriginRarity = 5,
            s => s.PassiveBooks[0].Slots[0].CurrentRarity = 5,
            s => s.PassiveBooks[0].Slots[1].SourceSlotIndex = 64,
            s => s.PassiveBooks[0].Slots[1].SourceBookToken = 30,
            s => s.PassiveBooks[0].Slots[1].CurrentCost = 4,
            s => s.PassiveBooks[1].ReceiverBookToken = 0,
            s => s.PassiveBooks[0].Slots.Add(s.PassiveBooks[0].Slots[1]),
            s => { s.PassiveBooks[0].Slots[1].CurrentId = PassiveMirror.EmptyId; s.PassiveBooks[0].Slots[1].CurrentCost = 0;
                s.PassiveBooks[0].Slots[1].CurrentRarity = 0; s.PassiveBooks[0].Slots[1].SourceBookToken = 0; s.PassiveBooks[0].Slots[1].SourceSlotIndex = 255; },
            s => { s.PassiveBooks[2].ReceiverBookToken = 0; s.PassiveBooks[0].SourceTokens.Remove(30); s.PassiveBooks[2].Slots[0].Flags |= PassiveSlotFlags.Given; },
            s => s.PassiveBooks[1].Flags |= PassiveBookFlags.Unsupported,
            s => s.PassiveBooks[0].Slots.AddRange(Enumerable.Range(0, 63).Select(i => Slot(500 + i))) })
        {
            sample = MetadataSample(); mutation(sample); Check(!PassiveMirror.ValidateData(sample, out _), "invalid metadata DTO");
            var failed = false; try { sample.Encode(Room); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "encoder rejects invalid metadata DTO");
        }
        sample = MetadataSample(); packet = sample.Encode(Room); var start = packet.Length - PassiveMirror.EncodeContent(sample).Length;
        foreach (var change in new[] { new int[] { 0, 2 }, new int[] { 1, 255 }, new int[] { 2, 1 }, new int[] { 4, 255 },
            new int[] { 14, 8 }, new int[] { 15, 13 }, new int[] { 16, 5 }, new int[] { 17, 5 },
            new int[] { 46, 5 }, new int[] { 65, 2 }, new int[] { 66, 5 }, new int[] { 75, 64 } })
        {
            var bad = (byte[])packet.Clone(); bad[start + change[0]] = (byte)change[1];
            Check(!ProgressSnapshot.TryDecode(bad, Room, out _), "malformed passive field " + change[0]);
        }
        for (var length = start; length < packet.Length; length++) Check(!ProgressSnapshot.TryDecode(packet.Take(length).ToArray(), Room, out _), "truncated passive graph " + length);
        foreach (PassivesReason reason in Enum.GetValues(typeof(PassivesReason)))
        {
            if (reason == PassivesReason.None) continue;
            sample = Snapshot(); sample.PassivesReason = reason; var bytes = sample.Encode(Room);
            Check(PassiveMirror.EncodeContent(sample).Length == 6 && ProgressSnapshot.TryDecode(bytes, Room, out parsed) &&
                !parsed.PassivesAvailable && parsed.PassivesReason == reason && parsed.UnitDecks[0].Cards.Count == 1 && parsed.CoreBooksAvailable,
                "explicit unavailable fallback preserves cards/core " + reason);
        }
    }
    private static PassiveModel Model(int id, int instance, int cost = 0)
    {
        var xml = new PassiveXmlInfo { id = new LorId { id = id }, cost = cost };
        return new PassiveModel { originpassive = xml, originData = new PassiveModel.PassiveModelSavedData
            { currentpassive = xml, givePassiveBookId = instance, receivepassivebookId = instance } };
    }
    private static BookModel Book(int instance)
    { return new BookModel { BookId = new LorId { id = 200 }, instanceId = instance }; }
    private static void Capture()
    {
        var library = LibraryModel.Instance;
        try
        {
            LibraryModel.Instance = new LibraryModel { Chapter = 7 };
            var target = Book(1000); var source = Book(2000); var unused = Book(3000);
            target.PassiveModels.Add(Model(101, 1000)); target.PassiveModels.Add(Model(PassiveMirror.EmptyId, 1000));
            source.PassiveModels.Add(Model(202, 2000, 3)); unused.PassiveModels.Add(Model(303, 3000, -1));
            var sample = Snapshot(); sample.CoreBooks[0].BookReference = target; sample.CoreBooks[1].BookReference = source; sample.CoreBooks[2].BookReference = unused;
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks[0].MaxCost == 12 && sample.PassiveBooks[0].MaxSources == 4 &&
                (sample.PassiveBooks[0].Flags & PassiveBookFlags.ReceiverAllowed) != 0 && (sample.PassiveBooks[1].Flags & PassiveBookFlags.SourceAllowed) != 0,
                "ordinary lazy origin capture editable without popup");
            Check(source.PassiveModels[0].reservedData == null && source.reservedData.equipedBookIdListInPassive.Count == 0,
                "capture never initializes native passive or book reserves");
            var copy = sample.Encode(Room); Check(ProgressSnapshot.TryDecode(copy, Room, out _), "captured native metadata valid");
            foreach (var pair in new[] { new[] { 3, 0 }, new[] { 4, 6 }, new[] { 5, 8 }, new[] { 6, 10 }, new[] { 7, 12 } })
            { LibraryModel.Instance.Chapter = pair[0]; PassiveMirror.Capture(sample); Check(sample.PassiveBooks[0].MaxCost == pair[1], "host progress budget " + pair[0]); }
            target.originData.equipedBookIdListInPassive.AddRange(new[] { 2000, 3000 });
            source.originData.equipedPassiveBookInstanceId = unused.originData.equipedPassiveBookInstanceId = 1000;
            source.PassiveModels[0].originData.givePassiveBookId = 1000;
            target.PassiveModels[1].originData.currentpassive = source.PassiveModels[0].originpassive;
            target.PassiveModels[1].originData.receivepassivebookId = 2000;
            sample.CoreBooks[1].Flags = sample.CoreBooks[2].Flags = CoreBookFlags.PassiveBound;
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks[0].SourceTokens.SequenceEqual(new ulong[] { 20, 30 }) &&
                sample.PassiveBooks[0].Slots[1].SourceSlotIndex == 0 && sample.PassiveBooks[0].Slots[1].CurrentId == 202,
                "loaded committed graph preserves inherited and zero borrowed source");
            Check(sample.PassiveBooks[1].ReceiverBookToken == 10 && (sample.PassiveBooks[1].Slots[0].Flags & PassiveSlotFlags.Given) != 0 &&
                (sample.PassiveBooks[1].Flags & PassiveBookFlags.SourceAllowed) != 0, "own existing donor retained as available source");
            Check(target.reservedData.equipedBookIdListInPassive.Count == 0 && target.PassiveModels.All(m => m.reservedData == null), "loaded graph capture uses origin exclusively");
            var originContent = PassiveMirror.EncodeContent(sample);
            target.PassiveModels[1].InitReservedData(); target.PassiveModels[1].reservedData.currentpassive.id.id = 404;
            target.reservedData.equipedBookIdListInPassive.Add(777);
            PassiveMirror.Capture(sample);
            Check(originContent.SequenceEqual(PassiveMirror.EncodeContent(sample)), "reserved draft cannot change committed capture");
            // A same-ID source duplicate has no unique native slot identity and is explicit unsupported.
            source.PassiveModels.Add(Model(202, 2000, 3)); source.PassiveModels[1].originData.givePassiveBookId = 1000;
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks.All(b => b.Flags == PassiveBookFlags.Unsupported), "ambiguous source index invalidates complete relation endpoints");
            source.PassiveModels.RemoveAt(1);
            source.PassiveModels[0].originpassive.id.packageId = "Workshop";
            PassiveMirror.Capture(sample); Check(sample.PassivesAvailable && sample.PassiveBooks.All(b => b.Flags == PassiveBookFlags.Unsupported), "unsupported mod passive relation never invents XML");
            source.PassiveModels[0].originpassive.id.packageId = null;
            var unrelated = Book(4000); unrelated.PassiveModels.Add(Model(404, 4000));
            var independent = Core(40, 4000); independent.BookReference = unrelated; sample.CoreBooks.Add(independent);
            source.PassiveModels[0].originData.givePassiveBookId = 9999;
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks.Take(3).All(b => b.Flags == PassiveBookFlags.Unsupported) &&
                (sample.PassiveBooks[3].Flags & PassiveBookFlags.SourceAllowed) != 0, "wrong actual recipient disables relation closure and preserves unrelated source");
            source.PassiveModels[0].originData.givePassiveBookId = 1000;
            target.PassiveModels[1] = Model(PassiveMirror.EmptyId, 1000);
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks.Take(3).All(b => b.Flags == PassiveBookFlags.Unsupported) &&
                sample.PassiveBooks[3].Slots.Count == 1, "orphan given source cannot invent a recipient slot");
            target.PassiveModels[1].originData.currentpassive = source.PassiveModels[0].originpassive;
            target.PassiveModels[1].originData.receivepassivebookId = 2000;
            var duplicateRecipient = Model(PassiveMirror.EmptyId, 1000);
            duplicateRecipient.originData.currentpassive = source.PassiveModels[0].originpassive; duplicateRecipient.originData.receivepassivebookId = 2000;
            target.PassiveModels.Add(duplicateRecipient);
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks.Take(3).All(b => b.Flags == PassiveBookFlags.Unsupported) &&
                sample.PassiveBooks[3].Slots.Count == 1, "two recipient slots cannot borrow one native source slot");
            target.PassiveModels.RemoveAt(2);
            target.originData.equipedBookIdListInPassive.Clear(); source.originData.equipedPassiveBookInstanceId = unused.originData.equipedPassiveBookInstanceId = -1;
            target.PassiveModels[1] = Model(PassiveMirror.EmptyId, 1000); source.PassiveModels[0].originData.givePassiveBookId = 2000;
            unused.PassiveModels.Clear(); for (var i = 0; i < 65; i++) unused.PassiveModels.Add(Model(500 + i, 3000));
            PassiveMirror.Capture(sample); Check(sample.PassivesAvailable && sample.PassiveBooks[2].Flags == PassiveBookFlags.Unsupported && sample.PassiveBooks[2].Slots.Count == 0 &&
                (sample.PassiveBooks[0].Flags & PassiveBookFlags.ReceiverAllowed) != 0, "oversized book slots disable only complete unsupported row");
            unused.PassiveModels.RemoveAt(64); PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks[2].Slots.Count == 64 && ProgressSnapshot.TryDecode(sample.Encode(Room), Room, out _), "64 slot boundary capture and decode");
            unrelated.PassiveModels[0].originpassive.cost = 1001;
            PassiveMirror.Capture(sample);
            Check(sample.PassivesAvailable && sample.PassiveBooks[3].Flags == PassiveBookFlags.Unsupported && sample.PassiveBooks[0].Slots.Count == 2,
                "unsupported metadata bounds disable only the complete book row");
            sample = Snapshot(); sample.CoreBooks.Clear();
            for (var b = 0; b < 100; b++)
            {
                var physical = Book(1000 + b); var core = Core((ulong)(b == 0 ? 10 : 100 + b), 1000 + b); core.BookReference = physical;
                if (b == 0) { core.Flags = CoreBookFlags.Equipped; core.OccupiedFloorId = 1; core.OccupiedUnitIndex = 0; }
                for (var s = 0; s < 64; s++) physical.PassiveModels.Add(Model(500 + s, physical.instanceId));
                sample.CoreBooks.Add(core);
            }
            PassiveMirror.Capture(sample);
            Check(!sample.PassivesAvailable && sample.PassivesReason == PassivesReason.PacketLimit && sample.PassiveBooks.Count == 0 && sample.CoreBooksAvailable && sample.CoreBooks.Count == 100,
                "passive packet overflow clears entire passive graph while retaining core inventory");
            Check(ProgressSnapshot.TryDecode(sample.Encode(Room), Room, out var fallback) && fallback.UnitDecks[0].Cards.Count == 1 && fallback.CardStock[0].Count == 8,
                "packet overflow retains legacy card authority fields");
            sample = Snapshot(); sample.CoreBooks.Clear(); sample.CoreBooksAvailable = false; sample.CoreBooksReason = CoreBooksReason.PacketLimit;
            PassiveMirror.Capture(sample); Check(!sample.PassivesAvailable && sample.PassivesReason == PassivesReason.CoreInventoryUnavailable, "missing core inventory explicit passive status");
        }
        finally { LibraryModel.Instance = library; }
    }
}
