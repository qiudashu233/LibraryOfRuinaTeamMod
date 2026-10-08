namespace RuinaCoop
{
    // A click belongs to the exact role and display generation that produced it.
    internal struct NativeDeckEventToken
    {
        internal readonly ProgressSnapshot Snapshot;
        internal readonly byte UnitIndex;
        internal readonly uint Generation;
        internal readonly uint ClaimRevision;
        internal readonly uint DeckRevision;
        internal readonly int BookInstanceId;
        internal readonly ulong BookToken;
        internal readonly NativeDeckBinding Owner;

        internal NativeDeckEventToken(NativeDeckBinding owner, ProgressSnapshot snapshot,
            byte unitIndex, uint generation, uint claimRevision, uint deckRevision, int bookInstanceId)
        {
            Owner = owner;
            Snapshot = snapshot;
            UnitIndex = unitIndex;
            Generation = generation;
            ClaimRevision = claimRevision;
            DeckRevision = deckRevision;
            BookInstanceId = bookInstanceId;
            BookToken = snapshot.UnitDecks[unitIndex].BookToken;
        }
    }

    // This owns display identity only. RelaySession owns outstanding requests and ACK state.
    internal sealed class NativeDeckBinding
    {
        private readonly ulong _localId;
        private readonly int _stageId;
        private readonly byte _floorId;
        private readonly ulong[] _roster;
        private int _bookId;
        private int _bookInstanceId;
        private ulong _bookToken;
        private ulong _selectedOwner;
        private uint _claimRevision;
        private uint _deckRevision;
        private uint _generation;

        internal ulong RoomId { get; private set; }
        internal byte UnitIndex { get; private set; }
        internal ProgressSnapshot Snapshot { get; private set; }
        internal bool Valid { get; private set; }
        internal bool CanEdit
        {
            get
            {
                if (!Valid || !MatchesContext(Snapshot)) return false;
                var deck = Snapshot.UnitDecks[UnitIndex];
                return deck.UnitIdentity != 0 && Snapshot.ClaimOwners[UnitIndex] == _localId &&
                    !Snapshot.DecksFrozen && deck.BookId > 0 && !deck.Fixed && !deck.MultiDeck;
            }
        }

        private NativeDeckBinding(ulong roomId, ProgressSnapshot snapshot, byte unitIndex, ulong localId)
        {
            RoomId = roomId;
            Snapshot = snapshot;
            UnitIndex = unitIndex;
            _localId = localId;
            _stageId = snapshot.SelectedStageId;
            _floorId = snapshot.SelectedFloorId;
            _roster = new ulong[snapshot.UnitDecks.Count];
            for (var i = 0; i < _roster.Length; i++) _roster[i] = snapshot.UnitDecks[i].UnitIdentity;
            CaptureSelection();
            Valid = true;
        }

        internal static bool TryCreate(ulong roomId, ProgressSnapshot snapshot, byte unitIndex,
            ulong localId, out NativeDeckBinding binding)
        {
            binding = null;
            if (roomId == 0 || localId == 0 || !IsPrepared(snapshot) || unitIndex >= snapshot.UnitDecks.Count)
            {
                return false;
            }
            binding = new NativeDeckBinding(roomId, snapshot, unitIndex, localId);
            return true;
        }

        // False means the context is permanently invalid. Older callbacks are ignored.
        internal bool TryUpdate(ulong roomId, ProgressSnapshot snapshot)
        {
            if (!Valid) return false;
            if (roomId != RoomId || snapshot == null)
            {
                Close();
                return false;
            }
            if (snapshot.Sequence < Snapshot.Sequence) return true;
            if (!MatchesContext(snapshot) ||
                (_selectedOwner == _localId && snapshot.ClaimOwners[UnitIndex] != _localId))
            {
                Close();
                return false;
            }
            if (ReferenceEquals(snapshot, Snapshot)) return true;
            Snapshot = snapshot;
            CaptureSelection();
            return true;
        }

        internal bool TrySelectUnit(byte unitIndex)
        {
            if (!Valid || !MatchesContext(Snapshot) || unitIndex >= Snapshot.UnitDecks.Count)
            {
                return false;
            }
            if (unitIndex == UnitIndex) return true;
            UnitIndex = unitIndex;
            CaptureSelection();
            return true;
        }

        // A host-confirmed page change is allowed only for this still-owned unit.
        // Ordinary TryUpdate remains strict so unrelated callers cannot revive old controls.
        internal bool TryRebindCorePage(ulong roomId, ProgressSnapshot snapshot, ulong expectedUnitIdentity)
        {
            if (!Valid || roomId != RoomId || snapshot == null || snapshot.Sequence <= Snapshot.Sequence ||
                !MatchesRoster(snapshot) || expectedUnitIdentity == 0 ||
                Snapshot.UnitDecks[UnitIndex].UnitIdentity != expectedUnitIdentity ||
                snapshot.UnitDecks[UnitIndex].UnitIdentity != expectedUnitIdentity ||
                _selectedOwner != _localId || snapshot.ClaimOwners[UnitIndex] != _localId || snapshot.DecksFrozen)
                return false;
            var old = Snapshot.UnitDecks[UnitIndex];
            var current = snapshot.UnitDecks[UnitIndex];
            if (old.Fixed || old.MultiDeck || current.BookId <= 0 || current.BookToken == 0 ||
                current.Fixed || current.MultiDeck ||
                !HasSupportedEquippedBook(snapshot, current) ||
                (current.BookId == _bookId && current.BookInstanceId == _bookInstanceId && current.BookToken == _bookToken))
                return false;
            Snapshot = snapshot;
            CaptureSelection();
            return true;
        }

        private bool HasSupportedEquippedBook(ProgressSnapshot snapshot, ProgressSnapshot.UnitDeckEntry current)
        {
            if (!snapshot.CoreBooksAvailable || snapshot.CoreBooksReason != CoreBooksReason.None) return false;
            var book = snapshot.CoreBooks.Find(entry => entry != null && entry.BookToken == current.BookToken);
            return book != null && book.Kind == CoreBookKind.Ordinary && book.Flags == CoreBookFlags.Equipped &&
                book.BookId == current.BookId && book.BookInstanceId > 0 && book.BookInstanceId == current.BookInstanceId &&
                book.OccupiedFloorId == _floorId && book.OccupiedUnitIndex == UnitIndex && book.Display.Available;
        }

        internal NativeDeckEventToken CaptureEvent()
        {
            return new NativeDeckEventToken(this, Snapshot, UnitIndex, _generation,
                _claimRevision, _deckRevision, _bookInstanceId);
        }

        internal bool CanSubmit(NativeDeckEventToken expected, int cardId, DeckAction action)
        {
            if (!CanEdit || !ReferenceEquals(expected.Owner, this) ||
                !ReferenceEquals(expected.Snapshot, Snapshot) || expected.Generation != _generation ||
                expected.UnitIndex != UnitIndex || expected.BookInstanceId != _bookInstanceId ||
                expected.BookToken != _bookToken ||
                expected.ClaimRevision != _claimRevision || expected.DeckRevision != _deckRevision ||
                Snapshot.ClaimRevision != _claimRevision || Snapshot.DeckRevision != _deckRevision)
            {
                return false;
            }
            var request = new DeckRequest
            {
                RequestId = 1, StageId = _stageId, FloorId = _floorId, UnitIndex = expected.UnitIndex,
                ClaimRevision = expected.ClaimRevision, DeckRevision = expected.DeckRevision,
                CardId = cardId, Action = action
            };
            return DeckAuthority.Validate(Snapshot, _localId, request) == DeckResultCode.Accepted;
        }

        internal void Close()
        {
            if (!Valid) return;
            Valid = false;
            _generation++;
        }

        private void CaptureSelection()
        {
            var deck = Snapshot.UnitDecks[UnitIndex];
            _bookId = deck.BookId;
            _bookInstanceId = deck.BookInstanceId;
            _bookToken = deck.BookToken;
            _selectedOwner = Snapshot.ClaimOwners[UnitIndex];
            _claimRevision = Snapshot.ClaimRevision;
            _deckRevision = Snapshot.DeckRevision;
            _generation++;
        }

        private bool MatchesContext(ProgressSnapshot snapshot)
        {
            if (!MatchesRoster(snapshot)) return false;
            var deck = snapshot.UnitDecks[UnitIndex];
            return deck.BookId == _bookId && deck.BookInstanceId == _bookInstanceId &&
                (deck.BookToken == _bookToken || !Snapshot.CoreBooksAvailable || !snapshot.CoreBooksAvailable);
        }

        private bool MatchesRoster(ProgressSnapshot snapshot)
        {
            if (!IsPrepared(snapshot) || snapshot.SelectedStageId != _stageId ||
                snapshot.SelectedFloorId != _floorId || snapshot.UnitDecks.Count != _roster.Length ||
                UnitIndex >= snapshot.UnitDecks.Count)
            {
                return false;
            }
            for (var i = 0; i < _roster.Length; i++)
            {
                if (snapshot.UnitDecks[i].UnitIdentity != _roster[i]) return false;
            }
            return true;
        }

        private static bool IsPrepared(ProgressSnapshot snapshot)
        {
            if (snapshot == null || snapshot.SelectedStageId <= 0 ||
                snapshot.SelectedFloorId == PrepClaims.NoFloor || snapshot.UnitDecks.Count == 0 ||
                snapshot.UnitDecks.Count > 5 || snapshot.ClaimOwners.Count != snapshot.UnitDecks.Count)
            {
                return false;
            }
            var floor = snapshot.Floors.Find(candidate =>
                candidate != null && (byte)candidate.Sephirah == snapshot.SelectedFloorId);
            if (floor == null || floor.Units.Count != snapshot.UnitDecks.Count) return false;
            foreach (var deck in snapshot.UnitDecks)
            {
                if (deck == null) return false;
            }
            return true;
        }
    }
}
