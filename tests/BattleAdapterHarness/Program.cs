using System;
using System.Linq;
using LOR_DiceSystem;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private static void Main()
    {
        ResetXml();
        var xmlOrder = Fixture(false);
        ExpectValid(xmlOrder, "original XML enemy deck order");
        var snapshot = SnapshotFixture();
        var preparedOrder = BattleManifestCodec.FromPreparation(snapshot, 1, new[] { 200001 });
        Check(preparedOrder.Enemies.All(actor => actor.Cards.SequenceEqual(new[] { 1, 1, 2, 2, 3, 3 })),
            "actual preparation shape preserves cost/ID-sorted enemy deck order");
        ExpectValid(preparedOrder, "preparation sorted enemy deck [1,1,2,2,3,3] must be accepted by the production adapter");
        Check(!BattleProtocol.DigestEquals(BattleProtocol.ComputeDigest(BattleManifestCodec.Encode(xmlOrder)),
            BattleProtocol.ComputeDigest(BattleManifestCodec.Encode(preparedOrder))), "accepted multiset comparison does not erase manifest wire ordering");

        var random = new Random(173);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var permuted = Fixture();
            foreach (var actor in permuted.Enemies)
            {
                for (var last = actor.Cards.Count - 1; last > 0; last--)
                { var index = random.Next(last + 1); var previous = actor.Cards[last]; actor.Cards[last] = actor.Cards[index]; actor.Cards[index] = previous; }
            }
            ExpectValid(permuted, "same physical enemy card quantities after ordering permutation " + attempt);
        }
        foreach (var cards in new[]
        {
            new[] { 1, 1, 1, 2, 2, 3 }, new[] { 1, 1, 2, 2, 3, 4 }, new[] { 1, 1, 2, 2, 3 },
            new[] { 1, 1, 2, 2, 3, 3, 3 }, new[] { 1, 2, 3 }, new[] { 1, 1, 2, 2, 3, 0 }
        })
        {
            var invalid = Fixture(); invalid.Enemies[0].Cards.Clear(); invalid.Enemies[0].Cards.AddRange(cards);
            ExpectInvalid(invalid, "enemy card multiplicity/content/count differs from supported XML deck");
        }
        foreach (var mutation in new Action<BattleManifest>[]
        {
            value => value.StageId = 5, value => value.Enemies.RemoveAt(1),
            value => value.Librarians[0].Cards.RemoveAt(0), value => value.Librarians[0].Cards.Add(1),
            value => value.Librarians[0].Cards[0] = 6, value => value.Librarians[0].Passives.Add(1),
            value => value.Enemies[0].Passives.Add(1), value => value.Enemies[0].EnemyId++,
            value => value.Enemies[1].BookId++, value => value.Librarians[0].ControllerId = 2,
            value => value.Librarians[1].UnitIdentity = value.Librarians[0].UnitIdentity,
            value => value.Librarians[1].BookToken = value.Librarians[0].BookToken,
            value => value.Enemies[1].EnemyIdentity = value.Enemies[0].EnemyIdentity
        })
        { ResetXml(); var invalid = Fixture(); mutation(invalid); ExpectInvalid(invalid, "invalid actor/card/passive/stage identity"); }

        foreach (var bookId in Enumerable.Range(1, 20).Where(id => id != 6 && id != 8).Concat(new[] { 200001, 200002, 200003 }))
        {
            ResetXml(); var valid = Fixture(); valid.Librarians[0].BookId = bookId;
            ExpectValid(valid, "ordinary passive-free supported key page " + bookId);
        }
        foreach (var bookId in new[] { 0, 6, 8, 21, 200004, 999999 })
        { ResetXml(); var invalid = Fixture(); invalid.Librarians[0].BookId = bookId; ExpectInvalid(invalid, "excluded/unknown core book " + bookId); }

        foreach (var mutation in new Action[]
        {
            () => BookXmlList.Instance.Books.Remove(200001),
            () => BookXmlList.Instance.Books[200001].id.packageId = "mod-package",
            () => BookXmlList.Instance.Books[200001].id.id = 200002,
            () => BookXmlList.Instance.Books[200001].EquipEffect.PassiveList.Add(1),
            () => BookXmlList.Instance.Books[200001].EquipEffect.OnlyCard.Add(1),
            () => BookXmlList.Instance.Books[200001].EquipEffect.CardList.Add(1),
            () => StageClassInfoList.Instance.Stages.Remove(3),
            () => StageClassInfoList.Instance.Stages[3].id.packageId = "mod-package",
            () => StageClassInfoList.Instance.Stages[3].id.id = 4,
            () => StageClassInfoList.Instance.Stages[3].waveList.Add(new StageWaveInfo()),
            () => StageClassInfoList.Instance.Stages[3].waveList[0].enemyUnitIdList.RemoveAt(0),
            () => StageClassInfoList.Instance.Stages[3].waveList[0].enemyUnitIdList[0] = new LorId(1004),
            () => StageClassInfoList.Instance.Stages[3].waveList[0].enemyUnitIdList[0].packageId = "mod-package",
            () => StageClassInfoList.Instance.Stages[3].waveList[0].aggroScript = "custom-aggro",
            () => StageClassInfoList.Instance.Stages[3].waveList[0].managerScript = "custom-manager",
            () => ItemXmlDataList.instance.Cards.Remove(3),
            () => ItemXmlDataList.instance.Cards[3].id.id = 4,
            () => ItemXmlDataList.instance.Cards[3].id.packageId = "mod-package",
            () => ItemXmlDataList.instance.Cards[3].Script = "custom-card",
            () => ItemXmlDataList.instance.Cards[3].PriorityScript = "custom-priority",
            () => ItemXmlDataList.instance.Cards[3].SpecialEffect = "custom-effect",
            () => ItemXmlDataList.instance.Cards[3].SkinChange = "custom-skin",
            () => ItemXmlDataList.instance.Cards[3].MapChange = "custom-map",
            () => ItemXmlDataList.instance.Cards[3].DiceBehaviourList[0].Script = "custom-die",
            () => ItemXmlDataList.instance.Cards[3].DiceBehaviourList[0].ActionScript = "custom-die-action",
            () => ItemXmlDataList.instance.Cards[5].Script = "unsupported-unused-basic-card"
        })
        { ResetXml(); mutation(); ExpectInvalid(Fixture(), "unsupported or mismatched XML catalogue dependency"); }
        Check(ForbiddenRuntime.Calls == 0, "all checks executed production ValidateManifest without touching runtime/game/Steam/save APIs");
        Console.WriteLine("PASS BattleAdapterHarness " + _checks + " checks; actual production ValidateManifest and FromPreparation; XML dictionaries only, no Unity/Steam/native model/save execution.");
    }

    private static void Check(bool condition, string name)
    { _checks++; if (!condition) throw new InvalidOperationException("CHECK " + _checks + " FAILED: " + name); }

    private static void ExpectValid(BattleManifest value, string name)
    {
        var before = BattleManifestCodec.Encode(value); string reason;
        Check(NativeBattleStateAdapter.ValidateManifest(value, out reason), name + "; rejected with: " + reason);
        Check(reason == null, name + " clears the failure reason");
        Check(before.SequenceEqual(BattleManifestCodec.Encode(value)), name + " does not mutate manifest order or contents");
    }
    private static void ExpectInvalid(BattleManifest value, string name)
    {
        string reason;
        Check(!NativeBattleStateAdapter.ValidateManifest(value, out reason), name + " must fail closed");
        Check(!string.IsNullOrEmpty(reason), name + " includes a failure reason");
    }
    private static void ResetXml()
    {
        BookXmlList.Instance.Books.Clear(); StageClassInfoList.Instance.Stages.Clear(); ItemXmlDataList.instance.Cards.Clear();
        foreach (var id in Enumerable.Range(1, 20).Concat(new[] { 200001, 200002, 200003 }))
            BookXmlList.Instance.Books[id] = new BookXmlInfo { id = new LorId(id) };
        var stage = new StageClassInfo { id = new LorId(3) }; var wave = new StageWaveInfo();
        wave.enemyUnitIdList.AddRange(new[] { new LorId(1003), new LorId(1004) }); stage.waveList.Add(wave); StageClassInfoList.Instance.Stages[3] = stage;
        for (var id = 1; id <= 5; id++)
        { var card = new DiceCardXmlInfo { id = new LorId(id) }; card.DiceBehaviourList.Add(new DiceBehaviour()); ItemXmlDataList.instance.Cards[id] = card; }
    }
    private static BattleManifest Fixture(bool sorted = true)
    {
        var result = new BattleManifest { HostId = 1, StageId = 3, FloorId = 10 }; result.InvitationBooks.Add(200001);
        for (var index = 0; index < 2; index++)
        {
            var librarian = new BattleActorConfiguration { ActorId = (uint)(index + 1), RosterIndex = (byte)index,
                UnitIdentity = (ulong)(10 + index), ControllerId = (ulong)(index + 1), BookToken = (ulong)(30 + index),
                BookId = 200001 + index, BookInstanceId = index, Name = index == 0 ? "Host" : "Guest" };
            librarian.Cards.AddRange(new[] { 1, 1, 1, 2, 2, 2, 3, 3, 3 }); result.Librarians.Add(librarian);
            var enemy = new BattleEnemyConfiguration { ActorId = (uint)(101 + index), EnemyIdentity = (ulong)(257 + index),
                EnemyId = 1003 + index, BookId = 101003 + index, Name = "Enemy" };
            enemy.Cards.AddRange(sorted ? new[] { 1, 1, 2, 2, 3, 3 } : new[] { 1, 2, 3, 1, 2, 3 }); result.Enemies.Add(enemy);
        }
        return result;
    }
    private static ProgressSnapshot SnapshotFixture()
    {
        var manifest = Fixture();
        var result = new ProgressSnapshot { SelectedStageId = 3, SelectedFloorId = 10, ClaimRevision = 4, DeckRevision = 5,
            Preparation = new PreparationSnapshot { Available = true, Reason = PreparationReason.None, Phase = PreparationPhase.Editing,
                ContextId = 20, Revision = 6, StageId = 3, FloorId = 10, MaxUnits = 3, CurrentWaveIndex = 0, ClaimRevision = 4, DeckRevision = 5 } };
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = 10 }; result.Floors.Add(floor);
        result.Preparation.Floors.Add(new PreparationFloorEntry { FloorId = 10, CanParticipate = true });
        var wave = new PreparationWaveEntry { WaveIndex = 0 }; result.Preparation.Waves.Add(wave);
        for (byte index = 0; index < 2; index++)
        {
            var actor = manifest.Librarians[index]; var deck = new ProgressSnapshot.UnitDeckEntry
            { UnitIdentity = actor.UnitIdentity, BookToken = actor.BookToken, BookId = actor.BookId, BookInstanceId = actor.BookInstanceId };
            deck.Cards.AddRange(actor.Cards); deck.Display.Available = true; deck.Display.MaxHp = 100; deck.Display.Break = 50;
            result.UnitDecks.Add(deck); result.ClaimOwners.Add(actor.ControllerId); floor.Units.Add(actor.Name);
            result.Preparation.Participants.Add(new PreparationUnitEntry { UnitIndex = index, UnitIdentity = actor.UnitIdentity, CanParticipate = true, Participating = true });
            result.Preparation.Controllers.Add(new PreparationControllerEntry { PlayerId = actor.ControllerId, Connected = true, Ready = true });
            var source = manifest.Enemies[index]; var enemy = new PreparationEnemyEntry { EnemyIdentity = source.EnemyIdentity,
                EnemyId = source.EnemyId, BookId = source.BookId, Name = source.Name, CardsVisible = true };
            enemy.Cards.AddRange(source.Cards); enemy.Display.Available = true; enemy.Display.MaxHp = 30; enemy.Display.Break = 15; wave.Enemies.Add(enemy);
        }
        return result;
    }
}
