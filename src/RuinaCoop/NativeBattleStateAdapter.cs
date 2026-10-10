using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LOR_DiceSystem;

namespace RuinaCoop
{
    // Read-only host adapter. The guest never calls Capture and never creates a
    // StageController/BattleUnitModel to consume this data.
    internal static class NativeBattleStateAdapter
    {
        internal static bool ValidateManifest(BattleManifest manifest, out string reason)
        {
            if (!BattleManifestCodec.Validate(manifest, out reason)) return false;
            reason = "4A仅支持尹事务所首次接待（关卡3）、两位馆员、基础战斗书页1至5和无被动基础/鼠核心书页。";
            if (manifest.StageId != 3 || manifest.Enemies.Count != 2) return false;
            for (var i = 0; i < 2; i++)
            {
                var actor = manifest.Librarians[i];
                if (!SupportedLibrarianBook(actor.BookId) || actor.Passives.Count != 0 || actor.Cards.Count != 9 || actor.Cards.Any(id => id < 1 || id > 5)) return false;
                var book = BookXmlList.Instance.GetData(actor.BookId);
                if (book == null || !book.id.IsBasic() || book.EquipEffect.PassiveList.Count != 0 || book.EquipEffect.OnlyCard.Count != 0 || book.EquipEffect.CardList.Count != 0) return false;
                var enemy = manifest.Enemies[i];
                if (enemy.EnemyId != 1003 + i || enemy.BookId != 101003 + i || enemy.Passives.Count != 0 ||
                    !enemy.Cards.SequenceEqual(new[] { 1, 2, 3, 1, 2, 3 })) return false;
            }
            var stage = StageClassInfoList.Instance.GetData(3);
            if (stage == null || !stage.id.IsBasic() || stage.waveList.Count != 1 || stage.waveList[0].enemyUnitIdList.Count != 2 ||
                stage.waveList[0].enemyUnitIdList[0] != new LorId(1003) || stage.waveList[0].enemyUnitIdList[1] != new LorId(1004) ||
                !string.IsNullOrEmpty(stage.waveList[0].aggroScript) || !string.IsNullOrEmpty(stage.waveList[0].managerScript)) return false;
            for (var id = 1; id <= 5; id++)
            {
                var card = ItemXmlDataList.instance.GetCardItem(new LorId(id), false);
                if (!SupportedCard(card)) return false;
            }
            reason = null; return true;
        }

