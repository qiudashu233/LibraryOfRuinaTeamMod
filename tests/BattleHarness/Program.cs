using System;
using System.Linq;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private static readonly byte[] Manifest = { 1, 3, 7, 11 };
    private static readonly byte[] State = { 2, 4, 8, 16 };
    private static readonly byte[] ManifestDigest = BattleProtocol.ComputeDigest(Manifest);
    private static readonly byte[] StateDigest = BattleProtocol.ComputeDigest(State);

    private static void Main()
    {
        ProtocolChecks();
        BeginChecks();
        OfferChecks();
        CommitChecks();
        StateChecks();
        FailureChecks();
        ManifestChecks.Run(Check);
        InitialStateChecks.Run(Check);
        Console.WriteLine("BattleHarness passed " + _checks + " checks (pure envelope, manifest, first-act state and host initialization barrier; no Unity or Steam execution).");
    }

    private static void Check(bool condition, string description)
    {
        _checks++;
        if (!condition) throw new Exception("Check " + _checks + " failed: " + description);
    }

    private static void RejectEncode(BattleMessage message, string description)
    {
        var rejected = false;
        try { BattleProtocol.Encode(message); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, description);
    }

    private static BattleMessage Message(BattleMessageType type, byte[] payload = null)
    {
        return new BattleMessage
        {
            Type = type, RoomId = 10, BattleSessionId = 100, PreparationContext = 20,
            PreparationRevision = 3, Payload = payload,
            Digest = type == BattleMessageType.InitialState || type == BattleMessageType.StateAck || type == BattleMessageType.Initialized
                ? (byte[])StateDigest.Clone() : (byte[])ManifestDigest.Clone()
        };
    }

    private static bool Decode(byte[] packet, ulong room = 10)
    {
        BattleMessage message;
        var accepted = BattleProtocol.TryDecode(packet, room, out message);
        Check(accepted ? message != null : message == null, "decoder publishes output only on success");
        return accepted;
    }

    private static void ProtocolChecks()
    {
        Check(!BattleProtocol.IsBattlePacket(null), "null is not a battle packet");
        Check(!BattleProtocol.IsBattlePacket(new byte[3]), "partial magic is not recognized");
        foreach (BattleMessageType type in Enum.GetValues(typeof(BattleMessageType)))
        {
            var payload = type == BattleMessageType.Offer ? Manifest : type == BattleMessageType.InitialState ? State : null;
            var encoded = BattleProtocol.Encode(Message(type, payload));
            Check(encoded.Length == BattleProtocol.HeaderBytes + (payload == null ? 0 : payload.Length), "exact envelope size for " + type);
            Check(BattleProtocol.IsBattlePacket(encoded), "battle magic recognized for " + type);
            Check(Decode(encoded), "round trip for " + type);
            Check(!Decode(encoded, 11), "wrong room rejected for " + type);
        }
        var offer = BattleProtocol.Encode(Message(BattleMessageType.Offer, Manifest));
        for (var length = 0; length < BattleProtocol.HeaderBytes; length++)
            Check(!Decode(offer.Take(length).ToArray()), "truncated header rejected at " + length);
        for (var length = BattleProtocol.HeaderBytes; length < offer.Length; length++)
            Check(!Decode(offer.Take(length).ToArray()), "truncated payload rejected at " + length);
        Check(!Decode(offer.Concat(new byte[] { 0 }).ToArray()), "trailing bytes rejected");
        Check(!Decode(null), "null packet rejected");
        Check(!Decode(offer, 0), "zero expected room rejected");
        Check(!Decode(new byte[BattleProtocol.MaxPacketBytes + 1]), "oversize packet rejected");
        var bad = (byte[])offer.Clone(); bad[4] = 2;
        Check(BattleProtocol.IsBattlePacket(bad) && !Decode(bad), "bad wire version remains identifiable and cannot decode");
        bad = (byte[])offer.Clone(); bad[5] = 0;
        Check(!Decode(bad), "zero message type rejected");
        bad[5] = 10; Check(!Decode(bad), "unknown message type rejected");
        foreach (var range in new[] { Tuple.Create(6, 8), Tuple.Create(14, 8), Tuple.Create(22, 8), Tuple.Create(30, 4) })
        {
            bad = (byte[])offer.Clone(); Array.Clear(bad, range.Item1, range.Item2);
            Check(!Decode(bad), "zero identity field rejected at " + range.Item1);
        }
        bad = (byte[])offer.Clone(); bad[34] ^= 128;
        Check(!Decode(bad), "altered payload digest rejected");
        bad = (byte[])offer.Clone(); bad[bad.Length - 1] ^= 1;
        Check(!Decode(bad), "altered payload rejected");
        foreach (var invalidLength in new[] { -1, int.MinValue, int.MaxValue, BattleProtocol.MaxPayloadBytes + 1, 0 })
        {
            bad = (byte[])offer.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(invalidLength), 0, bad, 66, 4);
            Check(!Decode(bad), "invalid payload length rejected: " + invalidLength);
        }
        foreach (BattleMessageType type in Enum.GetValues(typeof(BattleMessageType)))
        {
            if (type == BattleMessageType.Offer || type == BattleMessageType.InitialState) continue;
            RejectEncode(Message(type, Manifest), "nonempty acknowledgement/control payload rejected: " + type);
            bad = (byte[])offer.Clone(); bad[5] = (byte)type;
            Check(!Decode(bad), "wire acknowledgement/control payload rejected: " + type);
        }
        RejectEncode(null, "null message cannot encode");
        RejectEncode(Message(BattleMessageType.Offer), "offer requires actual manifest bytes");
        RejectEncode(Message(BattleMessageType.InitialState), "state requires actual state bytes");
        var invalid = Message(BattleMessageType.Offer, new byte[BattleProtocol.MaxPayloadBytes + 1]);
        RejectEncode(invalid, "oversize payload cannot encode");
        invalid = Message(BattleMessageType.Offer, Manifest); invalid.Digest = new byte[31];
        RejectEncode(invalid, "short digest cannot encode");
        invalid = Message(BattleMessageType.Offer, Manifest); invalid.Digest = StateDigest;
        RejectEncode(invalid, "digest must hash the encoded payload");
        invalid = Message(BattleMessageType.OfferAck); invalid.RoomId = 0;
        RejectEncode(invalid, "zero room cannot encode");
        var maximum = new byte[BattleProtocol.MaxPayloadBytes]; maximum[maximum.Length - 1] = 1;
        var maximumMessage = Message(BattleMessageType.InitialState, maximum);
        maximumMessage.Digest = BattleProtocol.ComputeDigest(maximum);
        Check(Decode(BattleProtocol.Encode(maximumMessage)), "exact maximum payload round trips");
        Check(!BattleProtocol.DigestEquals(null, ManifestDigest), "null digest never matches");
        Check(!BattleProtocol.DigestEquals(new byte[33], ManifestDigest), "long digest never matches");
        var source = (byte[])Manifest.Clone(); var sourceMessage = Message(BattleMessageType.Offer, source);
        var copiedPacket = BattleProtocol.Encode(sourceMessage); source[0]++;
        Check(Decode(copiedPacket), "encoded packet owns payload bytes");
        BattleMessage decoded;
        Check(BattleProtocol.TryDecode(copiedPacket, 10, out decoded), "decode copy setup");
        copiedPacket[copiedPacket.Length - 1]++;
        Check(BattleProtocol.DigestEquals(decoded.Digest, BattleProtocol.ComputeDigest(decoded.Payload)), "decoded packet owns payload bytes");
        var random = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            bad = (byte[])offer.Clone(); bad[70 + random.Next(Manifest.Length)] ^= (byte)random.Next(1, 256);
            Check(!Decode(bad), "corrupt payload is rejected without exceptions");
        }
    }

    private static BattleStartCoordinator Begin(bool twoPlayers = true, ulong battle = 100)
    {
        var coordinator = new BattleStartCoordinator();
        Check(coordinator.Begin(10, battle, 20, 3, ManifestDigest, 1, twoPlayers ? new ulong[] { 1, 2 } : new ulong[] { 1 }, 0, 10), "valid begin");
        return coordinator;
    }

    private static void BeginChecks()
    {
        var coordinator = new BattleStartCoordinator();
        Check(coordinator.Phase == BattleStartPhase.Idle && !coordinator.Committed, "initial idle state");
        Check(!coordinator.Begin(0, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, 0), "zero room rejected");
        Check(!coordinator.Begin(10, 0, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, 0), "zero battle rejected");
        Check(!coordinator.Begin(10, 100, 0, 3, ManifestDigest, 1, new ulong[] { 1 }, 0), "zero context rejected");
        Check(!coordinator.Begin(10, 100, 20, 0, ManifestDigest, 1, new ulong[] { 1 }, 0), "zero revision rejected");
        Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 0, new ulong[] { 1 }, 0), "zero host rejected");
        Check(!coordinator.Begin(10, 100, 20, 3, new byte[31], 1, new ulong[] { 1 }, 0), "invalid digest size rejected");
        foreach (var controllers in new[] { new ulong[0], new ulong[] { 0, 1 }, new ulong[] { 1, 1 }, new ulong[] { 2 }, new ulong[] { 1, 2, 3 } })
            Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, controllers, 0), "invalid controllers rejected");
        foreach (var timeout in new[] { 0, -1, 121, double.NaN, double.PositiveInfinity })
            Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, 0, timeout), "invalid timeout rejected");
        Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, -1), "negative clock rejected");
        Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, double.NaN), "NaN clock rejected");
        Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, double.MaxValue), "unrepresentable deadline rejected");
        coordinator = Begin(false);
        Check(coordinator.CanCommit, "host-only run immediately reaches final validation barrier");
        Check(!coordinator.Begin(10, 101, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, 0), "active run cannot be overwritten");
        var digest = coordinator.Identity.ManifestDigest; digest[0]++;
        Check(BattleProtocol.DigestEquals(coordinator.Identity.ManifestDigest, ManifestDigest), "identity digest getter does not expose mutable state");
        var original = (byte[])ManifestDigest.Clone(); coordinator.Cancel("restart");
        Check(coordinator.Begin(10, 101, 20, 3, original, 1, new ulong[] { 1 }, 0), "cancelled coordinator reusable with new battle identity");
        original[0]++;
        Check(BattleProtocol.DigestEquals(coordinator.Identity.ManifestDigest, ManifestDigest), "begin takes ownership of digest by copy");
    }

    private static void OfferChecks()
    {
        var coordinator = Begin();
        Check(coordinator.Phase == BattleStartPhase.Offering, "guest offer acknowledgement pending");
        Check(!coordinator.TryCommit(() => true, 1), "cannot commit before all controlling players acknowledge");
        Check(!coordinator.PublishInitialState(StateDigest, 1), "cannot publish state before commit");
        Check(!coordinator.AcknowledgeOffer(3, Message(BattleMessageType.OfferAck), 1), "observer acknowledgement does not count");
        Check(!coordinator.AcknowledgeOffer(1, Message(BattleMessageType.OfferAck), 1), "duplicate local host acknowledgement does not count");
        Check(!coordinator.AcknowledgeOffer(2, Message(BattleMessageType.StateAck), 1), "wrong acknowledgement type rejected");
        foreach (var change in new Action<BattleMessage>[]
        {
            message => message.RoomId++, message => message.BattleSessionId++, message => message.PreparationContext++,
            message => message.PreparationRevision++, message => message.Digest = StateDigest, message => message.Payload = new byte[] { 1 }
        })
        {
            var ack = Message(BattleMessageType.OfferAck); change(ack);
            Check(!coordinator.AcknowledgeOffer(2, ack, 1), "wrong offer acknowledgement binding rejected");
        }
        Check(coordinator.AcknowledgeOffer(2, Message(BattleMessageType.OfferAck), 2), "matching guest acknowledgement accepted");
        Check(coordinator.CanCommit && !coordinator.Committed, "all acknowledgements only make transaction commit eligible");
        Check(!coordinator.AcknowledgeOffer(2, Message(BattleMessageType.Finished), 2), "finished is not an offer confirmation");
        Check(!coordinator.AcknowledgeOffer(2, Message(BattleMessageType.OfferAck), 3), "duplicate final offer acknowledgement rejected");
        coordinator.Cancel("operator cancelled");
        Check(coordinator.CanReturnToPreparation, "before-commit cancellation may restore preparation");
        Check(!coordinator.Begin(10, 100, 20, 3, ManifestDigest, 1, new ulong[] { 1, 2 }, 3), "cancelled battle id cannot be reused");
        Check(coordinator.Begin(10, 101, 21, 4, ManifestDigest, 1, new ulong[] { 1, 2 }, 3), "new reception may start a new offer");
        Check(!coordinator.AcknowledgeOffer(2, Message(BattleMessageType.OfferAck), 4), "late old acknowledgement cannot advance a new battle");
        Check(coordinator.Phase == BattleStartPhase.Offering, "old acknowledgement leaves new offer pending");
    }

    private static void CommitChecks()
    {
        var coordinator = Begin(false);
        Check(!coordinator.TryCommit(() => false, 1), "final configuration disagreement prevents commit");
        Check(coordinator.CanReturnToPreparation && !coordinator.Committed, "failed final validation cancels before commitment");
        coordinator = Begin(false);
        Check(!coordinator.TryCommit(() => { throw new InvalidOperationException(); }, 1), "validation exception prevents commit");
        Check(coordinator.CanReturnToPreparation && coordinator.FailureReason.Contains("InvalidOperationException"), "validation exception produces recoverable reason");
        coordinator = Begin(false);
        Check(!coordinator.TryCommit(null, 1) && !coordinator.Committed, "missing validation cannot commit");
        Check(!coordinator.TryCommit(() => { coordinator.Cancel("during validation"); return true; }, 2), "validation callback cannot commit a cancelled transaction");
        coordinator = Begin(false);
        Check(!coordinator.TryCommit(() =>
        {
            coordinator.Cancel("replace");
            coordinator.Begin(10, 101, 21, 4, ManifestDigest, 1, new ulong[] { 1 }, 2, 10);
            return true;
        }, 2), "validation callback cannot commit a replacement transaction");
        Check(!coordinator.Committed && coordinator.Identity.BattleSessionId == 101, "replacement remains uncommitted");
        coordinator = Begin(false);
        Check(coordinator.TryCommit(() => true, 1), "final configuration confirmation commits");
        Check(coordinator.Committed && coordinator.Phase == BattleStartPhase.Committed, "commit is an irreversible barrier");
        Check(!coordinator.TryCommit(() => true, 2), "duplicate commit rejected");
        Check(coordinator.Cancel("late cancellation") && coordinator.Frozen && !coordinator.CanReturnToPreparation, "post-commit cancellation freezes instead of returning to preparation");
        Check(!coordinator.Begin(10, 101, 20, 3, ManifestDigest, 1, new ulong[] { 1 }, 2), "frozen committed coordinator cannot start another run");
    }

    private static BattleStartCoordinator CommittedCoordinator()
    {
        var coordinator = Begin();
        Check(coordinator.AcknowledgeOffer(2, Message(BattleMessageType.OfferAck), 1), "state setup offer ack");
        Check(coordinator.TryCommit(() => true, 2), "state setup commit");
        return coordinator;
    }

    private static void StateChecks()
    {
        var coordinator = CommittedCoordinator();
        Check(!coordinator.PublishInitialState(new byte[31], 3), "malformed state digest cannot begin state barrier");
        var stateDigest = (byte[])StateDigest.Clone();
        Check(coordinator.PublishInitialState(stateDigest, 3), "publish state begins independent confirmation barrier");
        stateDigest[0]++;
        Check(!coordinator.PublishInitialState(StateDigest, 4), "published initial state cannot be replaced mid barrier");
        Check(!coordinator.AcknowledgeState(3, Message(BattleMessageType.StateAck), 4), "spectator state acknowledgement is not a controller confirmation");
        var ack = Message(BattleMessageType.StateAck); ack.Digest = ManifestDigest;
        Check(!coordinator.AcknowledgeState(2, ack, 4), "manifest digest is not a state digest");
        ack = Message(BattleMessageType.StateAck); ack.PreparationContext++;
        Check(!coordinator.AcknowledgeState(2, ack, 4), "state acknowledgement binds reception identity");
        Check(!coordinator.AcknowledgeState(2, Message(BattleMessageType.OfferAck), 4), "offer acknowledgement cannot confirm first act");
        Check(coordinator.AcknowledgeState(2, Message(BattleMessageType.StateAck), 5), "matching guest state digest accepted");
        Check(coordinator.Phase == BattleStartPhase.AwaitingStateAcks, "guest alone cannot initialize without host state confirmation");
        Check(!coordinator.AcknowledgeState(2, Message(BattleMessageType.StateAck), 6), "duplicate state confirmation does not count twice");
        Check(coordinator.AcknowledgeState(1, Message(BattleMessageType.StateAck), 6), "host confirms authoritative state");
        Check(coordinator.Phase == BattleStartPhase.Initialized && coordinator.Committed, "all controllers initialize first act");
        Check(!coordinator.AcknowledgeState(1, Message(BattleMessageType.Finished), 6) && coordinator.Committed, "finished cannot roll back coordinator commitment");
        Check(!coordinator.Tick(1000), "initialization timer stops after first-act confirmation");
        Check(!coordinator.ParticipantLeft(3), "spectator departure does not freeze initialized battle");
        Check(coordinator.ParticipantLeft(2) && coordinator.Frozen, "controller departure after initialization freezes battle");
        Check(!coordinator.AcknowledgeState(1, Message(BattleMessageType.StateAck), 1001), "old state acknowledgement cannot unfreeze battle");
        coordinator = Begin(false);
        Check(coordinator.TryCommit(() => true, 1) && coordinator.PublishInitialState(StateDigest, 2), "solo host state setup");
        Check(coordinator.AcknowledgeState(1, Message(BattleMessageType.StateAck), 3) && coordinator.Phase == BattleStartPhase.Initialized, "one-controller battle needs exactly host state confirmation");
    }

    private static void FailureChecks()
    {
        var coordinator = Begin();
        Check(!coordinator.Tick(9.999), "offer remains live before deadline");
        Check(!coordinator.AcknowledgeOffer(2, Message(BattleMessageType.OfferAck), 10), "acknowledgement at deadline cannot outrun timeout");
        Check(coordinator.CanReturnToPreparation && !coordinator.Committed, "offer timeout is recoverable");
        coordinator = Begin(false);
        Check(!coordinator.TryCommit(() => true, 10), "ready-to-commit stage still expires");
        Check(coordinator.CanReturnToPreparation, "precommit timeout can return to preparation");
        coordinator = Begin();
        Check(!coordinator.ParticipantLeft(9) && coordinator.Phase == BattleStartPhase.Offering, "observer departure is ignored while offering");
        Check(coordinator.ParticipantLeft(2) && coordinator.CanReturnToPreparation, "controller disconnect before commit cancels");
        coordinator = Begin(false);
        Check(coordinator.TryCommit(() => true, 1), "loading timeout setup");
        Check(!coordinator.Tick(10.999) && coordinator.Tick(11) && coordinator.Frozen, "loading timeout after commit freezes");
        coordinator = CommittedCoordinator();
        Check(coordinator.ParticipantLeft(1) && coordinator.Frozen, "host departure after commit freezes");
        coordinator = CommittedCoordinator();
        Check(coordinator.PublishInitialState(StateDigest, 3), "state timeout setup");
        Check(coordinator.AcknowledgeState(1, Message(BattleMessageType.StateAck), 4), "state timeout partial confirmation");
        Check(!coordinator.AcknowledgeState(2, Message(BattleMessageType.StateAck), 13), "last state acknowledgement at deadline cannot bypass timeout");
        Check(coordinator.Frozen && coordinator.Committed && !coordinator.CanReturnToPreparation, "state timeout remains committed and frozen");
        Check(!coordinator.Cancel("duplicate") && !coordinator.Fail("duplicate"), "frozen terminal state is idempotent");
        coordinator = Begin();
        Check(coordinator.Fail("room left") && coordinator.CanReturnToPreparation, "explicit room leave before commit cancels");
        coordinator = CommittedCoordinator();
        Check(coordinator.Fail("room left") && coordinator.Frozen, "explicit room leave after commit freezes");
        coordinator = Begin(); coordinator.Tick(2);
        Check(coordinator.Tick(1) && coordinator.CanReturnToPreparation, "backward clock cannot extend offer timeout");
        coordinator = CommittedCoordinator();
        Check(coordinator.Tick(double.NaN) && coordinator.Frozen, "invalid clock after commit freezes");
        coordinator = new BattleStartCoordinator();
        Check(!coordinator.Cancel("idle") && !coordinator.ParticipantLeft(1), "idle coordinator ignores cancellation and departures");
    }
}
