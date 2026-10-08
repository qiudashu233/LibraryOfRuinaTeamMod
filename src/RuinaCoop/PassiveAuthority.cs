using System.Collections.Generic;

namespace RuinaCoop
{
    // No reserved-state getters or host objects: validate a complete replacement plan.
    internal static class PassiveAuthority
    {
        internal static PassiveResultCode Validate(ProgressSnapshot snapshot, ulong sender, PassiveRequest request)
        {
            if (sender == 0 || !PassiveProtocol.ValidRequest(request)) return PassiveResultCode.InvalidRequest;
            if (snapshot == null || snapshot.SelectedStageId <= 0 || snapshot.SelectedFloorId == PrepClaims.NoFloor ||
                request.StageId != snapshot.SelectedStageId || request.FloorId != snapshot.SelectedFloorId)
                return PassiveResultCode.NotReady;
            if (request.ClaimRevision != snapshot.ClaimRevision) return PassiveResultCode.StaleClaim;
            if (request.DeckRevision != snapshot.DeckRevision) return PassiveResultCode.StaleDeck;
            var floor = snapshot.Floors.Find(entry => entry != null && (byte)entry.Sephirah == request.FloorId);
            if (floor == null || snapshot.UnitDecks.Count != floor.Units.Count || snapshot.ClaimOwners.Count != snapshot.UnitDecks.Count ||
                request.UnitIndex >= snapshot.UnitDecks.Count) return PassiveResultCode.InvalidRequest;
            if (snapshot.ClaimOwners[request.UnitIndex] != sender) return PassiveResultCode.NotOwner;
            if (snapshot.DecksFrozen) return PassiveResultCode.Frozen;
            if (!snapshot.CoreBooksAvailable || snapshot.CoreBooksReason != CoreBooksReason.None ||
                !snapshot.PassivesAvailable || snapshot.PassivesReason != PassivesReason.None)
                return PassiveResultCode.UnsupportedInventory;
            var deck = snapshot.UnitDecks[request.UnitIndex];
            if (deck == null || deck.UnitIdentity != request.UnitIdentity || deck.BookToken != request.BookToken)
                return PassiveResultCode.StaleDeck;
            var targetCore = snapshot.CoreBooks.Find(book => book != null && book.BookToken == request.BookToken);
            var target = snapshot.PassiveBooks.Find(book => book != null && book.BookToken == request.BookToken);
            if (deck.Fixed || deck.MultiDeck || targetCore == null || targetCore.Kind != CoreBookKind.Ordinary ||
                targetCore.Flags != CoreBookFlags.Equipped || targetCore.BookId != deck.BookId || targetCore.BookInstanceId != deck.BookInstanceId ||
                targetCore.OccupiedFloorId != request.FloorId || targetCore.OccupiedUnitIndex != request.UnitIndex ||
                target == null || (target.Flags & PassiveBookFlags.ReceiverAllowed) == 0 || (target.Flags & PassiveBookFlags.Unsupported) != 0 ||
                target.ReceiverBookToken != 0 || target.Slots.Count != request.Slots.Length)
                return PassiveResultCode.UnsupportedTarget;
            if (request.SourceBookTokens.Length > target.MaxSources) return PassiveResultCode.SourceLimit;
            var selected = new Dictionary<ulong, ProgressSnapshot.PassiveBookEntry>();
            foreach (var token in request.SourceBookTokens)
            {
                var core = snapshot.CoreBooks.Find(book => book != null && book.BookToken == token);
                var source = snapshot.PassiveBooks.Find(book => book != null && book.BookToken == token);
                if (core == null || source == null) return PassiveResultCode.UnknownSource;
                if ((core.Flags & (CoreBookFlags.Equipped | CoreBookFlags.OwnerMismatch)) != 0 ||
                    core.OccupiedFloorId != PrepClaims.NoFloor || core.OccupiedUnitIndex != byte.MaxValue ||
                    source.ReceiverBookToken != 0 && (source.ReceiverBookToken != request.BookToken || !target.SourceTokens.Contains(token)))
                    return PassiveResultCode.SourceOccupied;
                if (core.Kind != CoreBookKind.Ordinary || (core.Flags & ~CoreBookFlags.PassiveBound) != 0 ||
                    source.SourceTokens.Count != 0 || (source.Flags & PassiveBookFlags.SourceAllowed) == 0 ||
                    (source.Flags & PassiveBookFlags.Unsupported) != 0)
                    return PassiveResultCode.UnsupportedSource;
                selected.Add(token, source);
            }
            var assigned = new HashSet<string>();
            var ids = new HashSet<int>(); var types = new HashSet<int>();
            var inherited = new List<ProgressSnapshot.PassiveSlotEntry>();
            var cost = 0;
            for (var i = 0; i < request.Slots.Length; i++)
            {
                var selection = request.Slots[i]; var slot = target.Slots[i];
                if (slot == null) return PassiveResultCode.UnsupportedTarget;
                if (selection.Mode == PassiveSelectionMode.RestoreNative)
                {
                    if (selection.ExpectedOriginPassiveId != slot.OriginId) return PassiveResultCode.StaleDeck;
                    if (slot.OriginId != PassiveMirror.EmptyId) { ids.Add(slot.OriginId); if (slot.InnerTypeId != -1) types.Add(slot.InnerTypeId); }
                    continue;
                }
                ProgressSnapshot.PassiveBookEntry source;
                if (!selected.TryGetValue(selection.SourceBookToken, out source) || selection.SourceSlotIndex >= source.Slots.Count)
                    return PassiveResultCode.UnknownSource;
                var sourceSlot = source.Slots[selection.SourceSlotIndex];
                if (sourceSlot == null || sourceSlot.OriginId != selection.ExpectedOriginPassiveId) return PassiveResultCode.StaleDeck;
                if ((slot.Flags & PassiveSlotFlags.Locked) != 0 || slot.OriginId != PassiveMirror.EmptyId ||
                    (slot.Flags & PassiveSlotFlags.CanReceive) == 0) return PassiveResultCode.LockedSlot;
                if (sourceSlot.OriginId == PassiveMirror.EmptyId || (sourceSlot.Flags & PassiveSlotFlags.CanGive) == 0 ||
                    (sourceSlot.Flags & PassiveSlotFlags.Hidden) != 0 ||
                    (sourceSlot.Flags & PassiveSlotFlags.Given) != 0 && source.ReceiverBookToken != request.BookToken)
                    return PassiveResultCode.UnsupportedSource;
                if (!assigned.Add(selection.SourceBookToken + ":" + selection.SourceSlotIndex)) return PassiveResultCode.IncompatiblePassive;
                inherited.Add(sourceSlot); cost += sourceSlot.Cost;
            }
            // Native duplicates may already exist, but an imported passive must not
            // collide with any retained native or other imported ID/inner type.
            foreach (var slot in inherited)
                if (!ids.Add(slot.OriginId) || slot.InnerTypeId != -1 && !types.Add(slot.InnerTypeId))
                    return PassiveResultCode.IncompatiblePassive;
            if (cost > target.MaxCost) return PassiveResultCode.CostExceeded;
            return PassiveResultCode.Accepted;
        }
    }
}