        internal static BattleInitialState Capture(BattleManifest manifest)
        {
            Require(DeckGuard.Session != null && DeckGuard.Session.IsHost, "Only the authenticated host may capture native battle state.");
            string reason;
            if (!ValidateManifest(manifest, out reason)) throw new InvalidOperationException(reason);
            var controller = StageController.Instance;
            var stage = controller.GetStageModel(); var floor = controller.GetCurrentStageFloorModel(); var wave = controller.GetCurrentWaveModel();
            Require(stage != null && floor != null && wave != null && stage.ClassInfo.id == new LorId(manifest.StageId), "The authoritative reception changed.");
            Exact(stage, typeof(StageModel)); Exact(floor, typeof(StageLibraryFloorModel)); Exact(wave, typeof(StageWaveModel));
            Require(controller.EnemyStageManager != null && controller.EnemyStageManager.GetType() == typeof(EnemyTeamStageManager), "A custom enemy stage manager is unsupported.");
            var aggro = NativeUi.Get(controller, "_aggroSetter");
            Require(aggro == null || aggro.GetType() == typeof(EnemyUnitAggroSetter), "A custom aggro manager is unsupported.");
            var state = new BattleInitialState { StageId = manifest.StageId, FloorId = (byte)controller.CurrentFloor, WaveIndex = checked(controller.CurrentWave - 1),
                Round = controller.RoundTurn, Phase = (int)controller.Phase, Map = stage.GetCurrentMapInfo() ?? "",
                StageStorageCount = Count(NativeUi.Get(stage, "_stageDataStorage")) };
            var stageCounterNames = new[] { "_burnKillCount", "_matanKillCount", "_heartKillCount" };
            for (var i = 0; i < stageCounterNames.Length; i++) state.StageCounters[i] = Int(stage, stageCounterNames[i]);
            var flags = new[] { "danggoUsed", "enemyTeamLevel5", "playerTeamLevel5", "enterBinah" };
            for (var i = 0; i < flags.Length; i++) if (Bool(stage, flags[i])) state.StageFlags |= (byte)(1 << i);
            Require(Count(NativeUi.Get(floor, "_selectedList")) == 0 && Count(NativeUi.Get(floor, "_selectedEgoList")) == 0 &&
                Count(NativeUi.Get(wave, "_selectedList")) == 0 && Count(NativeUi.Get(floor, "addedunitList")) == 2, "Existing emotion pages or a changed selected roster are unsupported.");
            CaptureTeam(floor.team, state.LibrarianTeamEmotion); CaptureTeam(wave.team, state.EnemyTeamEmotion);
            var all = BattleObjectManager.instance.GetList();
            Require(all.Count == 4, "The first-round actor count differs from the manifest.");
            var nativeMap = new Dictionary<BattleUnitModel, uint>();
            var actors = new List<BattleUnitModel>();
            var roster = floor.GetUnitBattleDataList();
            foreach (var entry in manifest.Librarians)
            {
                Require(entry.RosterIndex < roster.Count, "The librarian roster changed.");
                var data = roster[entry.RosterIndex];
                var unit = all.SingleOrDefault(model => model.faction == Faction.Player && ReferenceEquals(model.UnitData, data));
                Require(unit != null && unit.Book.BookId == new LorId(entry.BookId) && unit.Book.instanceId == entry.BookInstanceId, "The librarian equipment changed.");
                Require(unit.index == manifest.Librarians.Count(other => other.RosterIndex < entry.RosterIndex), "The selected librarian order changed.");
                nativeMap.Add(unit, entry.ActorId); actors.Add(unit);
            }
            for (var i = 0; i < manifest.Enemies.Count; i++)
            {
                var entry = manifest.Enemies[i]; var data = wave.UnitList[i];
                var unit = all.SingleOrDefault(model => model.faction == Faction.Enemy && ReferenceEquals(model.UnitData, data));
                Require(unit != null && unit.UnitData.unitData.EnemyUnitId == new LorId(entry.EnemyId) && unit.Book.BookId == new LorId(entry.BookId), "The enemy roster changed.");
                nativeMap.Add(unit, entry.ActorId); actors.Add(unit);
            }
            uint nextCardId = 1;
            foreach (var unit in actors)
            {
                var actor = CaptureActor(unit, nativeMap, ref nextCardId);
                actor.ConfigurationIdentity = actor.Enemy ? manifest.Enemies.Find(entry => entry.ActorId == actor.ActorId).EnemyIdentity : manifest.Librarians.Find(entry => entry.ActorId == actor.ActorId).UnitIdentity;
                state.Actors.Add(actor);
            }
            if (!BattleInitialStateCodec.ValidateAgainstManifest(state, manifest, out reason)) throw new InvalidOperationException(reason + " " + CaptureSummary(state));
            return state;
        }

