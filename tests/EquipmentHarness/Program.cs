using System;
using System.Linq;
using RuinaCoop;
using UI;

internal static class Program
{
    private const ulong Room = 987;
    private const ulong Owner = 123;
    private const ulong Other = 456;
    private static int _checks;
    private static void Check(bool condition, string name)
    { _checks++; if (!condition) throw new Exception("FAIL: " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("FAIL: " + name);
    }

    private static ProgressSnapshot Snapshot()
    {
        var snapshot = new ProgressSnapshot
        {
            Sequence = 1, SelectedStageId = 101, SelectedFloorId = 1, ClaimRevision = 7, DeckRevision = 11,
            CoreBooksAvailable = true, CoreBooksReason = CoreBooksReason.None
        };
        snapshot.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Stage" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        for (byte i = 0; i < 2; i++)
        {
            floor.Units.Add("Duplicate display name"); snapshot.ClaimOwners.Add(Owner);
            var deck = new ProgressSnapshot.UnitDeckEntry
            { UnitIdentity = (ulong)(100 + i), BookId = 200 + i, BookInstanceId = 300 + i, BookToken = (ulong)(400 + i), Capacity = 9 };
            deck.Cards.Add(600); snapshot.UnitDecks.Add(deck);
            snapshot.CoreBooks.Add(Book(deck.BookToken, deck.BookId, deck.BookInstanceId, i));
        }
        snapshot.Floors.Add(floor);
        // Duplicate XML pages still have distinct host instance tokens.
        snapshot.CoreBooks.Add(Book(500, 202, 302));
        snapshot.CoreBooks.Add(Book(501, 202, 303));
        return snapshot;
    }

    private static ProgressSnapshot.CoreBookEntry Book(ulong token, int id, int instance, byte unit = byte.MaxValue)
    {
        var book = new ProgressSnapshot.CoreBookEntry { BookToken = token, BookId = id, BookInstanceId = instance };
        book.Display.Available = true; book.Display.MaxHp = 75; book.Display.Break = 45;
        book.Display.PassiveIds.Add(1001); book.CurrentCards.Add(600);
        if (unit != byte.MaxValue)
        { book.Flags = CoreBookFlags.Equipped; book.OccupiedFloorId = 1; book.OccupiedUnitIndex = unit; }
        return book;
    }

    private static CorePageRequest Request(ProgressSnapshot snapshot, byte unit = 0, ulong target = 500)
    {
        var deck = snapshot.UnitDecks[unit];
        return new CorePageRequest
        {
            RequestId = 1, StageId = snapshot.SelectedStageId, FloorId = snapshot.SelectedFloorId,
            UnitIndex = unit, UnitIdentity = deck.UnitIdentity, OldBookToken = deck.BookToken,
            TargetBookToken = target, ClaimRevision = snapshot.ClaimRevision, DeckRevision = snapshot.DeckRevision
        };
    }
    private static void Expect(ProgressSnapshot snapshot, CorePageRequest request, CorePageResultCode code, string name, ulong sender = Owner)
    { Check(EquipmentAuthority.Validate(snapshot, sender, request) == code, name); }

    private static void Protocol()
    {
        var snapshot = Snapshot(); var request = Request(snapshot);
        var packet = EquipmentProtocol.EncodeRequest(Room, request);
        Check(packet.Length == 55 && EquipmentProtocol.TryDecodeRequest(packet, Room, out var decoded), "exact request size round trips");
        EquipmentProtocol.TryDecodeRequest(packet, Room, out decoded);
        Check(decoded.OldBookToken == 400 && decoded.TargetBookToken == 500 && decoded.UnitIdentity == 100 &&
            decoded.DeckRevision == 11 && decoded.ClaimRevision == 7 && decoded.UnitIndex == 0, "wire binds old instance, target instance, role identity and displayed versions");
        Check(!EquipmentProtocol.TryDecodeRequest(packet, Room + 1, out _) &&
            !EquipmentProtocol.TryDecodeRequest(packet, 0, out _) && !EquipmentProtocol.TryDecodeRequest(null, Room, out _), "foreign room, zero room and null packet rejected");
        for (var length = 0; length < packet.Length; length++)
            Check(!EquipmentProtocol.TryDecodeRequest(packet.Take(length).ToArray(), Room, out _), "truncated request length " + length);
        Check(!EquipmentProtocol.TryDecodeRequest(packet.Concat(new byte[1]).ToArray(), Room, out _), "request trailing byte rejected");
        foreach (var offset in new[] { 0, 4, 5, 13, 17, 21, 22, 23, 31, 47 })
        {
            var bad = (byte[])packet.Clone();
            if (offset == 0 || offset == 4 || offset == 5) bad[offset] ^= 0xff;
            else if (offset == 13 || offset == 17) Array.Clear(bad, offset, 4);
            else if (offset == 21) bad[offset] = 11;
            else if (offset == 22) bad[offset] = 5;
            else Array.Clear(bad, offset, 8);
            Check(!EquipmentProtocol.TryDecodeRequest(bad, Room, out _), "malformed header/identity at byte " + offset);
        }
        var sameTarget = (byte[])packet.Clone(); Array.Copy(packet, 31, sameTarget, 47, 8);
        Check(!EquipmentProtocol.TryDecodeRequest(sameTarget, Room, out _), "same old and target instance rejected");
        request.RequestId = uint.MaxValue; request.ClaimRevision = uint.MaxValue; request.DeckRevision = uint.MaxValue;
        request.UnitIdentity = ulong.MaxValue; request.OldBookToken = ulong.MaxValue; request.TargetBookToken = ulong.MaxValue - 1;
        Check(EquipmentProtocol.TryDecodeRequest(EquipmentProtocol.EncodeRequest(Room, request), Room, out decoded) &&
            decoded.RequestId == uint.MaxValue && decoded.UnitIdentity == ulong.MaxValue, "unsigned identity/version boundaries preserve bits");
        request = Request(snapshot); request.RequestId = 0;
        Reject(() => EquipmentProtocol.EncodeRequest(Room, request), "encoder refuses zero request id");
        Reject(() => EquipmentProtocol.EncodeRequest(0, Request(snapshot)), "encoder refuses zero room");
        foreach (CorePageResultCode result in Enum.GetValues(typeof(CorePageResultCode)))
        {
            var reply = new CorePageReply { RequestId = 2, Result = result, DeckRevision = uint.MaxValue };
            var ack = EquipmentProtocol.EncodeReply(Room, reply);
            Check(ack.Length == 22 && EquipmentProtocol.TryDecodeReply(ack, Room, out var parsed) &&
                parsed.Result == result && parsed.DeckRevision == uint.MaxValue, "reply result and revision round trip: " + result);
        }
        var validReply = EquipmentProtocol.EncodeReply(Room, new CorePageReply { RequestId = 1 });
        for (var length = 0; length < validReply.Length; length++)
            Check(!EquipmentProtocol.TryDecodeReply(validReply.Take(length).ToArray(), Room, out _), "truncated reply length " + length);
        Check(!EquipmentProtocol.TryDecodeReply(validReply.Concat(new byte[1]).ToArray(), Room, out _) &&
            !EquipmentProtocol.TryDecodeReply(validReply, Room + 1, out _), "reply extension and foreign room rejected");
        validReply[17] = 14;
        Check(!EquipmentProtocol.TryDecodeReply(validReply, Room, out _), "unknown reply result rejected");
    }

    private static void Authority()
    {
        var snapshot = Snapshot(); var request = Request(snapshot);
        Expect(snapshot, request, CorePageResultCode.Accepted, "owned ordinary current and available target accepted");
        Expect(snapshot, Request(snapshot, 0, 501), CorePageResultCode.Accepted, "second copy of same XML is distinct selectable instance");
        Expect(snapshot, request, CorePageResultCode.NotOwner, "foreign owner denied", Other);
        snapshot.ClaimOwners[0] = 0;
        Expect(snapshot, request, CorePageResultCode.NotOwner, "unclaimed role cannot use equipment path");
        snapshot.ClaimOwners[0] = Owner; snapshot.DecksFrozen = true;
        Expect(snapshot, request, CorePageResultCode.Frozen, "battle preparation freeze forbids replacing core");
        snapshot.DecksFrozen = false; snapshot.ClaimRevision++;
        Expect(snapshot, request, CorePageResultCode.StaleClaim, "stale claim version rejected");
        snapshot = Snapshot(); snapshot.DeckRevision++;
        Expect(snapshot, request, CorePageResultCode.StaleDeck, "concurrent shared inventory change rejects stale version");
        snapshot = Snapshot(); snapshot.UnitDecks[0].UnitIdentity++;
        Expect(snapshot, request, CorePageResultCode.StaleDeck, "same name/book but replaced unit object rejected");
        snapshot = Snapshot(); snapshot.UnitDecks[0].BookToken++;
        Expect(snapshot, request, CorePageResultCode.StaleDeck, "same book XML but changed current instance rejected");
        snapshot = Snapshot(); request = Request(snapshot); request.TargetBookToken = 999;
        Expect(snapshot, request, CorePageResultCode.UnknownTarget, "unadvertised target instance rejected");
        snapshot = Snapshot(); request = Request(snapshot, 0, 401);
        Expect(snapshot, request, CorePageResultCode.TargetOccupied, "another role's equipped copy cannot be stolen");
        foreach (CoreBookFlags flag in Enum.GetValues(typeof(CoreBookFlags)))
        {
            if (flag == CoreBookFlags.None) continue;
            snapshot = Snapshot(); snapshot.CoreBooks[2].Flags = flag;
            var expected = flag == CoreBookFlags.Equipped || flag == CoreBookFlags.OwnerMismatch ? CorePageResultCode.TargetOccupied : CorePageResultCode.UnsupportedTarget;
            Expect(snapshot, Request(snapshot), expected, "target flag blocks replacement: " + flag);
        }
        foreach (var kind in new[] { CoreBookKind.Default, CoreBookKind.Special })
        {
            snapshot = Snapshot(); snapshot.CoreBooks[2].Kind = kind;
            Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedTarget, "default/special never target: " + kind);
        }
        snapshot = Snapshot(); snapshot.CoreBooks[0].Kind = CoreBookKind.Default;
        snapshot.CoreBooks[0].Flags |= CoreBookFlags.CannotEquip;
        Expect(snapshot, Request(snapshot), CorePageResultCode.Accepted, "ordinary default current can change out");
        snapshot.CoreBooks[0].Flags |= CoreBookFlags.FixedDeck;
        Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedCurrentBook, "default current with locked deck remains blocked");
        snapshot = Snapshot(); snapshot.CoreBooks[0].Kind = CoreBookKind.Special;
        Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedCurrentBook, "special current cannot change out");
        foreach (var flag in new[] { CoreBookFlags.PassiveBound, CoreBookFlags.DraftMismatch, CoreBookFlags.OwnerMismatch })
        {
            snapshot = Snapshot(); snapshot.CoreBooks[0].Flags |= flag;
            Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedCurrentBook, "current donor, passive draft, bad owner refused: " + flag);
        }
        snapshot = Snapshot(); snapshot.CoreBooks[2].Display.PassiveIds.Clear(); snapshot.CoreBooks[2].Display.PassiveIds.Add(2001);
        Expect(snapshot, Request(snapshot), CorePageResultCode.Accepted, "committed effective passive receiver remains eligible without donor/draft flags");
        snapshot.CoreBooks[2].Display.Available = false;
        Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedTarget, "target lacks authoritative effective stats refused");
        foreach (var reason in new[] { CoreBooksReason.NotCaptured, CoreBooksReason.TooManyBooks, CoreBooksReason.PacketLimit, CoreBooksReason.CaptureFailed })
        {
            snapshot = Snapshot(); snapshot.CoreBooksAvailable = false; snapshot.CoreBooksReason = reason; snapshot.CoreBooks.Clear();
            Expect(snapshot, Request(snapshot), CorePageResultCode.UnsupportedInventory, "unavailable inventory reason: " + reason);
        }
        snapshot = Snapshot(); request = Request(snapshot); request.StageId++;
        Expect(snapshot, request, CorePageResultCode.NotReady, "request from previous stage rejected");
        request = Request(snapshot); request.FloorId = 2;
        Expect(snapshot, request, CorePageResultCode.NotReady, "request from previous floor rejected");
        request = Request(snapshot); request.RequestId = 0;
        Expect(snapshot, request, CorePageResultCode.InvalidRequest, "zero id is not a replayable operation");
    }

    private static void Lifecycle()
    {
        var snapshot = Snapshot(); var generation = 10UL;
        var old = new CorePageClickToken(snapshot, 0, 500, generation);
        Check(old.CanSubmit(snapshot, 0, Owner, true, true, false, generation), "visible owned target accepts exact displayed context");
        Check(!old.CanSubmit(snapshot, 1, Owner, true, true, false, generation), "slot0 button cannot edit slot1 even within same snapshot");
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, false, generation + 2), "role0->role1->role0 does not revive previous button");
        Check(!old.CanSubmit(snapshot, 0, Owner, false, true, false, generation), "closed page rejects late callback");
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, false, generation + 1), "reopened page with same DTO rejects old callback");
        Check(!old.CanSubmit(Snapshot(), 0, Owner, true, true, false, generation), "new displayed DTO with equal versions requires a newly rendered button");
        Check(!old.CanSubmit(snapshot, 0, Owner, true, false, false, generation), "dirty display window rejects input");
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, true, generation), "pending keeps all core buttons locked until ACK and snapshot");
        Check(!old.CanSubmit(snapshot, 0, Other, true, true, false, generation), "readonly role cannot bypass permission through equipment UI");
        snapshot.DecksFrozen = true;
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, false, generation), "frozen role cannot bypass equipment UI");
        snapshot.DecksFrozen = false; snapshot.DeckRevision++;
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, false, generation), "mutated displayed version rejects stored button");
        snapshot = Snapshot(); old = new CorePageClickToken(snapshot, 0, 500, generation);
        snapshot.CoreBooks[2].Flags = CoreBookFlags.Equipped;
        Check(!old.CanSubmit(snapshot, 0, Owner, true, true, false, generation), "new occupancy blocks target even if the UI enable state is stale");
        Check(!default(CorePageClickToken).CanSubmit(snapshot, 0, Owner, true, true, false, generation), "empty click token cannot submit");

        snapshot = Snapshot(); var button = new object(); var presses = new CorePagePressState();
        presses.Capture(button, new CorePageClickToken(snapshot, 0, 500, generation));
        Check(!presses.TryConsume(button, snapshot, 1, Owner, true, true, false, generation + 1, 501, out _),
            "held mouse press cannot adopt reused button's new role/target after refresh");
        presses.Capture(button, new CorePageClickToken(snapshot, 0, 500, generation));
        Check(!presses.TryConsume(button, snapshot, 0, Owner, true, true, false, generation + 2, 500, out _),
            "held press stays invalid after same-slot refresh/role roundtrip");
        presses.Capture(button, new CorePageClickToken(snapshot, 0, 500, generation)); presses.Clear();
        Check(!presses.TryConsume(button, snapshot, 0, Owner, true, true, false, generation + 1, 500, out _),
            "closed page clears pointer press even when reused slot returns");
        presses.Capture(button, new CorePageClickToken(snapshot, 0, 501, generation + 1));
        Check(presses.TryConsume(button, snapshot, 0, Owner, true, true, false, generation + 1, 501, out var click) && click.TargetBookToken == 501,
            "fresh mouse/controller press after refresh submits its exact row");
        Check(!presses.TryConsume(button, snapshot, 0, Owner, true, true, false, generation + 1, 501, out _),
            "one press cannot generate duplicate sends without another input event");

        // Claim/data revision changes still come through the separate binding lifecycle.
        snapshot = Snapshot(); Check(NativeDeckBinding.TryCreate(Room, snapshot, 0, Owner, out var binding), "native binding opens for equipment lifecycle");
        var before = binding.CaptureEvent(); var changed = Snapshot(); changed.Sequence++; changed.DeckRevision++;
        changed.UnitDecks[0].BookId = 202; changed.UnitDecks[0].BookInstanceId = 302; changed.UnitDecks[0].BookToken = 500;
        changed.CoreBooks[0].Flags = CoreBookFlags.None; changed.CoreBooks[0].OccupiedFloorId = 255; changed.CoreBooks[0].OccupiedUnitIndex = 255;
        changed.CoreBooks[2].Flags = CoreBookFlags.Equipped; changed.CoreBooks[2].OccupiedFloorId = 1; changed.CoreBooks[2].OccupiedUnitIndex = 0;
        Check(binding.TryRebindCorePage(Room, changed, 100), "ACK-authorized same-role ordinary core replacement can rebind");
        Check(binding.Snapshot == changed && binding.UnitIndex == 0 && !binding.CanSubmit(before, 600, DeckAction.Remove), "core rebind retires old combat-page callbacks");
        Check(!binding.TryRebindCorePage(Room + 1, changed, 100), "core rebind cannot migrate room");
        snapshot = Snapshot(); NativeDeckBinding.TryCreate(Room, snapshot, 0, Owner, out binding);
        changed = Snapshot(); changed.Sequence++; changed.ClaimOwners[0] = Other;
        Check(!binding.TryRebindCorePage(Room, changed, 100), "core rebind cannot recover lost ownership");
    }

    private static void UiReasons()
    {
        var snapshot = Snapshot(); var target = snapshot.CoreBooks[2];
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, false, true) == null,
            "owned available target is rendered enabled independently of the dirty click guard");
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, true, true) == "等待房主确认",
            "pending labels the actual wait instead of generic readonly");
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, false, false, true) == "联机尚未就绪",
            "not-ready reason is distinct from inventory flags");
        snapshot.ClaimOwners[0] = 0;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, false, true, false, true) == "馆员尚未认领",
            "unclaimed role explains why claiming is required");
        snapshot.ClaimOwners[0] = Other;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, false, true, false, true) == "其他玩家的馆员",
            "foreign role remains readonly with accurate reason");
        snapshot.ClaimOwners[0] = Owner;
        target.Flags = CoreBookFlags.PassiveBound;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, false, true) == "被动来源被占用",
            "claiming cannot erase a passive donor restriction");
        target.Flags = CoreBookFlags.DraftMismatch;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, false, true) == "被动草稿未应用",
            "draft flag is visible rather than all books appearing generically readonly");
        target.Flags = CoreBookFlags.None; snapshot.CoreBooks[0].Flags |= CoreBookFlags.DraftMismatch;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, false, true) == "当前书页：被动草稿未应用",
            "current page restriction also prevents rendering an unusable target enabled");
        snapshot.CoreBooks[0].Flags = CoreBookFlags.Equipped;
        Check(CorePageUiAccess.ReadOnlyReason(snapshot, 0, target, Owner, true, true, false, false) == "界面绑定已失效",
            "missing row binding has a separate failure reason");
    }

    public static void Main()
    { Protocol(); Authority(); Lifecycle(); UiReasons(); _checks += EquipmentCodecChecks.Run(); Console.WriteLine("EquipmentHarness: " + _checks + " checks passed."); }
}
