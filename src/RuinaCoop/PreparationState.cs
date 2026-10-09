using System;
using System.Collections.Generic;
using System.Linq;

namespace RuinaCoop
{
    internal enum PreparationPhase : byte
    { Selection = 0, Editing = 1, StartPending = 2, Loading = 3, Battle = 4, Settlement = 5, Return = 6 }
    internal enum PreparationReason : byte
    { None = 0, NotCaptured = 1, UnsupportedStage = 2, UnsupportedData = 3, CaptureFailed = 4, PacketLimit = 5 }
    internal enum PreparationUnitReason : byte
    { None = 0, Unavailable = 1, Defeated = 2, UnitLimit = 3, UnknownIdentity = 4, FloorUnavailable = 5 }

    // Presentation and permission data only. This is not a battle initialization manifest.
    internal sealed class PreparationSnapshot
    {
        internal bool Available;
        internal PreparationReason Reason = PreparationReason.NotCaptured;
        internal PreparationPhase Phase;
        internal uint Revision;
        internal ulong ContextId;
        internal int StageId;
        internal byte FloorId = byte.MaxValue;
        internal byte MaxUnits;
        internal byte CurrentWaveIndex = byte.MaxValue;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal readonly List<PreparationFloorEntry> Floors = new List<PreparationFloorEntry>();
        internal readonly List<PreparationUnitEntry> Participants = new List<PreparationUnitEntry>();
        internal readonly List<PreparationWaveEntry> Waves = new List<PreparationWaveEntry>();
        internal readonly List<PreparationControllerEntry> Controllers = new List<PreparationControllerEntry>();
        // Candidate ownership stays local and is never encoded.
        [NonSerialized] internal object CandidateOwner;
        [NonSerialized] internal ulong CandidateGeneration;
    }
    internal sealed class PreparationFloorEntry
    {
        internal byte FloorId;
        internal bool CanParticipate;
        internal PreparationUnitReason Reason;
    }
    internal sealed class PreparationUnitEntry
    {
        internal byte UnitIndex;
        internal ulong UnitIdentity;
        internal bool CanParticipate;
        internal bool Participating;
        internal PreparationUnitReason Reason;
    }
    internal sealed class PreparationWaveEntry
    {
        internal byte WaveIndex;
        internal readonly List<PreparationEnemyEntry> Enemies = new List<PreparationEnemyEntry>();
    }
    internal sealed class PreparationEnemyEntry
    {
        internal ulong EnemyIdentity;
        internal int EnemyId;
        internal int BookId;
        internal string Name = "";
        internal bool Unknown;
        internal bool CardsVisible;
        internal readonly List<int> Cards = new List<int>();
        internal readonly ProgressSnapshot.UnitDisplayEntry Display = new ProgressSnapshot.UnitDisplayEntry();
    }
    internal sealed class PreparationControllerEntry
    {
        internal ulong PlayerId;
        internal bool Connected;
        internal bool Ready;
    }

    // Preview/Commit keeps preparation revisions and readiness at the snapshot publication boundary.
    // Unity callers serialize use of this class on the host main thread.
    internal sealed class PreparationState
    {
        private PreparationSnapshot _current;
        private ulong _generation;
        internal PreparationSnapshot Current { get { return _current == null ? null : PreparationMirror.Clone(_current); } }

        internal PreparationSnapshot Preview(PreparationSnapshot captured, uint claimRevision, uint deckRevision,
            ulong hostId, IEnumerable<ulong> connectedPlayers, IList<ulong> claimOwners)
        {
            if (hostId == 0 || connectedPlayers == null || claimOwners == null) throw new ArgumentException("Invalid preparation controller context.");
            var candidate = PreparationMirror.Clone(captured ?? new PreparationSnapshot());
            candidate.Controllers.Clear();
            candidate.ClaimRevision = claimRevision;
            candidate.DeckRevision = deckRevision;
            var connected = new HashSet<ulong>(connectedPlayers);
            if (connected.Contains(0)) throw new ArgumentException("Invalid connected player identity.");
            // The host is present while its local session is running.
            connected.Add(hostId);
            if (candidate.Available && candidate.Phase == PreparationPhase.Editing)
            {
                if (claimOwners.Count != candidate.Participants.Count) throw new ArgumentException("Claims do not match the preparation roster.");
                var controllers = new SortedSet<ulong>();
                foreach (var unit in candidate.Participants)
                    if (unit.Participating) controllers.Add(claimOwners[unit.UnitIndex] == 0 ? hostId : claimOwners[unit.UnitIndex]);
                foreach (var player in controllers)
                    candidate.Controllers.Add(new PreparationControllerEntry { PlayerId = player, Connected = connected.Contains(player) });
            }
            string reason;
            if (!PreparationMirror.Validate(candidate, out reason)) throw new ArgumentException(reason);
            var unchanged = _current != null && Same(PreparationMirror.EncodeContent(candidate), PreparationMirror.EncodeContent(_current));
            if (unchanged)
            {
                candidate.Revision = _current.Revision;
                foreach (var controller in candidate.Controllers)
                {
                    var previous = _current.Controllers.Find(row => row.PlayerId == controller.PlayerId);
                    controller.Ready = controller.Connected && previous != null && previous.Ready;
                }
            }
            else
            {
                if (_current != null && _current.Revision == uint.MaxValue) throw new InvalidOperationException("Preparation revision exhausted; restart the room.");
                candidate.Revision = _current == null ? 1 : _current.Revision + 1;
            }
            Stamp(candidate);
            return candidate;
        }

        internal PreparationSnapshot PreviewReady(ulong sender, uint expectedRevision, bool ready, bool connected,
            out PreparationReadyResultCode result)
        {
            result = PreparationReadyResultCode.NotReady;
            if (sender == 0 || expectedRevision == 0) { result = PreparationReadyResultCode.InvalidRequest; return null; }
            if (_current == null || !_current.Available || _current.Phase != PreparationPhase.Editing) return null;
            if (_current.Revision != expectedRevision) { result = PreparationReadyResultCode.StaleRevision; return null; }
            var controller = _current.Controllers.Find(row => row.PlayerId == sender);
            if (controller == null) { result = PreparationReadyResultCode.Observer; return null; }
            if (!connected || !controller.Connected) { result = PreparationReadyResultCode.Disconnected; return null; }
            var candidate = PreparationMirror.Clone(_current);
            candidate.Controllers.Find(row => row.PlayerId == sender).Ready = ready;
            Stamp(candidate);
            result = PreparationReadyResultCode.Accepted;
            return candidate;
        }

        internal bool Commit(PreparationSnapshot candidate)
        {
            string reason;
            if (candidate == null || !ReferenceEquals(candidate.CandidateOwner, this) || candidate.CandidateGeneration != _generation ||
                !PreparationMirror.Validate(candidate, out reason)) return false;
            _current = PreparationMirror.Clone(candidate);
            _generation++;
            return true;
        }
        internal void Reset() { _current = null; _generation++; }
        internal bool AllControllersReady
        {
            get { return _current != null && _current.Available && _current.Phase == PreparationPhase.Editing &&
                _current.Controllers.Count > 0 && _current.Controllers.All(row => row.Connected && row.Ready); }
        }
        // Stage 3.3 never opens the native battle start path, even when everybody is ready.
        internal bool CanStartBattle { get { return false; } }
        private void Stamp(PreparationSnapshot candidate) { candidate.CandidateOwner = this; candidate.CandidateGeneration = _generation; }
        private static bool Same(byte[] left, byte[] right) { return left.Length == right.Length && left.SequenceEqual(right); }
    }
}