        private static BattleActorState CaptureActor(BattleUnitModel unit, Dictionary<BattleUnitModel, uint> map, ref uint nextCardId)
        {
            Require(unit.GetType() == typeof(BattleUnitModel) && string.IsNullOrEmpty(unit.UnitData.unitData.aiScript), "A custom battle unit or AI script is unsupported.");
            Exact(unit.UnitData, typeof(UnitBattleDataModel)); Exact(unit.UnitData.unitData, typeof(UnitDataModel)); Exact(unit.Book, typeof(BookModel));
            Exact(unit.breakDetail, typeof(BattleUnitBreakDetail)); Exact(unit.cardSlotDetail, typeof(BattlePlayingCardSlotDetail));
            Exact(unit.allyCardDetail, typeof(BattleAllyCardDetail)); Exact(unit.bufListDetail, typeof(BattleUnitBufListDetail));
            Exact(unit.passiveDetail, typeof(BattleUnitPassiveDetail)); Exact(unit.personalEgoDetail, typeof(BattlePersonalEgoCardDetail));
            Exact(unit.emotionDetail, typeof(BattleUnitEmotionDetail)); Exact(unit.cardSlotDetail.keepCard, typeof(BattleKeepedCardDataInUnitModel));
            Require(unit.targetSetter == null || unit.targetSetter.GetType() == typeof(EnemyUnitTargetSetter), "A custom target selector is unsupported.");
            Require(unit.currentDiceAction == null && unit.cardSlotDetail.keepCard != null && unit.cardSlotDetail.keepCard.card == null &&
                unit.cardSlotDetail.keepCard.currentBehavior == null && unit.cardSlotDetail.keepCard.currentBehaviorUI == null &&
                Count(unit.cardSlotDetail.keepCard.cardBehaviorQueue) == 0 && Count(unit.cardSlotDetail.keepCard.subTargets) == 0 &&
                Count(NativeUi.Get(unit.cardSlotDetail.keepCard, "_excludedIndies")) == 0 && Count(unit.cardSlotDetail.cardQueue) == 0, "Unexpected active or retained actions at first input.");
            var a = new BattleActorState { ActorId = map[unit], NativeUnitId = unit.id, NativeIndex = unit.index, Enemy = unit.faction == Faction.Enemy,
                BookId = unit.Book.BookId.id, BookInstanceId = unit.Book.instanceId, TurnState = (int)unit.turnState,
                Hp = unit.hp, StageHp = unit.UnitData.hp, MaxHp = unit.MaxHp, BreakGauge = unit.breakDetail.breakGauge,
                MaxBreakGauge = unit.breakDetail.GetDefaultBreakGauge(), BreakLife = unit.breakDetail.breakLife, MaxBreakLife = unit.MaxBreakLife,
                NextTurnBreak = unit.breakDetail.nextTurnBreak, BlockBreakRecovery = unit.breakDetail.blockRecoverBreakByEvaision,
                Dead = unit.IsDead(), Extinct = unit.IsExtinction(), Knockout = unit.IsKnockout(),
                PlayPoint = unit.PlayPoint, MaxPlayPoint = unit.MaxPlayPoint, ReservedPlayPoint = unit.cardSlotDetail.ReservedPlayPoint,
                LostPlayPoint = Int(unit.cardSlotDetail, "_losePlayPoint"), NextRoundPlayPoint = Int(unit.cardSlotDetail, "_nextRoundPlayPoint"),
                StartingPlayPoint = Int(unit.cardSlotDetail, "_startingPlayPoint"), DefaultRecoverPoint = Int(unit.cardSlotDetail, "_defaultRecoverPoint"), RecoverPoint = Int(unit.cardSlotDetail, "_recoverPoint"),
                MaxHand = Int(unit.allyCardDetail, "_maxHand"), MaxDrawHand = Int(unit.allyCardDetail, "_maxDrawHand") };
            var emotion = unit.emotionDetail;
            a.EmotionLevel = emotion.EmotionLevel; a.MaxEmotionLevel = emotion.MaximumEmotionLevel;
            a.MaxEmotionCoins = emotion.MaximumCoinNumber; a.MaxEgoCoins = emotion.MaximumCoinNumberforEgo;
            a.MentalState = Int(emotion, "_mentalState"); a.EmotionSkillPoint = emotion.skillPoint; a.ForcedLevelUps = Int(emotion, "_forcelyLevelUpCount");
            CaptureCoins(NativeUi.Get(emotion, "_emotionCoins"), a.EmotionCoins); CaptureCoins(emotion.totalEmotionCoins, a.TotalEmotionCoins);
            CaptureCoins(NativeUi.Get(emotion, "_emotionCoinsForEgoCooltime"), a.EgoEmotionCoins);
            var statNames = new[] { "hpRate", "breakRate", "dmgAdder", "breakAdder", "hpAdder", "breakGageAdder", "guardTakenBreakRate" };
            var statBonus = NativeUi.Get(emotion, "_statBonus");
            Require(statBonus != null && statBonus.GetType() == typeof(StatBonus), "An unsupported emotion stat model was created.");
            for (var i = 0; i < statNames.Length; i++) a.EmotionStatBonus[i] = Int(statBonus, statNames[i]);
            a.Resistances[0] = (int)unit.GetResistHP(BehaviourDetail.Slash); a.Resistances[1] = (int)unit.GetResistHP(BehaviourDetail.Penetrate); a.Resistances[2] = (int)unit.GetResistHP(BehaviourDetail.Hit);
            a.Resistances[3] = (int)unit.GetResistBP(BehaviourDetail.Slash); a.Resistances[4] = (int)unit.GetResistBP(BehaviourDetail.Penetrate); a.Resistances[5] = (int)unit.GetResistBP(BehaviourDetail.Hit);
            a.EffectQueueCounts[0] = Count(unit.bufListDetail.GetActivatedBufList()); a.EffectQueueCounts[1] = Count(unit.bufListDetail.GetReadyBufList()); a.EffectQueueCounts[2] = Count(unit.bufListDetail.GetReadyReadyBufList());
            a.EffectQueueCounts[3] = Count(unit.passiveDetail.PassiveList); a.EffectQueueCounts[4] = Count(unit.passiveDetail.ReadyPassiveList);
            a.EffectQueueCounts[5] = Count(emotion.PassiveList); a.EffectQueueCounts[6] = Count(NativeUi.Get(unit, "_connectedBufs"));
            a.EffectQueueCounts[7] = Count(unit.personalEgoDetail.GetCardAll());
            if (unit.savedCardDetail != null) foreach (var name in CardZones) a.EffectQueueCounts[8] += Count(NativeUi.Get(unit.savedCardDetail, name));
            Require(a.EffectQueueCounts.All(x => x == 0), "A current or queued buff, passive, EGO or replacement deck is unsupported.");
            foreach (var die in unit.speedDiceResult)
            {
                Exact(die, typeof(SpeedDice));
                a.SpeedDice.Add(new BattleSpeedDieState { Min = die.min, Max = die.faces, Value = die.value, Broken = die.breaked, Controllable = die.isControlable });
            }
            var cards = new Dictionary<BattleDiceCardModel, uint>();
            for (var zone = 0; zone < CardZones.Length; zone++)
            {
                var position = 0;
                foreach (BattleDiceCardModel card in (IEnumerable)NativeUi.Get(unit.allyCardDetail, CardZones[zone]))
                {
                    Require(card != null && card.GetType() == typeof(BattleDiceCardModel) && SupportedCard(card.XmlData), "An unsupported runtime combat page was created.");
                    Exact(card.XmlData, typeof(DiceCardXmlInfo));
                    Require(NativeUi.Get(card, "_originalXmlData") == null, "A combat page has transformed from its original XML definition.");
                    uint existing;
                    if (cards.TryGetValue(card, out existing))
                    {
                        Require(zone == (int)BattleCardZone.Reserved && a.Cards.Find(entry => entry.InstanceId == existing).Zone == BattleCardZone.Used,
                            "One combat page instance appears in incompatible zones.");
                        a.CardZones[zone].Add(existing); position++; continue;
                    }
                    Require(card.owner == null || ReferenceEquals(card.owner, unit), "A combat page owner differs from its zone.");
                    var script = NativeUi.Get(card, "_script"); Require(script == null || script.GetType() == typeof(DiceCardSelfAbilityBase), "An active combat page script is unsupported.");
                    Require(Count(card.GetBufList()) == 0, "A combat page has a queued or active card effect.");
                    var c = new BattleCardState { InstanceId = nextCardId++, CardId = card.GetID().id, Zone = (BattleCardZone)zone, Position = checked((byte)position++),
                        Cost = card.GetCost(), CurrentCost = card.CurCost, OriginalCost = card.GetOriginCost(), CostAdder = Int(card, "_costAdder"),
                        PriorityAdder = card.GetPriorityAdder(), Priority = card.GetPriority(0), MaxCooltime = card.MaxCooltimeValue, CurrentCooltime = card.CurrentCooltimeValue, BufCount = Count(card.GetBufList()) };
                    if (card.exhaust) c.Flags |= 1; if (card.temporary) c.Flags |= 2; if (card.costSpended) c.Flags |= 4; if (card.isCopiedCard) c.Flags |= 8; if (Bool(card, "_costZero")) c.Flags |= 16;
                    cards.Add(card, c.InstanceId); a.Cards.Add(c); a.CardZones[zone].Add(c.InstanceId);
                }
            }
            for (var slot = 0; slot < unit.cardSlotDetail.cardAry.Count; slot++)
            {
                var intent = unit.cardSlotDetail.cardAry[slot]; if (intent == null || intent.card == null) continue;
                Exact(intent, typeof(BattlePlayingCardDataInUnitModel));
                uint cardId, target, early;
                Require(ReferenceEquals(intent.owner, unit) && cards.TryGetValue(intent.card, out cardId) && intent.target != null && map.TryGetValue(intent.target, out target) && intent.earlyTarget != null && map.TryGetValue(intent.earlyTarget, out early), "An enemy intent has an unknown actor or combat page.");
                // Assign separately because the guard above intentionally fails
                // closed without relying on C# definite-assignment inference.
                cardId = cards[intent.card]; target = map[intent.target]; early = map[intent.earlyTarget];
                Require(!intent.isDestroyed && !intent.isKeepedCard && intent.currentBehavior == null && intent.currentBehaviorUI == null &&
                    (intent.cardAbility == null || intent.cardAbility.GetType() == typeof(DiceCardSelfAbilityBase)), "An enemy intent has already begun resolution.");
                a.Intent.Add(new BattleCardIntentState { Slot = checked((byte)slot), CardInstanceId = cardId, TargetActorId = target, TargetSlot = intent.targetSlotOrder,
                    EarlyTargetActorId = early, EarlyTargetSlot = intent.earlyTargetOrder, Speed = intent.speedDiceResultValue, EmotionMultiplier = intent.emotionMultiplier,
                    FirstAction = intent.isFirstAction, IgnorePower = intent.ignorePower, BehaviourQueueCount = Count(intent.cardBehaviorQueue),
                    SubTargetCount = Count(intent.subTargets), ExcludedDiceCount = Count(NativeUi.Get(intent, "_excludedIndies")) });
            }
            return a;
        }

