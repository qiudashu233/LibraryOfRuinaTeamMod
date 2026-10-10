using System;
using System.Linq;
using RuinaCoop;

internal static class InitialStateChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var value = Fixture(); var manifest = ManifestFixture(); string reason;
        check(BattleInitialStateCodec.Validate(value, out reason), "first-act runtime fixture validates");
        check(BattleInitialStateCodec.ValidateAgainstManifest(value, manifest, out reason), "runtime first act matches frozen configuration");
        var bytes = BattleInitialStateCodec.Encode(value); BattleInitialState decoded;
        check(BattleInitialStateCodec.TryDecode(bytes, out decoded), "first-act runtime state round trips");
        check(bytes.SequenceEqual(BattleInitialStateCodec.Encode(decoded)), "runtime first-act encoding is canonical");
        check(decoded.Actors[0].Cards[0].CardId == decoded.Actors[0].Cards[1].CardId &&
            decoded.Actors[0].Cards[0].InstanceId != decoded.Actors[0].Cards[1].InstanceId, "same XML card has distinct runtime instance ids");
        check(decoded.Actors[0].Cards[2].Zone == BattleCardZone.Hand && decoded.Actors[0].Cards[2].Position == 0,
            "hand is distinguished from deck with a per-zone position");
        check(decoded.Actors[2].Intent[0].CardInstanceId == decoded.Actors[2].Cards[2].InstanceId &&
            decoded.Actors[2].Intent[0].TargetActorId == 1 && decoded.Actors[2].Intent[0].TargetSlot == 0,
            "enemy intent identifies an actual card instance and exact target die");
        check(decoded.Actors[3].SpeedDice[0].Value == 2 && decoded.Actors[3].SpeedDice[0].Controllable,
            "rolled speed and control flags round trip");
        check(BattleInitialStateCodec.Digest(bytes) == BitConverter.ToString(BattleProtocol.ComputeDigest(bytes)).Replace("-", ""),
            "logged runtime digest hashes exact encoded state");
        // Vanilla records enterBinah from the unit's default book 8 even after
        // equipping an ordinary page. Keep that captured achievement-entry
        // marker independently of the current supported equipment IDs.
        var binahManifest = ManifestFixture(); var binahState = Fixture(); uint binahCardInstance = 1;
        for (var actorIndex = 0; actorIndex < 4; actorIndex++)
        {
            var actor = binahState.Actors[actorIndex]; actor.Cards.Clear(); foreach (var zone in actor.CardZones) zone.Clear();
            var cardList = actorIndex < 2 ? binahManifest.Librarians[actorIndex].Cards : binahManifest.Enemies[actorIndex - 2].Cards;
            cardList.Clear(); cardList.AddRange(actorIndex < 2 ? new[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 } : new[] { 1, 2, 3, 1, 2, 3 });
            actor.BookId = actorIndex < 2 ? 200001 + actorIndex : 101003 + actorIndex - 2;
            if (actorIndex < 2) { binahManifest.Librarians[actorIndex].BookId = actor.BookId; }
            else { binahManifest.Enemies[actorIndex - 2].BookId = actor.BookId; binahManifest.Enemies[actorIndex - 2].EnemyId = 1003 + actorIndex - 2; }
            for (var cardIndex = 0; cardIndex < cardList.Count; cardIndex++)
            {
                var zone = cardIndex == cardList.Count - 1 ? BattleCardZone.Hand : BattleCardZone.Deck;
                var card = new BattleCardState { InstanceId = binahCardInstance++, CardId = cardList[cardIndex], Zone = zone,
                    Position = zone == BattleCardZone.Hand ? (byte)0 : (byte)cardIndex };
                actor.Cards.Add(card); actor.CardZones[(int)zone].Add(card.InstanceId);
            }
            if (actor.Enemy) actor.Intent[0].CardInstanceId = actor.Cards.Last().InstanceId;
        }
        binahManifest.Librarians[0].Name = "Binah";
        check(BattleInitialStateCodec.ValidateAgainstManifest(binahState, binahManifest, out reason), "ordinary passive-free mouse pages and full basic decks form a valid first-act fixture");
        var beforeBinahEntry = BattleInitialStateCodec.Encode(binahState); binahState.StageFlags = 8;
        check(BattleInitialStateCodec.ValidateAgainstManifest(binahState, binahManifest, out reason), "default Binah entry marker remains legal after equipping an ordinary whitelist page");
        var binahEntryBytes = BattleInitialStateCodec.Encode(binahState);
        check(BattleInitialStateCodec.TryDecode(binahEntryBytes, out decoded) && decoded.StageFlags == 8 && decoded.Actors[0].BookId == 200001,
            "enterBinah flag and current ordinary equipped page round trip independently");
        check(BattleInitialStateCodec.Digest(beforeBinahEntry) != BattleInitialStateCodec.Digest(binahEntryBytes), "legal enterBinah achievement-entry flag is covered by runtime digest");
        foreach (var unsupportedFlag in new byte[] { 1, 2, 4, 16, 32, 64, 128, 9 })
        {
            binahState.StageFlags = unsupportedFlag;
            check(!BattleInitialStateCodec.Validate(binahState, out reason), "non-Binah or unknown stage flag remains rejected: " + unsupportedFlag);
        }
        for (var length = 0; length < bytes.Length; length++)
            check(!BattleInitialStateCodec.TryDecode(bytes.Take(length).ToArray(), out decoded) && decoded == null,
                "first-act truncation rejected at " + length);
        check(!BattleInitialStateCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out decoded), "first-act trailing bytes rejected");
        check(!BattleInitialStateCodec.TryDecode(null, out decoded), "null runtime state rejected");
        check(!BattleInitialStateCodec.TryDecode(new byte[BattleInitialStateCodec.MaximumBytes + 1], out decoded), "runtime state packet limit enforced");
        var changed = (byte[])bytes.Clone(); changed[0]++;
        check(!BattleInitialStateCodec.TryDecode(changed, out decoded), "unknown runtime schema rejected");

        foreach (var mutation in new Action<BattleInitialState>[]
        {
            state => state.StageId = 4, state => state.FloorId = 0, state => state.FloorId = 11,
            state => state.WaveIndex = 1, state => state.Round = 2, state => state.Phase = 6,
            state => state.Map = null, state => state.Map = "bad\nmap", state => state.Map = new string('中', 86), state => state.Map = "\ud800",
            state => state.StageStorageCount = 1, state => state.StageCounters[0] = 1, state => state.StageFlags = 1,
            state => state.LibrarianTeamEmotion[0] = -1, state => state.EnemyTeamEmotion[0] = 10001,
            state => state.Actors.RemoveAt(0), state => state.Actors.Add(state.Actors[0]), state => state.Actors[0] = null,
            state => state.Actors[1].ActorId = 1, state => state.Actors[0].Enemy = true,
            state => state.Actors[0].ConfigurationIdentity = 0,
            state => state.Actors[1].ConfigurationIdentity = state.Actors[0].ConfigurationIdentity,
            state => state.Actors[3].ConfigurationIdentity = state.Actors[2].ConfigurationIdentity,
            state => state.Actors[1].NativeUnitId = state.Actors[0].NativeUnitId, state => state.Actors[0].NativeUnitId = -1,
            state => state.Actors[0].NativeIndex = 5, state => state.Actors[1].NativeIndex = state.Actors[0].NativeIndex,
            state => state.Actors[3].NativeIndex = state.Actors[2].NativeIndex, state => state.Actors[0].BookId = 0,
            state => state.Actors[0].Hp = float.NaN, state => state.Actors[0].StageHp = float.PositiveInfinity,
            state => state.Actors[0].Hp = 0, state => state.Actors[0].Hp = 31, state => state.Actors[0].StageHp = 31,
            state => state.Actors[0].MaxHp = 10001, state => state.Actors[0].BreakGauge = -1,
            state => state.Actors[0].BreakGauge = 16, state => state.Actors[0].MaxBreakGauge = 10001,
            state => state.Actors[0].BreakLife = 2, state => state.Actors[0].MaxBreakLife = 11,
            state => state.Actors[0].Dead = true, state => state.Actors[0].Extinct = true,
            state => state.Actors[0].Knockout = true, state => state.Actors[0].NextTurnBreak = true,
            state => state.Actors[0].BlockBreakRecovery = true, state => state.Actors[0].TurnState = 33,
            state => state.Actors[0].PlayPoint = -1, state => state.Actors[0].PlayPoint = 4,
            state => state.Actors[0].MaxPlayPoint = 21, state => state.Actors[0].ReservedPlayPoint = 21,
            state => state.Actors[0].LostPlayPoint = 21, state => state.Actors[0].NextRoundPlayPoint = -21,
            state => state.Actors[0].MaxHand = 0, state => state.Actors[0].MaxDrawHand = 65,
            state => state.Actors[0].EmotionLevel = 1, state => state.Actors[0].MaxEmotionLevel = 6,
            state => state.Actors[0].MaxEmotionCoins = 101, state => state.Actors[0].MaxEgoCoins = -1,
            state => state.Actors[0].MentalState = 5, state => state.Actors[0].EmotionSkillPoint = 11,
            state => state.Actors[0].ForcedLevelUps = 1, state => state.Actors[0].EmotionCoins.Add(3),
            state => state.Actors[0].TotalEmotionCoins.AddRange(Enumerable.Repeat((byte)1, 129)),
            state => state.Actors[0].Resistances[0] = 17, state => state.Actors[0].EffectQueueCounts[0] = 1,
            state => state.Actors[0].EmotionStatBonus[0] = 1,
            state => state.Actors[0].Cards.Clear(), state => state.Actors[0].Cards[0] = null,
            state => state.Actors[0].Cards[0].InstanceId = 0,
            state => state.Actors[1].Cards[0].InstanceId = state.Actors[0].Cards[0].InstanceId,
            state => state.Actors[0].Cards[0].CardId = 6, state => state.Actors[0].Cards[0].Zone = (BattleCardZone)5,
            state => state.Actors[0].Cards[0].Zone = BattleCardZone.Hand,
            state => state.Actors[0].Cards[1].Position = 0, state => state.Actors[0].Cards[0].Position = 1,
            state => state.Actors[0].CardZones[0].Add(999), state => state.Actors[0].CardZones[0].Add(0),
            state => state.Actors[0].CardZones[0].Add(state.Actors[0].CardZones[0][0]),
            state => state.Actors[0].CardZones[0].RemoveAt(0),
            state => state.Actors[0].CardZones[2].Add(state.Actors[0].CardZones[1][0]),
            state => state.Actors[0].CardZones[4].Add(state.Actors[0].CardZones[0][0]),
            state => state.Actors[0].Cards[0].Flags = 32, state => state.Actors[0].Cards[0].Cost = 21,
            state => state.Actors[0].Cards[0].CurrentCost = -1, state => state.Actors[0].Cards[0].OriginalCost = 21,
            state => state.Actors[0].Cards[0].CostAdder = int.MinValue,
            state => state.Actors[0].Cards[0].PriorityAdder = int.MinValue, state => state.Actors[0].Cards[0].Priority = int.MinValue,
            state => state.Actors[0].Cards[0].MaxCooltime = float.NaN, state => state.Actors[0].Cards[0].CurrentCooltime = 1,
            state => state.Actors[0].Cards[0].BufCount = 1, state => state.Actors[0].SpeedDice.Clear(),
            state => state.Actors[0].SpeedDice[0] = null, state => state.Actors[0].SpeedDice[0].Min = -1,
            state => state.Actors[0].SpeedDice[0].Max = 0, state => state.Actors[0].SpeedDice[0].Value = 1001,
            state => state.Actors[2].Intent[0] = null, state => state.Actors[2].Intent[0].Slot = 1,
            state => state.Actors[2].Intent[0].CardInstanceId = state.Actors[0].Cards[0].InstanceId,
            state => state.Actors[2].Intent[0].TargetActorId = 101, state => state.Actors[2].Intent[0].EarlyTargetActorId = 102,
            state => state.Actors[2].Intent[0].TargetSlot = 1, state => state.Actors[2].Intent[0].EarlyTargetSlot = -1,
            state => state.Actors[2].Intent[0].Speed = 1001, state => state.Actors[2].Intent[0].EmotionMultiplier = 11,
            state => state.Actors[2].Intent[0].BehaviourQueueCount = 1, state => state.Actors[2].Intent[0].SubTargetCount = 1,
            state => state.Actors[2].Intent[0].ExcludedDiceCount = 1,
            state => state.Actors[0].Intent.Add(state.Actors[2].Intent[0])
        })
        {
            var invalid = Fixture(); mutation(invalid);
            check(!BattleInitialStateCodec.Validate(invalid, out reason), "invalid runtime data rejects before importing or presenting");
            var rejected = false;
            try { BattleInitialStateCodec.Encode(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid runtime data cannot encode");
        }
        var incompatibleManifest = ManifestFixture(); incompatibleManifest.FloorId = 2;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(value, incompatibleManifest, out reason), "state floor must match frozen manifest");
        incompatibleManifest = ManifestFixture(); incompatibleManifest.Librarians[0].BookInstanceId++;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(value, incompatibleManifest, out reason), "state book instance must match frozen manifest");
        incompatibleManifest = ManifestFixture(); incompatibleManifest.Librarians[0].Cards[0] = 3;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(value, incompatibleManifest, out reason), "runtime card multiset must match manifest including multiplicity");
        incompatibleManifest = ManifestFixture(); incompatibleManifest.Librarians[0].UnitIdentity = 999;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(value, incompatibleManifest, out reason), "runtime librarian binds frozen unit identity rather than its display name");
        incompatibleManifest = ManifestFixture(); incompatibleManifest.Enemies[0].EnemyIdentity = 999;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(value, incompatibleManifest, out reason), "runtime enemy binds frozen enemy identity");
        var nonconsecutive = ManifestFixture(); nonconsecutive.Librarians[0].RosterIndex = 1; nonconsecutive.Librarians[1].RosterIndex = 4;
        check(BattleInitialStateCodec.ValidateAgainstManifest(value, nonconsecutive, out reason), "selected roster slots 1 and 4 map to native librarian indices 0 and 1");
        var sharedNumberState = Fixture(); var sharedNumberManifest = ManifestFixture();
        sharedNumberState.Actors[2].ConfigurationIdentity = sharedNumberState.Actors[0].ConfigurationIdentity;
        sharedNumberManifest.Enemies[0].EnemyIdentity = sharedNumberManifest.Librarians[0].UnitIdentity;
        check(BattleInitialStateCodec.ValidateAgainstManifest(sharedNumberState, sharedNumberManifest, out reason),
            "librarian and enemy token namespaces can legally use the same numeric identity");
        var misbound = Fixture(); misbound.Actors[0].NativeIndex = 1; misbound.Actors[1].NativeIndex = 0;
        check(!BattleInitialStateCodec.ValidateAgainstManifest(misbound, nonconsecutive, out reason), "swapping unique native indices cannot rebind selected librarians");
        var rearranged = Fixture(); rearranged.Actors[0].Cards[1].CardId = 2; rearranged.Actors[0].Cards[2].CardId = 1;
        check(BattleInitialStateCodec.ValidateAgainstManifest(rearranged, manifest, out reason), "runtime shuffled order is independent of configured deck order");
        check(BattleInitialStateCodec.Digest(bytes) != BattleInitialStateCodec.Digest(BattleInitialStateCodec.Encode(rearranged)),
            "runtime ordered card identities and zones are covered by digest");

        var sharedUse = Fixture(); var usedActor = sharedUse.Actors[2]; var usedCard = usedActor.Cards[2];
        usedActor.CardZones[1].Clear(); usedActor.CardZones[2].Add(usedCard.InstanceId); usedActor.CardZones[4].Add(usedCard.InstanceId);
        usedCard.Zone = BattleCardZone.Used;
        check(BattleInitialStateCodec.ValidateAgainstManifest(sharedUse, manifest, out reason), "Used and Reserved legally reference one physical card without changing deck multiplicity");
        var sharedBytes = BattleInitialStateCodec.Encode(sharedUse);
        check(BattleInitialStateCodec.TryDecode(sharedBytes, out decoded) && decoded.Actors[2].Cards.Count == 3,
            "shared Used/Reserved memberships do not create an extra card instance");
        check(ReferenceEquals(decoded.Actors[2].CardsInZone(BattleCardZone.Used).Single(), decoded.Actors[2].CardsInZone(BattleCardZone.Reserved).Single()),
            "Used and Reserved resolve to the same unique catalogue instance");
        usedActor.CardZones[4].Clear();
        check(BattleInitialStateCodec.Digest(sharedBytes) != BattleInitialStateCodec.Digest(BattleInitialStateCodec.Encode(sharedUse)),
            "all five ordered zone memberships are part of the authoritative digest");
        var reservedOffset = Fixture(); var offsetActor = reservedOffset.Actors[0];
        offsetActor.Cards[1].Zone = BattleCardZone.Used; offsetActor.Cards[1].Position = 0;
        offsetActor.Cards[2].Zone = BattleCardZone.Reserved; offsetActor.Cards[2].Position = 1;
        offsetActor.CardZones[0].RemoveAt(1); offsetActor.CardZones[1].Clear();
        offsetActor.CardZones[2].Add(offsetActor.Cards[1].InstanceId);
        offsetActor.CardZones[4].AddRange(new[] { offsetActor.Cards[1].InstanceId, offsetActor.Cards[2].InstanceId });
        check(BattleInitialStateCodec.TryDecode(BattleInitialStateCodec.Encode(reservedOffset), out decoded) &&
            decoded.Actors[0].CardsInZone(BattleCardZone.Reserved).Select(card => card.InstanceId).SequenceEqual(offsetActor.CardZones[4]),
            "a new Reserved catalogue entry may follow a shared Used card at position one");
        var cooldown = Fixture(); cooldown.Actors[0].Cards[0].MaxCooltime = 9; cooldown.Actors[0].Cards[0].CurrentCooltime = 2;
        check(BattleInitialStateCodec.TryDecode(BattleInitialStateCodec.Encode(cooldown), out decoded) && decoded.Actors[0].Cards[0].MaxCooltime == 9,
            "nonnegative bounded native cooldown metadata is preserved for basic card ids");

        foreach (var mutation in new Action<BattleInitialState>[]
        {
            state => state.Map = "another map", state => state.Actors[0].StageHp = 29,
            state => state.LibrarianTeamEmotion[0] = 1, state => state.EnemyTeamEmotion[0] = 1,
            state => state.Actors[0].PlayPoint = 2, state => state.Actors[0].EmotionCoins.Add(1),
            state => state.Actors[0].Resistances[0] = 1, state => state.Actors[0].Cards[0].Cost = 2,
            state => state.Actors[0].ConfigurationIdentity = 999,
            state => state.Actors[0].Cards[0].Flags = 1,
            state => { state.Actors[0].Cards[0].InstanceId = 100; state.Actors[0].CardZones[0][0] = 100; },
            state => state.Actors[0].SpeedDice[0].Value = 3, state => state.Actors[0].SpeedDice[0].Broken = true,
            state => state.Actors[2].Intent[0].TargetActorId = 2, state => state.Actors[2].Intent[0].IgnorePower = true
        })
        {
            var altered = Fixture(); mutation(altered);
            check(BattleInitialStateCodec.Digest(bytes) != BattleInitialStateCodec.Digest(BattleInitialStateCodec.Encode(altered)),
                "runtime field mutation changes authoritative state digest");
        }
        var maximum = Fixture(); maximum.Map = new string('x', 256); uint instance = 1;
        foreach (var actor in maximum.Actors)
        {
            actor.Cards.Clear(); actor.SpeedDice.Clear(); actor.Intent.Clear();
            foreach (var zone in actor.CardZones) zone.Clear();
            actor.EmotionCoins.AddRange(Enumerable.Repeat((byte)2, 128));
            actor.TotalEmotionCoins.AddRange(Enumerable.Repeat((byte)2, 128));
            actor.EgoEmotionCoins.AddRange(Enumerable.Repeat((byte)2, 128));
            for (byte position = 0; position < 64; position++)
            { actor.CardZones[0].Add(instance); actor.Cards.Add(new BattleCardState { InstanceId = instance++, CardId = 1, Zone = BattleCardZone.Deck, Position = position }); }
            for (var index = 0; index < 8; index++) actor.SpeedDice.Add(new BattleSpeedDieState { Min = 1, Max = 3, Value = 2 });
        }
        check(BattleInitialStateCodec.TryDecode(BattleInitialStateCodec.Encode(maximum), out decoded) && decoded.Actors[3].Cards.Count == 64,
            "maximum card/die/coin/map counts round trip within packet bound");
        maximum.Actors[0].SpeedDice.Add(new BattleSpeedDieState());
        check(!BattleInitialStateCodec.Validate(maximum, out reason), "ninth speed die rejected");
    }

    private static BattleInitialState Fixture()
    {
        var result = new BattleInitialState { StageId = 3, FloorId = 1, WaveIndex = 0, Round = 1, Phase = 5, Map = "YunOffice" };
        for (var n = 0; n < 4; n++)
        {
            var actor = new BattleActorState { ActorId = (uint)(n < 2 ? n + 1 : 101 + n - 2), Enemy = n >= 2,
                ConfigurationIdentity = (ulong)(n < 2 ? 10 + n : 100 + n - 2),
                NativeUnitId = n, NativeIndex = n % 2, BookId = n + 1, BookInstanceId = n < 2 ? n : 0,
                Hp = 30, StageHp = 30, MaxHp = 30, BreakGauge = 15, MaxBreakGauge = 15, BreakLife = 1, MaxBreakLife = 1,
                PlayPoint = 3, MaxPlayPoint = 3, StartingPlayPoint = 3, DefaultRecoverPoint = 1, RecoverPoint = 1, MaxHand = 8, MaxDrawHand = 8 };
            for (byte p = 0; p < 3; p++) actor.Cards.Add(new BattleCardState { InstanceId = (uint)(n * 3 + p + 1), CardId = p < 2 ? 1 : 2,
                Zone = p < 2 ? BattleCardZone.Deck : BattleCardZone.Hand, Position = p < 2 ? p : (byte)0,
                Cost = 1, CurrentCost = 1, OriginalCost = 1 });
            foreach (var card in actor.Cards) actor.CardZones[(int)card.Zone].Add(card.InstanceId);
            actor.SpeedDice.Add(new BattleSpeedDieState { Min = 1, Max = 3, Value = 2, Controllable = true });
            if (actor.Enemy) actor.Intent.Add(new BattleCardIntentState { Slot = 0, CardInstanceId = actor.Cards[2].InstanceId,
                TargetActorId = 1, EarlyTargetActorId = 1, TargetSlot = 0, EarlyTargetSlot = 0, Speed = 2, EmotionMultiplier = 1, FirstAction = true });
            result.Actors.Add(actor);
        }
        return result;
    }

    private static BattleManifest ManifestFixture()
    {
        var result = new BattleManifest { HostId = 1, StageId = 3, FloorId = 1 };
        for (var n = 0; n < 2; n++)
        {
            var actor = new BattleActorConfiguration { ActorId = (uint)(n + 1), RosterIndex = (byte)n, UnitIdentity = (ulong)(10 + n),
                ControllerId = (ulong)(n + 1), BookToken = (ulong)(30 + n), BookId = n + 1, BookInstanceId = n, Name = "same" };
            actor.Cards.AddRange(new[] { 1, 1, 2 }); result.Librarians.Add(actor);
            var enemy = new BattleEnemyConfiguration { ActorId = (uint)(101 + n), EnemyIdentity = (ulong)(100 + n), EnemyId = n + 1, BookId = n + 3, Name = "same" };
            enemy.Cards.AddRange(new[] { 1, 1, 2 }); result.Enemies.Add(enemy);
        }
        return result;
    }
}
