namespace RuinaCoop
{
    // Pure snapshot validation; only the host transaction resolves or touches real game objects.
    internal static class EquipmentAuthority
    {
        internal static CorePageResultCode Validate(ProgressSnapshot snapshot, ulong sender, CorePageRequest request)
        {
            if (sender == 0 || request.RequestId == 0 || request.StageId <= 0 || request.FloorId < 1 || request.FloorId > 10 || request.UnitIndex >= 5 ||
                request.UnitIdentity == 0 || request.OldBookToken == 0 || request.TargetBookToken == 0 ||
                request.TargetBookToken == request.OldBookToken)
                return CorePageResultCode.InvalidRequest;
            if (snapshot == null || snapshot.SelectedStageId <= 0 || snapshot.SelectedFloorId == PrepClaims.NoFloor ||
                request.StageId != snapshot.SelectedStageId || request.FloorId != snapshot.SelectedFloorId)
                return CorePageResultCode.NotReady;
            if (request.ClaimRevision != snapshot.ClaimRevision) return CorePageResultCode.StaleClaim;
            if (request.DeckRevision != snapshot.DeckRevision) return CorePageResultCode.StaleDeck;
            var floor = snapshot.Floors.Find(entry => entry != null && (byte)entry.Sephirah == snapshot.SelectedFloorId);
            if (floor == null || snapshot.UnitDecks.Count != floor.Units.Count ||
                snapshot.ClaimOwners.Count != snapshot.UnitDecks.Count || request.UnitIndex >= snapshot.UnitDecks.Count)
                return CorePageResultCode.InvalidRequest;
            if (snapshot.ClaimOwners[request.UnitIndex] != sender) return CorePageResultCode.NotOwner;
            if (snapshot.DecksFrozen) return CorePageResultCode.Frozen;
            if (!snapshot.CoreBooksAvailable || snapshot.CoreBooksReason != CoreBooksReason.None)
                return CorePageResultCode.UnsupportedInventory;
            var deck = snapshot.UnitDecks[request.UnitIndex];
            if (deck == null || deck.UnitIdentity != request.UnitIdentity || deck.BookToken != request.OldBookToken)
                return CorePageResultCode.StaleDeck;
            if (deck.BookId <= 0 || deck.Fixed || deck.MultiDeck) return CorePageResultCode.UnsupportedCurrentBook;
            var current = snapshot.CoreBooks.Find(book => book != null && book.BookToken == request.OldBookToken);
            if (current == null || current.BookId != deck.BookId || current.BookInstanceId != deck.BookInstanceId ||
                current.Kind == CoreBookKind.Special || current.OccupiedFloorId != request.FloorId ||
                current.OccupiedUnitIndex != request.UnitIndex || (current.Flags & CoreBookFlags.Equipped) == 0)
                return CorePageResultCode.UnsupportedCurrentBook;
            var permittedCurrent = CoreBookFlags.Equipped;
            // A unit can replace its own ordinary default page, but defaults are never targets.
            if (current.Kind == CoreBookKind.Default) permittedCurrent |= CoreBookFlags.CannotEquip;
            if ((current.Flags & ~permittedCurrent) != 0) return CorePageResultCode.UnsupportedCurrentBook;
            var target = snapshot.CoreBooks.Find(book => book != null && book.BookToken == request.TargetBookToken);
            if (target == null) return CorePageResultCode.UnknownTarget;
            if ((target.Flags & (CoreBookFlags.Equipped | CoreBookFlags.OwnerMismatch)) != 0 ||
                target.OccupiedFloorId != PrepClaims.NoFloor || target.OccupiedUnitIndex != byte.MaxValue)
                return CorePageResultCode.TargetOccupied;
            if (target.Kind != CoreBookKind.Ordinary || target.Flags != CoreBookFlags.None ||
                target.BookId <= 0 || target.BookInstanceId <= 0 || !target.Display.Available)
                return CorePageResultCode.UnsupportedTarget;
            return CorePageResultCode.Accepted;
        }
    }
}
