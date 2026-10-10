using System;
using System.IO;
using System.Linq;
using RuinaCoop;

internal static class ManifestChecks
{
    private static Action<bool, string> Check;

    internal static void Run(Action<bool, string> check)
    {
        Check = check;
        var fixture = Fixture();
        var bytes = BattleManifestCodec.Encode(fixture);
        BattleManifest parsed;
        Check(BattleManifestCodec.TryDecode(bytes, out parsed), "manifest round trip");
        Check(parsed.Librarians[0].Name == parsed.Librarians[1].Name && parsed.Librarians[0].ActorId != parsed.Librarians[1].ActorId,
            "same-name librarians retain independent actor identities");
        Check(parsed.Librarians[0].Cards.SequenceEqual(new[] { 1, 1, 2 }) && parsed.Librarians[1].Cards.SequenceEqual(new[] { 1, 2, 1 }),
            "repeated cards retain exact per-librarian deck order");
        Check(parsed.Enemies[0].Cards.SequenceEqual(new[] { 5, 5, 6 }), "enemy repeated cards retain order");
        Check(parsed.InvitationBooks.SequenceEqual(new[] { 20, 20 }), "duplicate invitation book quantities are retained");
        Check(bytes.SequenceEqual(BattleManifestCodec.Encode(parsed)), "manifest re-encoding is canonical");
        var changed = Fixture(); changed.Librarians[0].Cards.Clear(); changed.Librarians[0].Cards.AddRange(new[] { 1, 2, 1 });
        Check(!BattleProtocol.DigestEquals(BattleProtocol.ComputeDigest(bytes), BattleProtocol.ComputeDigest(BattleManifestCodec.Encode(changed))),
            "same card multiset with different order produces a different manifest digest");
        changed = Fixture(); changed.Librarians[1].ControllerId = 3;
        Check(!bytes.SequenceEqual(BattleManifestCodec.Encode(changed)), "controller identity is included in manifest bytes");
        for (var length = 0; length < bytes.Length; length++)
            Check(!BattleManifestCodec.TryDecode(bytes.Take(length).ToArray(), out parsed) && parsed == null, "manifest truncation rejected at " + length);
        Check(!BattleManifestCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out parsed), "manifest trailing bytes rejected");
        Check(!BattleManifestCodec.TryDecode(new byte[8193], out parsed), "manifest byte ceiling enforced");
        var bad = (byte[])bytes.Clone(); bad[0]++;
        Check(!BattleManifestCodec.TryDecode(bad, out parsed), "unknown manifest schema rejected");
        bad = (byte[])bytes.Clone(); bad[14] = 4;
        Check(!BattleManifestCodec.TryDecode(bad, out parsed), "oversized invitation count rejected on wire");
        bad = (byte[])bytes.Clone(); bad[23] = 3;
        Check(!BattleManifestCodec.TryDecode(bad, out parsed), "unsupported actor count rejected on wire");
        bad = (byte[])bytes.Clone(); bad[61] = 1; bad[62] = 1;
        Check(!BattleManifestCodec.TryDecode(bad, out parsed), "257-byte name declaration rejected on wire");
        bad = (byte[])bytes.Clone(); bad[63] = 0xff;
        Check(!BattleManifestCodec.TryDecode(bad, out parsed), "malformed UTF8 rejected on wire");

