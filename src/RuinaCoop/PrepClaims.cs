using System;
using System.Collections.Generic;

namespace RuinaCoop
{
    internal enum ClaimAction : byte { Claim = 1, Release = 2 }

    internal enum ClaimResultCode : byte
    {
        Accepted = 0,
        NotReady = 1,
        StaleRevision = 2,
        InvalidSlot = 3,
        AlreadyClaimed = 4,
        NotOwner = 5,
        InvalidRequest = 6
    }

    // Host-owned preparation state. Slot identity is the selected floor plus its
    // current librarian index; any roster change invalidates all old claims.
    internal sealed class PrepClaims
    {
        internal const byte NoFloor = byte.MaxValue;

        private int _stageId;
        private byte _floorId = NoFloor;
        private object[] _unitReferences = new object[0];
        private ulong[] _owners = new ulong[0];
        private ulong _contextId;
        private byte _participationMask;

        internal uint Revision { get; private set; }
        internal byte FloorId { get { return _floorId; } }

        internal ulong OwnerAt(byte unitIndex)
        {
            return unitIndex < _owners.Length ? _owners[unitIndex] : 0;
        }

        internal void Reconcile(ProgressSnapshot snapshot)
        {
            if (_stageId != snapshot.SelectedStageId)
            {
                _stageId = snapshot.SelectedStageId;
                ResetFloor();
            }
            if (_floorId != NoFloor)
            {
                var floor = FindFloor(snapshot, _floorId);
                if (floor == null || !SameUnits(floor.UnitReferences))
                {
                    ResetFloor();
                }
            }
            snapshot.SelectedFloorId = _floorId;
            snapshot.ClaimRevision = Revision;
            snapshot.ClaimOwners.Clear();
            snapshot.ClaimOwners.AddRange(_owners);
        }

        internal bool SelectFloor(ProgressSnapshot snapshot, byte floorId)
        {
            if (snapshot == null || snapshot.SelectedStageId == 0 ||
                snapshot.SelectedStageId != _stageId)
            {
                return false;
            }
            var floor = FindFloor(snapshot, floorId);
            if (floor == null || floor.Units.Count == 0 || floor.Units.Count > 5 ||
                floor.UnitReferences.Count != floor.Units.Count)
            {
                return false;
            }
            if (_floorId == floorId && SameUnits(floor.UnitReferences))
            {
                return true;
            }
            _floorId = floorId;
            _unitReferences = floor.UnitReferences.ToArray();
            _owners = new ulong[floor.Units.Count];
            Revision++;
            return true;
        }

        internal ClaimResultCode Apply(ulong playerId, int stageId, byte floorId,
            byte unitIndex, uint expectedRevision, ClaimAction action)
        {
            if (playerId == 0 || (action != ClaimAction.Claim && action != ClaimAction.Release))
            {
                return ClaimResultCode.InvalidRequest;
            }
            if (_stageId == 0 || stageId != _stageId || floorId != _floorId ||
                _floorId == NoFloor)
            {
                return ClaimResultCode.NotReady;
            }
            if (expectedRevision != Revision)
            {
                return ClaimResultCode.StaleRevision;
            }
            if (unitIndex >= _owners.Length)
            {
                return ClaimResultCode.InvalidSlot;
            }
            if (_contextId != 0 && (_participationMask & (1 << unitIndex)) == 0)
                return ClaimResultCode.InvalidSlot;
            var owner = _owners[unitIndex];
            if (action == ClaimAction.Claim)
            {
                if (owner != 0 && owner != playerId)
                {
                    return ClaimResultCode.AlreadyClaimed;
                }
                if (owner == 0)
                {
                    _owners[unitIndex] = playerId;
                    Revision++;
                }
            }
            else
            {
                if (owner != playerId)
                {
                    return ClaimResultCode.NotOwner;
                }
                _owners[unitIndex] = 0;
                Revision++;
            }
            return ClaimResultCode.Accepted;
        }

        internal void ReconcilePreparation(PreparationSnapshot preparation)
        {
            if (preparation == null || !preparation.Available || preparation.Phase != PreparationPhase.Editing)
            {
                ResetFloor();
                _contextId = 0;
                _participationMask = 0;
                return;
            }
            byte mask = 0;
            foreach (var unit in preparation.Participants)
                if (unit.UnitIndex < _owners.Length && unit.CanParticipate && unit.Participating)
                    mask |= (byte)(1 << unit.UnitIndex);
            var changed = _contextId != preparation.ContextId || _participationMask != mask;
            for (var i = 0; i < _owners.Length; i++)
            {
                if (_contextId != preparation.ContextId || (mask & (1 << i)) == 0)
                {
                    changed |= _owners[i] != 0;
                    _owners[i] = 0;
                }
            }
            if (changed) Revision++;
            _contextId = preparation.ContextId;
            _participationMask = mask;
        }

        internal bool ReleaseAbsent(Func<ulong, bool> isMember)
        {
            var changed = false;
            for (var i = 0; i < _owners.Length; i++)
            {
                if (_owners[i] != 0 && !isMember(_owners[i]))
                {
                    _owners[i] = 0;
                    changed = true;
                }
            }
            if (changed)
            {
                Revision++;
            }
            return changed;
        }

        private void ResetFloor()
        {
            if (_floorId != NoFloor || _owners.Length != 0)
            {
                Revision++;
            }
            _floorId = NoFloor;
            _unitReferences = new object[0];
            _owners = new ulong[0];
        }

        private bool SameUnits(List<object> units)
        {
            if (units.Count != _unitReferences.Length)
            {
                return false;
            }
            for (var i = 0; i < units.Count; i++)
            {
                if (!ReferenceEquals(units[i], _unitReferences[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static ProgressSnapshot.FloorEntry FindFloor(ProgressSnapshot snapshot, byte id)
        {
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah == id)
                {
                    return floor;
                }
            }
            return null;
        }
    }
}
