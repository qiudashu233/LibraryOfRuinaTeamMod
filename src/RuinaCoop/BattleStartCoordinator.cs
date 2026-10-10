using System;
using System.Collections.Generic;

namespace RuinaCoop
{
    internal enum BattleStartPhase : byte
    {
        Idle = 0, Offering = 1, ReadyToCommit = 2, Committed = 3,
        AwaitingStateAcks = 4, Initialized = 5, Cancelled = 6, Frozen = 7
    }

    internal sealed class BattleStartIdentity
    {
        private readonly byte[] _manifestDigest;
        internal ulong RoomId { get; private set; }
        internal ulong BattleSessionId { get; private set; }
        internal ulong PreparationContext { get; private set; }
        internal uint PreparationRevision { get; private set; }
        internal byte[] ManifestDigest { get { return (byte[])_manifestDigest.Clone(); } }

        internal BattleStartIdentity(ulong room, ulong battle, ulong context, uint revision, byte[] digest)
        {
            RoomId = room; BattleSessionId = battle; PreparationContext = context;
            PreparationRevision = revision; _manifestDigest = (byte[])digest.Clone();
        }

        internal bool Matches(BattleMessage message, byte[] digest)
        {
            return message != null && message.RoomId == RoomId && message.BattleSessionId == BattleSessionId &&
                message.PreparationContext == PreparationContext && message.PreparationRevision == PreparationRevision &&
                BattleProtocol.DigestEquals(message.Digest, digest) &&
                (message.Payload == null || message.Payload.Length == 0);
        }
    }

    // Host-only transaction barrier. Reaching Initialized permits presentation
    // of the first act; it does not grant permission to execute battle inputs.
    internal sealed class BattleStartCoordinator
    {
        private readonly HashSet<ulong> _usedBattleIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _controllers = new HashSet<ulong>();
        private readonly HashSet<ulong> _offerAcks = new HashSet<ulong>();
        private readonly HashSet<ulong> _stateAcks = new HashSet<ulong>();
        private byte[] _stateDigest;
        private double _deadline;
        private double _timeoutSeconds;
        private double _lastNow;

        internal BattleStartPhase Phase { get; private set; }
        internal BattleStartIdentity Identity { get; private set; }
        internal bool Committed { get; private set; }
        internal bool Frozen { get { return Phase == BattleStartPhase.Frozen; } }
        internal bool CanCommit { get { return Phase == BattleStartPhase.ReadyToCommit; } }
        internal bool CanReturnToPreparation { get { return !Committed && Phase == BattleStartPhase.Cancelled; } }
        internal string FailureReason { get; private set; }

        internal bool Begin(ulong room, ulong battle, ulong context, uint revision, byte[] manifestDigest,
            ulong hostId, IEnumerable<ulong> controllers, double now, double timeoutSeconds = 15)
        {
            if ((Phase != BattleStartPhase.Idle && Phase != BattleStartPhase.Cancelled) || Committed ||
                room == 0 || battle == 0 || context == 0 || revision == 0 || hostId == 0 ||
                manifestDigest == null || manifestDigest.Length != BattleProtocol.DigestBytes ||
                controllers == null || !Finite(now) || now < 0 || !Finite(timeoutSeconds) || timeoutSeconds <= 0 ||
                timeoutSeconds > 120 || !Finite(now + timeoutSeconds) || now + timeoutSeconds <= now ||
                _usedBattleIds.Contains(battle)) return false;
            var participants = new HashSet<ulong>();
            foreach (var controller in controllers)
            {
                if (controller == 0 || !participants.Add(controller) || participants.Count > 2) return false;
            }
            if (participants.Count == 0 || !participants.Contains(hostId)) return false;
            Identity = new BattleStartIdentity(room, battle, context, revision, manifestDigest);
            _usedBattleIds.Add(battle); _controllers.Clear();
            foreach (var participant in participants) _controllers.Add(participant);
            _offerAcks.Clear(); _offerAcks.Add(hostId); _stateAcks.Clear(); _stateDigest = null;
            _timeoutSeconds = timeoutSeconds; _lastNow = now; _deadline = now + timeoutSeconds;
            FailureReason = "";
            Phase = _offerAcks.Count == _controllers.Count ? BattleStartPhase.ReadyToCommit : BattleStartPhase.Offering;
            return true;
        }

