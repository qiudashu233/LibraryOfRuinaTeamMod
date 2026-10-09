using System;
using System.IO;
using System.Linq;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private static void Main()
    {
        Protocol(); Codec(); Bounds(); State(); CrossSnapshot(); LifecycleChecks.Run(Check);
        Console.WriteLine("PASS preparation " + _checks + " checks");
    }
    private static void Check(bool condition, string name) { _checks++; if (!condition) throw new Exception(name); }
    private static byte[] Encode(PreparationSnapshot value)
    { using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) { PreparationMirror.WriteData(writer, value); return stream.ToArray(); } }
    private static bool Decode(byte[] bytes, out PreparationSnapshot value)
    { using (var reader = new BinaryReader(new MemoryStream(bytes, false))) { string reason; return PreparationMirror.TryReadData(reader, out value, out reason); } }
    private static void Bad(PreparationSnapshot value, string name) { string reason; Check(!PreparationMirror.Validate(value, out reason), name); }
    private static void Throws(Action action, string name)
    { try { action(); Check(false, name); } catch (ArgumentException) { Check(true, name); } }
    private static PreparationSnapshot Fixture()
    {
        var value = new PreparationSnapshot { Available = true, Reason = PreparationReason.None, Phase = PreparationPhase.Editing,
            ContextId = 7, StageId = 10001, FloorId = 1, MaxUnits = 2, CurrentWaveIndex = 0, Revision = 9, ClaimRevision = 3, DeckRevision = 5 };
        value.Floors.Add(new PreparationFloorEntry { FloorId = 1, CanParticipate = true });
        value.Floors.Add(new PreparationFloorEntry { FloorId = 2, Reason = PreparationUnitReason.Defeated });
        for (byte i = 0; i < 3; i++) value.Participants.Add(new PreparationUnitEntry
            { UnitIndex = i, UnitIdentity = (ulong)(10 + i), CanParticipate = i < 2, Participating = i < 2, Reason = i < 2 ? PreparationUnitReason.None : PreparationUnitReason.Unavailable });
        var wave = new PreparationWaveEntry { WaveIndex = 0 };
        var enemy = new PreparationEnemyEntry { EnemyIdentity = 100, EnemyId = 200, BookId = 300, Name = "测试敌方", CardsVisible = true };
        enemy.Cards.AddRange(new[] { 1, 1, 2 }); enemy.Display.Available = true; enemy.Display.MaxHp = 150; enemy.Display.Break = 75;
        enemy.Display.PassiveIds.AddRange(new[] { 30, 40 }); enemy.Display.AppearanceAvailable = true; enemy.Display.DefaultBookId = 300;
        enemy.Display.CharacterSkin = "EnemySkin"; enemy.Display.HairColor = 0x112233ff;
        wave.Enemies.Add(enemy); wave.Enemies.Add(new PreparationEnemyEntry { EnemyIdentity = 101, Unknown = true, Name = "???" }); value.Waves.Add(wave);
        return value;
    }
    private static void Protocol()
    {
        var request = new PreparationReadyRequest { RequestId = 7, Revision = 9, Ready = true };
        var bytes = PreparationProtocol.EncodeRequest(123, request); PreparationReadyRequest parsed;
        Check(bytes.Length == 22 && PreparationProtocol.TryDecodeRequest(bytes, 123, out parsed) && parsed.Ready && parsed.Revision == 9, "Ready request roundtrip");
        for (var i = 0; i < bytes.Length; i++) Check(!PreparationProtocol.TryDecodeRequest(bytes.Take(i).ToArray(), 123, out parsed), "Short ready request " + i);
        Check(!PreparationProtocol.TryDecodeRequest(bytes.Concat(new byte[] { 0 }).ToArray(), 123, out parsed), "Ready request tail");
        Check(!PreparationProtocol.TryDecodeRequest(bytes, 124, out parsed), "Ready request room");
        foreach (var offset in new[] { 0, 4, 13, 17, 21 }) { var bad = (byte[])bytes.Clone(); bad[offset] = offset == 21 ? (byte)2 : (byte)0; Check(!PreparationProtocol.TryDecodeRequest(bad, 123, out parsed), "Invalid ready header " + offset); }
        var reply = PreparationProtocol.EncodeReply(123, new PreparationReadyReply { RequestId = 7, Revision = 9, Result = PreparationReadyResultCode.Accepted });
        PreparationReadyReply ack;
        Check(PreparationProtocol.TryDecodeReply(reply, 123, out ack) && ack.Revision == 9, "Ready reply roundtrip");
        for (var i = 0; i < reply.Length; i++) Check(!PreparationProtocol.TryDecodeReply(reply.Take(i).ToArray(), 123, out ack), "Short ready reply " + i);
        var invalid = (byte[])reply.Clone(); invalid[17] = 255; Check(!PreparationProtocol.TryDecodeReply(invalid, 123, out ack), "Unknown ready result");
        Array.Clear(invalid, 18, 4); invalid[17] = 0; Check(!PreparationProtocol.TryDecodeReply(invalid, 123, out ack), "Accepted zero revision");
    }
    private static void Codec()
    {
        var fixture = Fixture(); var bytes = Encode(fixture); PreparationSnapshot parsed;
        Check(Decode(bytes, out parsed) && parsed.ContextId == 7 && parsed.Participants.Count == 3 && parsed.Waves[0].Enemies[0].Cards.SequenceEqual(new[] { 1, 1, 2 }), "Preparation roundtrip");
        Check(parsed.Waves[0].Enemies[1].Unknown && parsed.Waves[0].Enemies[1].BookId == 0 && !parsed.Waves[0].Enemies[1].Display.Available, "Unknown remains hidden");
        Check(parsed.Waves[0].Enemies[0].Display.CharacterSkin == "EnemySkin" && parsed.Waves[0].Enemies[0].Display.HairColor == 0x112233ff, "Actual appearance roundtrip");
        for (var i = 0; i < bytes.Length; i++) Check(!Decode(bytes.Take(i).ToArray(), out parsed), "Short preparation section " + i);
        var tailed = bytes.Concat(new byte[] { 0 }).ToArray(); tailed[0]++; Check(!Decode(tailed, out parsed), "Preparation internal tail");
        foreach (var offset in new[] { 2, 3, 4 }) { var bad = (byte[])bytes.Clone(); bad[offset] = 255; Check(!Decode(bad, out parsed), "Invalid preparation header " + offset); }
        var same = PreparationMirror.Clone(fixture); same.Revision++; same.Controllers.Add(new PreparationControllerEntry { PlayerId = 1, Connected = true });
        var canonical = PreparationMirror.EncodeContent(same); same.Controllers[0].Ready = true; same.Revision++;
        Check(canonical.SequenceEqual(PreparationMirror.EncodeContent(same)), "Canonical excludes revision and ready");
        same.Controllers[0].Connected = false; same.Controllers[0].Ready = false;
        Check(!canonical.SequenceEqual(PreparationMirror.EncodeContent(same)), "Canonical includes controller connection");
        same = Fixture(); same.ContextId++; Check(!PreparationMirror.EncodeContent(fixture).SequenceEqual(PreparationMirror.EncodeContent(same)), "Canonical includes reception context");
        same = Fixture(); same.Participants[2].Participating = true; Bad(same, "Cannot select unavailable unit");
        same = Fixture(); same.Participants[2].CanParticipate = true; same.Participants[2].Reason = 0; same.Participants[2].Participating = true; Bad(same, "Cannot exceed stage unit limit");
        same = Fixture(); same.Participants[2].UnitIdentity = 10; Bad(same, "Duplicate roster identity");
        same = Fixture(); same.Participants[0].UnitIndex = 1; Bad(same, "Unordered roster index");
        same = Fixture(); same.Floors.Add(same.Floors[0]); Bad(same, "Duplicate floor");
        same = Fixture(); same.CurrentWaveIndex = 1; Bad(same, "Invalid viewed wave");
        same = Fixture(); same.Waves[0].Enemies[1].BookId = 42; Bad(same, "Unknown key page leakage");
        same = Fixture(); same.Waves[0].Enemies[1].Display.MaxHp = 42; Bad(same, "Unknown stats leakage");
        same = Fixture(); same.Waves[0].Enemies[0].CardsVisible = false; Bad(same, "Hidden deck leakage");
        same = Fixture(); same.Waves[0].Enemies[0].Display.PassiveIds.Add(30); Bad(same, "Repeated actual passive identity");
        same = Fixture(); same.Waves[0].Enemies[0].Name = new string('中', 86); Bad(same, "UTF8 byte bound");
        same = Fixture(); same.Waves[0].Enemies[0].Name = "x\ud800"; Bad(same, "Invalid Unicode");
        same = Fixture(); same.Waves[0].Enemies[0].Name = "a\nb"; Bad(same, "Control text");
        same = Fixture(); same.Available = false; same.Reason = PreparationReason.CaptureFailed; Bad(same, "Unavailable cannot publish partial pool");
        Check(Decode(Encode(null), out parsed) && !parsed.Available && parsed.Phase == PreparationPhase.Selection, "Missing preparation compatibility");
    }
    private static void Bounds()
    {
        var value = Fixture(); PreparationSnapshot parsed;
        var bytes = Encode(value);
        foreach (var offset in new[] { 32, 39, 76, 78 })
        { var malformed = (byte[])bytes.Clone(); malformed[offset] = 255; Check(!Decode(malformed, out parsed), "Oversized nested count " + offset); }
        bytes = Encode(null); bytes[0] = 255; bytes[1] = 255; Check(!Decode(bytes, out parsed), "Section length exceeds hard bound");
        value.Waves[0].Enemies[0].Cards.Clear(); value.Waves[0].Enemies[0].Cards.AddRange(Enumerable.Repeat(1, 64));
        Check(Decode(Encode(value), out parsed) && parsed.Waves[0].Enemies[0].Cards.Count == 64, "64 enemy cards supported");
        value.Waves[0].Enemies[0].Cards.Add(1); Bad(value, "65 enemy cards rejected");
        value = Fixture(); value.Waves[0].Enemies[0].Name = new string('x', 256);
        Check(Decode(Encode(value), out parsed) && parsed.Waves[0].Enemies[0].Name.Length == 256, "256 byte text supported");
        value.Waves[0].Enemies[0].Name += "x"; Bad(value, "257 byte text rejected");
        value = Fixture(); value.Floors[0].CanParticipate = false; value.Floors[0].Reason = PreparationUnitReason.Defeated;
        Bad(value, "Unavailable floor cannot select participants");
        value = Fixture(); value.Waves.Clear(); value.CurrentWaveIndex = 0;
        ulong identity = 1000;
        for (byte waveIndex = 0; waveIndex < 4; waveIndex++)
        {
            var wave = new PreparationWaveEntry { WaveIndex = waveIndex };
            for (var i = 0; i < 32; i++) wave.Enemies.Add(new PreparationEnemyEntry { EnemyIdentity = identity++, Unknown = true });
            value.Waves.Add(wave);
        }
        Check(Decode(Encode(value), out parsed) && parsed.Waves.Sum(row => row.Enemies.Count) == 128, "128 bounded enemy slots supported");
        value.Waves.Add(new PreparationWaveEntry { WaveIndex = 4 }); value.Waves[4].Enemies.Add(new PreparationEnemyEntry { EnemyIdentity = identity++, Unknown = true });
        Bad(value, "129 enemies rejected");
        value.Waves.RemoveAt(4); value.Waves[0].Enemies.Add(new PreparationEnemyEntry { EnemyIdentity = identity++, Unknown = true });
        Bad(value, "33 enemies in one wave rejected");
        value.Waves[0].Enemies.RemoveAt(32);
        foreach (var enemy in value.Waves.SelectMany(wave => wave.Enemies))
        {
            enemy.Unknown = false; enemy.EnemyId = 1; enemy.BookId = 1; enemy.Name = new string('x', 256);
            enemy.CardsVisible = true; enemy.Cards.AddRange(Enumerable.Repeat(1, 64));
        }
        string reason; Check(PreparationMirror.Validate(value, out reason), "Large pool is semantically valid");
        Throws(() => Encode(value), "Large section fails intact instead of truncating");
        var state = new PreparationState();
        Throws(() => state.Preview(Fixture(), 1, 1, 0, new ulong[] { 1 }, new ulong[] { 1, 1, 1 }), "Invalid host cannot produce revision");
        Throws(() => state.Preview(Fixture(), 1, 1, 1, new ulong[] { 0 }, new ulong[] { 1, 1, 1 }), "Invalid connection identity rejected");
        Throws(() => state.Preview(Fixture(), 1, 1, 1, new ulong[] { 1 }, new ulong[0]), "Claims and roster count mismatch rejected");
    }
    private static void State()
    {
        var state = new PreparationState(); var captured = Fixture(); var owners = new ulong[] { 2, 0, 0 };
        var first = state.Preview(captured, 3, 5, 1, new ulong[] { 1, 2, 3 }, owners);
        Check(state.Current == null && first.Revision == 1 && first.Controllers.Count == 2, "Preview is not committed and observers excluded");
        captured.Participants[0].Participating = false;
        Check(first.Participants[0].Participating, "Capture detached from candidate");
        captured = Fixture(); Check(state.Commit(first), "Publish candidate commit");
        first.Participants[0].Participating = false; Check(state.Current.Participants[0].Participating, "Committed snapshot detached");
        Check(!state.Commit(first), "Candidate single commit");
        PreparationReadyResultCode result;
        Check(state.PreviewReady(2, 2, true, true, out result) == null && result == PreparationReadyResultCode.StaleRevision, "Stale ready revision");
        Check(state.PreviewReady(3, 1, true, true, out result) == null && result == PreparationReadyResultCode.Observer, "Observer cannot ready");
        Check(state.PreviewReady(2, 1, true, false, out result) == null && result == PreparationReadyResultCode.Disconnected, "Disconnected cannot ready");
        var ready = state.PreviewReady(2, 1, true, true, out result);
        Check(result == PreparationReadyResultCode.Accepted && !state.Current.Controllers.Single(row => row.PlayerId == 2).Ready, "Ready preview does not mutate published state");
        Check(state.Commit(ready) && state.Current.Revision == 1 && !state.AllControllersReady, "Ready commit same revision");
        ready = state.PreviewReady(1, 1, true, true, out result); Check(state.Commit(ready) && state.AllControllersReady && !state.CanStartBattle, "Host fallback ready but stage3.3 cannot start");
        var repeated = state.Preview(captured, 3, 5, 1, new ulong[] { 1, 2, 3 }, owners);
        Check(repeated.Revision == 1 && repeated.Controllers.All(row => row.Ready), "Unchanged captures retain ready");
        var changed = state.Preview(captured, 3, 6, 1, new ulong[] { 1, 2, 3 }, owners);
        Check(changed.Revision == 2 && changed.Controllers.All(row => !row.Ready) && state.AllControllersReady, "Equipment preview resets candidate ready only");
        Check(state.Commit(changed) && !state.AllControllersReady, "Equipment successful publish invalidates ready");
        Check(!state.Commit(repeated), "Old concurrent candidate rejected");
        changed = state.Preview(captured, 4, 6, 1, new ulong[] { 1, 2, 3 }, owners); Check(changed.Revision == 3, "Claims invalidate ready"); Check(state.Commit(changed), "Claim commit");
        changed = state.Preview(captured, 4, 6, 1, new ulong[] { 1, 3 }, owners); Check(changed.Revision == 4 && !changed.Controllers.Single(row => row.PlayerId == 2).Connected, "Controller disconnect advances preparation"); Check(state.Commit(changed), "Disconnect commit");
        captured.ContextId = 8; changed = state.Preview(captured, 4, 6, 1, new ulong[] { 1, 3 }, owners); Check(changed.Revision == 5, "Same stage new native context advances revision");
        var outsider = new PreparationState(); Check(!outsider.Commit(changed), "Candidate cannot cross state instances");
        state.Reset(); Check(!state.Commit(changed) && state.Current == null, "Reset invalidates pending candidate");
        var selection = state.Preview(null, 0, 0, 1, new ulong[] { 1 }, new ulong[0]); Check(selection.Phase == PreparationPhase.Selection && selection.ContextId == 0 && state.Commit(selection), "Null captures selection");
        Check(state.PreviewReady(1, 1, true, true, out result) == null && result == PreparationReadyResultCode.NotReady, "Selection cannot ready");
    }
    private static void CrossSnapshot()
    {
        var preparation = Fixture(); var snapshot = new ProgressSnapshot { Preparation = preparation, SelectedStageId = preparation.StageId, SelectedFloorId = 1, ClaimRevision = 3, DeckRevision = 5 };
        foreach (var unit in preparation.Participants) { snapshot.UnitDecks.Add(new ProgressSnapshot.UnitDeckEntry { UnitIdentity = unit.UnitIdentity }); snapshot.ClaimOwners.Add(0); }
        string reason;
        Check(PreparationMirror.Validate(snapshot, out reason) && PreparationMirror.CanUseUnit(snapshot, 0) && !PreparationMirror.CanUseUnit(snapshot, 2), "Cross snapshot participation permissions");
        snapshot.DeckRevision++; Check(!PreparationMirror.Validate(snapshot, out reason) && !PreparationMirror.CanUseUnit(snapshot, 0), "Cross snapshot dependency mismatch blocks edit"); snapshot.DeckRevision--;
        snapshot.ClaimOwners[2] = 1; Check(!PreparationMirror.Validate(snapshot, out reason), "Unselected librarian cannot retain claim"); snapshot.ClaimOwners[2] = 0;
        snapshot.UnitDecks[0].UnitIdentity++; Check(!PreparationMirror.Validate(snapshot, out reason), "Cross snapshot roster mismatch");
        snapshot.UnitDecks[0].UnitIdentity--; preparation.Revision = 0; Check(!PreparationMirror.Validate(snapshot, out reason), "Published preparation requires revision");
        snapshot.Preparation = null; Check(PreparationMirror.Validate(snapshot, out reason) && PreparationMirror.CanUseUnit(snapshot, 0), "Existing fixture compatibility");
    }
}
