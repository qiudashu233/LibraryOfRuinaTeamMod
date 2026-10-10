using System;
using System.Collections.Generic;
using LOR_DiceSystem;

namespace LOR_DiceSystem
{
    internal enum BehaviourDetail { Slash, Penetrate, Hit }
    internal sealed class DiceBehaviour { internal string Script, ActionScript; }
    internal sealed class DiceCardXmlInfo
    {
        internal RuinaCoop.LorId id;
        internal string Script, PriorityScript, SpecialEffect, SkinChange, MapChange;
        internal readonly List<DiceBehaviour> DiceBehaviourList = new List<DiceBehaviour>();
    }
}

namespace RuinaCoop
{
    // ValidateManifest uses these in-memory XML dictionaries. Every API that
    // could reach native/runtime models throws: a test cannot silently turn
    // this harness into an emulated battle or claim native game validation.
    internal static class ForbiddenRuntime
    {
        internal static int Calls;
        internal static T Call<T>() { Calls++; throw new InvalidOperationException("Native runtime execution is forbidden in the adapter manifest harness."); }
    }
    internal sealed class LorId : IEquatable<LorId>
    {
        internal int id; internal string packageId;
        internal LorId(int value) { id = value; }
        internal bool IsBasic() { return string.IsNullOrEmpty(packageId); }
        public bool Equals(LorId other) { return !ReferenceEquals(other, null) && id == other.id && (packageId ?? "") == (other.packageId ?? ""); }
        public override bool Equals(object value) { return Equals(value as LorId); }
        public override int GetHashCode() { return id ^ (packageId ?? "").GetHashCode(); }
        public static bool operator ==(LorId left, LorId right) { return ReferenceEquals(left, right) || !ReferenceEquals(left, null) && left.Equals(right); }
        public static bool operator !=(LorId left, LorId right) { return !(left == right); }
    }
    internal sealed class BookEquipEffect
    {
        internal readonly List<int> PassiveList = new List<int>();
        internal readonly List<int> OnlyCard = new List<int>();
        internal readonly List<int> CardList = new List<int>();
    }
    internal sealed class BookXmlInfo { internal LorId id; internal readonly BookEquipEffect EquipEffect = new BookEquipEffect(); }
    internal sealed class BookXmlList
    {
        internal static readonly BookXmlList Instance = new BookXmlList();
        internal readonly Dictionary<int, BookXmlInfo> Books = new Dictionary<int, BookXmlInfo>();
        internal BookXmlInfo GetData(int id) { BookXmlInfo value; return Books.TryGetValue(id, out value) ? value : null; }
    }
    internal sealed class StageWaveInfo
    {
        internal readonly List<LorId> enemyUnitIdList = new List<LorId>();
        internal string aggroScript, managerScript;
    }
    internal sealed class StageClassInfo { internal LorId id; internal readonly List<StageWaveInfo> waveList = new List<StageWaveInfo>(); }
    internal sealed class StageClassInfoList
    {
        internal static readonly StageClassInfoList Instance = new StageClassInfoList();
        internal readonly Dictionary<int, StageClassInfo> Stages = new Dictionary<int, StageClassInfo>();
        internal StageClassInfo GetData(int id) { StageClassInfo value; return Stages.TryGetValue(id, out value) ? value : null; }
    }
    internal sealed class ItemXmlDataList
    {
        internal static readonly ItemXmlDataList instance = new ItemXmlDataList();
        internal readonly Dictionary<int, DiceCardXmlInfo> Cards = new Dictionary<int, DiceCardXmlInfo>();
        internal DiceCardXmlInfo GetCardItem(LorId id, bool ignored) { DiceCardXmlInfo value; return Cards.TryGetValue(id.id, out value) ? value : null; }
    }
    internal sealed class SessionStub { internal bool IsHost; }
    internal static class DeckGuard { internal static SessionStub Session; }
    internal static class NativeUi { internal static object Get(object target, string name) { return ForbiddenRuntime.Call<object>(); } }
    internal enum Faction { Player, Enemy }
    internal sealed class StageController
    {
        internal static StageController Instance { get { return ForbiddenRuntime.Call<StageController>(); } }
        internal EnemyTeamStageManager EnemyStageManager;
        internal int CurrentFloor, CurrentWave, RoundTurn, Phase;
        internal StageModel GetStageModel() { return ForbiddenRuntime.Call<StageModel>(); }
        internal StageLibraryFloorModel GetCurrentStageFloorModel() { return ForbiddenRuntime.Call<StageLibraryFloorModel>(); }
        internal StageWaveModel GetCurrentWaveModel() { return ForbiddenRuntime.Call<StageWaveModel>(); }
    }
    internal sealed class StageModel
    {
        internal StageClassInfo ClassInfo;
        internal string GetCurrentMapInfo() { return ForbiddenRuntime.Call<string>(); }
    }
    internal sealed class StageLibraryFloorModel
    {
        internal EmotionBattleTeamModel team;
        internal List<UnitBattleDataModel> GetUnitBattleDataList() { return ForbiddenRuntime.Call<List<UnitBattleDataModel>>(); }
    }
    internal sealed class StageWaveModel { internal EmotionBattleTeamModel team; internal List<UnitBattleDataModel> UnitList; }
    internal sealed class EnemyTeamStageManager { }
    internal sealed class EnemyUnitAggroSetter { }
    internal sealed class EnemyUnitTargetSetter { }
    internal sealed class BattleObjectManager
    {
        internal static BattleObjectManager instance { get { return ForbiddenRuntime.Call<BattleObjectManager>(); } }
        internal List<BattleUnitModel> GetList() { return ForbiddenRuntime.Call<List<BattleUnitModel>>(); }
    }
    internal sealed class UnitDataModel { internal LorId EnemyUnitId; internal string aiScript; }
    internal sealed class UnitBattleDataModel { internal UnitDataModel unitData; internal float hp; }
    internal sealed class BookModel { internal LorId BookId; internal int instanceId; }
    internal sealed class BattleUnitModel
    {
        internal Faction faction; internal UnitBattleDataModel UnitData; internal BookModel Book;
        internal int index, id, turnState, MaxHp, MaxBreakLife, PlayPoint, MaxPlayPoint;
        internal float hp;
        internal BattleUnitBreakDetail breakDetail;
        internal BattlePlayingCardSlotDetail cardSlotDetail;
        internal BattleAllyCardDetail allyCardDetail;
        internal BattleUnitBufListDetail bufListDetail;
        internal BattleUnitPassiveDetail passiveDetail;
        internal BattlePersonalEgoCardDetail personalEgoDetail;
        internal BattleUnitEmotionDetail emotionDetail;
        internal object targetSetter, currentDiceAction, savedCardDetail;
        internal List<SpeedDice> speedDiceResult;
        internal bool IsDead() { return ForbiddenRuntime.Call<bool>(); }
        internal bool IsExtinction() { return ForbiddenRuntime.Call<bool>(); }
        internal bool IsKnockout() { return ForbiddenRuntime.Call<bool>(); }
        internal int GetResistHP(BehaviourDetail detail) { return ForbiddenRuntime.Call<int>(); }
        internal int GetResistBP(BehaviourDetail detail) { return ForbiddenRuntime.Call<int>(); }
    }
    internal sealed class BattleUnitBreakDetail
    {
        internal int breakGauge, breakLife; internal bool nextTurnBreak, blockRecoverBreakByEvaision;
        internal int GetDefaultBreakGauge() { return ForbiddenRuntime.Call<int>(); }
    }
    internal class BattlePlayingCardDataInUnitModel
    {
        internal BattleDiceCardModel card; internal BattleUnitModel owner, target, earlyTarget;
        internal object currentBehavior, currentBehaviorUI, cardAbility;
        internal List<object> cardBehaviorQueue, subTargets;
        internal int targetSlotOrder, earlyTargetOrder, speedDiceResultValue, emotionMultiplier;
        internal bool isDestroyed, isKeepedCard, isFirstAction, ignorePower;
    }
    internal sealed class BattleKeepedCardDataInUnitModel : BattlePlayingCardDataInUnitModel { }
    internal sealed class BattlePlayingCardSlotDetail
    {
        internal BattleKeepedCardDataInUnitModel keepCard;
        internal List<object> cardQueue; internal int ReservedPlayPoint;
        internal List<BattlePlayingCardDataInUnitModel> cardAry;
    }
    internal sealed class BattleAllyCardDetail { }
    internal sealed class BattleUnitBufListDetail
    {
        internal List<object> GetActivatedBufList() { return ForbiddenRuntime.Call<List<object>>(); }
        internal List<object> GetReadyBufList() { return ForbiddenRuntime.Call<List<object>>(); }
        internal List<object> GetReadyReadyBufList() { return ForbiddenRuntime.Call<List<object>>(); }
    }
    internal sealed class BattleUnitPassiveDetail { internal List<object> PassiveList, ReadyPassiveList; }
    internal sealed class BattlePersonalEgoCardDetail { internal List<object> GetCardAll() { return ForbiddenRuntime.Call<List<object>>(); } }
    internal sealed class BattleUnitEmotionDetail
    {
        internal int EmotionLevel, MaximumEmotionLevel, MaximumCoinNumber, MaximumCoinNumberforEgo, skillPoint;
        internal List<EmotionCoin> totalEmotionCoins; internal List<object> PassiveList;
    }
    internal sealed class EmotionCoin { internal int CoinType; }
    internal sealed class EmotionBattleTeamModel
    {
        internal int GetTotalPositiveCoin() { return ForbiddenRuntime.Call<int>(); }
        internal int GetTotalNegativeCoin() { return ForbiddenRuntime.Call<int>(); }
    }
    internal sealed class StatBonus { }
    internal sealed class SpeedDice { internal int min, faces, value; internal bool breaked, isControlable; }
    internal sealed class DiceCardSelfAbilityBase { }
    internal sealed class BattleDiceCardModel
    {
        internal DiceCardXmlInfo XmlData; internal BattleUnitModel owner;
        internal int CurCost; internal float MaxCooltimeValue, CurrentCooltimeValue;
        internal bool exhaust, temporary, costSpended, isCopiedCard;
        internal List<object> GetBufList() { return ForbiddenRuntime.Call<List<object>>(); }
        internal LorId GetID() { return ForbiddenRuntime.Call<LorId>(); }
        internal int GetCost() { return ForbiddenRuntime.Call<int>(); }
        internal int GetOriginCost() { return ForbiddenRuntime.Call<int>(); }
        internal int GetPriorityAdder() { return ForbiddenRuntime.Call<int>(); }
        internal int GetPriority(int ignored) { return ForbiddenRuntime.Call<int>(); }
    }
}