        internal bool AcknowledgeOffer(ulong sender, BattleMessage message)
        { return AcknowledgeOffer(sender, message, _lastNow); }

        internal bool AcknowledgeOffer(ulong sender, BattleMessage message, double now)
        {
            Tick(now);
            if (Phase != BattleStartPhase.Offering || !_controllers.Contains(sender) || message == null ||
                message.Type != BattleMessageType.OfferAck || !Identity.Matches(message, Identity.ManifestDigest) ||
                !_offerAcks.Add(sender)) return false;
            if (_offerAcks.Count == _controllers.Count) Phase = BattleStartPhase.ReadyToCommit;
            return true;
        }

        internal bool TryCommit(Func<bool> validateFinalConfiguration, double now)
        {
            Tick(now);
            if (!CanCommit || validateFinalConfiguration == null) return false;
            var committingIdentity = Identity;
            try
            {
                if (!validateFinalConfiguration()) { Cancel("Final preparation configuration changed."); return false; }
            }
            catch (Exception error) { Cancel("Final configuration validation failed: " + error.GetType().Name); return false; }
            if (!CanCommit || !ReferenceEquals(committingIdentity, Identity)) return false;
            Committed = true; Phase = BattleStartPhase.Committed; _deadline = now + _timeoutSeconds;
            return true;
        }

        internal bool PublishInitialState(byte[] stateDigest, double now)
        {
            Tick(now);
            if (Phase != BattleStartPhase.Committed || stateDigest == null || stateDigest.Length != BattleProtocol.DigestBytes) return false;
            _stateDigest = (byte[])stateDigest.Clone(); _stateAcks.Clear();
            Phase = BattleStartPhase.AwaitingStateAcks; _deadline = now + _timeoutSeconds;
            return true;
        }

        internal bool AcknowledgeState(ulong sender, BattleMessage message)
        { return AcknowledgeState(sender, message, _lastNow); }

        internal bool AcknowledgeState(ulong sender, BattleMessage message, double now)
        {
            Tick(now);
            if (Phase != BattleStartPhase.AwaitingStateAcks || !_controllers.Contains(sender) || message == null ||
                message.Type != BattleMessageType.StateAck || !Identity.Matches(message, _stateDigest) ||
                !_stateAcks.Add(sender)) return false;
            if (_stateAcks.Count == _controllers.Count) Phase = BattleStartPhase.Initialized;
            return true;
        }

        internal bool Cancel(string reason)
        {
            if (Phase == BattleStartPhase.Idle || Phase == BattleStartPhase.Cancelled || Phase == BattleStartPhase.Frozen) return false;
            FailureReason = reason ?? "Cancelled.";
            Phase = Committed ? BattleStartPhase.Frozen : BattleStartPhase.Cancelled;
            return true;
        }

        internal bool Fail(string reason) { return Cancel(reason ?? "Battle initialization failed."); }

        internal bool ParticipantLeft(ulong player)
        {
            if (!_controllers.Contains(player)) return false;
            return Fail("Controlling player disconnected.");
        }

        internal bool Tick(double now)
        {
            if (Phase == BattleStartPhase.Idle || Phase == BattleStartPhase.Cancelled ||
                Phase == BattleStartPhase.Frozen || Phase == BattleStartPhase.Initialized) return false;
            if (!Finite(now) || now < _lastNow) return Fail("Invalid battle initialization clock.");
            _lastNow = now;
            return now >= _deadline && Fail("Battle initialization timed out.");
        }

        private static bool Finite(double number) { return !double.IsNaN(number) && !double.IsInfinity(number); }
    }
}
