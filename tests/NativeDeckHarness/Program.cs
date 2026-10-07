using System;
using RuinaCoop;
using UI;

internal static class Program
{
    private const ulong Room = 111;
    private const ulong Local = 222;
    private const ulong Other = 333;
    private static int _checks;

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static ProgressSnapshot Snapshot(uint sequence = 1)
    {
        var result = new ProgressSnapshot
        {
            Sequence = sequence, SelectedStageId = 101, SelectedFloorId = (byte)SephirahType.Malkuth,
            ClaimRevision = 11, DeckRevision = 22
        };
        result.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Test" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        floor.Units.Add("Identical name");
        floor.Units.Add("Identical name");
        result.Floors.Add(floor);
        result.ClaimOwners.Add(Local);
        result.ClaimOwners.Add(Local);
        for (var i = 0; i < 2; i++)
        {
            var deck = new ProgressSnapshot.UnitDeckEntry
            {
                UnitIdentity = (ulong)(400 + i), BookId = 500 + i, BookInstanceId = 600 + i, Capacity = 9
            };
            deck.Cards.Add(100);
            result.UnitDecks.Add(deck);
        }
        result.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 100, Count = 1 });
        result.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 101, Count = 2 });
        return result;
    }

    private static NativeDeckBinding Open(ProgressSnapshot snapshot = null, byte unit = 0)
    {
        Check(NativeDeckBinding.TryCreate(Room, snapshot ?? Snapshot(), unit, Local, out var binding),
            "prepared role can create a binding");
        return binding;
    }

    private static void CreationAndAuthority()
    {
        Check(!NativeDeckBinding.TryCreate(0, Snapshot(), 0, Local, out _) &&
            !NativeDeckBinding.TryCreate(Room, null, 0, Local, out _) &&
            !NativeDeckBinding.TryCreate(Room, Snapshot(), 0, 0, out _) &&
            !NativeDeckBinding.TryCreate(Room, Snapshot(), 255, Local, out _), "invalid binding inputs rejected");
        var snapshot = Snapshot();
        snapshot.ClaimOwners.Clear();
        Check(!NativeDeckBinding.TryCreate(Room, snapshot, 0, Local, out _), "owner roster mismatch rejected");
        snapshot = Snapshot();
        snapshot.Floors[0].Units.RemoveAt(1);
        Check(!NativeDeckBinding.TryCreate(Room, snapshot, 0, Local, out _), "librarian roster mismatch rejected");
        snapshot = Snapshot();
        snapshot.SelectedStageId = 0;
        Check(!NativeDeckBinding.TryCreate(Room, snapshot, 0, Local, out _), "no stage is not prepared");
        snapshot = Snapshot();
        snapshot.SelectedFloorId = PrepClaims.NoFloor;
        Check(!NativeDeckBinding.TryCreate(Room, snapshot, 0, Local, out _), "no floor is not prepared");

        var binding = Open();
        Check(binding.Valid && binding.CanEdit && binding.UnitIndex == 0 && binding.RoomId == Room,
            "owned ordinary role is editable");
        var token = binding.CaptureEvent();
        Check(binding.CanSubmit(token, 101, DeckAction.Add), "stocked card is addable");
        Check(binding.CanSubmit(token, 100, DeckAction.Remove), "equipped card is removable");
        Check(!binding.CanSubmit(token, 0, DeckAction.Add) &&
            !binding.CanSubmit(token, 999, DeckAction.Add) &&
            !binding.CanSubmit(token, 101, DeckAction.Remove) &&
            !binding.CanSubmit(token, 100, (DeckAction)255), "invalid card, inventory and action rejected");
        var secondBinding = Open(binding.Snapshot);
        Check(!secondBinding.CanSubmit(token, 101, DeckAction.Add), "token cannot cross binding instances");
        Check(!binding.CanSubmit(default(NativeDeckEventToken), 101, DeckAction.Add), "default token rejected");

        foreach (var owner in new[] { 0UL, Other })
        {
            snapshot = Snapshot();
            snapshot.ClaimOwners[0] = owner;
            binding = Open(snapshot);
            Check(binding.Valid && !binding.CanEdit && !binding.CanSubmit(binding.CaptureEvent(), 101, DeckAction.Add),
                "unclaimed or foreign role remains viewable but readonly");
        }
        snapshot = Snapshot();
        snapshot.UnitDecks[0].UnitIdentity = 0;
        binding = Open(snapshot);
        Check(!binding.CanEdit, "missing unit identity cannot enable edits");
        snapshot = Snapshot();
        snapshot.UnitDecks[0].Fixed = true;
        binding = Open(snapshot);
        Check(!binding.CanEdit, "fixed key page is readonly");
        snapshot = Snapshot();
        snapshot.UnitDecks[0].MultiDeck = true;
        binding = Open(snapshot);
        Check(!binding.CanEdit, "multiple key page decks are readonly");
    }

    private static void DisplayGenerations()
    {
        var binding = Open();
        var oldSnapshot = binding.Snapshot;
        var oldClick = binding.CaptureEvent();
        var newer = Snapshot(2);
        newer.DeckRevision++;
        newer.CardStock.RemoveAt(0);
        Check(binding.TryUpdate(Room, newer) && ReferenceEquals(binding.Snapshot, newer),
            "same role accepts authoritative inventory/deck refresh");
        Check(!binding.CanSubmit(oldClick, 101, DeckAction.Add), "old displayed snapshot cannot send after refresh");
        Check(binding.CanSubmit(binding.CaptureEvent(), 101, DeckAction.Add), "newly displayed state can send");
        Check(!binding.CanSubmit(binding.CaptureEvent(), 100, DeckAction.Add), "new inventory shortage is enforced");
        Check(binding.TryUpdate(Room, oldSnapshot) && ReferenceEquals(binding.Snapshot, newer) && binding.Valid,
            "late older snapshot cannot replace current display");
        var sameClick = binding.CaptureEvent();
        Check(binding.TryUpdate(Room, newer) && binding.CanSubmit(sameClick, 101, DeckAction.Add),
            "same snapshot observation does not invalidate its current controls");

        var priorRoleClick = binding.CaptureEvent();
        Check(binding.TrySelectUnit(1) && binding.UnitIndex == 1, "another role can be selected in the same snapshot");
        Check(!binding.CanSubmit(priorRoleClick, 101, DeckAction.Add),
            "old role click cannot be combined with same-snapshot new role");
        Check(binding.CanSubmit(binding.CaptureEvent(), 101, DeckAction.Add), "new role's own click is valid");
        var intermediateClick = binding.CaptureEvent();
        Check(binding.TrySelectUnit(0) && !binding.CanSubmit(intermediateClick, 101, DeckAction.Add) &&
            !binding.CanSubmit(priorRoleClick, 101, DeckAction.Add), "selecting back does not revive old controls");
        Check(!binding.TrySelectUnit(255) && binding.UnitIndex == 0, "out-of-range selection preserves current role");

        var claimUpdate = Snapshot(3);
        claimUpdate.ClaimRevision++;
        claimUpdate.DeckRevision = newer.DeckRevision;
        claimUpdate.ClaimOwners[1] = Other;
        var beforeClaim = binding.CaptureEvent();
        Check(binding.TryUpdate(Room, claimUpdate) && binding.CanEdit,
            "other player's claim update keeps current owned role open");
        Check(!binding.CanSubmit(beforeClaim, 101, DeckAction.Add) &&
            binding.CanSubmit(binding.CaptureEvent(), 101, DeckAction.Add), "claim revision refresh retires old click");

        var frozen = Snapshot(4);
        frozen.DecksFrozen = true;
        frozen.ClaimRevision = claimUpdate.ClaimRevision;
        frozen.DeckRevision = newer.DeckRevision + 1;
        frozen.ClaimOwners[1] = Other;
        Check(binding.TryUpdate(Room, frozen) && binding.Valid && !binding.CanEdit &&
            !binding.CanSubmit(binding.CaptureEvent(), 101, DeckAction.Add), "freeze keeps display open and blocks edits");
        var thawed = Snapshot(5);
        thawed.ClaimRevision = claimUpdate.ClaimRevision;
        thawed.DeckRevision = frozen.DeckRevision + 1;
        thawed.ClaimOwners[1] = Other;
        Check(binding.TryUpdate(Room, thawed) && binding.CanEdit, "preparation reopening restores current-role editing");
    }

    private static void Invalidation()
    {
        foreach (var change in new Action<ProgressSnapshot>[]
        {
            s => s.SelectedStageId++,
            s => s.SelectedFloorId = (byte)SephirahType.Yesod,
            s => s.UnitDecks[0].UnitIdentity++,
            s => s.UnitDecks[1].UnitIdentity++,
            s => s.UnitDecks[0].BookInstanceId++,
            s => s.UnitDecks[0].BookId++,
            s => s.ClaimOwners[0] = Other,
            s => s.ClaimOwners[0] = 0,
            s => s.UnitDecks.RemoveAt(1)
        })
        {
            var binding = Open();
            var click = binding.CaptureEvent();
            var newer = Snapshot(2);
            change(newer);
            Check(!binding.TryUpdate(Room, newer) && !binding.Valid && !binding.CanEdit &&
                !binding.CanSubmit(click, 101, DeckAction.Add), "context/ownership change revokes old editor");
            Check(!binding.TryUpdate(Room, Snapshot(3)) && !binding.TrySelectUnit(1),
                "later snapshot cannot reopen an invalidated editor");
        }
        var otherRoom = Open();
        Check(!otherRoom.TryUpdate(Room + 1, Snapshot(2)) && !otherRoom.Valid, "another room invalidates binding");
        var closed = Open();
        var closedClick = closed.CaptureEvent();
        closed.Close();
        Check(!closed.TryUpdate(Room, Snapshot(2)) && !closed.Valid &&
            !closed.CanSubmit(closedClick, 101, DeckAction.Add), "close plus late callback never reopens or submits");
        var readonlySnapshot = Snapshot();
        readonlySnapshot.ClaimOwners[0] = Other;
        var readonlyBinding = Open(readonlySnapshot);
        Check(readonlyBinding.TryUpdate(Room, Snapshot(2)) && readonlyBinding.CanEdit,
            "a viewed role becomes editable after the local player gains its claim");
    }

    private static void Main()
    {
        CreationAndAuthority();
        DisplayGenerations();
        Invalidation();
        Console.WriteLine("PASS: native role identity, display generations, readonly/frozen state and lifecycle; " + _checks + " checks.");
        Console.WriteLine("Transport ACK ordering is checked against compiled RelaySession by native-target-smoke.ps1.");
    }
}