        private static readonly string[] CardZones = { "_cardInDeck", "_cardInHand", "_cardInUse", "_cardInDiscarded", "_cardInReserved" };
        private static readonly string[] TeamFields = { "_emotionLevel", "_prevEmotionLevel", "emotionLevelMax", "_emotionTotalCoinNumber", "emotionTotalBonus", "_emotionCoinNumber", "_currentLevelNeedEmotionMaxCoin", "skillPoint", "egoSelectionPoint", "currentSelectEmotionLevel", "_emotionCoinNumberForEgoCooltime", "_currentLevelNeedEmotionMaxCointForEgo" };
        private static void CaptureTeam(EmotionBattleTeamModel team, int[] fields)
        {
            Require(team != null && team.GetType() == typeof(EmotionBattleTeamModel), "An unsupported emotion team model was created.");
            for (var i = 0; i < TeamFields.Length; i++) fields[i] = Int(team, TeamFields[i]);
            fields[12] = team.GetTotalPositiveCoin(); fields[13] = team.GetTotalNegativeCoin();
        }
        private static void CaptureCoins(object source, List<byte> result)
        { foreach (EmotionCoin coin in (IEnumerable)source) { Require(coin != null, "An emotion coin is missing."); result.Add(checked((byte)coin.CoinType)); } }
        private static bool SupportedLibrarianBook(int id) { return (id >= 1 && id <= 20 && id != 6 && id != 8) || (id >= 200001 && id <= 200003); }
        private static bool SupportedCard(DiceCardXmlInfo card)
        {
            return card != null && card.id.IsBasic() && card.id.id >= 1 && card.id.id <= 5 && string.IsNullOrEmpty(card.Script) &&
                string.IsNullOrEmpty(card.PriorityScript) && string.IsNullOrEmpty(card.SpecialEffect) && string.IsNullOrEmpty(card.SkinChange) && string.IsNullOrEmpty(card.MapChange) &&
                card.DiceBehaviourList.All(die => string.IsNullOrEmpty(die.Script) && string.IsNullOrEmpty(die.ActionScript));
        }
        private static int Count(object value) { if (value == null) return 0; var collection = value as ICollection; if (collection != null) return collection.Count; var count = 0; foreach (var ignored in (IEnumerable)value) count++; return count; }
        private static int Int(object target, string field) { return Convert.ToInt32(NativeUi.Get(target, field)); }
        private static bool Bool(object target, string field) { return Convert.ToBoolean(NativeUi.Get(target, field)); }
        private static void Exact(object value, Type expected) { Require(value != null && value.GetType() == expected, "Unsupported runtime model: " + expected.Name + "."); }
        private static string CaptureSummary(BattleInitialState state)
        {
            var text = "stage=" + state.StageId + " floor=" + state.FloorId + " wave=" + state.WaveIndex + " round=" + state.Round + " phase=" + state.Phase;
            foreach (var actor in state.Actors.Take(4))
                text += " | actor=" + actor.ActorId + " hp=" + actor.Hp.ToString("R", CultureInfo.InvariantCulture) + "/" + actor.MaxHp +
                    " break=" + actor.BreakGauge + "/" + actor.MaxBreakGauge + " life=" + actor.BreakLife + "/" + actor.MaxBreakLife +
                    " light=" + actor.PlayPoint + "/" + actor.MaxPlayPoint + " emotion=" + actor.EmotionLevel + "/" + actor.MaxEmotionLevel +
                    " dice=" + actor.SpeedDice.Count + " cards=" + actor.Cards.Count + " zones=" + string.Join(",", actor.CardZones.Select(zone => zone.Count.ToString()).ToArray());
            return text.Length <= 600 ? text : text.Substring(0, 600);
        }
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}
