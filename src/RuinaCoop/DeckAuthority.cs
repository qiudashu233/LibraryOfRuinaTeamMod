namespace RuinaCoop
{
    // Checks authority and shared inventory before the host invokes vanilla deck rules.
    internal static class DeckAuthority
    {
        internal static DeckResultCode Validate(ProgressSnapshot snapshot, ulong sender, DeckRequest request)
        {
            if (sender == 0 || request.RequestId == 0 ||
                (request.Action != DeckAction.Add && request.Action != DeckAction.Remove))
            {
                return DeckResultCode.InvalidRequest;
            }
            if (request.CardId <= 0)
            {
                return DeckResultCode.InvalidCard;
            }
            if (snapshot == null || snapshot.SelectedStageId <= 0 ||
                snapshot.SelectedFloorId == PrepClaims.NoFloor ||
                request.StageId != snapshot.SelectedStageId || request.FloorId != snapshot.SelectedFloorId)
            {
                return DeckResultCode.NotReady;
            }
            if (request.ClaimRevision != snapshot.ClaimRevision)
            {
                return DeckResultCode.StaleClaim;
            }
            if (request.DeckRevision != snapshot.DeckRevision)
            {
                return DeckResultCode.StaleDeck;
            }
            if (request.UnitIndex >= snapshot.ClaimOwners.Count ||
                request.UnitIndex >= snapshot.UnitDecks.Count)
            {
                return DeckResultCode.InvalidRequest;
            }
            if (snapshot.ClaimOwners[request.UnitIndex] != sender)
            {
                return DeckResultCode.NotOwner;
            }
            if (snapshot.DecksFrozen)
            {
                return DeckResultCode.Frozen;
            }

            var deck = snapshot.UnitDecks[request.UnitIndex];
            if (deck == null)
            {
                return DeckResultCode.NotReady;
            }
            if (deck.BookId <= 0 || deck.Fixed || deck.MultiDeck)
            {
                return DeckResultCode.UnsupportedBook;
            }
            if (request.Action == DeckAction.Remove)
            {
                return deck.Cards.Contains(request.CardId)
                    ? DeckResultCode.Accepted : DeckResultCode.InvalidCard;
            }
            foreach (var item in snapshot.CardStock)
            {
                if (item != null && item.Id == request.CardId && item.Count > 0)
                {
                    return DeckResultCode.Accepted;
                }
            }
            return DeckResultCode.InvalidCard;
        }
    }
}
