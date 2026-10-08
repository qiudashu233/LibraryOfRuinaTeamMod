using System;
using System.Linq;
using RuinaCoop;
internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); _checks++; }
    private static ProgressSnapshot Snapshot()
    {
        var snapshot = new ProgressSnapshot { Sequence = 7, SelectedStageId = 1, SelectedFloorId = 1, ClaimRevision = 4,
            DeckRevision = 8, CoreBooksAvailable = true, CoreBooksReason = CoreBooksReason.None,
            PassivesAvailable = true, PassivesReason = PassivesReason.None };
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        floor.Units.Add("Host"); snapshot.Floors.Add(floor); snapshot.ClaimOwners.Add(20);
        snapshot.UnitDecks.Add(new ProgressSnapshot.UnitDeckEntry { UnitIdentity = 100, BookToken = 10, BookId = 1, BookInstanceId = 11, Capacity = 9 });
        snapshot.CoreBooks.Add(new ProgressSnapshot.CoreBookEntry { BookToken = 10, BookId = 1, BookInstanceId = 11,
            Kind = CoreBookKind.Ordinary, Flags = CoreBookFlags.Equipped, OccupiedFloorId = 1, OccupiedUnitIndex = 0 });
        var receiver = new ProgressSnapshot.PassiveBookEntry { BookToken = 10, Flags = PassiveBookFlags.ReceiverAllowed, MaxCost = 6, MaxSources = 4 };
        receiver.Slots.Add(new ProgressSnapshot.PassiveSlotEntry { OriginId = 1, CurrentId = 1, InnerTypeId = 3, SourceSlotIndex = 255 });
        for (var i = 0; i < 3; i++) receiver.Slots.Add(new ProgressSnapshot.PassiveSlotEntry { OriginId = 9999999, CurrentId = 9999999,
            Flags = PassiveSlotFlags.CanReceive, SourceSlotIndex = 255 });
        snapshot.PassiveBooks.Add(receiver);
        for (ulong token = 20; token < 25; token++)
        {
            snapshot.CoreBooks.Add(new ProgressSnapshot.CoreBookEntry { BookToken = token, BookId = 2, BookInstanceId = (int)token,
                Kind = CoreBookKind.Ordinary, OccupiedFloorId = 255, OccupiedUnitIndex = 255 });
            var source = new ProgressSnapshot.PassiveBookEntry { BookToken = token, Flags = PassiveBookFlags.SourceAllowed, MaxSources = 4 };
            source.Slots.Add(new ProgressSnapshot.PassiveSlotEntry { OriginId = (int)token, CurrentId = (int)token, Cost = 3, CurrentCost = 3,
                Flags = PassiveSlotFlags.CanGive, SourceSlotIndex = 255 });
            snapshot.PassiveBooks.Add(source);
        }
        return snapshot;
    }
    private static NativePassiveDraft Draft(ProgressSnapshot snapshot = null, ulong local = 20)
    {
        NativePassiveDraft draft; Check(NativePassiveDraft.TryCreate(77, snapshot ?? Snapshot(), 0, local, out draft), "create detached draft"); return draft;
    }
    private static PassiveRequest Request(NativePassiveDraft draft)
    { return new PassiveRequest { RequestId = 1, StageId = draft.Snapshot.SelectedStageId, FloorId = draft.Snapshot.SelectedFloorId,
        UnitIndex = draft.UnitIndex, UnitIdentity = draft.Snapshot.UnitDecks[0].UnitIdentity, BookToken = 10,
        ClaimRevision = draft.Snapshot.ClaimRevision, DeckRevision = draft.Snapshot.DeckRevision, Slots = draft.Selections(), SourceBookTokens = draft.Sources() }; }
    private static void Lifecycle()
    {
        var snapshot = Snapshot(); var draft = Draft(snapshot); var old = draft.CaptureEvent();
        Check(draft.CanEdit, "owner editable"); Check(draft.TryAttach(old, 20), "attach source privately");
        Check(snapshot.PassiveBooks[0].SourceTokens.Count == 0, "attach does not mutate displayed snapshot");
        Check(!draft.TryAttach(old, 21), "old callback revoked after action");
        Check(draft.TryInherit(draft.CaptureEvent(), 20, 0), "fill original empty slot");
        Check(draft.Choice(0).SourceBookToken == 0 && draft.Choice(1).SourceBookToken == 20, "retained native slot never replaced");
        Check(snapshot.PassiveBooks[0].Slots[1].CurrentId == 9999999, "inherited draft does not change authority state");
        Check(!draft.TryInherit(draft.CaptureEvent(), 20, 0), "duplicate cannot be inherited twice");
        Check(draft.TryRestore(draft.CaptureEvent(), 1), "restore inherited only");
        Check(draft.Sources().SequenceEqual(new ulong[] { 20 }), "zero borrowed source remains selected");
        Check(!draft.TryRestore(draft.CaptureEvent(), 0), "cannot delete native passive");
        var copies = draft.Selections(); copies[0].ExpectedOriginPassiveId = 99;
        var sourceCopy = draft.Sources(); sourceCopy[0] = 99;
        Check(draft.Selections()[0].ExpectedOriginPassiveId == 1 && draft.Sources()[0] == 20, "complete request arrays do not alias draft");
        old = draft.CaptureEvent(); draft.Close();
        Check(!draft.IsCurrent(old) && !draft.TryAttach(old, 21), "late callbacks after close stay closed");
        var reopened = Draft(snapshot); Check(!reopened.IsCurrent(old), "close reopen same snapshot cannot revive callback");
        var view = Draft(Snapshot(), 99); Check(!view.CanEdit && !view.TryAttach(view.CaptureEvent(), 20), "read only observer cannot borrow through source path");
        var frozen = Snapshot(); frozen.DecksFrozen = true; view = Draft(frozen); Check(!view.CanEdit, "frozen view read only");
        draft = Draft(); snapshot = Snapshot(); snapshot.Sequence = 8;
        old = draft.CaptureEvent(); Check(draft.TryUpdate(77, snapshot) && !draft.Conflict, "new same content snapshot accepted");
        Check(!draft.IsCurrent(old), "sequence refresh revokes held row callback");
        Check(draft.TryAttach(draft.CaptureEvent(), 20), "draft changes after valid refresh");
        snapshot = Snapshot(); snapshot.Sequence = 9; snapshot.DeckRevision++;
        Check(draft.TryUpdate(77, snapshot) && draft.Conflict && !draft.CanEdit && draft.IsSelected(20), "resource change keeps view draft but blocks apply");
        Check(!draft.MarkSubmitted(draft.CaptureEvent(), snapshot), "conflicted draft never submits to new version");
        foreach (var cause in new[] { "room", "role", "book", "owner", "roster", "floor", "stage", "unavailable", "frozen" })
        {
            draft = Draft(); snapshot = Snapshot(); snapshot.Sequence = 8; ulong room = 77;
            switch (cause) { case "room": room = 88; break; case "role": snapshot.UnitDecks[0].UnitIdentity++; break;
                case "book": snapshot.UnitDecks[0].BookToken++; break; case "owner": snapshot.ClaimOwners[0] = 99; break;
                case "roster": snapshot.UnitDecks.Add(new ProgressSnapshot.UnitDeckEntry()); break; case "floor": snapshot.SelectedFloorId = 2; break;
                case "stage": snapshot.SelectedStageId = 2; break; case "unavailable": snapshot.PassivesAvailable = false; break; case "frozen": snapshot.DecksFrozen = true; break; }
            Check(!draft.TryUpdate(room, snapshot) && !draft.Valid, "close on changed " + cause);
        }
        draft = Draft(); old = draft.CaptureEvent(); Check(draft.MarkSubmitted(old, draft.Snapshot), "explicit apply freezes own draft");
        Check(draft.Submitted && !draft.CanEdit && !draft.TryAttach(old, 20), "pending blocks draft editing and duplicate apply");
        draft.SubmissionRejected(); Check(!draft.Submitted && draft.CanEdit && !draft.IsCurrent(old), "rejected apply allows fresh event only");
        draft = Draft(); snapshot = Snapshot(); Check(!draft.MarkSubmitted(draft.CaptureEvent(), snapshot), "different reference same values cannot apply");
    }
    private static void Authority()
    {
        var draft = Draft(); var snapshot = draft.Snapshot;
        Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.Accepted, "native retained final plan accepted");
        Check(draft.TryAttach(draft.CaptureEvent(), 20) && draft.TryInherit(draft.CaptureEvent(), 20, 0), "prepare plan");
        Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.Accepted, "valid complete inherit plan");
        Check(PassiveAuthority.Validate(snapshot, 99, Request(draft)) == PassiveResultCode.NotOwner, "authority rejects nonowner");
        var request = Request(draft); request.ClaimRevision--; Check(PassiveAuthority.Validate(snapshot, 20, request) == PassiveResultCode.StaleClaim, "claim concurrency rejected");
        request = Request(draft); request.DeckRevision--; Check(PassiveAuthority.Validate(snapshot, 20, request) == PassiveResultCode.StaleDeck, "resource concurrency rejected");
        snapshot.DecksFrozen = true; Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.Frozen, "authority frozen"); snapshot.DecksFrozen = false;
        request = Request(draft); request.Slots[0] = request.Slots[1]; Check(PassiveAuthority.Validate(snapshot, 20, request) == PassiveResultCode.LockedSlot, "native populated slot overwrite rejected");
        request = Request(draft); request.Slots[1].ExpectedOriginPassiveId++; Check(PassiveAuthority.Validate(snapshot, 20, request) == PassiveResultCode.StaleDeck, "source native ID identity bound");
        snapshot.PassiveBooks[1].ReceiverBookToken = 30; Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.SourceOccupied, "source consumed by another receiver rejected"); snapshot.PassiveBooks[1].ReceiverBookToken = 0;
        snapshot.PassiveBooks[0].MaxCost = 2; Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.CostExceeded, "host cost budget"); snapshot.PassiveBooks[0].MaxCost = 6;
        snapshot.PassiveBooks[1].Slots[0].Flags |= PassiveSlotFlags.Hidden; Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.UnsupportedSource, "hidden source cannot borrow"); snapshot.PassiveBooks[1].Slots[0].Flags = PassiveSlotFlags.CanGive;
        draft.TryRestore(draft.CaptureEvent(), 1); Check(PassiveAuthority.Validate(snapshot, 20, Request(draft)) == PassiveResultCode.Accepted, "zero inherited but selected source retained accepted");
        draft = Draft(); for (ulong token = 20; token < 24; token++) Check(draft.TryAttach(draft.CaptureEvent(), token), "source limit accepts " + token);
        Check(!draft.TryAttach(draft.CaptureEvent(), 24), "source limit blocks fifth");
        Check(draft.TryInherit(draft.CaptureEvent(), 20, 0) && draft.TryInherit(draft.CaptureEvent(), 21, 0), "cost fills two slots");
        Check(!draft.TryInherit(draft.CaptureEvent(), 22, 0), "draft prechecks total cost");
        Check(draft.TryDetach(draft.CaptureEvent(), 20) && draft.Choice(1).SourceBookToken == 0, "detach removes every borrowed passive from same source");
        Check(draft.TryRestoreAll(draft.CaptureEvent()) && draft.Sources().Length == 0 && draft.Selections().All(slot => slot.Mode == PassiveSelectionMode.RestoreNative), "reset all preserves native plan and frees sources");
    }
    public static void Main()
    {
        Lifecycle(); Authority(); _checks += PassiveCodecChecks.Run();
        Console.WriteLine("PASS: " + _checks + " passive draft/authority/lifecycle checks.");
    }
}