        foreach (var mutation in new Action<BattleManifest>[]
        {
            value => value.HostId = 0, value => value.StageId = 0, value => value.FloorId = 0, value => value.FloorId = 11,
            value => value.HostId = 3, value => value.Librarians.RemoveAt(1), value => value.Librarians.Add(value.Librarians[0]),
            value => value.Enemies.Clear(), value => value.Librarians[0] = null, value => value.Enemies[0] = null,
            value => value.Librarians[1].ActorId = 1, value => value.Librarians[1].UnitIdentity = value.Librarians[0].UnitIdentity,
            value => value.Librarians[1].ControllerId = value.Librarians[0].ControllerId,
            value => value.Librarians[1].RosterIndex = value.Librarians[0].RosterIndex, value => value.Librarians[1].RosterIndex = 5,
            value => value.Librarians[0].UnitIdentity = 0, value => value.Librarians[0].ControllerId = 0,
            value => value.Librarians[0].BookToken = 0, value => value.Librarians[0].BookId = 0,
            value => value.Librarians[1].BookToken = value.Librarians[0].BookToken,
            value => value.Enemies[0].ActorId = 1, value => value.Enemies[0].EnemyIdentity = 0,
            value => value.Enemies[0].EnemyId = 0, value => value.Enemies[0].BookId = 0,
            value => value.Librarians[0].Cards.Clear(), value => value.Enemies[0].Cards.Clear(),
            value => value.Librarians[0].Cards.Add(-1), value => value.Enemies[0].Cards.Add(0),
            value => value.Librarians[0].Passives.Add(100), value => value.Enemies[0].Passives.Add(200),
            value => value.Librarians[0].Passives.Add(0), value => value.Enemies[0].Passives.Add(-1),
            value => value.Librarians[0].Name = null, value => value.Enemies[0].Name = null,
            value => value.Librarians[0].Name = "a\nb", value => value.Enemies[0].Name = "a\0b",
            value => value.Librarians[0].Name = "\ud800", value => value.Enemies[0].Name = new string('中', 86),
            value => value.InvitationBooks.AddRange(new[] { 20, 20 }), value => value.InvitationBooks[0] = 0,
            value => value.Librarians[0].Cards.AddRange(Enumerable.Repeat(1, 7)),
            value => value.Enemies[0].Cards.AddRange(Enumerable.Repeat(1, 62)),
            value => value.Librarians[0].Passives.AddRange(Enumerable.Range(1000, 32)),
            value => value.Enemies[0].Passives.AddRange(Enumerable.Range(1000, 32))
        })
        {
            var invalid = Fixture(); mutation(invalid); string reason;
            Check(!BattleManifestCodec.Validate(invalid, out reason), "invalid manifest fields reject before native initialization");
            var rejected = false;
            try { BattleManifestCodec.Encode(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid manifest fields cannot encode");
        }
        changed = Fixture();
        changed.Enemies.Add(new BattleEnemyConfiguration { ActorId = 102, EnemyIdentity = changed.Enemies[0].EnemyIdentity, EnemyId = 1, BookId = 1 });
        changed.Enemies[1].Cards.Add(1); string invalidReason;
        Check(!BattleManifestCodec.Validate(changed, out invalidReason), "duplicate enemy identity rejected independently of actor ids");

        var maximum = Fixture(); maximum.InvitationBooks.Add(21);
        foreach (var actor in maximum.Librarians)
        {
            actor.Name = new string('x', 256); actor.Cards.Clear(); actor.Cards.AddRange(Enumerable.Repeat(1, 9));
            actor.Passives.Clear(); actor.Passives.AddRange(Enumerable.Range(1, 32));
        }
        maximum.Enemies.Clear();
        for (var i = 0; i < 5; i++)
        {
            var enemy = new BattleEnemyConfiguration { ActorId = (uint)(101 + i), EnemyIdentity = (ulong)(200 + i), EnemyId = 10, BookId = 20, Name = new string('x', 256) };
            enemy.Cards.AddRange(Enumerable.Repeat(1, 64)); enemy.Passives.AddRange(Enumerable.Range(1, 32)); maximum.Enemies.Add(enemy);
        }
        Check(BattleManifestCodec.TryDecode(BattleManifestCodec.Encode(maximum), out parsed) && parsed.Enemies.Count == 5 && parsed.Enemies[4].Cards.Count == 64,
            "all maximum nested bounds encode without truncation");
        maximum.Enemies.Add(maximum.Enemies[0]);
        Check(!BattleManifestCodec.Validate(maximum, out invalidReason), "six enemies rejected");
        var snapshot = SnapshotFixture();
        var fromPreparation = BattleManifestCodec.FromPreparation(snapshot, 1, new[] { 20, 20 });
        Check(fromPreparation.Librarians[0].UnitIdentity == 10 && fromPreparation.Librarians[1].ControllerId == 2 &&
            fromPreparation.Enemies[0].EnemyIdentity == 100, "manifest preserves explicit preparation actor and ownership identities");
        snapshot.UnitDecks[0].Cards[0] = 9;
        Check(fromPreparation.Librarians[0].Cards[0] == 1, "manifest owns an independent deck copy");
        foreach (var mutation in new Action<ProgressSnapshot>[]
        {
            value => value.Preparation.Waves.Add(new PreparationWaveEntry { WaveIndex = 1 }),
            value => value.UnitDecks[0].Fixed = true, value => value.UnitDecks[0].MultiDeck = true,
            value => value.UnitDecks[0].Display.Available = false, value => value.Preparation.Waves[0].Enemies[0].CardsVisible = false,
            value => value.Preparation.Waves[0].Enemies[0].Display.Available = false,
            value => value.ClaimOwners[1] = 1, value => value.Floors.Clear(), value => value.Floors[0].Units.Clear()
        })
        {
            snapshot = SnapshotFixture(); mutation(snapshot);
            var rejected = false;
            try { BattleManifestCodec.FromPreparation(snapshot, 1, new[] { 20 }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "incompatible preparation rejected with an explicit initialization reason");
        }
    }

    private static BattleManifest Fixture()
    {
        var result = new BattleManifest { HostId = 1, StageId = 10000, FloorId = 1 };
        result.InvitationBooks.AddRange(new[] { 20, 20 });
        for (var i = 0; i < 2; i++)
        {
            var actor = new BattleActorConfiguration { ActorId = (uint)(i + 1), RosterIndex = (byte)i, UnitIdentity = (ulong)(10 + i),
                ControllerId = (ulong)(1 + i), BookToken = (ulong)(30 + i), BookId = 1000 + i, BookInstanceId = i, Name = "同名" };
            actor.Cards.AddRange(i == 0 ? new[] { 1, 1, 2 } : new[] { 1, 2, 1 }); actor.Passives.Add(100); result.Librarians.Add(actor);
        }
        var enemy = new BattleEnemyConfiguration { ActorId = 101, EnemyIdentity = 100, EnemyId = 200, BookId = 300, Name = "同名" };
        enemy.Cards.AddRange(new[] { 5, 5, 6 }); enemy.Passives.Add(200); result.Enemies.Add(enemy);
        return result;
    }

    private static ProgressSnapshot SnapshotFixture()
    {
        var result = new ProgressSnapshot { SelectedStageId = 10000, SelectedFloorId = 1, ClaimRevision = 3, DeckRevision = 4,
            Preparation = new PreparationSnapshot { Available = true, Reason = PreparationReason.None, Phase = PreparationPhase.Editing,
                ContextId = 20, Revision = 3, StageId = 10000, FloorId = 1, MaxUnits = 2, CurrentWaveIndex = 0, ClaimRevision = 3, DeckRevision = 4 } };
        result.Preparation.Floors.Add(new PreparationFloorEntry { FloorId = 1, CanParticipate = true });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = 1 }; result.Floors.Add(floor);
        for (byte i = 0; i < 2; i++)
        {
            var deck = new ProgressSnapshot.UnitDeckEntry { UnitIdentity = (ulong)(10 + i), BookToken = (ulong)(30 + i), BookId = 1000 + i, BookInstanceId = i };
            deck.Cards.AddRange(new[] { 1, 1, 2 }); deck.Display.Available = true; deck.Display.MaxHp = 100; deck.Display.Break = 50;
            deck.Display.PassiveIds.Add(100); result.UnitDecks.Add(deck); result.ClaimOwners.Add((ulong)(i + 1)); floor.Units.Add("同名");
            result.Preparation.Participants.Add(new PreparationUnitEntry { UnitIndex = i, UnitIdentity = (ulong)(10 + i), CanParticipate = true, Participating = true });
            result.Preparation.Controllers.Add(new PreparationControllerEntry { PlayerId = (ulong)(i + 1), Connected = true, Ready = true });
        }
        var wave = new PreparationWaveEntry { WaveIndex = 0 }; result.Preparation.Waves.Add(wave);
        var enemy = new PreparationEnemyEntry { EnemyIdentity = 100, EnemyId = 200, BookId = 300, Name = "同名", CardsVisible = true };
        enemy.Cards.AddRange(new[] { 5, 5, 6 }); enemy.Display.Available = true; enemy.Display.MaxHp = 100; enemy.Display.Break = 50;
        enemy.Display.PassiveIds.Add(200); wave.Enemies.Add(enemy);
        return result;
    }
}
